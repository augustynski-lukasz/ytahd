using System;

namespace YTAHD.Core.Core
{
    public static class FrameProtocolHelpers
    {
        public static byte[] CreateDataFramePacket(int frameIndex, int totalDataFrames, int groupStart, int groupCount, int payloadLength, ReadOnlySpan<byte> payload, int payloadCapacity = 0)
        {
            return FramePacketCodec.CreateDataFramePacket(frameIndex, totalDataFrames, groupStart, groupCount, payloadLength, payload, payloadCapacity);
        }

        public static byte[] CreateParityFramePacket(int groupStart, int groupCount, int totalDataFrames, ReadOnlySpan<byte> parityPayload)
        {
            return FramePacketCodec.CreateParityFramePacket(groupStart, groupCount, totalDataFrames, parityPayload);
        }

        public static int GetPayloadBytesPerFrame(int width, int height, int macroblockSize, int headerBytes)
        {
            return FrameLayoutCalculator.CalculatePayloadBytesPerFrame(width, height, macroblockSize, macroblockSize, headerBytes, 0);
        }

        public static bool TryParseFramePacket(byte[] packet, out byte frameType, out int frameIndex, out int totalDataFrames, out int groupStart, out int groupCount, out int payloadLength, out byte[] payload)
        {
            return FramePacketCodec.TryDecode(packet, out frameType, out frameIndex, out totalDataFrames, out groupStart, out groupCount, out payloadLength, out payload);
        }

        public static int GetPacketQualityScore(ReadOnlySpan<byte> packet)
        {
            return PacketQualityScorer.Score(packet);
        }

        public static byte[] ConvertRgbaToRgb(ReadOnlySpan<byte> rgbaFrame, int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));

            int pixelCount = width * height;
            var rgbFrame = new byte[pixelCount * 3];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcIndex = (y * width + x) * 4;
                    int dstIndex = (y * width + x) * 3;
                    rgbFrame[dstIndex] = rgbaFrame[srcIndex];
                    rgbFrame[dstIndex + 1] = rgbaFrame[srcIndex + 1];
                    rgbFrame[dstIndex + 2] = rgbaFrame[srcIndex + 2];
                }
            }

            return rgbFrame;
        }
    }
}
