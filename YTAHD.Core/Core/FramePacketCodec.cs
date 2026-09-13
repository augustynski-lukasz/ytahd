using System;
using System.Security.Cryptography;

namespace YTAHD.Core.Core
{
    public static class FramePacketCodec
    {
        public static byte[] CreateDataFramePacket(int frameIndex, int totalDataFrames, int groupStart, int groupCount, int payloadLength, ReadOnlySpan<byte> payload, int payloadCapacity = 0)
        {
            int capacity = payloadCapacity > 0 ? payloadCapacity : payload.Length;
            byte[] framePacket = new byte[FramePacket.HeaderBytes + capacity];
            WriteFrameHeader(framePacket, FramePacket.FrameTypeData, frameIndex, totalDataFrames, groupStart, groupCount, payloadLength);

            var hash = SHA256.HashData(payload.Slice(0, Math.Min(payloadLength, payload.Length)));
            Buffer.BlockCopy(hash, 0, framePacket, GetHashOffset(FramePacket.FrameVersion), hash.Length);
            Buffer.BlockCopy(payload.ToArray(), 0, framePacket, FramePacket.HeaderBytes, Math.Min(capacity, payload.Length));

            return framePacket;
        }

        public static byte[] CreateParityFramePacket(int groupStart, int groupCount, int totalDataFrames, ReadOnlySpan<byte> parityPayload)
        {
            byte[] parityPacket = new byte[FramePacket.HeaderBytes + parityPayload.Length];
            WriteFrameHeader(parityPacket, FramePacket.FrameTypeParity, 0, totalDataFrames, groupStart, groupCount, parityPayload.Length);

            var parityHash = SHA256.HashData(parityPayload);
            Buffer.BlockCopy(parityHash, 0, parityPacket, GetHashOffset(FramePacket.FrameVersion), parityHash.Length);
            parityPayload.CopyTo(parityPacket.AsSpan(FramePacket.HeaderBytes, parityPacket.Length - FramePacket.HeaderBytes));

            return parityPacket;
        }

        /// <summary>
        /// Creates a stream manifest frame (CR-20260912-05 stage 2). The manifest body is the
        /// frame payload, so it is protected by the same per-frame SHA-256 as data and parity
        /// frames and can ride inside a durability parity group like any other symbol.
        /// </summary>
        public static byte[] CreateManifestFramePacket(int totalDataFrames, ReadOnlySpan<byte> manifestPayload)
            => CreateManifestFramePacket(totalDataFrames, 0, 1, manifestPayload);

        /// <summary>
        /// Creates a stream-manifest chunk frame. A manifest body larger than one frame's payload
        /// capacity is split into chunks; <paramref name="chunkIndex"/>/<paramref name="chunkCount"/>
        /// ride in the otherwise-unused frameIndex/groupCount header fields so single-chunk
        /// manifests stay byte-identical with the pre-chunking wire format.
        /// </summary>
        public static byte[] CreateManifestFramePacket(int totalDataFrames, int chunkIndex, int chunkCount, ReadOnlySpan<byte> chunkPayload)
        {
            if (chunkIndex < 0 || chunkCount < 1 || chunkIndex >= chunkCount)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkIndex), "Manifest chunk index must satisfy 0 <= chunkIndex < chunkCount.");
            }

            byte[] manifestPacket = new byte[FramePacket.HeaderBytes + chunkPayload.Length];
            WriteFrameHeader(manifestPacket, FramePacket.FrameTypeManifest, chunkIndex, totalDataFrames, 0, chunkCount, chunkPayload.Length);

            var manifestHash = SHA256.HashData(chunkPayload);
            Buffer.BlockCopy(manifestHash, 0, manifestPacket, GetHashOffset(FramePacket.FrameVersion), manifestHash.Length);
            chunkPayload.CopyTo(manifestPacket.AsSpan(FramePacket.HeaderBytes, manifestPacket.Length - FramePacket.HeaderBytes));

            return manifestPacket;
        }

        public static bool TryDecode(ReadOnlySpan<byte> packet, out byte frameType, out int frameIndex, out int totalDataFrames, out int groupStart, out int groupCount, out int payloadLength, out byte[] payload)
        {
            frameType = 0;
            frameIndex = 0;
            totalDataFrames = 0;
            groupStart = 0;
            groupCount = 0;
            payloadLength = 0;
            payload = Array.Empty<byte>();

            if (packet.Length < FramePacket.LegacyHeaderBytes)
            {
                return false;
            }

            int magic = (packet[0] << 8) | packet[1];
            byte version = packet[2];
            if (magic != FramePacket.FrameMagic || (version != FramePacket.LegacyFrameVersion && version != FramePacket.FrameVersion))
            {
                return false;
            }

            int headerBytes = GetHeaderBytes(version);
            if (packet.Length < headerBytes)
            {
                return false;
            }

            frameType = packet[3];
            if (frameType != FramePacket.FrameTypeData && frameType != FramePacket.FrameTypeParity && frameType != FramePacket.FrameTypeManifest)
            {
                return false;
            }

            frameIndex = (packet[4] << 24) | (packet[5] << 16) | (packet[6] << 8) | packet[7];
            totalDataFrames = (packet[8] << 24) | (packet[9] << 16) | (packet[10] << 8) | packet[11];
            groupStart = (packet[12] << 24) | (packet[13] << 16) | (packet[14] << 8) | packet[15];
            groupCount = packet[16];
            payloadLength = version == FramePacket.LegacyFrameVersion
                ? (packet[17] << 8) | packet[18]
                : ReadInt32BigEndian(packet.Slice(17, 4));

            if (version == FramePacket.LegacyFrameVersion)
            {
                payloadLength = ResolveLegacyPayloadLength(packet, payloadLength, headerBytes);
            }

            if (totalDataFrames <= 0 || groupStart < 0 || groupCount <= 0 || payloadLength < 0 || payloadLength > packet.Length - headerBytes)
            {
                return false;
            }

            // Strict per-frame integrity (CR-20260912-05 stage 1): the header hash is authoritative
            // for both data and parity frames. A packet whose payload does not match it is corrupt,
            // not merely weak, and must be rejected before accumulation. v1 legacy frames keep the
            // hash-gated wrapped-length recovery above; their hash is checked there.
            var expectedHash = packet.Slice(GetHashOffset(version), 32);
            if (!PayloadHashMatches(packet, headerBytes, payloadLength, expectedHash))
            {
                return false;
            }

            payload = packet.Slice(headerBytes, payloadLength).ToArray();
            return true;
        }

        private static int ResolveLegacyPayloadLength(ReadOnlySpan<byte> packet, int declaredPayloadLength, int headerBytes)
        {
            int payloadCapacity = packet.Length - headerBytes;
            if (declaredPayloadLength < 0 || declaredPayloadLength > payloadCapacity)
            {
                return declaredPayloadLength;
            }

            var expectedHash = packet.Slice(GetHashOffset(FramePacket.LegacyFrameVersion), 32);
            if (PayloadHashMatches(packet, headerBytes, declaredPayloadLength, expectedHash))
            {
                return declaredPayloadLength;
            }

            for (int candidateLength = declaredPayloadLength + 65536; candidateLength <= payloadCapacity; candidateLength += 65536)
            {
                if (PayloadHashMatches(packet, headerBytes, candidateLength, expectedHash))
                {
                    return candidateLength;
                }
            }

            return declaredPayloadLength;
        }

        private static bool PayloadHashMatches(ReadOnlySpan<byte> packet, int headerBytes, int payloadLength, ReadOnlySpan<byte> expectedHash)
        {
            if (payloadLength < 0 || payloadLength > packet.Length - headerBytes)
            {
                return false;
            }

            var payload = packet.Slice(headerBytes, payloadLength);
            var actualHash = SHA256.HashData(payload);
            return actualHash.AsSpan().SequenceEqual(expectedHash);
        }

        private static int GetHeaderBytes(byte version)
        {
            return version == FramePacket.LegacyFrameVersion ? FramePacket.LegacyHeaderBytes : FramePacket.HeaderBytes;
        }

        private static int GetHashOffset(byte version)
        {
            return version == FramePacket.LegacyFrameVersion ? 19 : 21;
        }

        private static int ReadInt32BigEndian(ReadOnlySpan<byte> bytes)
        {
            long value = ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3];
            return value > int.MaxValue ? -1 : (int)value;
        }

        public static bool TryNormalizeWithTolerance(ReadOnlySpan<byte> packet, Span<byte> normalizedPacket, out int shift)
        {
            shift = 0;
            if (packet.Length < FramePacket.HeaderBytes || normalizedPacket.Length < packet.Length)
            {
                return false;
            }

            if (TryDecode(packet, out _, out _, out _, out _, out _, out _, out _))
            {
                packet.CopyTo(normalizedPacket);
                return true;
            }

            int commonShift = EstimateCommonShift(packet);
            if (commonShift != int.MinValue)
            {
                var normalized = packet.ToArray();
                for (int i = 0; i < normalized.Length; i++)
                {
                    normalized[i] = (byte)Math.Clamp(normalized[i] - commonShift, 0, 255);
                }

                if (TryDecode(normalized, out _, out _, out _, out _, out _, out _, out _))
                {
                    shift = commonShift;
                    normalized.AsSpan().CopyTo(normalizedPacket);
                    return true;
                }
            }

            int magic0Delta = Math.Abs(packet[0] - 0x59);
            int magic1Delta = Math.Abs(packet[1] - 0x54);
            int versionDelta = Math.Min(Math.Abs(packet[2] - FramePacket.FrameVersion), Math.Abs(packet[2] - FramePacket.LegacyFrameVersion));
            int frameTypeByte = packet[3];
            if (magic0Delta <= 16 && magic1Delta <= 16 && versionDelta <= 4 && (frameTypeByte == FramePacket.FrameTypeData || frameTypeByte == FramePacket.FrameTypeParity || frameTypeByte == FramePacket.FrameTypeManifest))
            {
                packet.CopyTo(normalizedPacket);
                normalizedPacket[0] = 0x59;
                normalizedPacket[1] = 0x54;
                return TryDecode(normalizedPacket, out _, out _, out _, out _, out _, out _, out _);
            }

            return false;
        }

        public static bool TryDecodeWithTolerance(ReadOnlySpan<byte> packet, out byte frameType, out int frameIndex, out int totalDataFrames, out int groupStart, out int groupCount, out int payloadLength, out byte[] payload)
        {
            if (TryDecode(packet, out frameType, out frameIndex, out totalDataFrames, out groupStart, out groupCount, out payloadLength, out payload))
            {
                return true;
            }

            if (packet.Length < FramePacket.HeaderBytes)
            {
                return false;
            }

            var normalized = new byte[packet.Length];
            if (TryNormalizeWithTolerance(packet, normalized, out _))
            {
                return TryDecode(normalized, out frameType, out frameIndex, out totalDataFrames, out groupStart, out groupCount, out payloadLength, out payload);
            }

            return false;
        }

        private static int EstimateCommonShift(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < FramePacket.HeaderBytes)
            {
                return int.MinValue;
            }

            int expectedMagic0 = 0x59;
            int expectedMagic1 = 0x54;
            int expectedVersion = packet[2] == FramePacket.LegacyFrameVersion ? FramePacket.LegacyFrameVersion : FramePacket.FrameVersion;
            int expectedType = FramePacket.FrameTypeData;
            int sum = 0;
            int count = 0;

            for (int i = 0; i < Math.Min(packet.Length, 8); i++)
            {
                int expectedValue = i switch
                {
                    0 => expectedMagic0,
                    1 => expectedMagic1,
                    2 => expectedVersion,
                    3 => expectedType,
                    _ => 0
                };

                sum += packet[i] - expectedValue;
                count++;
            }

            int shift = count == 0 ? int.MinValue : (int)Math.Round((double)sum / count);
            if (shift == int.MinValue)
            {
                return int.MinValue;
            }

            int magic0Delta = Math.Abs(packet[0] - (expectedMagic0 + shift));
            int magic1Delta = Math.Abs(packet[1] - (expectedMagic1 + shift));
            int versionDelta = Math.Abs(packet[2] - (expectedVersion + shift));
            int frameTypeByte = packet[3];
            bool typeIsValid = frameTypeByte == FramePacket.FrameTypeData || frameTypeByte == FramePacket.FrameTypeParity || frameTypeByte == FramePacket.FrameTypeManifest;
            if (magic0Delta <= 16 && magic1Delta <= 16 && versionDelta <= 8 && typeIsValid)
            {
                return shift;
            }

            return int.MinValue;
        }

        public static void WriteFrameHeader(byte[] framePacket, byte frameType, int frameIndex, int totalDataFrames, int groupStart, int groupCount, int payloadLength)
        {
            if (framePacket == null)
                throw new ArgumentNullException(nameof(framePacket));

            if (framePacket.Length < FramePacket.HeaderBytes)
                throw new ArgumentOutOfRangeException(nameof(framePacket));

            if (payloadLength < 0)
                throw new ArgumentOutOfRangeException(nameof(payloadLength));

            framePacket[0] = (byte)((FramePacket.FrameMagic >> 8) & 0xFF);
            framePacket[1] = (byte)(FramePacket.FrameMagic & 0xFF);
            framePacket[2] = FramePacket.FrameVersion;
            framePacket[3] = frameType;
            framePacket[4] = (byte)((frameIndex >> 24) & 0xFF);
            framePacket[5] = (byte)((frameIndex >> 16) & 0xFF);
            framePacket[6] = (byte)((frameIndex >> 8) & 0xFF);
            framePacket[7] = (byte)(frameIndex & 0xFF);
            framePacket[8] = (byte)((totalDataFrames >> 24) & 0xFF);
            framePacket[9] = (byte)((totalDataFrames >> 16) & 0xFF);
            framePacket[10] = (byte)((totalDataFrames >> 8) & 0xFF);
            framePacket[11] = (byte)(totalDataFrames & 0xFF);
            framePacket[12] = (byte)((groupStart >> 24) & 0xFF);
            framePacket[13] = (byte)((groupStart >> 16) & 0xFF);
            framePacket[14] = (byte)((groupStart >> 8) & 0xFF);
            framePacket[15] = (byte)(groupStart & 0xFF);
            framePacket[16] = (byte)groupCount;
            framePacket[17] = (byte)((payloadLength >> 24) & 0xFF);
            framePacket[18] = (byte)((payloadLength >> 16) & 0xFF);
            framePacket[19] = (byte)((payloadLength >> 8) & 0xFF);
            framePacket[20] = (byte)(payloadLength & 0xFF);
        }
    }
}
