using System;

namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Phase 1: Monochrome 16x16 macroblock modulator placeholder.
    /// </summary>
    public sealed class BinaryGridModulator : IModulator, IFrameBitDecoderProvider
    {
        private readonly int _macroblockWidth;
        private readonly int _macroblockHeight;

        /// <inheritdoc />
        public Core.IFrameBitDecoder CreateFrameBitDecoder() => new Core.BinaryGridFrameBitDecoder();

        public BinaryGridModulator(int macroblockWidth = 16, int macroblockHeight = 16)
        {
            if (macroblockWidth <= 0) throw new ArgumentOutOfRangeException(nameof(macroblockWidth));
            if (macroblockHeight <= 0) throw new ArgumentOutOfRangeException(nameof(macroblockHeight));

            _macroblockWidth = macroblockWidth;
            _macroblockHeight = macroblockHeight;
        }

        public int MacroblockWidth => _macroblockWidth;
        public int MacroblockHeight => _macroblockHeight;

        public int GetPayloadBytesPerFrame(ModulatorGeometry geometry)
        {
            int blockWidth = geometry.MacroblockSize > 0 ? geometry.MacroblockSize : _macroblockWidth;
            int blockHeight = geometry.MacroblockSize > 0 ? geometry.MacroblockSize : _macroblockHeight;
            return YTAHD.Core.Core.FrameLayoutCalculator.CalculatePayloadBytesPerFrame(geometry.Width, geometry.Height, blockWidth, blockHeight, geometry.HeaderBytes, geometry.BorderWidth);
        }

        public int GetPayloadBytesPerFrame(int width, int height, int headerBytes, int borderWidth = 0, int macroblockSize = 0)
        {
            return GetPayloadBytesPerFrame(new ModulatorGeometry(width, height, macroblockSize > 0 ? macroblockSize : _macroblockWidth, headerBytes, borderWidth));
        }

        public int GetPacketBufferLength(ModulatorGeometry geometry, int payloadBytesPerFrame)
        {
            _ = geometry;
            return geometry.HeaderBytes + payloadBytesPerFrame;
        }

        public int GetPacketBufferLength(int width, int height, int headerBytes, int payloadBytesPerFrame, int bitsPerFrame, int macroblockSize = 0)
        {
            _ = width;
            _ = height;
            _ = bitsPerFrame;
            _ = macroblockSize;
            return headerBytes + payloadBytesPerFrame;
        }

        public int GetBorderWidth(ModulatorGeometry geometry)
        {
            _ = geometry;
            return 0;
        }

        public int GetBorderWidth(int width, int height, int macroblockSize = 0)
        {
            _ = width;
            _ = height;
            _ = macroblockSize;
            return 0;
        }

        public byte[] CreateFrame(ModulatorGeometry geometry, ReadOnlySpan<byte> payload)
        {
            int width = geometry.Width;
            int height = geometry.Height;
            int borderWidth = geometry.BorderWidth;
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (borderWidth < 0 || borderWidth > Math.Min(width, height) / 2) throw new ArgumentOutOfRangeException(nameof(borderWidth));

            var frame = new byte[width * height * 4];
            int blocksX = Math.Max(1, (width - (borderWidth * 2)) / _macroblockWidth);
            int blocksY = Math.Max(1, (height - (borderWidth * 2)) / _macroblockHeight);
            int totalBits = blocksX * blocksY;

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int bitIndex = (by * blocksX) + bx;
                    if (bitIndex >= totalBits || bitIndex >= payload.Length * 8)
                    {
                        continue;
                    }

                    int byteIndex = bitIndex / 8;
                    int bitInByte = 7 - (bitIndex % 8);
                    bool bit = ((payload[byteIndex] >> bitInByte) & 1) == 1;
                    byte value = bit ? (byte)255 : (byte)0;

                    int x = borderWidth + (bx * _macroblockWidth);
                    int y = borderWidth + (by * _macroblockHeight);

                    for (int py = 0; py < _macroblockHeight; py++)
                    {
                        for (int px = 0; px < _macroblockWidth; px++)
                        {
                            int screenX = x + px;
                            int screenY = y + py;
                            if (screenX >= width || screenY >= height)
                                continue;

                            int idx = (screenY * width + screenX) * 4;
                            frame[idx + 0] = value;
                            frame[idx + 1] = value;
                            frame[idx + 2] = value;
                            frame[idx + 3] = 255;
                        }
                    }
                }
            }

            return frame;
        }

        public byte[] CreateFrame(int width, int height, int borderWidth, ReadOnlySpan<byte> payload)
        {
            return CreateFrame(new ModulatorGeometry(width, height, _macroblockWidth, 0, borderWidth), payload);
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
