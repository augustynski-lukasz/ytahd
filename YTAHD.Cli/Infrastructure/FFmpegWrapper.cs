using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace YTAHD.Cli.Infrastructure
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

        public FFmpegWrapper(int width = 3840, int height = 2160, int fps = 60)
        {
            _width = width;
            _height = height;
            _fps = fps;
        }

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var psi = new ProcessStartInfo("ffmpeg", "-version")
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
            // Example ffmpeg args for rawvideo input; callers should adapt pixel format/resolution.
            var args = $"-f rawvideo -pix_fmt rgb24 -s {_width}x{_height} -r {_fps} -i - -c:v libx264 -pix_fmt yuv420p \"{outputPath}\"";

            var psi = new ProcessStartInfo("ffmpeg", args)
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
