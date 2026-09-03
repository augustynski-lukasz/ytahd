using System;
using Xunit;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    public class MotionFrameBitDecoderTests
    {
        private const int Width = 200;
        private const int Height = 160;
        private const int BorderWidth = 20;
        private const int CellSize = MotionTileBasis.CellSize;

        private static byte[] BuildFullCapacityPayload(out int totalCells)
        {
            int usableWidth = Width - BorderWidth * 2;
            int usableHeight = Height - BorderWidth * 2;
            totalCells = (usableWidth / CellSize) * (usableHeight / CellSize);

            var payload = new byte[totalCells];
            for (int i = 0; i < totalCells; i++) payload[i] = (byte)(i * 53 % 256);
            return payload;
        }

        private static byte[] DecodeFrame(byte[] frame, int packetLength)
        {
            var decoder = new MotionFrameBitDecoder();
            var packet = new byte[packetLength];
            decoder.Decode(frame, Width, Height, CellSize, Width * 3, frame.Length, packet, BorderWidth);
            return packet;
        }

        private static int CountMatches(byte[] expected, byte[] actual)
        {
            int matches = 0;
            for (int i = 0; i < expected.Length; i++)
                if (expected[i] == actual[i]) matches++;
            return matches;
        }

        [Fact]
        public void Clean_Frame_RoundTrips_ExactPayload()
        {
            var payload = BuildFullCapacityPayload(out int totalCells);
            var mod = new MotionVectorModulator();
            byte[] frame = mod.CreateFrame(new ModulatorGeometry(Width, Height, mod.MacroblockWidth, 0, BorderWidth), payload);

            byte[] decoded = DecodeFrame(frame, totalCells);

            Assert.Equal(payload, decoded);
        }

        [Theory]
        [InlineData(4)]
        [InlineData(8)]
        public void GaussianNoise_RecoversPayload_AboveAccuracyThreshold(double sigma)
        {
            var payload = BuildFullCapacityPayload(out int totalCells);
            var mod = new MotionVectorModulator();
            byte[] frame = mod.CreateFrame(new ModulatorGeometry(Width, Height, mod.MacroblockWidth, 0, BorderWidth), payload);

            ApplyGaussianNoise(frame, sigma, seed: 1234);
            byte[] decoded = DecodeFrame(frame, totalCells);

            int matches = CountMatches(payload, decoded);
            Assert.True(matches >= totalCells * 0.9, $"Only {matches}/{totalCells} cells matched under sigma={sigma} noise.");
        }

        [Fact]
        public void BoxBlur_RecoversPayload_AboveAccuracyThreshold()
        {
            var payload = BuildFullCapacityPayload(out int totalCells);
            var mod = new MotionVectorModulator();
            byte[] frame = mod.CreateFrame(new ModulatorGeometry(Width, Height, mod.MacroblockWidth, 0, BorderWidth), payload);

            ApplyBoxBlur3x3(frame, Width, Height);
            byte[] decoded = DecodeFrame(frame, totalCells);

            int matches = CountMatches(payload, decoded);
            Assert.True(matches >= totalCells * 0.9, $"Only {matches}/{totalCells} cells matched under 3x3 box blur.");
        }

        [Theory]
        [InlineData(10)]
        [InlineData(-10)]
        public void LumaShift_RecoversPayload_AboveAccuracyThreshold(int shift)
        {
            var payload = BuildFullCapacityPayload(out int totalCells);
            var mod = new MotionVectorModulator();
            byte[] frame = mod.CreateFrame(new ModulatorGeometry(Width, Height, mod.MacroblockWidth, 0, BorderWidth), payload);

            ApplyLumaShift(frame, shift);
            byte[] decoded = DecodeFrame(frame, totalCells);

            int matches = CountMatches(payload, decoded);
            Assert.True(matches >= totalCells * 0.9, $"Only {matches}/{totalCells} cells matched under luma shift={shift}.");
        }

        [Fact]
        public void CanonicalFrame_Is_Detected_As_Canonical()
        {
            var mod = new MotionVectorModulator();
            byte[] frame = mod.CreateFrame(new ModulatorGeometry(Width, Height, mod.MacroblockWidth, 0, BorderWidth), ReadOnlySpan<byte>.Empty);
            ApplyGaussianNoise(frame, sigma: 4, seed: 42);

            Assert.True(MotionFrameBitDecoder.IsCanonicalFrame(frame, Width, Height, BorderWidth));
        }

        [Fact]
        public void DisplacedFrame_Is_Not_Detected_As_Canonical()
        {
            var payload = BuildFullCapacityPayload(out _);
            var mod = new MotionVectorModulator();
            byte[] frame = mod.CreateFrame(new ModulatorGeometry(Width, Height, mod.MacroblockWidth, 0, BorderWidth), payload);

            Assert.False(MotionFrameBitDecoder.IsCanonicalFrame(frame, Width, Height, BorderWidth));
        }

        // ── synthetic degradation helpers ──────────────────────────────────────────

        private static void ApplyGaussianNoise(byte[] rgbaFrame, double sigma, int seed)
        {
            var rnd = new Random(seed);
            for (int i = 0; i < rgbaFrame.Length; i += 4)
            {
                double n1 = NextGaussian(rnd, sigma);
                double n2 = NextGaussian(rnd, sigma);
                double n3 = NextGaussian(rnd, sigma);
                rgbaFrame[i] = Clamp(rgbaFrame[i] + n1);
                rgbaFrame[i + 1] = Clamp(rgbaFrame[i + 1] + n2);
                rgbaFrame[i + 2] = Clamp(rgbaFrame[i + 2] + n3);
            }
        }

        private static double NextGaussian(Random rnd, double sigma)
        {
            double u1 = 1.0 - rnd.NextDouble();
            double u2 = rnd.NextDouble();
            double z0 = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            return z0 * sigma;
        }

        private static void ApplyBoxBlur3x3(byte[] rgbaFrame, int width, int height)
        {
            var copy = (byte[])rgbaFrame.Clone();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int sum = 0, count = 0;
                        for (int oy = -1; oy <= 1; oy++)
                        {
                            for (int ox = -1; ox <= 1; ox++)
                            {
                                int sx = x + ox, sy = y + oy;
                                if (sx < 0 || sx >= width || sy < 0 || sy >= height) continue;
                                sum += copy[(sy * width + sx) * 4 + c];
                                count++;
                            }
                        }

                        rgbaFrame[(y * width + x) * 4 + c] = (byte)(sum / count);
                    }
                }
            }
        }

        private static void ApplyLumaShift(byte[] rgbaFrame, int shift)
        {
            for (int i = 0; i < rgbaFrame.Length; i += 4)
            {
                rgbaFrame[i] = Clamp(rgbaFrame[i] + shift);
                rgbaFrame[i + 1] = Clamp(rgbaFrame[i + 1] + shift);
                rgbaFrame[i + 2] = Clamp(rgbaFrame[i + 2] + shift);
            }
        }

        private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }
}
