using System;

namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Phase 1: Monochrome 16x16 macroblock modulator placeholder.
    /// </summary>
    public sealed class BinaryGridModulator : IModulator
    {
        public int MacroblockWidth => 16;
        public int MacroblockHeight => 16;

        public int GetPayloadBytesPerFrame(int width, int height, int headerBytes, int borderWidth = 0, int macroblockSize = 0)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (headerBytes < 0) throw new ArgumentOutOfRangeException(nameof(headerBytes));
            if (borderWidth < 0) throw new ArgumentOutOfRangeException(nameof(borderWidth));

            int blockWidth = macroblockSize > 0 ? macroblockSize : MacroblockWidth;
            int blockHeight = macroblockSize > 0 ? macroblockSize : MacroblockHeight;
            int usableWidth = Math.Max(0, width - (borderWidth * 2));
            int usableHeight = Math.Max(0, height - (borderWidth * 2));
            int blocksX = Math.Max(1, usableWidth / blockWidth);
            int blocksY = Math.Max(1, usableHeight / blockHeight);
            int bitsPerFrame = blocksX * blocksY;
            int payloadBitsPerFrame = bitsPerFrame - (headerBytes * 8);
            return Math.Max(0, payloadBitsPerFrame / 8);
        }

        public byte[] CreateFrame(int width, int height, int borderWidth, ReadOnlySpan<byte> payload)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (borderWidth < 0 || borderWidth > Math.Min(width, height) / 2) throw new ArgumentOutOfRangeException(nameof(borderWidth));

            var frame = new byte[width * height * 4];
            int blocksX = Math.Max(1, (width - (borderWidth * 2)) / MacroblockWidth);
            int blocksY = Math.Max(1, (height - (borderWidth * 2)) / MacroblockHeight);
            int payloadIndex = 0;

            for (int by = 0; by < blocksY && payloadIndex < payload.Length; by++)
            {
                for (int bx = 0; bx < blocksX && payloadIndex < payload.Length; bx++)
                {
                    int x = borderWidth + (bx * MacroblockWidth);
                    int y = borderWidth + (by * MacroblockHeight);
                    byte value = payload[payloadIndex++];
                    byte bit = (byte)((value & 0x80) != 0 ? 255 : 0);

                    for (int py = 0; py < MacroblockHeight; py++)
                    {
                        for (int px = 0; px < MacroblockWidth; px++)
                        {
                            int screenX = x + px;
                            int screenY = y + py;
                            if (screenX >= width || screenY >= height)
                                continue;

                            int idx = (screenY * width + screenX) * 4;
                            frame[idx + 0] = bit;
                            frame[idx + 1] = bit;
                            frame[idx + 2] = bit;
                            frame[idx + 3] = 255;
                        }
                    }
                }
            }

            return frame;
        }

        public void Encode(ReadOnlySpan<byte> input, Span<byte> pixelBuffer)
        {
            // Very small placeholder: fill pixelBuffer depending on input first byte
            byte fill = input.Length > 0 ? input[0] : (byte)0;
            for (int i = 0; i < pixelBuffer.Length; i++)
                pixelBuffer[i] = fill;
        }

        public void Decode(ReadOnlySpan<byte> pixelBuffer, Span<byte> output)
        {
            // Placeholder: copy first pixel value to output[0]
            if (output.Length > 0 && pixelBuffer.Length > 0)
                output[0] = pixelBuffer[0];
        }
    }
}
