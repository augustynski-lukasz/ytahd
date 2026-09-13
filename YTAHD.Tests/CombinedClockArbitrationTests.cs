using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Application;
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

        private static DurabilityMatrixOptions MatrixOptions() => new()
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        };

        private static async Task<MemoryStream> EncodeExactlyTwoParityGroupsAsync(bool useDurabilityMatrix = false)
        {
            int payloadBytesPerFrame = GetPayloadBytesPerFrame();
            var payload = new byte[payloadBytesPerFrame * 8]; // 8 data frames = exactly 2 full groups of 4
            new Random(99).NextBytes(payload);

            var tmpIn = Path.GetTempFileName();
            await File.WriteAllBytesAsync(tmpIn, payload);
            try
            {
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(new BinaryGridModulator(), fake, new VideoCodecOptions
                {
                    Width = Width,
                    Height = Height,
                    MacroblockSize = Macroblock,
                    Fps = 30,
                    UseDurabilityMatrix = useDurabilityMatrix,
                    DurabilityMatrixOptions = MatrixOptions()
                });
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

        // ── CR-20260913-02: hole-tolerant multi-erasure repair ──────────────────────

        [Fact]
        public async Task SilentlyDropped_WholeParityGroup_Yields_Documented_Hole_And_Loss_Map()
        {
            // Encoded with the durability matrix so the stream carries the manifest schedule
            // the hole-tolerant decoder needs; the redundant start manifest survives truncation.
            // Under the matrix the payload (8 frames' worth) becomes many small symbol groups,
            // so the "dropped parity group" is the stream's FINAL group, not group 1.
            var matrixOptions = MatrixOptions();
            var buffer = await EncodeExactlyTwoParityGroupsAsync(useDurabilityMatrix: true);

            int payloadBytesPerFrame = GetPayloadBytesPerFrame();
            int totalPayloadBytes = payloadBytesPerFrame * 8;
            int groupBytes = matrixOptions.GroupSize * matrixOptions.SymbolSize;
            int lastGroupIndex = (int)Math.Ceiling(totalPayloadBytes / (double)groupBytes) - 1;
            int symbolsInLastGroup = (int)Math.Ceiling((totalPayloadBytes - lastGroupIndex * groupBytes) / (double)matrixOptions.SymbolSize);

            int frameVideoBytes = Width * Height * 3;
            // Strip the final group entirely: its data frames + its parity frame + the
            // end-manifest copy, each written PhysicalFramesPerLogicalFrame times.
            int logicalFramesAtTail = symbolsInLastGroup + 1 /* parity */ + 1 /* end manifest */;
            int physicalFramesToDrop = logicalFramesAtTail * PhysicalFramesPerLogicalFrame;
            int bytesToDrop = physicalFramesToDrop * frameVideoBytes;
            var truncated = new MemoryStream(buffer.ToArray(), 0, (int)buffer.Length - bytesToDrop, writable: false);

            const int trueLogicalFrameCount = 10; // what the (unaffected) audio track would report

            // Durability-matrix decode path (the legacy path has no schedule to size holes).
            var orchestrator = new DecodeStreamOrchestrator(
                new BinaryGridModulator(), Width, Height, Macroblock,
                useDurabilityMatrix: true,
                matrixOptions);
            var output = await orchestrator.ProcessAsync(truncated, expectedOutputBytes: 0, audioDatagramCount: trueLogicalFrameCount);

            var metrics = orchestrator.LastDecodeMetrics;
            Assert.True(metrics.HasAudioVideoDatagramMismatch(), "Expected the audio clock to flag the silently dropped parity group.");

            // Loss map: exactly the final group, identified precisely for the consumer.
            Assert.Single(metrics.MissingDatagramIds);
            Assert.Equal(lastGroupIndex, metrics.MissingDatagramIds[0]);

            // Output still spans the manifest-declared schedule; hole bytes are zeros and
            // everything before the hole is intact.
            Assert.Equal(totalPayloadBytes, output.Length);
            int holeStart = lastGroupIndex * groupBytes;
            Assert.All(output[holeStart..], b => Assert.Equal(0, b));

            var original = new byte[totalPayloadBytes];
            new Random(99).NextBytes(original);
            Assert.Equal(original[..holeStart], output[..holeStart]);
        }
    }
}
