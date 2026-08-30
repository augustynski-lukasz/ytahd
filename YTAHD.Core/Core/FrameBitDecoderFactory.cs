using System;
using YTAHD.Core.Modulation;

namespace YTAHD.Core.Core
{
    public interface IFrameBitDecoder
    {
        void Decode(ReadOnlySpan<byte> frame, int width, int height, int macroblockSize, int rowBytes, int frameBytes, Span<byte> packet);
    }

    public static class FrameBitDecoderFactory
    {
        public static IFrameBitDecoder CreateForModulator(IModulator modulator)
        {
            if (modulator == null)
            {
                throw new ArgumentNullException(nameof(modulator));
            }

            if (modulator is BinaryGridModulator)
            {
                return new BinaryGridFrameBitDecoder();
            }

            if (modulator is PseudoQamModulator)
            {
                return new PseudoQamFrameBitDecoder();
            }

            if (modulator is DctModulator)
            {
                return new DctFrameBitDecoder();
            }

            throw new NotSupportedException($"No frame bit decoder available for modulator '{modulator.GetType().Name}'.");
        }
    }

    public sealed class BinaryGridFrameBitDecoder : IFrameBitDecoder
    {
        public void Decode(ReadOnlySpan<byte> frame, int width, int height, int macroblockSize, int rowBytes, int frameBytes, Span<byte> packet)
        {
            int blocksX = width / macroblockSize;
            int blocksY = height / macroblockSize;

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int frameBitIndex = by * blocksX + bx;
                    if (frameBitIndex >= packet.Length * 8)
                    {
                        continue;
                    }

                    int sampleX = bx * macroblockSize + macroblockSize / 2;
                    int sampleY = by * macroblockSize + macroblockSize / 2;
                    int idx = sampleY * rowBytes + sampleX * 3;
                    int bitValue = 0;
                    if (idx >= 0 && idx + 2 < frameBytes)
                    {
                        byte r = frame[idx];
                        byte g = frame[idx + 1];
                        byte b = frame[idx + 2];
                        int luminance = (r + g + b) / 3;
                        bitValue = luminance > 127 ? 1 : 0;
                    }

                    if (bitValue == 1)
                    {
                        int byteIdx = frameBitIndex / 8;
                        int bitInByte = 7 - (frameBitIndex % 8);
                        packet[byteIdx] |= (byte)(1 << bitInByte);
                    }
                }
            }
        }
    }

    public sealed class PseudoQamFrameBitDecoder : IFrameBitDecoder
    {
        public void Decode(ReadOnlySpan<byte> frame, int width, int height, int macroblockSize, int rowBytes, int frameBytes, Span<byte> packet)
        {
            int blocksX = width / macroblockSize;
            int blocksY = height / macroblockSize;
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int bitIndex = by * blocksX + bx;
                    if (bitIndex >= packet.Length * 8)
                    {
                        continue;
                    }

                    int sampleX = bx * macroblockSize + macroblockSize / 2;
                    int sampleY = by * macroblockSize + macroblockSize / 2;
                    int idx = sampleY * rowBytes + sampleX * 3;
                    if (idx + 2 >= frameBytes)
                    {
                        continue;
                    }

                    int luminance = (frame[idx] + frame[idx + 1] + frame[idx + 2]) / 3;
                    int bit = luminance > 127 ? 1 : 0;
                    if (bit == 1)
                    {
                        int byteIdx = bitIndex / 8;
                        int bitInByte = 7 - (bitIndex % 8);
                        packet[byteIdx] |= (byte)(1 << bitInByte);
                    }
                }
            }
        }
    }

    public sealed class DctFrameBitDecoder : IFrameBitDecoder
    {
        public void Decode(ReadOnlySpan<byte> frame, int width, int height, int macroblockSize, int rowBytes, int frameBytes, Span<byte> packet)
        {
            packet.Clear();
        }
    }
}
