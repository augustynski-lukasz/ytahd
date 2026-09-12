using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Application;
using YTAHD.Core.Core;
using YTAHD.Core.Infrastructure;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    /// <summary>
    /// Workstream F6 real-codec validation for the bounded parallel pipeline: the same payload
    /// must survive an encode/decode round trip byte-for-byte at every supported degree of
    /// parallelism, and the parallel run must produce the same protocol framing as the serial
    /// run. Synthetic-only coverage is not sufficient here (see
    /// docs/decisions/CR-20260912-02-pipeline-performance-validation.md).
    /// </summary>
    public class ParallelPipelineRealCodecTests
    {
        private const int Width = 640;
        private const int Height = 480;
        private const int MacroblockSize = 16;
        private const int Fps = 30;

        /// <summary>
        /// Job settings exercised by the matrix: the unconfigured default, a forced serial run
        /// (deterministic debugging), the auto policy, and explicit worker counts either side of
        /// the auto cap.
        /// </summary>
        private static readonly (string Label, int Requested)[] JobSettings =
        {
            ("default", 0),
            ("serial", 1),
            ("auto", ParallelismPolicy.Auto),
            ("workers-2", 2),
            ("workers-4", 4)
        };

        public static IEnumerable<object[]> ModulatorCases()
        {
            yield return new object[] { "phase1", 1024 };
            yield return new object[] { "phase2", 1024 };
            yield return new object[] { "phase3", 128 };
            yield return new object[] { "phase4", 64 };
        }

        [Theory]
        [MemberData(nameof(ModulatorCases))]
        public async Task RealFfmpeg_RoundTrip_Survives_Every_Degree_Of_Parallelism(string modeName, int payloadSize)
        {
            var ffmpegPath = TestFfmpeg.GetAvailableFfmpegPath();
            Assert.False(
                string.IsNullOrWhiteSpace(ffmpegPath),
                $"ffmpeg must be available (PATH or YTAHD_FFMPEG_PATH) for the {modeName} parallel round-trip test.");

            var payload = new byte[payloadSize];
            new Random(31337 + payloadSize + modeName.Length).NextBytes(payload);

            int? serialFrameCount = null;
            int? serialFramesDecoded = null;

            foreach (var (label, requested) in JobSettings)
            {
                var result = await RoundTripAsync(ffmpegPath!, modeName, payload, requested);

                Assert.True(
                    result.PayloadMatches,
                    $"{modeName} payload of {payloadSize} bytes was not recovered exactly with jobs={label} ({requested}).");
                Assert.Equal(payload.Length, result.DecodeMetrics.TotalDecodedPayloadBytes);
                Assert.True(
                    result.EncodeMetrics.TotalFramesWritten > 0,
                    $"{modeName} jobs={label} wrote no frames.");
                Assert.True(
                    result.DecodeMetrics.TotalFramesDecoded > 0,
                    $"{modeName} jobs={label} decoded no frames.");

                if (serialFrameCount == null)
                {
                    serialFrameCount = result.EncodeMetrics.TotalFramesWritten;
                    serialFramesDecoded = result.DecodeMetrics.TotalFramesDecoded;
                }
                else
                {
                    // Parallelism must not change the protocol framing: same physical frames
                    // written, same logical frames recovered.
                    Assert.Equal(serialFrameCount.Value, result.EncodeMetrics.TotalFramesWritten);
                    Assert.Equal(serialFramesDecoded!.Value, result.DecodeMetrics.TotalFramesDecoded);
                }
            }
        }

        [Fact]
        public async Task RealFfmpeg_Parallel_Encode_Produces_Identical_Frame_Sequence_To_Serial()
        {
            var ffmpegPath = TestFfmpeg.GetAvailableFfmpegPath();
            Assert.False(
                string.IsNullOrWhiteSpace(ffmpegPath),
                "ffmpeg must be available (PATH or YTAHD_FFMPEG_PATH) for the parallel/serial equivalence test.");

            // Phase 3 uses the parallel inner render loop, so it is the strongest equivalence check.
            var payload = new byte[256];
            new Random(90210).NextBytes(payload);

            var serial = await RoundTripAsync(ffmpegPath!, "phase3", payload, requestedParallelism: 1);
            var parallel = await RoundTripAsync(ffmpegPath!, "phase3", payload, requestedParallelism: ParallelismPolicy.Auto);

            Assert.True(serial.PayloadMatches && parallel.PayloadMatches);

            Assert.Equal(serial.EncodeMetrics.TotalFramesWritten, parallel.EncodeMetrics.TotalFramesWritten);
            Assert.Equal(serial.EncodeMetrics.TotalDataFrames, parallel.EncodeMetrics.TotalDataFrames);
            Assert.Equal(serial.EncodeMetrics.PayloadBytesPerFrame, parallel.EncodeMetrics.PayloadBytesPerFrame);

            Assert.Equal(serial.DecodeMetrics.TotalFramesSeen, parallel.DecodeMetrics.TotalFramesSeen);
            Assert.Equal(serial.DecodeMetrics.TotalFramesDecoded, parallel.DecodeMetrics.TotalFramesDecoded);
            Assert.Equal(serial.DecodeMetrics.InvalidPacketCount, parallel.DecodeMetrics.InvalidPacketCount);
            Assert.Equal(serial.DecodeMetrics.CanonicalFrameCount, parallel.DecodeMetrics.CanonicalFrameCount);
            Assert.Equal(serial.DecodeMetrics.RecoveredGroupCount, parallel.DecodeMetrics.RecoveredGroupCount);
        }

        [Fact]
        public async Task RealFfmpeg_Parallel_Cancellation_Does_Not_Strand_The_Encode_Caller()
        {
            var ffmpegPath = TestFfmpeg.GetAvailableFfmpegPath();
            Assert.False(
                string.IsNullOrWhiteSpace(ffmpegPath),
                "ffmpeg must be available (PATH or YTAHD_FFMPEG_PATH) for the parallel cancellation test.");

            var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
            var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");

            try
            {
                // 64 KiB across the serial Phase 1 carrier is many frames, so cancellation fires
                // while the bounded pipeline is mid-flight.
                var payload = new byte[64 * 1024];
                new Random(515).NextBytes(payload);
                await File.WriteAllBytesAsync(inputFile, payload);

                var service = new YtahdCodecService(
                    BenchmarkModulator("phase1"),
                    new DefaultFFmpegWrapperFactory(ffmpegPath!));

                using var cts = new System.Threading.CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromMilliseconds(250));

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EncodeAsync(
                    new EncodeOptions
                    {
                        InputFile = inputFile,
                        OutputVideo = outputVideo,
                        Width = Width,
                        Height = Height,
                        MacroblockSize = MacroblockSize,
                        Fps = Fps,
                        VerifyFfmpeg = true,
                        MaxDegreeOfParallelism = 4
                    },
                    cts.Token));

                // Reaching this point means the bounded channel wait did not deadlock the caller.
                Assert.True(true);
            }
            finally
            {
                if (File.Exists(inputFile)) File.Delete(inputFile);
                if (File.Exists(outputVideo)) File.Delete(outputVideo);
            }
        }

        private static IModulator BenchmarkModulator(string mode) => mode switch
        {
            "phase1" => new BinaryGridModulator(),
            "phase2" => new PseudoQamModulator(),
            "phase3" => new DctModulator(),
            "phase4" => new MotionVectorModulator(),
            _ => throw new ArgumentException($"Unsupported modulator '{mode}'.")
        };

        private static async Task<RoundTripResult> RoundTripAsync(
            string ffmpegPath,
            string modeName,
            byte[] payload,
            int requestedParallelism)
        {
            var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
            var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
            var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

            try
            {
                await File.WriteAllBytesAsync(inputFile, payload);

                var service = new YtahdCodecService(
                    BenchmarkModulator(modeName),
                    new DefaultFFmpegWrapperFactory(ffmpegPath));

                await service.EncodeAsync(new EncodeOptions
                {
                    InputFile = inputFile,
                    OutputVideo = outputVideo,
                    Width = Width,
                    Height = Height,
                    MacroblockSize = MacroblockSize,
                    Fps = Fps,
                    VerifyFfmpeg = true,
                    MaxDegreeOfParallelism = requestedParallelism
                });

                await service.DecodeAsync(new DecodeOptions
                {
                    InputVideo = outputVideo,
                    OutputFile = outputFile,
                    Width = Width,
                    Height = Height,
                    MacroblockSize = MacroblockSize,
                    Fps = Fps,
                    VerifyFfmpeg = true,
                    MaxDegreeOfParallelism = requestedParallelism
                });

                var decoded = await File.ReadAllBytesAsync(outputFile);

                return new RoundTripResult
                {
                    PayloadMatches = payload.AsSpan().SequenceEqual(decoded),
                    EncodeMetrics = service.LastEncodeMetrics,
                    DecodeMetrics = service.LastDecodeMetrics
                };
            }
            finally
            {
                if (File.Exists(inputFile)) File.Delete(inputFile);
                if (File.Exists(outputVideo)) File.Delete(outputVideo);
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }

        private sealed class RoundTripResult
        {
            public required bool PayloadMatches { get; init; }
            public required EncodeMetrics EncodeMetrics { get; init; }
            public required DecodeMetrics DecodeMetrics { get; init; }
        }
    }
}
