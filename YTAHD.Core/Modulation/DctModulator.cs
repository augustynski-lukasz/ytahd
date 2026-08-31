using System;
using YTAHD.Core.Infrastructure;

namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Phase 3: DCT-domain carrier modulator.
    /// Each 8×8 block is synthesised via IDCT from a small set of low-frequency
    /// cosine coefficients.  Bit 1 sets a carrier coefficient to +CarrierAmplitude,
    /// bit 0 to -CarrierAmplitude.  The resulting frames appear as smooth organic
    /// gradients that video codecs preserve with high fidelity.
    /// On decode, a forward DCT recovers the coefficient signs.
    /// </summary>
    public sealed class DctModulator : IModulator
    {
        private const int BlockSize = DctCarrierBasis.BlockSize;   // 8
        // 1 byte per 8×8 block (8 carrier positions, 1 bit each).
        private const int BytesPerBlock = 1;

        public int MacroblockWidth => BlockSize;
        public int MacroblockHeight => BlockSize;

        public int GetPayloadBytesPerFrame(ModulatorGeometry geometry)
        {
            if (geometry.Width <= 0 || geometry.Height <= 0) throw new ArgumentOutOfRangeException(nameof(geometry));
            if (geometry.HeaderBytes < 0) throw new ArgumentOutOfRangeException(nameof(geometry));
            if (geometry.BorderWidth < 0) throw new ArgumentOutOfRangeException(nameof(geometry));

            int usableWidth = Math.Max(0, geometry.Width - geometry.BorderWidth * 2);
            int usableHeight = Math.Max(0, geometry.Height - geometry.BorderWidth * 2);
            int blocksX = Math.Max(1, usableWidth / BlockSize);
            int blocksY = Math.Max(1, usableHeight / BlockSize);
            int total = blocksX * blocksY * BytesPerBlock;
            if (geometry.HeaderBytes > 0)
                total = Math.Max(0, total - geometry.HeaderBytes);
            return total;
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
            _ = width; _ = height; _ = bitsPerFrame; _ = macroblockSize;
            return headerBytes + payloadBytesPerFrame;
        }

        public int GetBorderWidth(ModulatorGeometry geometry) { _ = geometry; return 32; }
        public int GetBorderWidth(int width, int height, int macroblockSize = 0) { _ = width; _ = height; _ = macroblockSize; return 32; }

        /// <summary>
        /// Single-block encode: synthesise one 8×8 pixel block (64 flat bytes, row-major)
        /// from the first byte of <paramref name="input"/> using IDCT.
        /// </summary>
        public void Encode(ReadOnlySpan<byte> input, Span<byte> pixelBuffer)
        {
            if (pixelBuffer.Length < BlockSize * BlockSize)
                throw new ArgumentException("Pixel buffer too small for one 8×8 block.", nameof(pixelBuffer));

            for (int py = 0; py < BlockSize; py++)
            {
                for (int px = 0; px < BlockSize; px++)
                {
                    pixelBuffer[py * BlockSize + px] = ComputeIdctPixel(input, bitBase: 0, px, py);
                }
            }
        }

        /// <summary>
        /// Single-block decode: recover the first byte of payload from an 8×8 pixel block
        /// (64 flat bytes, row-major) by applying forward DCT and reading coefficient signs.
        /// </summary>
        public void Decode(ReadOnlySpan<byte> pixelBuffer, Span<byte> output)
        {
            if (output.IsEmpty) return;
            DebugTrace.Log("DctModulator", $"Decode: pixelBufferLen={pixelBuffer.Length} outputLen={output.Length}");
            output.Clear();

            var carriers = DctCarrierBasis.CarrierPositions;
            var cos = DctCarrierBasis.CosTable;
            int bitsToRecover = Math.Min(output.Length * 8, carriers.Length);

            for (int ci = 0; ci < bitsToRecover; ci++)
            {
                var (u, v) = carriers[ci];
                double sum = 0;
                for (int py = 0; py < BlockSize; py++)
                    for (int px = 0; px < BlockSize; px++)
                        sum += pixelBuffer[py * BlockSize + px] * cos[px, u] * cos[py, v];

                double coeff = DctCarrierBasis.C(u) * DctCarrierBasis.C(v) / 4.0 * sum;
                int bit = coeff > 0 ? 1 : 0;
                int bytePos = ci / 8;
                int bitPos = 7 - (ci % 8);
                output[bytePos] = (byte)(output[bytePos] | (bit << bitPos));
            }
        }

        public byte[] CreateFrame(ModulatorGeometry geometry, ReadOnlySpan<byte> payload)
            => CreatePhase3Frame(geometry.Width, geometry.Height, geometry.BorderWidth, payload);

        public byte[] CreateFrame(int width, int height, int borderWidth, ReadOnlySpan<byte> payload)
            => CreatePhase3Frame(width, height, borderWidth, payload);

        /// <summary>
        /// Synthesise a full frame: each 8×8 block carries 8 payload bits via IDCT.
        /// The border region is filled with neutral gray (128).  Active blocks are smooth
        /// cosine-wave gradients — low-frequency signal that video codecs preserve well.
        /// </summary>
        public static byte[] CreatePhase3Frame(int width, int height, int borderWidth, ReadOnlySpan<byte> payload)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (borderWidth < 0 || borderWidth > Math.Min(width, height) / 2) throw new ArgumentOutOfRangeException(nameof(borderWidth));

            var frame = new byte[width * height * 4];

            // Fill entire frame with neutral gray (border + padding between blocks)
            for (int i = 0; i < frame.Length; i += 4)
            {
                frame[i] = 128; frame[i + 1] = 128; frame[i + 2] = 128; frame[i + 3] = 255;
            }

            int payloadBitIndex = 0;
            int totalBits = payload.Length * 8;

            for (int blockY = 0; blockY + BlockSize <= height - borderWidth * 2; blockY += BlockSize)
            {
                for (int blockX = 0; blockX + BlockSize <= width - borderWidth * 2; blockX += BlockSize)
                {
                    if (payloadBitIndex >= totalBits) break;
                    int bx = borderWidth + blockX;
                    int by = borderWidth + blockY;

                    for (int py = 0; py < BlockSize; py++)
                    {
                        for (int px = 0; px < BlockSize; px++)
                        {
                            byte pv = ComputeIdctPixel(payload, payloadBitIndex, px, py);
                            int idx = ((by + py) * width + (bx + px)) * 4;
                            frame[idx] = pv; frame[idx + 1] = pv; frame[idx + 2] = pv; frame[idx + 3] = 255;
                        }
                    }

                    payloadBitIndex += DctCarrierBasis.CarrierPositions.Length;
                }
                if (payloadBitIndex >= totalBits) break;
            }

            return frame;
        }

        // ── private helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Evaluate the IDCT at pixel column <paramref name="px"/>, row <paramref name="py"/>
        /// for an 8×8 block whose AC carriers are set from 8 payload bits at <paramref name="bitBase"/>.
        /// DC is always 1024 → mean pixel 128.
        /// </summary>
        private static byte ComputeIdctPixel(ReadOnlySpan<byte> payload, int bitBase, int px, int py)
        {
            var carriers = DctCarrierBasis.CarrierPositions;
            var cos = DctCarrierBasis.CosTable;

            // DC contribution: C(0)*C(0)/4 * DcCoeff = (1/√2)²/4 * 1024 = 0.5/4*1024 = 128
            double value = 128.0;

            for (int ci = 0; ci < carriers.Length; ci++)
            {
                var (u, v) = carriers[ci];
                int totalBitIdx = bitBase + ci;
                int bytePos = totalBitIdx / 8;
                int bitPos = 7 - (totalBitIdx % 8);
                int bit = bytePos < payload.Length ? (payload[bytePos] >> bitPos) & 1 : 0;
                double amplitude = bit == 1 ? DctCarrierBasis.CarrierAmplitude : -DctCarrierBasis.CarrierAmplitude;
                value += DctCarrierBasis.C(u) * DctCarrierBasis.C(v) / 4.0 * amplitude * cos[px, u] * cos[py, v];
            }

            return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
        }
    }
}

