using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Application;
using YTAHD.Core.Core;
using YTAHD.Core.Infrastructure;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    /// <summary>
    /// Real-child coverage for the pipe handling that keeps the parallel pipeline off the thread pool.
    /// </summary>
    /// <remarks>
    /// These tests deliberately drive real ffmpeg processes: the defect they guard against only exists
    /// because a child's standard streams are synchronous pipes, so synthetic stream fakes cannot
    /// reproduce it. See docs/decisions/CR-20260912-04-child-pipe-thread-pool-starvation.md.
    /// </remarks>
    public class ChildProcessPipeRealCodecTests
    {
        private const int PipeFrameWidth = 32;
        private const int PipeFrameHeight = 32;
        private const int PipeFrameCount = 400;

        // Matches the proven phase1 configuration from CR-20260912-02: a 640x480 frame at a 16px
        // macroblock carries 1200 blocks, which is what the 256-byte payload below needs.
        private const int Width = 640;
        private const int Height = 480;
        private const int MacroblockSize = 16;
        private const int Fps = 30;

        [Fact]
        public async Task ChildPipeStream_Streams_A_Real_Child_Pipe_To_Completion()
        {
            var ffmpegPath = TestFfmpeg.GetAvailableFfmpegPath();
            Assert.False(
                string.IsNullOrWhiteSpace(ffmpegPath),
                "ffmpeg must be available (PATH or YTAHD_FFMPEG_PATH) for the child-pipe streaming test.");

            var frameBytes = PipeFrameWidth * PipeFrameHeight * 3;
            var input = CreateRawVideoFramePattern(PipeFrameCount, frameBytes);

            var workDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            var inputFile = Path.Combine(workDirectory, "input.rgb");

            try
            {
                await File.WriteAllBytesAsync(inputFile, input);

                using var scope = ChildProcessScope.Start(
                    BuildRawVideoTranscodeStartInfo(ffmpegPath!, inputFile),
                    "Failed to start ffmpeg for the child-pipe streaming test.");
                var process = scope.Process;

                var stderrDrain = ChildProcessPipes.DrainAsync(process.StandardError);

                using (var stdout = new ChildPipeStream(process.StandardOutput.BaseStream))
                using (var sink = new MemoryStream())
                {
                    await stdout.CopyToAsync(sink);

                    // Every byte the child produced must arrive, in order, and the stream must report a
                    // real end of stream rather than a truncated one.
                    Assert.Equal((long)PipeFrameCount * frameBytes, sink.Length);
                    Assert.True(input.AsSpan().SequenceEqual(sink.ToArray()));
                }

                await process.WaitForExitAsync();
                await stderrDrain;

                Assert.Equal(0, process.ExitCode);
                Assert.True(scope.HasExited, "The child should have exited on its own, so nothing was killed.");
            }
            finally
            {
                TryDeleteDirectory(workDirectory);
            }
        }

        [Fact]
        public async Task Abandoned_Child_Is_Killed_Even_When_Its_Pipe_Is_Full_And_Unread()
        {
            var ffmpegPath = TestFfmpeg.GetAvailableFfmpegPath();
            Assert.False(
                string.IsNullOrWhiteSpace(ffmpegPath),
                "ffmpeg must be available (PATH or YTAHD_FFMPEG_PATH) for the abandoned-child test.");

            var frameBytes = PipeFrameWidth * PipeFrameHeight * 3;
            var input = CreateRawVideoFramePattern(PipeFrameCount, frameBytes);

            var workDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            var inputFile = Path.Combine(workDirectory, "input.rgb");

            try
            {
                await File.WriteAllBytesAsync(inputFile, input);

                int pid;
                using (var scope = ChildProcessScope.Start(
                    BuildRawVideoTranscodeStartInfo(ffmpegPath!, inputFile),
                    "Failed to start ffmpeg for the abandoned-child test."))
                {
                    var process = scope.Process;
                    pid = process.Id;
                    var stderrDrain = ChildProcessPipes.DrainAsync(process.StandardError);

                    // stdout is intentionally never read, so the child fills the pipe buffer and blocks.
                    Assert.True(
                        await WaitUntilAsync(() => IsAlive(pid), TimeSpan.FromSeconds(10)),
                        "The child exited before the pipe could fill, so the test cannot prove anything.");

                    // Give it a moment to reach the blocked state; it cannot finish, because nobody drains
                    // the very pipe it is writing to.
                    await Task.Delay(250);
                    Assert.True(IsAlive(pid), "The child finished despite an undrained pipe; the test is not exercising the block.");

                    _ = stderrDrain;
                }

                Assert.True(
                    await WaitUntilAsync(() => !IsAlive(pid), TimeSpan.FromSeconds(10)),
                    "Disposing the child scope left ffmpeg running: it would leak one real process per failed or cancelled operation.");
            }
            finally
            {
                TryDeleteDirectory(workDirectory);
            }
        }

        [Fact]
        public async Task Concurrent_RoundTrips_Complete_Without_Exhausting_The_Thread_Pool()
        {
            var ffmpegPath = TestFfmpeg.GetAvailableFfmpegPath();
            Assert.False(
                string.IsNullOrWhiteSpace(ffmpegPath),
                "ffmpeg must be available (PATH or YTAHD_FFMPEG_PATH) for the concurrent round-trip test.");

            // More concurrent children than the thread pool's base size on a small machine: before the
            // pipe handling was fixed this configuration left the pool with no free threads and the run
            // stalled indefinitely after reporting every test as passed.
            int concurrency = Math.Max(4, Environment.ProcessorCount + 2);
            var payloads = new byte[concurrency][];
            for (int i = 0; i < concurrency; i++)
            {
                payloads[i] = new byte[256];
                new Random(1000 + i).NextBytes(payloads[i]);
            }

            var stopwatch = Stopwatch.StartNew();

            var results = await Task.WhenAll(
                payloads.Select(payload => RoundTripAsync(ffmpegPath!, payload, ParallelismPolicy.Auto)));

            stopwatch.Stop();

            for (int i = 0; i < results.Length; i++)
            {
                Assert.True(
                    results[i],
                    $"Concurrent round trip {i} of {concurrency} did not recover its payload.");
            }

            // A generous ceiling: it only fails if the run degrades into thread-pool starvation, which
            // manifests as minutes rather than the few seconds this configuration costs when healthy.
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromMinutes(3),
                $"{concurrency} concurrent round trips took {stopwatch.Elapsed.TotalSeconds:F1}s, which suggests the pipeline is blocking thread-pool threads again.");
        }

        private static async Task<bool> RoundTripAsync(string ffmpegPath, byte[] payload, int requestedParallelism)
        {
            var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
            var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
            var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

            try
            {
                await File.WriteAllBytesAsync(inputFile, payload);

                var service = new YtahdCodecService(
                    new BinaryGridModulator(),
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
                return payload.AsSpan().SequenceEqual(decoded);
            }
            finally
            {
                if (File.Exists(inputFile)) File.Delete(inputFile);
                if (File.Exists(outputVideo)) File.Delete(outputVideo);
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }

        /// <summary>
        /// A rawvideo to rawvideo transcode, so the child produces a large, fully predictable byte stream
        /// without depending on any encoder or filter being present in the ffmpeg build.
        /// </summary>
        private static ProcessStartInfo BuildRawVideoTranscodeStartInfo(string ffmpegPath, string inputFile)
            => new(
                ffmpegPath,
                $"-nostdin -hide_banner -loglevel error -f rawvideo -pix_fmt rgb24 -s {PipeFrameWidth}x{PipeFrameHeight} " +
                $"-r 30 -i \"{inputFile}\" -f rawvideo -pix_fmt rgb24 -")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };

        private static byte[] CreateRawVideoFramePattern(int frameCount, int frameBytes)
        {
            var data = new byte[frameCount * frameBytes];
            for (int frame = 0; frame < frameCount; frame++)
            {
                for (int i = 0; i < frameBytes; i++)
                {
                    data[frame * frameBytes + i] = (byte)((frame + i) % 251);
                }
            }

            return data;
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return true;
                }

                await Task.Delay(50);
            }

            return condition();
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // Temp cleanup must never fail a test.
            }
        }
    }
}
