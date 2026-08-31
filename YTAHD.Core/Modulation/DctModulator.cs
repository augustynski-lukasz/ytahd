using System;

namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Phase 3: low-frequency DCT carrier modulator.
    /// The encoder stores payload bytes as signed amplitudes on the top-left DCT basis values,
    /// keeping the carrier inside the low-frequency region that video compression preserves best.
    /// </summary>
    public sealed class DctModulator : IModulator
    {
        private const int BasisSize = 8;
        private const int Cutoff = 4;

        public int MacroblockWidth => BasisSize;
        public int MacroblockHeight => BasisSize;

        public int GetPayloadBytesPerFrame(ModulatorGeometry geometry)
        {
            if (geometry.Width <= 0 || geometry.Height <= 0) throw new ArgumentOutOfRangeException(nameof(geometry));
            if (geometry.HeaderBytes < 0) throw new ArgumentOutOfRangeException(nameof(geometry));
            if (geometry.BorderWidth < 0) throw new ArgumentOutOfRangeException(nameof(geometry));

            int usableWidth = Math.Max(0, geometry.Width - (geometry.BorderWidth * 2));
            int usableHeight = Math.Max(0, geometry.Height - (geometry.BorderWidth * 2));
            int dctBlocksX = Math.Max(1, usableWidth / BasisSize);
            int dctBlocksY = Math.Max(1, usableHeight / BasisSize);
            int payloadBytesPerFrame = dctBlocksX * dctBlocksY * Cutoff * Cutoff;
            if (geometry.HeaderBytes > 0)
            {
                payloadBytesPerFrame = Math.Max(0, payloadBytesPerFrame - geometry.HeaderBytes);
            }

            return payloadBytesPerFrame;
        }

        public int GetPayloadBytesPerFrame(int width, int height, int headerBytes, int borderWidth = 0, int macroblockSize = 0)
        {
            return GetPayloadBytesPerFrame(new ModulatorGeometry(width, height, macroblockSize > 0 ? macroblockSize : MacroblockWidth, headerBytes, borderWidth));
        }

        public int GetPacketBufferLength(ModulatorGeometry geometry, int payloadBytesPerFrame)
        {
            _ = payloadBytesPerFrame;
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
            return 32;
        }

        public int GetBorderWidth(int width, int height, int macroblockSize = 0)
        {
            _ = width;
            _ = height;
            _ = macroblockSize;
            return 32;
        }

        public void Encode(ReadOnlySpan<byte> input, Span<byte> pixelBuffer)
        {
            if (pixelBuffer.Length < BasisSize * BasisSize)
            {
                throw new ArgumentException("Pixel buffer is too small to hold an 8x8 DCT carrier block.", nameof(pixelBuffer));
            }

            pixelBuffer.Clear();

            var basis = DctCarrierBasis.GenerateBasis(BasisSize, Cutoff);
            int payloadCount = Math.Min(input.Length, Cutoff * Cutoff);

            for (int i = 0; i < payloadCount; i++)
            {
                int y = i / Cutoff;
                int x = i % Cutoff;
                if (Math.Abs(basis[y, x]) < 0.0001d)
                {
                    continue;
                }

                pixelBuffer[y * BasisSize + x] = input[i];
            }

            for (int y = 0; y < BasisSize; y++)
            {
                for (int x = 0; x < BasisSize; x++)
                {
                    if (x < Cutoff && y < Cutoff)
                    {
                        continue;
                    }

                    pixelBuffer[y * BasisSize + x] = 128;
                }
            }
        }

        public byte[] CreateFrame(ModulatorGeometry geometry, ReadOnlySpan<byte> payload)
        {
            return CreatePhase3Frame(geometry.Width, geometry.Height, geometry.BorderWidth, payload);
        }

        public byte[] CreateFrame(int width, int height, int borderWidth, ReadOnlySpan<byte> payload)
        {
            return CreatePhase3Frame(width, height, borderWidth, payload);
        }

        public static byte[] CreatePhase3Frame(int width, int height, int borderWidth, ReadOnlySpan<byte> payload)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (borderWidth < 0 || borderWidth > Math.Min(width, height) / 2) throw new ArgumentOutOfRangeException(nameof(borderWidth));

            var frame = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = (y * width + x) * 4;
                    bool onBorder = x < borderWidth || x >= width - borderWidth || y < borderWidth || y >= height - borderWidth;
                    byte neutral = onBorder ? (byte)128 : (byte)0;
                    frame[idx + 0] = neutral;
                    frame[idx + 1] = neutral;
                    frame[idx + 2] = neutral;
                    frame[idx + 3] = 255;
                }
            }

            int payloadIndex = 0;
            int blockSize = 8;
            int carrierX = borderWidth + 8;
            int carrierY = borderWidth + 8;

            for (int blockY = 0; blockY < height - borderWidth * 2 && payloadIndex < payload.Length; blockY += blockSize)
            {
                for (int blockX = 0; blockX < width - borderWidth * 2 && payloadIndex < payload.Length; blockX += blockSize)
                {
                    int x = borderWidth + blockX;
                    int y = borderWidth + blockY;
                    if (x + blockSize > width || y + blockSize > height)
                    {
                        continue;
                    }

                    var payloadSlice = payload.Slice(payloadIndex, Math.Min(payload.Length - payloadIndex, Cutoff * Cutoff));
                    for (int yy = 0; yy < blockSize; yy++)
                    {
                        for (int xx = 0; xx < blockSize; xx++)
                        {
                            int idx = ((y + yy) * width + (x + xx)) * 4;
                            double distanceFromCarrier = double.MaxValue;
                            if (yy < Cutoff && xx < Cutoff)
                            {
                                distanceFromCarrier = 0.0d;
                            }
                            else
                            {
                                int centerX = Cutoff / 2;
                                int centerY = Cutoff / 2;
                                distanceFromCarrier = Math.Sqrt((xx - centerX) * (xx - centerX) + (yy - centerY) * (yy - centerY));
                            }

                            byte neutral = 0;
                            if (yy < Cutoff && xx < Cutoff)
                            {
                                int payloadIndexInBlock = (yy * Cutoff) + xx;
                                if (payloadIndexInBlock < payloadSlice.Length)
                                {
                                    neutral = payloadSlice[payloadIndexInBlock];
                                }
                                else
                                {
                                    neutral = 0;
                                }
                            }

                            frame[idx + 0] = neutral;
                            frame[idx + 1] = neutral;
                            frame[idx + 2] = neutral;
                            frame[idx + 3] = 255;
                        }
                    }

                    payloadIndex += payloadSlice.Length;
                }
            }

            return frame;
        }

        public void Decode(ReadOnlySpan<byte> pixelBuffer, Span<byte> output)
        {
            if (output.IsEmpty)
            {
                return;
            }

            output.Clear();

            var basis = DctCarrierBasis.GenerateBasis(BasisSize, Cutoff);
            int payloadCount = Math.Min(output.Length, Cutoff * Cutoff);

            var values = new double[payloadCount];
            int validCount = 0;
            double minValue = double.MaxValue;
            double maxValue = double.MinValue;

            for (int i = 0; i < payloadCount; i++)
            {
                int y = i / Cutoff;
                int x = i % Cutoff;
                if (Math.Abs(basis[y, x]) < 0.0001d)
                {
                    continue;
                }

                double sample = pixelBuffer[y * BasisSize + x];
                values[i] = sample;
                if (sample < minValue) minValue = sample;
                if (sample > maxValue) maxValue = sample;
                validCount++;
            }

            if (validCount == 0)
            {
                return;
            }

            if (Math.Abs(maxValue - minValue) < 0.0001d)
            {
                for (int i = 0; i < payloadCount; i++)
                {
                    int y = i / Cutoff;
                    int x = i % Cutoff;
                    if (Math.Abs(basis[y, x]) < 0.0001d)
                    {
                        continue;
                    }

                    output[i] = (byte)Math.Clamp(Math.Round(values[i]), 0, 255);
                }
                return;
            }

            for (int i = 0; i < payloadCount; i++)
            {
                int y = i / Cutoff;
                int x = i % Cutoff;
                if (Math.Abs(basis[y, x]) < 0.0001d)
                {
                    continue;
                }

                double normalized = ((values[i] - minValue) / (maxValue - minValue)) * 255d;
                output[i] = (byte)Math.Clamp(Math.Round(normalized), 0, 255);
            }
        }
    }
}
