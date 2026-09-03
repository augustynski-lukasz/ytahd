using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    /// <summary>
    /// A4: modulator-driven physical-frame emission pattern. Confirms Phase 1-3 keep the
    /// historical fixed 3x repeat untouched while Phase 4 emits a displaced frame followed by
    /// a canonical separator, and that the decode pipeline round-trips through both patterns.
    /// </summary>
    public class Phase4PipelineIntegrationTests
    {
        private const int Width = 640;
        private const int Height = 480;
        private const int Macroblock = 16;

        public static IEnumerable<object[]> LegacyModulators()
        {
            yield return new object[] { new BinaryGridModulator() };
            yield return new object[] { new DctModulator() };
        }

        [Theory]
        [MemberData(nameof(LegacyModulators))]
        public async Task LegacyModulators_StillEmit_FixedThreeRepeats_NoSeparator(IModulator mod)
        {
            var tmpIn = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[16];
                new Random(9).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                long frameSize = Width * Height * 3;
                long expectedBytes = encoder.LastEncodeMetrics.TotalFramesWritten * 3L * frameSize;

                Assert.Equal(expectedBytes, fake.WrittenBytes);
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }

        [Fact]
        public async Task MotionVectorModulator_Emits_DisplacedFrame_Then_CanonicalSeparator()
        {
            var mod = new MotionVectorModulator();
            var tmpIn = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[4];
                new Random(11).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                long frameSize = Width * Height * 3;
                long expectedBytes = encoder.LastEncodeMetrics.TotalFramesWritten * 2L * frameSize; // 1 displaced + 1 canonical

                Assert.Equal(expectedBytes, fake.WrittenBytes);
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }

        [Fact]
        public async Task MotionVectorModulator_RoundTrips_ThroughDecodeStreamOrchestrator()
        {
            var mod = new MotionVectorModulator();
            var tmpIn = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[4];
                new Random(11).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var buffer = fake.Process?.Buffer;
                Assert.NotNull(buffer);
                buffer.Position = 0;

                var orchestrator = new DecodeStreamOrchestrator(mod, Width, Height, Macroblock);
                var output = await orchestrator.ProcessAsync(buffer, data.Length);

                Assert.Equal(data, output);
                Assert.True(orchestrator.LastDecodeMetrics.CanonicalFrameCount > 0);
                Assert.Equal(0, orchestrator.LastDecodeMetrics.InvalidPacketCount);
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }

        [Fact]
        public async Task LegacyModulator_RoundTrip_Reports_No_CanonicalFrames()
        {
            var mod = new BinaryGridModulator();
            var tmpIn = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[16];
                new Random(9).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var buffer = fake.Process?.Buffer;
                Assert.NotNull(buffer);
                buffer.Position = 0;

                var orchestrator = new DecodeStreamOrchestrator(mod, Width, Height, Macroblock);
                var output = await orchestrator.ProcessAsync(buffer, data.Length);

                Assert.Equal(data, output);
                Assert.Equal(0, orchestrator.LastDecodeMetrics.CanonicalFrameCount);
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }
    }
}
