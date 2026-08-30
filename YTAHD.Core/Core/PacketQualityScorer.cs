using System;

namespace YTAHD.Core.Core
{
    public static class PacketQualityScorer
    {
        public static bool IsFramePacketValid(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < FramePacket.HeaderBytes)
            {
                return false;
            }

            if (!FramePacket.TryParse(packet.ToArray(), out var frameType, out _, out var declaredTotalFrames, out var groupStart, out var groupCount, out var payloadLength, out _))
            {
                return false;
            }

            if (declaredTotalFrames <= 0 || groupStart < 0 || groupCount <= 0 || payloadLength < 0 || payloadLength > packet.Length - FramePacket.HeaderBytes)
            {
                return false;
            }

            return frameType == FramePacket.FrameTypeData || frameType == FramePacket.FrameTypeParity;
        }

        public static int Score(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < FramePacket.HeaderBytes)
            {
                return 0;
            }

            if (!FramePacket.TryParse(packet.ToArray(), out var frameType, out _, out var declaredTotalFrames, out var groupStart, out var groupCount, out var payloadLength, out var payload))
            {
                return 0;
            }

            if (declaredTotalFrames <= 0 || groupStart < 0 || groupCount <= 0 || payloadLength < 0 || payloadLength > packet.Length - FramePacket.HeaderBytes)
            {
                return 0;
            }

            int score = 1000;
            score += payloadLength * 8;
            score += frameType == FramePacket.FrameTypeParity ? 32 : 64;
            score += Math.Max(0, declaredTotalFrames) * 2;

            int nonZeroCount = 0;
            int bitTransitionCount = 0;
            for (int i = 0; i < payload.Length; i++)
            {
                byte value = payload[i];
                if (value != 0)
                {
                    nonZeroCount++;
                }

                if (i > 0 && payload[i - 1] != value)
                {
                    bitTransitionCount++;
                }
            }

            score += nonZeroCount * 6;
            score += bitTransitionCount * 2;
            return score;
        }
    }
}
