using System;
using System.Threading;
using System.Threading.Tasks;
using YTAHD.Core.Modulation;

namespace YTAHD.Core.Core
{
    /// <summary>
    /// Decodes frames produced by <see cref="MotionVectorModulator"/>. Each cell's tile
    /// position is located with a full search over the finite offset alphabet directly
    /// against real frame pixels (luminance-averaged, tolerant of RGB drift like the other
    /// decoders). <see cref="IsCanonicalFrame"/> is a separate classifier used by the
    /// pipeline to detect "no data" frames before attempting packet decode.
    /// </summary>
    public sealed class MotionFrameBitDecoder : IFrameBitDecoder, IParallelismConfigurable
    {
        private readonly MotionTileProfile _profile;
        private readonly int CellSize;
        private readonly int TextureSize;
        private readonly int Guard;
        private readonly byte[,] Texture;

        public MotionFrameBitDecoder(MotionTileProfile? profile = null)
        {
            _profile = profile ?? MotionTileProfile.Default;
            CellSize = _profile.CellSize;
            TextureSize = _profile.TextureSize;
            Guard = _profile.MaxAbsOffsetPx;
            Texture = MotionTileBasis.GetTexture(_profile);
        }

        /// <inheritdoc />
        public int InnerDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

        public void Decode(ReadOnlySpan<byte> frame, int width, int height, int macroblockSize, int rowBytes, int frameBytes, Span<byte> packet, int borderWidth = 0)
        {
            packet.Clear();

            bool isRgba = frame.Length == width * height * 4;
            int bytesPerPixel = isRgba ? 4 : 3;
            int effectiveRowBytes = isRgba ? width * 4 : rowBytes;

            int blocksX = Math.Max(1, (width - borderWidth * 2) / CellSize);
            int blocksY = Math.Max(1, (height - borderWidth * 2) / CellSize);

            int blockIndex = 0;
            int totalBytes = packet.Length;

            for (int blockY = 0; blockY < blocksY && blockIndex < totalBytes; blockY++)
            {
                for (int blockX = 0; blockX < blocksX && blockIndex < totalBytes; blockX++)
                {
                    int cellOriginX = borderWidth + blockX * CellSize;
                    int cellOriginY = borderWidth + blockY * CellSize;

                    var (dx, dy, _, _) = FindBestOffset(_profile, frame, width, height, bytesPerPixel, effectiveRowBytes, frameBytes, cellOriginX, cellOriginY, includeHome: false);
                    if (MotionTileBasis.TryDecodeOffset(_profile, dx, dy, out byte value))
                    {
                        packet[blockIndex] = value;
                    }

                    blockIndex++;
                }
            }
        }

        public void DecodeMemory(ReadOnlyMemory<byte> frame, int width, int height, int macroblockSize, int rowBytes, int frameBytes, Memory<byte> packet, int borderWidth = 0)
        {
            packet.Span.Clear();

            bool isRgba = frame.Length == width * height * 4;
            int bytesPerPixel = isRgba ? 4 : 3;
            int effectiveRowBytes = isRgba ? width * 4 : rowBytes;

            int blocksX = Math.Max(1, (width - borderWidth * 2) / CellSize);
            int blocksY = Math.Max(1, (height - borderWidth * 2) / CellSize);
            int totalBytes = Math.Min(packet.Length, blocksX * blocksY);

            InnerLoopParallelism.ForEachRow(blocksY, InnerDegreeOfParallelism, blockY =>
            {
                for (int blockX = 0; blockX < blocksX; blockX++)
                {
                    int blockIndex = (blockY * blocksX) + blockX;
                    if (blockIndex >= totalBytes) continue;

                    int cellOriginX = borderWidth + blockX * CellSize;
                    int cellOriginY = borderWidth + blockY * CellSize;

                    var (dx, dy, _, _) = FindBestOffset(_profile, frame, width, height, bytesPerPixel, effectiveRowBytes, frameBytes, cellOriginX, cellOriginY, includeHome: false);
                    if (MotionTileBasis.TryDecodeOffset(_profile, dx, dy, out byte value))
                    {
                        packet.Span[blockIndex] = value;
                    }
                }
            });
        }

        /// <summary>Instance entry point used by the decode pipeline (see <see cref="IFrameBitDecoder"/>).</summary>
        public bool IsCanonicalFrame(ReadOnlySpan<byte> frame, int width, int height, int borderWidth)
            => IsCanonicalFrame(_profile, frame, width, height, borderWidth, canonicalFraction: 0.9, InnerDegreeOfParallelism);

        public bool IsCanonicalFrameMemory(ReadOnlyMemory<byte> frame, int width, int height, int borderWidth)
            => IsCanonicalFrameMemory(_profile, frame, width, height, borderWidth, canonicalFraction: 0.9, InnerDegreeOfParallelism);

        /// <summary>
        /// Classifies a decoded frame as canonical ("no data", every tile at home) by
        /// checking, per cell, whether the home position fits the observed pixels better
        /// than every alphabet data offset. Returns true when the fraction of home-dominant
        /// cells reaches <paramref name="canonicalFraction"/>.
        /// </summary>
        public static bool IsCanonicalFrame(ReadOnlySpan<byte> frame, int width, int height, int borderWidth = 32, double canonicalFraction = 0.9)
            => IsCanonicalFrame(MotionTileProfile.Default, frame, width, height, borderWidth, canonicalFraction, Environment.ProcessorCount);

        /// <inheritdoc cref="IsCanonicalFrame(ReadOnlySpan{byte}, int, int, int, double)"/>
        /// <param name="innerDegreeOfParallelism">Worker count for the per-row cell search; one is serial.</param>
        public static bool IsCanonicalFrame(MotionTileProfile profile, ReadOnlySpan<byte> frame, int width, int height, int borderWidth, double canonicalFraction, int innerDegreeOfParallelism)
        {
            bool isRgba = frame.Length == width * height * 4;
            int bytesPerPixel = isRgba ? 4 : 3;
            int effectiveRowBytes = isRgba ? width * 4 : width * bytesPerPixel;
            int frameBytes = frame.Length;
            int cellSize = profile.CellSize;

            int blocksX = Math.Max(1, (width - borderWidth * 2) / cellSize);
            int blocksY = Math.Max(1, (height - borderWidth * 2) / cellSize);
            int totalCells = blocksX * blocksY;
            if (totalCells <= 0) return false;

            int homeDominantCount = 0;
            for (int blockY = 0; blockY < blocksY; blockY++)
            {
                for (int blockX = 0; blockX < blocksX; blockX++)
                {
                    int cellOriginX = borderWidth + blockX * cellSize;
                    int cellOriginY = borderWidth + blockY * cellSize;

                    var (dx, dy, _, _) = FindBestOffset(profile, frame, width, height, bytesPerPixel, effectiveRowBytes, frameBytes, cellOriginX, cellOriginY, includeHome: true);
                    if (MotionTileBasis.IsCanonicalOffset(dx, dy))
                    {
                        homeDominantCount++;
                    }
                }
            }

            return homeDominantCount / (double)totalCells >= canonicalFraction;
        }

        public static bool IsCanonicalFrameMemory(ReadOnlyMemory<byte> frame, int width, int height, int borderWidth = 32, double canonicalFraction = 0.9)
            => IsCanonicalFrameMemory(MotionTileProfile.Default, frame, width, height, borderWidth, canonicalFraction, Environment.ProcessorCount);

        /// <inheritdoc cref="IsCanonicalFrameMemory(ReadOnlyMemory{byte}, int, int, int, double)"/>
        /// <param name="innerDegreeOfParallelism">Worker count for the per-row cell search; one is serial.</param>
        public static bool IsCanonicalFrameMemory(MotionTileProfile profile, ReadOnlyMemory<byte> frame, int width, int height, int borderWidth, double canonicalFraction, int innerDegreeOfParallelism)
        {
            bool isRgba = frame.Length == width * height * 4;
            int bytesPerPixel = isRgba ? 4 : 3;
            int effectiveRowBytes = isRgba ? width * 4 : width * bytesPerPixel;
            int frameBytes = frame.Length;
            int cellSize = profile.CellSize;

            int blocksX = Math.Max(1, (width - borderWidth * 2) / cellSize);
            int blocksY = Math.Max(1, (height - borderWidth * 2) / cellSize);
            int totalCells = blocksX * blocksY;
            if (totalCells <= 0) return false;

            int homeDominantCount = 0;
            InnerLoopParallelism.ForEachRow(blocksY, innerDegreeOfParallelism, blockY =>
            {
                int localHomeDominantCount = 0;
                for (int blockX = 0; blockX < blocksX; blockX++)
                {
                    int cellOriginX = borderWidth + blockX * cellSize;
                    int cellOriginY = borderWidth + blockY * cellSize;

                    var (dx, dy, _, _) = FindBestOffset(profile, frame, width, height, bytesPerPixel, effectiveRowBytes, frameBytes, cellOriginX, cellOriginY, includeHome: true);
                    if (MotionTileBasis.IsCanonicalOffset(dx, dy))
                    {
                        localHomeDominantCount++;
                    }
                }

                if (localHomeDominantCount > 0)
                {
                    Interlocked.Add(ref homeDominantCount, localHomeDominantCount);
                }
            });

            return homeDominantCount / (double)totalCells >= canonicalFraction;
        }

        // ── private helpers ──────────────────────────────────────────────────────────

        private static (int Dx, int Dy, long BestSad, long Margin) FindBestOffset(
            MotionTileProfile profile,
            ReadOnlySpan<byte> frame,
            int width,
            int height,
            int bytesPerPixel,
            int rowBytes,
            int frameBytes,
            int cellOriginX,
            int cellOriginY,
            bool includeHome)
        {
            var axisOffsets = MotionTileBasis.GetAxisOffsets(profile);

            long bestSad = long.MaxValue;
            long secondBestSad = long.MaxValue;
            int bestDx = 0, bestDy = 0;

            void Consider(int candidateDx, int candidateDy, long sad)
            {
                if (sad < bestSad)
                {
                    secondBestSad = bestSad;
                    bestSad = sad;
                    bestDx = candidateDx;
                    bestDy = candidateDy;
                }
                else if (sad < secondBestSad)
                {
                    secondBestSad = sad;
                }
            }

            if (includeHome)
            {
                Consider(0, 0, ComputeCandidateSad(profile, frame, width, height, bytesPerPixel, rowBytes, frameBytes, cellOriginX, cellOriginY, 0, 0));
            }

            foreach (int candidateDx in axisOffsets)
            {
                foreach (int candidateDy in axisOffsets)
                {
                    long sad = ComputeCandidateSad(profile, frame, width, height, bytesPerPixel, rowBytes, frameBytes, cellOriginX, cellOriginY, candidateDx, candidateDy);
                    Consider(candidateDx, candidateDy, sad);
                }
            }

            return (bestDx, bestDy, bestSad, secondBestSad - bestSad);
        }

        private static (int Dx, int Dy, long BestSad, long Margin) FindBestOffset(
            MotionTileProfile profile,
            ReadOnlyMemory<byte> frame,
            int width,
            int height,
            int bytesPerPixel,
            int rowBytes,
            int frameBytes,
            int cellOriginX,
            int cellOriginY,
            bool includeHome)
        {
            var axisOffsets = MotionTileBasis.GetAxisOffsets(profile);

            long bestSad = long.MaxValue;
            long secondBestSad = long.MaxValue;
            int bestDx = 0, bestDy = 0;

            void Consider(int candidateDx, int candidateDy, long sad)
            {
                if (sad < bestSad)
                {
                    secondBestSad = bestSad;
                    bestSad = sad;
                    bestDx = candidateDx;
                    bestDy = candidateDy;
                }
                else if (sad < secondBestSad)
                {
                    secondBestSad = sad;
                }
            }

            if (includeHome)
            {
                Consider(0, 0, ComputeCandidateSad(profile, frame, width, height, bytesPerPixel, rowBytes, frameBytes, cellOriginX, cellOriginY, 0, 0));
            }

            foreach (int candidateDx in axisOffsets)
            {
                foreach (int candidateDy in axisOffsets)
                {
                    long sad = ComputeCandidateSad(profile, frame, width, height, bytesPerPixel, rowBytes, frameBytes, cellOriginX, cellOriginY, candidateDx, candidateDy);
                    Consider(candidateDx, candidateDy, sad);
                }
            }

            return (bestDx, bestDy, bestSad, secondBestSad - bestSad);
        }

        private static long ComputeCandidateSad(
            MotionTileProfile profile,
            ReadOnlySpan<byte> frame,
            int width,
            int height,
            int bytesPerPixel,
            int rowBytes,
            int frameBytes,
            int cellOriginX,
            int cellOriginY,
            int candidateDx,
            int candidateDy)
        {
            var texture = MotionTileBasis.GetTexture(profile);
            int guard = profile.MaxAbsOffsetPx;
            int textureSize = profile.TextureSize;
            int windowX = cellOriginX + guard + candidateDx;
            int windowY = cellOriginY + guard + candidateDy;

            long sad = 0;
            for (int py = 0; py < textureSize; py++)
            {
                int y = windowY + py;
                if (y < 0 || y >= height) { sad += textureSize * 255; continue; }

                for (int px = 0; px < textureSize; px++)
                {
                    int x = windowX + px;
                    int idx = y * rowBytes + x * bytesPerPixel;
                    if (x < 0 || x >= width || idx + 2 >= frameBytes)
                    {
                        sad += 255;
                        continue;
                    }

                    int luma = (frame[idx] + frame[idx + 1] + frame[idx + 2]) / 3;
                    sad += Math.Abs(luma - texture[py, px]);
                }
            }

            return sad;
        }

        private static long ComputeCandidateSad(
            MotionTileProfile profile,
            ReadOnlyMemory<byte> frame,
            int width,
            int height,
            int bytesPerPixel,
            int rowBytes,
            int frameBytes,
            int cellOriginX,
            int cellOriginY,
            int candidateDx,
            int candidateDy)
        {
            var texture = MotionTileBasis.GetTexture(profile);
            int guard = profile.MaxAbsOffsetPx;
            int textureSize = profile.TextureSize;
            int windowX = cellOriginX + guard + candidateDx;
            int windowY = cellOriginY + guard + candidateDy;
            var frameSpan = frame.Span;

            long sad = 0;
            for (int py = 0; py < textureSize; py++)
            {
                int y = windowY + py;
                if (y < 0 || y >= height) { sad += textureSize * 255; continue; }

                for (int px = 0; px < textureSize; px++)
                {
                    int x = windowX + px;
                    int idx = y * rowBytes + x * bytesPerPixel;
                    if (x < 0 || x >= width || idx + 2 >= frameBytes)
                    {
                        sad += 255;
                        continue;
                    }

                    int luma = (frameSpan[idx] + frameSpan[idx + 1] + frameSpan[idx + 2]) / 3;
                    sad += Math.Abs(luma - texture[py, px]);
                }
            }

            return sad;
        }
    }
}
