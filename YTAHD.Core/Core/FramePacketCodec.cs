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
            Buffer.BlockCopy(hash, 0, framePacket, 19, hash.Length);
            Buffer.BlockCopy(payload.ToArray(), 0, framePacket, FramePacket.HeaderBytes, Math.Min(capacity, payload.Length));

            return framePacket;
        }

        public static byte[] CreateParityFramePacket(int groupStart, int groupCount, int totalDataFrames, ReadOnlySpan<byte> parityPayload)
        {
            byte[] parityPacket = new byte[FramePacket.HeaderBytes + parityPayload.Length];
            WriteFrameHeader(parityPacket, FramePacket.FrameTypeParity, 0, totalDataFrames, groupStart, groupCount, parityPayload.Length);

            var parityHash = SHA256.HashData(parityPayload);
            Buffer.BlockCopy(parityHash, 0, parityPacket, 19, parityHash.Length);
            parityPayload.CopyTo(parityPacket.AsSpan(FramePacket.HeaderBytes, parityPacket.Length - FramePacket.HeaderBytes));

            return parityPacket;
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

            if (packet.Length < FramePacket.HeaderBytes)
            {
                return false;
            }

            int magic = (packet[0] << 8) | packet[1];
            byte version = packet[2];
            if (magic != FramePacket.FrameMagic || version != FramePacket.FrameVersion)
            {
                return false;
            }

            frameType = packet[3];
            if (frameType != FramePacket.FrameTypeData && frameType != FramePacket.FrameTypeParity)
            {
                return false;
            }

            frameIndex = (packet[4] << 24) | (packet[5] << 16) | (packet[6] << 8) | packet[7];
            totalDataFrames = (packet[8] << 24) | (packet[9] << 16) | (packet[10] << 8) | packet[11];
            groupStart = (packet[12] << 24) | (packet[13] << 16) | (packet[14] << 8) | packet[15];
            groupCount = packet[16];
            payloadLength = (packet[17] << 8) | packet[18];

            if (totalDataFrames <= 0 || groupStart < 0 || groupCount <= 0 || payloadLength < 0 || payloadLength > packet.Length - FramePacket.HeaderBytes)
            {
                return false;
            }

            payload = packet.Slice(FramePacket.HeaderBytes, payloadLength).ToArray();
            return true;
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

            int commonShift = EstimateCommonShift(packet);
            if (commonShift != int.MinValue)
            {
                var normalized = packet.ToArray();
                for (int i = 0; i < normalized.Length; i++)
                {
                    normalized[i] = (byte)Math.Clamp(normalized[i] - commonShift, 0, 255);
                }

                if (TryDecode(normalized, out frameType, out frameIndex, out totalDataFrames, out groupStart, out groupCount, out payloadLength, out payload))
                {
                    return true;
                }
            }

            int magic0Delta = Math.Abs(packet[0] - 0x59);
            int magic1Delta = Math.Abs(packet[1] - 0x54);
            int versionDelta = Math.Abs(packet[2] - FramePacket.FrameVersion);
            int frameTypeByte = packet[3];
            if (magic0Delta > 16 || magic1Delta > 16 || versionDelta > 4 || (frameTypeByte != FramePacket.FrameTypeData && frameTypeByte != FramePacket.FrameTypeParity))
            {
                return false;
            }

            var fallback = packet.ToArray();
            fallback[0] = 0x59;
            fallback[1] = 0x54;
            fallback[2] = FramePacket.FrameVersion;

            return TryDecode(fallback, out frameType, out frameIndex, out totalDataFrames, out groupStart, out groupCount, out payloadLength, out payload);
        }

        private static int EstimateCommonShift(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < FramePacket.HeaderBytes)
            {
                return int.MinValue;
            }

            int expectedMagic0 = 0x59;
            int expectedMagic1 = 0x54;
            int expectedVersion = FramePacket.FrameVersion;
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
            bool typeIsValid = frameTypeByte == FramePacket.FrameTypeData || frameTypeByte == FramePacket.FrameTypeParity;
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
            framePacket[17] = (byte)((payloadLength >> 8) & 0xFF);
            framePacket[18] = (byte)(payloadLength & 0xFF);
        }
    }
}
