using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Application;
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
