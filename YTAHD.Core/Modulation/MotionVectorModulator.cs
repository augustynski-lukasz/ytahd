using System;
using YTAHD.Core.Infrastructure;

namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Phase 4: motion-vector carrier modulator (absolute-displacement scheme).
    /// Each cell holds a deterministic tile texture (<see cref="MotionTileBasis"/>) that
    /// either sits at its home position (canonical frame, no payload) or is shifted by a
    /// (dx, dy) pixel offset that encodes one payload byte (displaced frame). Decoding is
    /// single-frame: a full search over the finite offset alphabet locates each tile.
    /// </summary>
    public sealed class MotionVectorModulator : IModulator, IFrameEmissionStrategy
    {
        private const int CellSize = MotionTileBasis.CellSize;
        private const int TextureSize = MotionTileBasis.TextureSize;
        private const int Guard = MotionTileBasis.MaxAbsOffsetPx;
        private const int BytesPerBlock = 1;

        public int MacroblockWidth => CellSize;
        public int MacroblockHeight => CellSize;

        // Displaced frames need no physical repeats: the following canonical separator
        // frame is what marks the datagram boundary for the decoder. Tuned further in A5.
        public int RepeatCount => 1;
        public bool UsesCanonicalSeparator => true;

        public int GetPayloadBytesPerFrame(ModulatorGeometry geometry)
        {
            if (geometry.Width <= 0 || geometry.Height <= 0) throw new ArgumentOutOfRangeException(nameof(geometry));
            if (geometry.HeaderBytes < 0) throw new ArgumentOutOfRangeException(nameof(geometry));
            if (geometry.BorderWidth < 0) throw new ArgumentOutOfRangeException(nameof(geometry));

            int usableWidth = Math.Max(0, geometry.Width - geometry.BorderWidth * 2);
            int usableHeight = Math.Max(0, geometry.Height - geometry.BorderWidth * 2);
            int blocksX = Math.Max(1, usableWidth / CellSize);
            int blocksY = Math.Max(1, usableHeight / CellSize);
            int total = blocksX * blocksY * BytesPerBlock;
            if (geometry.HeaderBytes > 0)
                total = Math.Max(0, total - geometry.HeaderBytes);
            return total;
        }

        public int GetPacketBufferLength(ModulatorGeometry geometry, int payloadBytesPerFrame)
        {
            _ = payloadBytesPerFrame;
            return geometry.HeaderBytes + payloadBytesPerFrame;
        }

        public int GetBorderWidth(ModulatorGeometry geometry) { _ = geometry; return 32; }

        /// <summary>
        /// Single-cell encode: place the reference texture in a flat grayscale
        /// <see cref="CellSize"/>×<see cref="CellSize"/> buffer, shifted according to the
        /// first byte of <paramref name="input"/> (home position if empty).
        /// </summary>
        public void Encode(ReadOnlySpan<byte> input, Span<byte> pixelBuffer)
        {
            if (pixelBuffer.Length < CellSize * CellSize)
                throw new ArgumentException($"Pixel buffer too small for one {CellSize}×{CellSize} cell.", nameof(pixelBuffer));

            (int dx, int dy) = input.IsEmpty ? (0, 0) : MotionTileBasis.EncodeOffset(input[0]);
            RenderCellGrayscale(pixelBuffer, dx, dy);
        }

        /// <summary>
        /// Single-cell decode: full-search the offset alphabet against the reference
        /// texture and recover the first byte of payload (or leave output as-is if the
        /// cell is at its canonical home position).
        /// </summary>
        public void Decode(ReadOnlySpan<byte> pixelBuffer, Span<byte> output)
        {
            if (output.IsEmpty) return;
            DebugTrace.Log("MotionVectorModulator", $"Decode: pixelBufferLen={pixelBuffer.Length} outputLen={output.Length}");
            output.Clear();

            if (pixelBuffer.Length < CellSize * CellSize) return;

            var (dx, dy, _) = FindBestOffset(pixelBuffer);
            if (MotionTileBasis.TryDecodeOffset(dx, dy, out byte value))
            {
                output[0] = value;
            }
        }

        public byte[] CreateFrame(ModulatorGeometry geometry, ReadOnlySpan<byte> payload)
            => CreatePhase4Frame(geometry.Width, geometry.Height, geometry.BorderWidth, payload);

        /// <summary>
        /// Synthesise a full frame. Empty payload renders the canonical frame (every tile at
        /// its home position); non-empty payload renders a displaced frame where each tile is
        /// shifted by the offset encoding one payload byte. Cells beyond the payload length
        /// (last frame of a stream) are left as plain neutral background, undrawn.
        /// </summary>
        public static byte[] CreatePhase4Frame(int width, int height, int borderWidth, ReadOnlySpan<byte> payload)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (borderWidth < 0 || borderWidth > Math.Min(width, height) / 2) throw new ArgumentOutOfRangeException(nameof(borderWidth));

            var frame = new byte[width * height * 4];
            for (int i = 0; i < frame.Length; i += 4)
            {
                frame[i] = 128; frame[i + 1] = 128; frame[i + 2] = 128; frame[i + 3] = 255;
            }

            bool canonical = payload.IsEmpty;
            var texture = MotionTileBasis.Texture;
            int blockIndex = 0;

            for (int blockY = 0; blockY + CellSize <= height - borderWidth * 2; blockY += CellSize)
            {
                for (int blockX = 0; blockX + CellSize <= width - borderWidth * 2; blockX += CellSize)
                {
                    if (!canonical && blockIndex >= payload.Length)
                    {
                        blockIndex++;
                        continue;
                    }

                    (int dx, int dy) = canonical ? (0, 0) : MotionTileBasis.EncodeOffset(payload[blockIndex]);
                    int cellOriginX = borderWidth + blockX;
                    int cellOriginY = borderWidth + blockY;
                    int tileX = cellOriginX + Guard + dx;
                    int tileY = cellOriginY + Guard + dy;

                    for (int py = 0; py < TextureSize; py++)
                    {
                        for (int px = 0; px < TextureSize; px++)
                        {
                            byte pv = texture[py, px];
                            int idx = ((tileY + py) * width + (tileX + px)) * 4;
                            frame[idx] = pv; frame[idx + 1] = pv; frame[idx + 2] = pv; frame[idx + 3] = 255;
                        }
                    }

                    blockIndex++;
                }
            }

            return frame;
        }

        // ── private helpers ──────────────────────────────────────────────────────────

        private static void RenderCellGrayscale(Span<byte> cellBuffer, int dx, int dy)
        {
            for (int i = 0; i < CellSize * CellSize; i++)
                cellBuffer[i] = 128;

            var texture = MotionTileBasis.Texture;
            int tileX = Guard + dx;
            int tileY = Guard + dy;
            for (int py = 0; py < TextureSize; py++)
            {
                for (int px = 0; px < TextureSize; px++)
                {
                    cellBuffer[(tileY + py) * CellSize + (tileX + px)] = texture[py, px];
                }
            }
        }

        private static (int Dx, int Dy, long Sad) FindBestOffset(ReadOnlySpan<byte> cellBuffer)
        {
            var texture = MotionTileBasis.Texture;
            var axisOffsets = MotionTileBasis.GetAxisOffsets();

            long bestSad = long.MaxValue;
            int bestDx = 0, bestDy = 0;

            foreach (int candidateDx in axisOffsets)
            {
                foreach (int candidateDy in axisOffsets)
                {
                    int windowX = Guard + candidateDx;
                    int windowY = Guard + candidateDy;

                    long sad = 0;
                    for (int py = 0; py < TextureSize; py++)
                    {
                        int rowBase = (windowY + py) * CellSize + windowX;
                        for (int px = 0; px < TextureSize; px++)
                        {
                            sad += Math.Abs(cellBuffer[rowBase + px] - texture[py, px]);
                        }
                    }

                    if (sad < bestSad)
                    {
                        bestSad = sad;
                        bestDx = candidateDx;
                        bestDy = candidateDy;
                    }
                }
            }

            return (bestDx, bestDy, bestSad);
        }
    }
}
