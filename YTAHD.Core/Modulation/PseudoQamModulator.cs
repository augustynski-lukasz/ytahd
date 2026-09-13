using System;

namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Phase 2: pseudo-QAM multi-channel modulation using 16 PAM levels per R/G/B channel.
    /// Each byte is split into two 4-bit nibbles encoded onto the first two channels. The third
    /// channel is reserved as a parity / calibration value while the low/high nibble pair remain
    /// recoverable without loss.
    /// </summary>
    public sealed class PseudoQamModulator : IModulator, IFrameBitDecoderProvider
    {
        public int MacroblockWidth => 16;
        public int MacroblockHeight => 16;

        /// <inheritdoc />
        public Core.IFrameBitDecoder CreateFrameBitDecoder() => new Core.PseudoQamFrameBitDecoder();

        public int GetPayloadBytesPerFrame(ModulatorGeometry geometry)
        {
            if (geometry.Width <= 0 || geometry.Height <= 0) throw new ArgumentOutOfRangeException(nameof(geometry));
            if (geometry.HeaderBytes < 0) throw new ArgumentOutOfRangeException(nameof(geometry));
            if (geometry.BorderWidth < 0) throw new ArgumentOutOfRangeException(nameof(geometry));

            int blockWidth = geometry.MacroblockSize > 0 ? geometry.MacroblockSize : MacroblockWidth;
            int blockHeight = geometry.MacroblockSize > 0 ? geometry.MacroblockSize : MacroblockHeight;
            int usableWidth = Math.Max(0, geometry.Width - (geometry.BorderWidth * 2));
            int usableHeight = Math.Max(0, geometry.Height - (geometry.BorderWidth * 2));
            int blocksX = Math.Max(1, usableWidth / blockWidth);
            int blocksY = Math.Max(1, usableHeight / blockHeight);
            int payloadBytesPerFrame = blocksX * blocksY;
            int capacityAfterHeader = payloadBytesPerFrame - geometry.HeaderBytes;
            return Math.Max(0, capacityAfterHeader);
        }

        public int GetPayloadBytesPerFrame(int width, int height, int headerBytes, int borderWidth = 0, int macroblockSize = 0)
        {
            return GetPayloadBytesPerFrame(new ModulatorGeometry(width, height, macroblockSize > 0 ? macroblockSize : MacroblockWidth, headerBytes, borderWidth));
        }

        public int GetPacketBufferLength(ModulatorGeometry geometry, int payloadBytesPerFrame)
        {
            _ = payloadBytesPerFrame;
            return Math.Max(geometry.BitsPerFrame, geometry.HeaderBytes);
        }

        public int GetPacketBufferLength(int width, int height, int headerBytes, int payloadBytesPerFrame, int bitsPerFrame, int macroblockSize = 0)
        {
            _ = width;
            _ = height;
            _ = macroblockSize;
            return Math.Max(bitsPerFrame, headerBytes);
        }

        public int GetBorderWidth(ModulatorGeometry geometry)
        {
            _ = geometry;
            return 32;
        }

        public int GetBorderWidth(int width, int height, int macroblockSize = 0)
        {
            _ = width;
            _ = height;
            _ = macroblockSize;
            return 32;
        }

        private const int PamLevels = 16;
        private const int PamStep = 17; // 256 / 15, then quantized to 16-level grid with exact 0..255 values

        public static byte[] CreateReferencePalette()
        {
            var palette = new byte[PamLevels];
            for (int i = 0; i < PamLevels; i++)
            {
                palette[i] = (byte)(i * PamStep);
            }

            return palette;
        }

        public static void PaintCalibrationBorder(byte[] rgbaBuffer, int width, int height, int borderWidth)
        {
            if (rgbaBuffer == null) throw new ArgumentNullException(nameof(rgbaBuffer));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (borderWidth < 0 || borderWidth > Math.Min(width, height) / 2)
                throw new ArgumentOutOfRangeException(nameof(borderWidth));

            var palette = CreateReferencePalette();

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool onBorder = x < borderWidth || x >= width - borderWidth || y < borderWidth || y >= height - borderWidth;
                    if (!onBorder)
                    {
                        continue;
                    }

                    int index = (y * width + x) * 4;
                    int paletteIndex = (x / Math.Max(1, borderWidth)) % palette.Length;
                    byte level = palette[paletteIndex];

                    rgbaBuffer[index + 0] = level; // R
                    rgbaBuffer[index + 1] = (byte)(255 - level); // G
                    rgbaBuffer[index + 2] = (byte)(level ^ 0x55); // B
                    rgbaBuffer[index + 3] = 255;
                }
            }

            // Paint the 16-level pilot palette in the top border.
            int paletteStartX = borderWidth + 4;
            int paletteY = borderWidth / 2;
            for (int i = 0; i < palette.Length; i++)
            {
                int x = paletteStartX + (i * 4);
                int y = paletteY;
                int idx = (y * width + x) * 4;
                if (idx + 3 < rgbaBuffer.Length)
                {
                    rgbaBuffer[idx + 0] = palette[i];
                    rgbaBuffer[idx + 1] = palette[i];
                    rgbaBuffer[idx + 2] = palette[i];
                    rgbaBuffer[idx + 3] = 255;
                }
            }
        }

        public byte[] CreateFrame(ModulatorGeometry geometry, ReadOnlySpan<byte> payload)
        {
            return CreatePhase2Frame(geometry.Width, geometry.Height, geometry.BorderWidth, payload);
        }

        public byte[] CreateFrame(int width, int height, int borderWidth, ReadOnlySpan<byte> payload)
        {
            return CreatePhase2Frame(width, height, borderWidth, payload);
        }

        public static byte[] CreatePhase2Frame(int width, int height, int borderWidth, ReadOnlySpan<byte> payload)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (borderWidth < 0 || borderWidth > Math.Min(width, height) / 2) throw new ArgumentOutOfRangeException(nameof(borderWidth));

            var frame = new byte[width * height * 4];
            PaintCalibrationBorder(frame, width, height, borderWidth);

            const int macroblockSize = 16;
            int startX = borderWidth;
            int startY = borderWidth;
            int maxBlocksX = Math.Max(1, (width - (borderWidth * 2)) / macroblockSize);
            int maxBlocksY = Math.Max(1, (height - (borderWidth * 2)) / macroblockSize);
            int payloadIndex = 0;

            for (int blockY = 0; blockY < maxBlocksY && payloadIndex < payload.Length; blockY++)
            {
                for (int blockX = 0; blockX < maxBlocksX && payloadIndex < payload.Length; blockX++)
                {
                    int macroX = startX + (blockX * macroblockSize);
                    int macroY = startY + (blockY * macroblockSize);
                    byte value = payload[payloadIndex++];

                    byte low = (byte)(value & 0x0F);
                    byte high = (byte)((value >> 4) & 0x0F);
                    byte r = QuantizeNibble(low);
                    byte g = QuantizeNibble(high);
                    byte b = QuantizeNibble((byte)(low ^ high));

                    for (int y = 0; y < macroblockSize; y++)
                    {
                        for (int x = 0; x < macroblockSize; x++)
                        {
                            int px = macroX + x;
                            int py = macroY + y;
                            if (px < 0 || py < 0 || px >= width || py >= height)
                                continue;

                            int idx = (py * width + px) * 4;
                            frame[idx + 0] = r;
                            frame[idx + 1] = g;
                            frame[idx + 2] = b;
                            frame[idx + 3] = 255;
                        }
                    }
                }
            }

            return frame;
        }

        public static byte[] EstimateCalibrationProfile(byte[] rgbaBuffer, int width, int height, int borderWidth)
        {
            if (rgbaBuffer == null) throw new ArgumentNullException(nameof(rgbaBuffer));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (borderWidth < 0 || borderWidth > Math.Min(width, height) / 2) throw new ArgumentOutOfRangeException(nameof(borderWidth));

            var observed = new byte[PamLevels];
            int paletteStartX = borderWidth + 4;
            int paletteY = borderWidth / 2;

            for (int i = 0; i < PamLevels; i++)
            {
                int x = paletteStartX + (i * 4);
                int idx = (paletteY * width + x) * 4;
                if (idx + 2 < rgbaBuffer.Length)
                {
                    observed[i] = (byte)((rgbaBuffer[idx] + rgbaBuffer[idx + 1] + rgbaBuffer[idx + 2]) / 3);
                }
            }

            return observed;
        }

        public void Encode(ReadOnlySpan<byte> input, Span<byte> pixelBuffer)
        {
            pixelBuffer.Clear();

            for (int i = 0; i < input.Length; i++)
            {
                int idx = i * 3;
                if (idx + 2 >= pixelBuffer.Length)
                    break;

                byte value = input[i];
                byte low = (byte)(value & 0x0F);
                byte high = (byte)((value >> 4) & 0x0F);

                pixelBuffer[idx] = QuantizeNibble(low);
                pixelBuffer[idx + 1] = QuantizeNibble(high);
                pixelBuffer[idx + 2] = (byte)(pixelBuffer[idx] ^ pixelBuffer[idx + 1]);
            }
        }

        public void Decode(ReadOnlySpan<byte> pixelBuffer, Span<byte> output)
        {
            output.Clear();

            for (int i = 0; i < output.Length; i++)
            {
                int idx = i * 3;
                if (idx + 1 >= pixelBuffer.Length)
                    break;

                byte low = DequantizeNibble(pixelBuffer[idx]);
                byte high = DequantizeNibble(pixelBuffer[idx + 1]);
                output[i] = (byte)((high << 4) | low);
            }
        }

        private static byte QuantizeNibble(byte nibble)
        {
            // Map 4-bit values 0..15 to 16 evenly spaced PAM amplitudes (0,17,34,...,255)
            return (byte)(nibble * PamStep);
        }

        private static byte DequantizeNibble(byte pamValue)
        {
            // Reverse the 16-level PAM mapping and round to nearest nibble value.
            int half = PamStep / 2;
            int level = (pamValue + half) / PamStep;
            if (level < 0) level = 0;
            if (level > 15) level = 15;
            return (byte)level;
        }
    }
}
