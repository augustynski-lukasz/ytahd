using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    /// <summary>
    /// C1: combined-clock arbitration. The audio FSK datagram count is an independent
    /// ground truth for how many logical frames the encoder actually sent; comparing it
    /// against what the video pipeline reconstructed exposes silent whole-parity-group
    /// losses that XOR-parity alone cannot detect (it only recovers a single missing frame
    /// *within* a known group). See ADR F-20260903-02-audio-fsk-clock-design.md.
    /// </summary>
    public class CombinedClockArbitrationTests
    {
        private const int Width = 128;
        private const int Height = 64;
        private const int Macroblock = 1;
        private const int HeaderBytes = FramePacket.HeaderBytes;
        private const int PhysicalFramesPerLogicalFrame = 3; // BinaryGridModulator: no IFrameEmissionStrategy override

        private static int GetPayloadBytesPerFrame()
        {
            var mod = new BinaryGridModulator(Macroblock, Macroblock);
            return mod.GetPayloadBytesPerFrame(Width, Height, HeaderBytes, borderWidth: 0, macroblockSize: mod.MacroblockWidth);
        }

        private static async Task<MemoryStream> EncodeExactlyTwoParityGroupsAsync()
        {
            int payloadBytesPerFrame = GetPayloadBytesPerFrame();
            var payload = new byte[payloadBytesPerFrame * 8]; // 8 data frames = exactly 2 full groups of 4
            new Random(99).NextBytes(payload);

            var tmpIn = Path.GetTempFileName();
            await File.WriteAllBytesAsync(tmpIn, payload);
            try
            {
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(new BinaryGridModulator(), fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var buffer = fake.Process!.Buffer;
                buffer.Position = 0;
                return buffer;
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }

        [Fact]
        public async Task Complete_Stream_Reports_No_AudioVideo_Mismatch()
        {
            var buffer = await EncodeExactlyTwoParityGroupsAsync();

            // 8 data frames + 2 parity frames = 10 logical frames; the audio clock (built
            // from the same schedule) would report exactly this count for an intact stream.
            const int trueLogicalFrameCount = 10;

            var orchestrator = new DecodeStreamOrchestrator(new BinaryGridModulator(), Width, Height, Macroblock);
            await orchestrator.ProcessAsync(buffer, expectedOutputBytes: 0, audioDatagramCount: trueLogicalFrameCount);

            var metrics = orchestrator.LastDecodeMetrics;
            Assert.Equal(8, metrics.RecoveredDataFrameCount);
            Assert.Equal(2, metrics.RecoveredParityFrameCount);
            Assert.Equal(10, metrics.TotalRecoveredLogicalFrames);
            Assert.False(metrics.HasAudioVideoDatagramMismatch());
        }

        [Fact]
        public async Task SilentlyDropped_WholeParityGroup_Is_Detected_Via_AudioClock()
        {
            var buffer = await EncodeExactlyTwoParityGroupsAsync();
            int frameVideoBytes = Width * Height * 3;

            // Strip the second parity group entirely (its 4 data frames + 1 parity frame =
            // 5 logical frames, each written PhysicalFramesPerLogicalFrame times). XOR-parity
            // recovery only ever notices a *single* missing frame inside a group it has a
            // parity packet for; a whole group vanishing (data *and* parity) raises nothing.
            int physicalFramesToDrop = 5 * PhysicalFramesPerLogicalFrame;
            int bytesToDrop = physicalFramesToDrop * frameVideoBytes;
            var truncated = new MemoryStream(buffer.ToArray(), 0, (int)buffer.Length - bytesToDrop, writable: false);

            const int trueLogicalFrameCount = 10; // what the (unaffected) audio track would report

            var orchestrator = new DecodeStreamOrchestrator(new BinaryGridModulator(), Width, Height, Macroblock);
            await orchestrator.ProcessAsync(truncated, expectedOutputBytes: 0, audioDatagramCount: trueLogicalFrameCount);

            var metrics = orchestrator.LastDecodeMetrics;
            Assert.Equal(4, metrics.RecoveredDataFrameCount);
            Assert.Equal(1, metrics.RecoveredParityFrameCount);
            Assert.Equal(5, metrics.TotalRecoveredLogicalFrames);
            Assert.True(metrics.HasAudioVideoDatagramMismatch(), "Expected the audio clock to flag the silently dropped parity group.");
        }

        [Fact]
        public async Task No_AudioClock_Leaves_Mismatch_Signal_Null()
        {
            var buffer = await EncodeExactlyTwoParityGroupsAsync();

            var orchestrator = new DecodeStreamOrchestrator(new BinaryGridModulator(), Width, Height, Macroblock);
            await orchestrator.ProcessAsync(buffer, expectedOutputBytes: 0);

            Assert.Null(orchestrator.LastDecodeMetrics.AudioDatagramCount);
            Assert.Null(orchestrator.LastDecodeMetrics.HasAudioVideoDatagramMismatch());
        }
    }
}
