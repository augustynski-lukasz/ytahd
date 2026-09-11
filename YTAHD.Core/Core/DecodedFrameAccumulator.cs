using System;
using System.Collections.Generic;
using System.IO;
using YTAHD.Core.Modulation;

namespace YTAHD.Core.Core
{
    public sealed class DecodedFrameAccumulator
    {
        private readonly SortedDictionary<int, byte[]> _orderedPayload = new();
        private readonly Dictionary<int, byte[]> _parityPayloadByGroup = new();
        private readonly Dictionary<int, int> _groupCountByGroup = new();

        public SortedDictionary<int, byte[]> OrderedPayload => _orderedPayload;
        public Dictionary<int, byte[]> ParityPayloadByGroup => _parityPayloadByGroup;
        public Dictionary<int, int> GroupCountByGroup => _groupCountByGroup;
        public int TotalDataFrames { get; private set; } = -1;
        public bool SawInvalidPacket { get; private set; }
        public int RecoveredGroupCount { get; private set; }

        public bool TryAddDecodedFrame(
            ReadOnlySpan<byte> frame,
            int width,
            int height,
            int macroblockSize,
            int rowBytes,
            int frameBytes,
            int payloadBytesPerFrame,
            int bitsPerFrame,
            IModulator modulator)
        {
            var geometry = new ModulatorGeometry(width, height, macroblockSize, FramePacket.HeaderBytes, BitsPerFrame: bitsPerFrame);
            int borderWidth = modulator.GetBorderWidth(geometry);
            geometry = geometry with { BorderWidth = borderWidth };
            int framePacketBytes = modulator.GetPacketBufferLength(geometry, payloadBytesPerFrame);
            var packet = new byte[framePacketBytes];

            var strategy = FrameBitDecoderFactory.CreateForModulator(modulator ?? new BinaryGridModulator(macroblockSize, macroblockSize));
            strategy.Decode(frame, width, height, macroblockSize, rowBytes, frameBytes, packet, borderWidth);

            if (packet.Length < FramePacket.HeaderBytes)
            {
                SawInvalidPacket = true;
                return false;
            }

            if (!FramePacketCodec.TryDecodeWithTolerance(packet, out var frameType, out var frameIndex, out var declaredTotalFrames, out var groupStart, out var groupCount, out var payloadLength, out var payload))
            {
                SawInvalidPacket = true;
                return false;
            }

            if (frameIndex < 0 || payloadLength < 0)
            {
                SawInvalidPacket = true;
                return false;
            }
            if (declaredTotalFrames <= 0 || groupStart < 0 || groupCount <= 0)
            {
                SawInvalidPacket = true;
                return false;
            }

            if (TotalDataFrames < 0)
            {
                TotalDataFrames = declaredTotalFrames;
            }

            if (frameType == FramePacket.FrameTypeData)
            {
                _orderedPayload[frameIndex] = payload;
                _groupCountByGroup[groupStart] = groupCount;
            }
            else if (frameType == FramePacket.FrameTypeParity)
            {
                _parityPayloadByGroup[groupStart] = payload;
                _groupCountByGroup[groupStart] = groupCount;
            }

            return true;
        }

        public bool TryAddDecodedFrame(
            ReadOnlySpan<byte> frame,
            int width,
            int height,
            int macroblockSize,
            int rowBytes,
            int frameBytes,
            int payloadBytesPerFrame,
            int bitsPerFrame)
        {
            return TryAddDecodedFrame(frame, width, height, macroblockSize, rowBytes, frameBytes, payloadBytesPerFrame, bitsPerFrame, new BinaryGridModulator(macroblockSize, macroblockSize));
        }

        public void RecoverMissingPayloadFrames(int totalDataFrames, int payloadBytesPerFrame, int expectedOutputBytes)
        {
            foreach (var kv in _parityPayloadByGroup)
            {
                int groupStart = kv.Key;
                int groupCount = _groupCountByGroup.TryGetValue(groupStart, out var c) ? c : 0;
                if (groupCount <= 0)
                {
                    continue;
                }

                int missingIndex = -1;
                int missingCount = 0;
                for (int i = 0; i < groupCount; i++)
                {
                    int idx = groupStart + i;
                    if (idx >= totalDataFrames)
                    {
                        break;
                    }

                    if (!_orderedPayload.ContainsKey(idx))
                    {
                        missingIndex = idx;
                        missingCount++;
                    }
                }

                if (missingCount > 1)
                {
                    throw new InvalidDataException($"Parity group starting at frame {groupStart} has {missingCount} missing data frames; cannot recover more than one loss per group.");
                }

                if (missingCount != 1)
                {
                    continue;
                }

                var recovered = new byte[payloadBytesPerFrame];
                var parity = kv.Value;
                Buffer.BlockCopy(parity, 0, recovered, 0, Math.Min(parity.Length, recovered.Length));

                for (int i = 0; i < groupCount; i++)
                {
                    int idx = groupStart + i;
                    if (idx == missingIndex) continue;
                    if (!_orderedPayload.TryGetValue(idx, out var existingPayload)) continue;

                    for (int b = 0; b < recovered.Length; b++)
                    {
                        byte v = b < existingPayload.Length ? existingPayload[b] : (byte)0;
                        recovered[b] ^= v;
                    }
                }

                int recoveredLen = payloadBytesPerFrame;
                if (missingIndex == totalDataFrames - 1)
                {
                    int remainder = expectedOutputBytes - (payloadBytesPerFrame * (totalDataFrames - 1));
                    recoveredLen = Math.Max(0, Math.Min(payloadBytesPerFrame, remainder));
                }

                var recoveredPayload = new byte[recoveredLen];
                if (recoveredLen > 0)
                {
                    Buffer.BlockCopy(recovered, 0, recoveredPayload, 0, recoveredLen);
                }
                _orderedPayload[missingIndex] = recoveredPayload;
                RecoveredGroupCount++;
            }
        }

        public byte[] AssembleOutput(int expectedOutputBytes)
        {
            var outBuf = new byte[expectedOutputBytes];
            int written = 0;
            for (int expectedFrameIndex = 0; expectedFrameIndex < TotalDataFrames; expectedFrameIndex++)
            {
                if (written >= expectedOutputBytes)
                {
                    break;
                }

                if (!_orderedPayload.TryGetValue(expectedFrameIndex, out var payload))
                {
                    throw new InvalidDataException($"Missing frame index {expectedFrameIndex}. Frame may be lost or failed hash verification.");
                }

                int toCopy = Math.Min(payload.Length, expectedOutputBytes - written);
                if (toCopy > 0)
                {
                    Buffer.BlockCopy(payload, 0, outBuf, written, toCopy);
                    written += toCopy;
                }
            }

            if (written < expectedOutputBytes)
            {
                throw new InvalidDataException("Decoded payload is incomplete. Frames may be missing or invalid.");
            }

            return outBuf;
        }
    }
}
