using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace YTAHD.Core.Infrastructure
{
    /// <summary>
    /// Minimal wrapper around an ffmpeg process that accepts raw frames over stdin.
    /// This intentionally keeps dependencies to System.* so the project compiles without extra NuGet packages.
    /// </summary>
    public sealed class FFmpegWrapper : IFFmpegWrapper, IDisposable
    {
        private Process? _process;
        private readonly int _width;
        private readonly int _height;
        private readonly int _fps;
        private readonly string _ffmpegExecutablePath;

        public FFmpegWrapper(int width = 3840, int height = 2160, int fps = 60, string? ffmpegExecutablePath = null)
        {
            _width = width;
            _height = height;
            _fps = fps;
            _ffmpegExecutablePath = string.IsNullOrWhiteSpace(ffmpegExecutablePath) ? "ffmpeg" : ffmpegExecutablePath;
        }

        public string ExecutablePath => _ffmpegExecutablePath;

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var psi = new ProcessStartInfo(_ffmpegExecutablePath, "-version")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var p = Process.Start(psi);
                if (p == null) return false;
                await p.WaitForExitAsync();
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public async Task<IFFmpegProcess> StartAsync(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path is required.", nameof(outputPath));

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            // Use a lossless codec here: libx264 introduces lossy quantization and will corrupt the 
            // binary modulation payload before the custom decoder can recover it.
            var args = $"-y -f rawvideo -pix_fmt rgb24 -s {_width}x{_height} -r {_fps} -i - -c:v ffv1 -an \"{outputPath}\"";

            var psi = new ProcessStartInfo(_ffmpegExecutablePath, args)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg");

            // Optionally read stderr to observe ffmpeg progress asynchronously
            _ = Task.Run(async () =>
            {
                try
                {
                    using var sr = _process.StandardError;
                    while (!sr.EndOfStream)
                    {
                        var line = await sr.ReadLineAsync();
                        if (line is not null)
                            Console.Error.WriteLine(line);
                    }
                }
                catch { }
            });

            return new ProcessWrapper(_process);
        }

        private class ProcessWrapper : IFFmpegProcess
        {
            private readonly Process _p;
            public ProcessWrapper(Process p) => _p = p;
            public Stream StandardInput => _p.StandardInput.BaseStream;
            public Task WaitForExitAsync() => _p.WaitForExitAsync();
            public void Dispose()
            {
                try
                {
                    if (!_p.HasExited)
                    {
                        _p.Kill(true);
                    }
                }
                catch { }
                finally { _p.Dispose(); }
            }
        }

        public void Dispose()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill(true);
                }
                _process?.Dispose();
            }
            catch { }
        }
    }
}
