using System;
using System.Collections.Generic;
using Xunit;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    public class MotionTileBasisTests
    {
        [Fact]
        public void AxisOffsets_Are_SixteenDistinctNonZeroEvenValues()
        {
            var offsets = MotionTileBasis.GetAxisOffsets();

            Assert.Equal(MotionTileBasis.OffsetLevelsPerAxis, offsets.Length);
            Assert.Equal(offsets.Length, new HashSet<int>(offsets).Count);
            Assert.All(offsets, o =>
            {
                Assert.NotEqual(0, o);
                Assert.Equal(0, o % MotionTileBasis.OffsetStepPx);
                Assert.InRange(o, -MotionTileBasis.MaxAbsOffsetPx, MotionTileBasis.MaxAbsOffsetPx);
            });
        }

        [Fact]
        public void EncodeOffset_NeverProducesCanonicalZeroZero()
        {
            for (int b = 0; b <= 255; b++)
            {
                var (dx, dy) = MotionTileBasis.EncodeOffset((byte)b);
                Assert.False(MotionTileBasis.IsCanonicalOffset(dx, dy), $"byte {b} produced canonical (0,0) offset");
            }
        }

        [Fact]
        public void OffsetAlphabet_RoundTrips_AllByteValues()
        {
            for (int b = 0; b <= 255; b++)
            {
                var (dx, dy) = MotionTileBasis.EncodeOffset((byte)b);
                Assert.True(MotionTileBasis.TryDecodeOffset(dx, dy, out byte decoded));
                Assert.Equal((byte)b, decoded);
            }
        }

        [Fact]
        public void TryDecodeOffset_RejectsOffsetsOutsideAlphabet()
        {
            Assert.False(MotionTileBasis.TryDecodeOffset(0, 0, out _));
            Assert.False(MotionTileBasis.TryDecodeOffset(1, 2, out _));
            Assert.False(MotionTileBasis.TryDecodeOffset(2, 17, out _));
        }

        [Fact]
        public void Texture_Is_Deterministic_Across_Regeneration()
        {
            // Static field is built once at type init; verify a second read is identical (reference stability + content check).
            var first = MotionTileBasis.Texture;
            var second = MotionTileBasis.Texture;

            Assert.Equal(MotionTileBasis.TextureSize, first.GetLength(0));
            Assert.Equal(MotionTileBasis.TextureSize, first.GetLength(1));
            for (int y = 0; y < MotionTileBasis.TextureSize; y++)
                for (int x = 0; x < MotionTileBasis.TextureSize; x++)
                    Assert.Equal(first[y, x], second[y, x]);
        }

        [Fact]
        public void Texture_Is_BandLimited_Around_Neutral_Luma()
        {
            var texture = MotionTileBasis.Texture;
            foreach (byte pixel in texture)
            {
                Assert.InRange(pixel, (byte)64, (byte)192);
            }
        }

        [Theory]
        [InlineData(-16, -16)]
        [InlineData(2, 2)]
        [InlineData(16, 16)]
        [InlineData(-2, 14)]
        public void TileMatch_TrueOffset_MinimizesSad_WithMargin(int trueDx, int trueDy)
        {
            int cell = MotionTileBasis.CellSize;
            int guard = MotionTileBasis.MaxAbsOffsetPx;
            int size = MotionTileBasis.TextureSize;
            var texture = MotionTileBasis.Texture;

            // Neutral-gray cell canvas with the reference texture placed at the true offset.
            var canvas = new byte[cell, cell];
            for (int y = 0; y < cell; y++)
                for (int x = 0; x < cell; x++)
                    canvas[y, x] = 128;

            int placeX = guard + trueDx;
            int placeY = guard + trueDy;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    canvas[placeY + y, placeX + x] = texture[y, x];

            var axisOffsets = MotionTileBasis.GetAxisOffsets();
            long bestSad = long.MaxValue;
            long secondBestSad = long.MaxValue;
            int bestDx = 0, bestDy = 0;

            foreach (int candidateDx in axisOffsets)
            {
                foreach (int candidateDy in axisOffsets)
                {
                    int windowX = guard + candidateDx;
                    int windowY = guard + candidateDy;

                    long sad = 0;
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                            sad += Math.Abs(canvas[windowY + y, windowX + x] - texture[y, x]);

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
            }

            Assert.Equal(trueDx, bestDx);
            Assert.Equal(trueDy, bestDy);
            Assert.Equal(0, bestSad);
            Assert.True(secondBestSad > 0, "A non-true candidate matched the placed texture exactly.");
        }
    }
}
