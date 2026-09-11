using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Application;
using YTAHD.Core.Core;
using YTAHD.Core.Infrastructure;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    public class YtahdCodecServiceTests
    {
        private static string? GetAvailableFfmpegPath()
        {
            var candidates = new[]
            {
                "D:\\!Tools\\ffmpeg-20151019\\bin\\ffmpeg.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
                "ffmpeg.exe",
                "ffmpeg"
            };

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (candidate.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) || candidate.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(candidate, "-version")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };

                        using var process = System.Diagnostics.Process.Start(psi);
                        if (process != null)
                        {
                            process.WaitForExit();
                            if (process.ExitCode == 0)
                            {
                                return candidate;
                            }
                        }
                    }
                    catch
                    {
                        // Try the next candidate.
                    }

                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        [Fact]
        public async Task FFmpegWrapper_UsesExplicitExecutablePath_WhenProvided()
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be present on PATH or a known local install path for the explicit-path test.");

            var wrapper = new FFmpegWrapper(ffmpegExecutablePath: ffmpegPath);
            var isAvailable = await wrapper.IsAvailableAsync();

            Assert.True(isAvailable);
        }

        [Fact]
        public void FFmpegTools_ResolveExplicitDirectoryPath_WhenProvided()
        {
            var ffmpegDir = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}");
            Directory.CreateDirectory(ffmpegDir);

            try
            {
                var ffmpegPath = Path.Combine(ffmpegDir, "ffmpeg.exe");
                var ffprobePath = Path.Combine(ffmpegDir, "ffprobe.exe");
                File.WriteAllText(ffmpegPath, string.Empty);
                File.WriteAllText(ffprobePath, string.Empty);

                var wrapper = new FFmpegWrapper(ffmpegExecutablePath: ffmpegDir);
                var resolvedFfprobePath = FFmpegProbe.ResolveFfprobePath(ffmpegDir);

                Assert.Equal(ffmpegPath, wrapper.ExecutablePath);
                Assert.Equal(ffprobePath, resolvedFfprobePath);
            }
            finally
            {
                if (Directory.Exists(ffmpegDir)) Directory.Delete(ffmpegDir, recursive: true);
            }
        }

        private static async Task<int> GetActualVideoFrameCountAsync(string videoPath)
        {
            var ffprobePath = "ffprobe";
            if (File.Exists("D:\\!Tools\\ffmpeg-20151019\\bin\\ffprobe.exe"))
            {
                ffprobePath = "D:\\!Tools\\ffmpeg-20151019\\bin\\ffprobe.exe";
            }

            var psi = new System.Diagnostics.ProcessStartInfo(ffprobePath, $"-v error -select_streams v:0 -show_entries stream=nb_frames -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                return 0;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return int.TryParse(output.Trim(), out var frames) ? frames : 0;
        }

        [Fact]
        public async Task FFmpegWrapper_Rejects_NonPositiveFrameRate()
        {
            var wrapper = new FFmpegWrapper(640, 480, 0);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wrapper.StartAsync(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4")));

            Assert.Contains("FPS", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task FFprobe_Reports_FrameCount_For_Short_Video_Clip()
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be present on PATH or a known local install path for the FFprobe regression test.");

            var inputFile = Path.GetTempFileName();
            var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");

            try
            {
                var payload = new byte[20];
                new Random(42).NextBytes(payload);
                await File.WriteAllBytesAsync(inputFile, payload);

                var service = new YtahdCodecService(new BinaryGridModulator(), new DefaultFFmpegWrapperFactory(ffmpegPath));
                await service.EncodeAsync(new EncodeOptions
                {
                    InputFile = inputFile,
                    OutputVideo = outputVideo,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    VerifyFfmpeg = true
                });

                var actualFrames = await FFmpegProbe.GetVideoFrameCountAsync(outputVideo, ffmpegPath);
                Assert.True(actualFrames > 0, $"Expected ffprobe to report at least one frame for a short valid MP4 stream. Actual: {actualFrames}.");
            }
            finally
            {
                if (File.Exists(inputFile)) File.Delete(inputFile);
                if (File.Exists(outputVideo)) File.Delete(outputVideo);
            }
        }

        [Fact]
        public async Task YtahdCodecService_UsesDurabilityMatrix_WhenEnabled()
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be present on PATH or a known local install path for the service durability integration test.");

            var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
            var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
            var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

            try
            {
                var payload = new byte[256];
                new Random(777).NextBytes(payload);
                await File.WriteAllBytesAsync(inputFile, payload);

                var service = new YtahdCodecService(new BinaryGridModulator(), new DefaultFFmpegWrapperFactory(ffmpegPath));

                await service.EncodeAsync(new EncodeOptions
                {
                    InputFile = inputFile,
                    OutputVideo = outputVideo,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    VerifyFfmpeg = true,
                    UseDurabilityMatrix = true,
                    DurabilityMatrixOptions = new DurabilityMatrixOptions
                    {
                        SymbolSize = 32,
                        GroupSize = 4,
                        ParitySymbolsPerGroup = 1
                    }
                });

                await service.DecodeAsync(new DecodeOptions
                {
                    InputVideo = outputVideo,
                    OutputFile = outputFile,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    VerifyFfmpeg = true,
                    UseDurabilityMatrix = true,
                    DurabilityMatrixOptions = new DurabilityMatrixOptions
                    {
                        SymbolSize = 32,
                        GroupSize = 4,
                        ParitySymbolsPerGroup = 1
                    }
                });

                var decoded = await File.ReadAllBytesAsync(outputFile);
                Assert.Equal(payload, decoded);
            }
            finally
            {
                if (File.Exists(inputFile)) File.Delete(inputFile);
                if (File.Exists(outputVideo)) File.Delete(outputVideo);
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }

        [Fact]
        public async Task RealFfmpeg_Phase1_And_Phase2_RoundTrips_Across_Sizes()
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be present on PATH or a known local install path for the Phase 1/2 real payload-matrix test.");

            var payloadSizes = new[] { 512, 1024, 4096 };
            var modes = new (string Name, IModulator Modulator)[]
            {
                ("phase1", new BinaryGridModulator()),
                ("phase2", new PseudoQamModulator())
            };

            foreach (var (modeName, modulator) in modes)
            {
                foreach (var size in payloadSizes)
                {
                    var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
                    var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
                    var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

                    try
                    {
                        var payload = new byte[size];
                        new Random(1234 + size + modeName.Length).NextBytes(payload);
                        await File.WriteAllBytesAsync(inputFile, payload);

                        var service = new YtahdCodecService(modulator, new DefaultFFmpegWrapperFactory(ffmpegPath));

                        await service.EncodeAsync(new EncodeOptions
                        {
                            InputFile = inputFile,
                            OutputVideo = outputVideo,
                            Width = 640,
                            Height = 480,
                            MacroblockSize = 16,
                            Fps = 30,
                            VerifyFfmpeg = true
                        });

                        var actualFrames = await GetActualVideoFrameCountAsync(outputVideo);
                        Assert.True(actualFrames > 0, $"Expected {modeName} encoded output to contain at least one frame for size {size}. Actual: {actualFrames}.");
                        Assert.True(service.LastEncodeMetrics.TotalFramesWritten > 0, $"Expected {modeName} encode metrics to report written frames for size {size}.");

                        await service.DecodeAsync(new DecodeOptions
                        {
                            InputVideo = outputVideo,
                            OutputFile = outputFile,
                            Width = 640,
                            Height = 480,
                            MacroblockSize = 16,
                            Fps = 30,
                            VerifyFfmpeg = true
                        });

                        var decoded = await File.ReadAllBytesAsync(outputFile);
                        Assert.Equal(payload, decoded);
                        Assert.Equal(payload.Length, service.LastDecodeMetrics.TotalDecodedPayloadBytes);
                        Assert.True(service.LastDecodeMetrics.TotalFramesDecoded > 0, $"Expected {modeName} decode metrics to report decoded frames for size {size}.");
                    }
                    finally
                    {
                        if (File.Exists(inputFile)) File.Delete(inputFile);
                        if (File.Exists(outputVideo)) File.Delete(outputVideo);
                        if (File.Exists(outputFile)) File.Delete(outputFile);
                    }
                }
            }
        }

        private async Task RunRealFfmpegMatrixRoundTripAsync(string modeName, IModulator modulator, params int[] payloadSizes)
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), $"ffmpeg must be present on PATH or a known local install path for the real {modeName} payload-matrix test.");

            foreach (var size in payloadSizes)
            {
                var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
                var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
                var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

                try
                {
                    var payload = new byte[size];
                    new Random(1234 + size + modeName.Length).NextBytes(payload);
                    await File.WriteAllBytesAsync(inputFile, payload);

                    var service = new YtahdCodecService(modulator, new DefaultFFmpegWrapperFactory(ffmpegPath));

                    await service.EncodeAsync(new EncodeOptions
                    {
                        InputFile = inputFile,
                        OutputVideo = outputVideo,
                        Width = 640,
                        Height = 480,
                        MacroblockSize = 16,
                        Fps = 30,
                        VerifyFfmpeg = true
                    });

                    var actualFrames = await GetActualVideoFrameCountAsync(outputVideo);
                    Assert.True(actualFrames > 0, $"Expected {modeName} encoded output to contain at least one frame for size {size}. Actual: {actualFrames}.");
                    Assert.True(service.LastEncodeMetrics.TotalFramesWritten > 0, $"Expected {modeName} encode metrics to report written frames for size {size}.");

                    await service.DecodeAsync(new DecodeOptions
                    {
                        InputVideo = outputVideo,
                        OutputFile = outputFile,
                        Width = 640,
                        Height = 480,
                        MacroblockSize = 16,
                        Fps = 30,
                        VerifyFfmpeg = true
                    });

                    var decoded = await File.ReadAllBytesAsync(outputFile);
                    Assert.Equal(payload, decoded);
                    Assert.Equal(payload.Length, service.LastDecodeMetrics.TotalDecodedPayloadBytes);
                    Assert.True(service.LastDecodeMetrics.TotalFramesDecoded > 0, $"Expected {modeName} decode metrics to report decoded frames for size {size}.");
                }
                finally
                {
                    if (File.Exists(inputFile)) File.Delete(inputFile);
                    if (File.Exists(outputVideo)) File.Delete(outputVideo);
                    if (File.Exists(outputFile)) File.Delete(outputFile);
                }
            }
        }

        [Fact]
        public async Task RealFfmpeg_LargerPayloadMatrix_RoundTrips_For_Phase1()
        {
            await RunRealFfmpegMatrixRoundTripAsync("phase1", new BinaryGridModulator(), 512, 1024);
        }

        [Fact]
        public async Task RealFfmpeg_LargerPayloadMatrix_RoundTrips_For_Phase2()
        {
            await RunRealFfmpegMatrixRoundTripAsync("phase2", new PseudoQamModulator(), 512, 1024);
        }

        [Fact]
        public async Task RealFfmpeg_LargerPayloadMatrix_RoundTrips_For_Phase3()
        {
            await RunRealFfmpegMatrixRoundTripAsync("phase3", new DctModulator(), 64, 128);
        }

        [Fact]
        public async Task RealFfmpeg_LargerPayloadMatrix_RoundTrips_For_Phase4()
        {
            await RunRealFfmpegMatrixRoundTripAsync("phase4", new MotionVectorModulator(), 16, 64, 256);
        }

        [Fact]
        public async Task RealFfmpeg_MotionVectorModulator_SingleFrame_RoundTrip_DoesNotHang()
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be present on PATH or a known local install path for the Phase 4 real-frame regression test.");

            var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
            var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
            var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

            try
            {
                var payload = new byte[16];
                new Random(4201).NextBytes(payload);
                await File.WriteAllBytesAsync(inputFile, payload);

                var service = new YtahdCodecService(new MotionVectorModulator(), new DefaultFFmpegWrapperFactory(ffmpegPath));

                await service.EncodeAsync(new EncodeOptions
                {
                    InputFile = inputFile,
                    OutputVideo = outputVideo,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    VerifyFfmpeg = true
                });

                await service.DecodeAsync(new DecodeOptions
                {
                    InputVideo = outputVideo,
                    OutputFile = outputFile,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    VerifyFfmpeg = true
                });

                var decoded = await File.ReadAllBytesAsync(outputFile);
                Assert.Equal(payload, decoded);
            }
            finally
            {
                if (File.Exists(inputFile)) File.Delete(inputFile);
                if (File.Exists(outputVideo)) File.Delete(outputVideo);
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }

        [Fact]
        public async Task RealFfmpeg_DctModulator_SingleFrame_RoundTrip_DoesNotHang()
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be present on PATH or a known local install path for the DCT real-frame regression test.");

            var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
            var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
            var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

            try
            {
                var payload = new byte[64];
                new Random(4200).NextBytes(payload);
                await File.WriteAllBytesAsync(inputFile, payload);

                var service = new YtahdCodecService(new DctModulator(), new DefaultFFmpegWrapperFactory(ffmpegPath));

                await service.EncodeAsync(new EncodeOptions
                {
                    InputFile = inputFile,
                    OutputVideo = outputVideo,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    VerifyFfmpeg = true
                });

                await service.DecodeAsync(new DecodeOptions
                {
                    InputVideo = outputVideo,
                    OutputFile = outputFile,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    VerifyFfmpeg = true
                });

                var decoded = await File.ReadAllBytesAsync(outputFile);
                Assert.Equal(payload, decoded);
            }
            finally
            {
                if (File.Exists(inputFile)) File.Delete(inputFile);
                if (File.Exists(outputVideo)) File.Delete(outputVideo);
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }

        [Theory]
        [InlineData(16)]
        [InlineData(64)]
        [InlineData(256)]
        public async Task RealFfmpeg_AudioClock_DatagramCount_Matches_VideoLogicalFrameCount(int payloadSize)
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be present on PATH or a known local install path for the audio-clock real-codec test.");

            var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
            var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
            var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

            try
            {
                var payload = new byte[payloadSize];
                new Random(5152 + payloadSize).NextBytes(payload);
                await File.WriteAllBytesAsync(inputFile, payload);

                var service = new YtahdCodecService(new BinaryGridModulator(), new DefaultFFmpegWrapperFactory(ffmpegPath));

                await service.EncodeAsync(new EncodeOptions
                {
                    InputFile = inputFile,
                    OutputVideo = outputVideo,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    UseAudioClock = true,
                    VerifyFfmpeg = true
                });
                int expectedLogicalFrames = service.LastEncodeMetrics.TotalFramesWritten;

                await service.DecodeAsync(new DecodeOptions
                {
                    InputVideo = outputVideo,
                    OutputFile = outputFile,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    UseAudioClock = true,
                    VerifyFfmpeg = true
                });

                var decoded = await File.ReadAllBytesAsync(outputFile);
                Assert.Equal(payload, decoded);

                var audioDatagramCount = service.LastDecodeMetrics.AudioDatagramCount;
                Assert.NotNull(audioDatagramCount);
                // Real AAC re-encode introduces priming delay / frame smearing (see ADR
                // F-20260903-02-audio-fsk-clock-design.md); tolerance matches its documented
                // +-2 frame window rather than requiring exact equality.
                Assert.True(Math.Abs(audioDatagramCount!.Value - expectedLogicalFrames) <= 2,
                    $"Audio-derived datagram count {audioDatagramCount} vs video logical frame count {expectedLogicalFrames} exceeds the +-2 frame tolerance.");
            }
            finally
            {
                if (File.Exists(inputFile)) File.Delete(inputFile);
                if (File.Exists(outputVideo)) File.Delete(outputVideo);
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }

        [Fact]
        public async Task RealFfmpeg_AudioClock_MuxesAndExtracts_PcmAudioTrack()
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be present on PATH or a known local install path for the audio-clock real-codec test.");

            var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
            var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
            var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

            try
            {
                var payload = new byte[64];
                new Random(5150).NextBytes(payload);
                await File.WriteAllBytesAsync(inputFile, payload);

                var wrapperFactory = new DefaultFFmpegWrapperFactory(ffmpegPath);
                var service = new YtahdCodecService(new BinaryGridModulator(), wrapperFactory);

                await service.EncodeAsync(new EncodeOptions
                {
                    InputFile = inputFile,
                    OutputVideo = outputVideo,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    UseAudioClock = true,
                    VerifyFfmpeg = true
                });

                var decodeWrapper = wrapperFactory.CreateForDecode();
                var pcm = await decodeWrapper.TryExtractAudioPcmAsync(outputVideo);
                Assert.NotNull(pcm);
                Assert.True(pcm!.Length > 0, "Expected a non-empty PCM audio track after muxing.");

                // Decode must still succeed unaffected by the added audio track.
                await service.DecodeAsync(new DecodeOptions
                {
                    InputVideo = outputVideo,
                    OutputFile = outputFile,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    VerifyFfmpeg = true
                });

                var decoded = await File.ReadAllBytesAsync(outputFile);
                Assert.Equal(payload, decoded);
            }
            finally
            {
                if (File.Exists(inputFile)) File.Delete(inputFile);
                if (File.Exists(outputVideo)) File.Delete(outputVideo);
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }

        [Fact]
        public async Task RealFfmpeg_NoAudioClock_HasNoExtractableAudioTrack()
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be present on PATH or a known local install path for the audio-clock real-codec test.");

            var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
            var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");

            try
            {
                var payload = new byte[64];
                new Random(5151).NextBytes(payload);
                await File.WriteAllBytesAsync(inputFile, payload);

                var wrapperFactory = new DefaultFFmpegWrapperFactory(ffmpegPath);
                var service = new YtahdCodecService(new BinaryGridModulator(), wrapperFactory);

                await service.EncodeAsync(new EncodeOptions
                {
                    InputFile = inputFile,
                    OutputVideo = outputVideo,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    VerifyFfmpeg = true
                });

                var decodeWrapper = wrapperFactory.CreateForDecode();
                var pcm = await decodeWrapper.TryExtractAudioPcmAsync(outputVideo);
                Assert.True(pcm == null || pcm.Length == 0, "Expected no audio track when UseAudioClock is disabled (the default).");
            }
            finally
            {
                if (File.Exists(inputFile)) File.Delete(inputFile);
                if (File.Exists(outputVideo)) File.Delete(outputVideo);
            }
        }

        [Fact]
        public async Task RealFfmpeg_RoundTrip_EncodeDecode_Succeeds()
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be present on PATH or a known local install path for the real smoke test.");

            var inputFile = Path.GetTempFileName();
            var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
            var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");

            try
            {
                var payload = new byte[2048];
                new Random(321).NextBytes(payload);
                await File.WriteAllBytesAsync(inputFile, payload);

                var service = new YtahdCodecService(new BinaryGridModulator(), new DefaultFFmpegWrapperFactory(ffmpegPath));

                await service.EncodeAsync(new EncodeOptions
                {
                    InputFile = inputFile,
                    OutputVideo = outputVideo,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    VerifyFfmpeg = true
                });

                var actualFrames = await GetActualVideoFrameCountAsync(outputVideo);
                var encodeMetrics = service.LastEncodeMetrics;
                Assert.True(actualFrames > 0, $"Expected encoded video to contain at least one frame. Actual frames reported by ffprobe: {actualFrames}.");
                Assert.True(encodeMetrics.TotalFramesWritten > 0, $"Expected encode metrics to report written frames. Actual: {encodeMetrics.TotalFramesWritten}.");
                Assert.Equal(payload.Length, encodeMetrics.InputPayloadBytes);
                Assert.Equal(actualFrames, encodeMetrics.TotalFramesInVideo);

                await service.DecodeAsync(new DecodeOptions
                {
                    InputVideo = outputVideo,
                    OutputFile = outputFile,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    VerifyFfmpeg = true
                });

                var decoded = await File.ReadAllBytesAsync(outputFile);
                Assert.Equal(payload, decoded);
                Assert.Equal(payload.Length, service.LastDecodeMetrics.TotalDecodedPayloadBytes);
                Assert.True(service.LastDecodeMetrics.TotalFramesDecoded > 0, $"Expected decode metrics to report decoded frames. Actual: {service.LastDecodeMetrics.TotalFramesDecoded}.");
            }
            finally
            {
                if (File.Exists(inputFile)) File.Delete(inputFile);
                if (File.Exists(outputVideo)) File.Delete(outputVideo);
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }

        [Fact]
        public async Task RealFfmpeg_RoundTrip_EncodeDecode_Succeeds_For_PseudoQam()
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be present on PATH or a known local install path for the real smoke test.");

            var inputFile = Path.GetTempFileName();
            var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
            var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");

            try
            {
                var payload = new byte[512];
                new Random(77).NextBytes(payload);
                await File.WriteAllBytesAsync(inputFile, payload);

                var service = new YtahdCodecService(new PseudoQamModulator(), new DefaultFFmpegWrapperFactory(ffmpegPath));

                await service.EncodeAsync(new EncodeOptions
                {
                    InputFile = inputFile,
                    OutputVideo = outputVideo,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    VerifyFfmpeg = true
                });

                await service.DecodeAsync(new DecodeOptions
                {
                    InputVideo = outputVideo,
                    OutputFile = outputFile,
                    Width = 640,
                    Height = 480,
                    MacroblockSize = 16,
                    Fps = 30,
                    VerifyFfmpeg = true
                });

                var decoded = await File.ReadAllBytesAsync(outputFile);
                Assert.Equal(payload, decoded);
            }
            finally
            {
                if (File.Exists(inputFile)) File.Delete(inputFile);
                if (File.Exists(outputVideo)) File.Delete(outputVideo);
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }

        [Fact]
        public async Task RealCli_RoundTrip_EncodeDecode_Succeeds_For_PseudoQam()
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be present on PATH or a known local install path for the CLI smoke test.");

            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var cliProject = Path.Combine(repoRoot, "YTAHD.Cli", "YTAHD.Cli.csproj");
            Assert.True(File.Exists(cliProject), $"CLI project not found at '{cliProject}'.");

            var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
            var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
            var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

            try
            {
                var payload = new byte[256];
                new Random(1234).NextBytes(payload);
                await File.WriteAllBytesAsync(inputFile, payload);

                using var encodeProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"run --project \"{cliProject}\" -- encode \"{inputFile}\" \"{outputVideo}\" --modulator phase2 --ffmpeg-path \"{ffmpegPath}\" --width 640 --height 480 --fps 30",
                        WorkingDirectory = repoRoot,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                encodeProcess.Start();
                var encodeStdOut = await encodeProcess.StandardOutput.ReadToEndAsync();
                var encodeStdErr = await encodeProcess.StandardError.ReadToEndAsync();
                await encodeProcess.WaitForExitAsync();

                Assert.True(encodeProcess.ExitCode == 0, $"Phase 2 CLI encode failed. StdOut: {encodeStdOut} StdErr: {encodeStdErr}");

                using var decodeProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"run --project \"{cliProject}\" -- decode \"{outputVideo}\" \"{outputFile}\" --modulator phase2 --ffmpeg-path \"{ffmpegPath}\"",
                        WorkingDirectory = repoRoot,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                decodeProcess.Start();
                var decodeStdOut = await decodeProcess.StandardOutput.ReadToEndAsync();
                var decodeStdErr = await decodeProcess.StandardError.ReadToEndAsync();
                await decodeProcess.WaitForExitAsync();

                Assert.True(decodeProcess.ExitCode == 0, $"Phase 2 CLI decode failed. StdOut: {decodeStdOut} StdErr: {decodeStdErr}");

                var decoded = await File.ReadAllBytesAsync(outputFile);
                Assert.Equal(payload, decoded);
            }
            finally
            {
                if (File.Exists(inputFile)) File.Delete(inputFile);
                if (File.Exists(outputVideo)) File.Delete(outputVideo);
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }

        [Fact]
        public async Task RealCli_RoundTrip_EncodeDecode_Succeeds_For_Phase1()
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be present for the Phase 1 CLI guardrail.");

            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var cliProject = Path.Combine(repoRoot, "YTAHD.Cli", "YTAHD.Cli.csproj");
            Assert.True(File.Exists(cliProject), $"CLI project not found at '{cliProject}'.");

            var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
            var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
            var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

            try
            {
                var payload = new byte[1024];
                new Random(4321).NextBytes(payload);
                await File.WriteAllBytesAsync(inputFile, payload);

                using var encodeProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"run --project \"{cliProject}\" -- encode \"{inputFile}\" \"{outputVideo}\" --modulator phase1 --ffmpeg-path \"{ffmpegPath}\" --width 640 --height 480 --fps 30",
                        WorkingDirectory = repoRoot,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                encodeProcess.Start();
                var encodeStdOut = await encodeProcess.StandardOutput.ReadToEndAsync();
                var encodeStdErr = await encodeProcess.StandardError.ReadToEndAsync();
                await encodeProcess.WaitForExitAsync();

                Assert.True(encodeProcess.ExitCode == 0, $"Phase 1 CLI encode failed. StdOut: {encodeStdOut} StdErr: {encodeStdErr}");

                using var decodeProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"run --project \"{cliProject}\" -- decode \"{outputVideo}\" \"{outputFile}\" --modulator phase1 --ffmpeg-path \"{ffmpegPath}\"",
                        WorkingDirectory = repoRoot,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                decodeProcess.Start();
                var decodeStdOut = await decodeProcess.StandardOutput.ReadToEndAsync();
                var decodeStdErr = await decodeProcess.StandardError.ReadToEndAsync();
                await decodeProcess.WaitForExitAsync();

                Assert.True(decodeProcess.ExitCode == 0, $"Phase 1 CLI decode failed. StdOut: {decodeStdOut} StdErr: {decodeStdErr}");

                var decoded = await File.ReadAllBytesAsync(outputFile);
                Assert.Equal(payload, decoded);
            }
            finally
            {
                if (File.Exists(inputFile)) File.Delete(inputFile);
                if (File.Exists(outputVideo)) File.Delete(outputVideo);
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }

        [Fact]
        public async Task EncodeAsync_WritesData_UsingServiceApi()
        {
            var tmpIn = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[64];
                new Random(123).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(128, 64, 30);
                var service = new YtahdCodecService(mod, new FakeFFmpegWrapperFactory(fake));

                await service.EncodeAsync(new EncodeOptions
                {
                    InputFile = tmpIn,
                    OutputVideo = "out.mp4",
                    Width = 128,
                    Height = 64,
                    MacroblockSize = 1,
                    Fps = 30,
                    VerifyFfmpeg = true
                });

                Assert.True(fake.WrittenBytes > 0);
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }

        [Fact]
        public async Task DecodeFromRgbStreamAsync_RoundTrips_UsingServiceApi()
        {
            var tmpIn = Path.GetTempFileName();
            var tmpOut = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[256];
                new Random(222).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(128, 64, 30);
                var service = new YtahdCodecService(mod, new FakeFFmpegWrapperFactory(fake));

                await service.EncodeAsync(new EncodeOptions
                {
                    InputFile = tmpIn,
                    OutputVideo = "out.mp4",
                    Width = 128,
                    Height = 64,
                    MacroblockSize = 1,
                    Fps = 30,
                    VerifyFfmpeg = false
                });

                var raw = fake.Process?.Buffer;
                Assert.NotNull(raw);
                raw.Position = 0;

                await service.DecodeFromRgbStreamAsync(new DecodeRgbOptions
                {
                    RgbStream = raw,
                    Width = 128,
                    Height = 64,
                    MacroblockSize = 1,
                    ExpectedOutputBytes = data.Length,
                    OutputFile = tmpOut
                });

                var outData = await File.ReadAllBytesAsync(tmpOut);
                Assert.Equal(data, outData);
            }
            finally
            {
                File.Delete(tmpIn);
                File.Delete(tmpOut);
            }
        }
    }
}
