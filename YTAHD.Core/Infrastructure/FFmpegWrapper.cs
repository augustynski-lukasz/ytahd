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
        // Must match YTAHD.Core.Audio.FskGenerator.SampleRate.
        private const int AudioSampleRate = 44100;

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
            _ffmpegExecutablePath = FfmpegToolPathResolver.ResolveFfmpegPath(ffmpegExecutablePath);
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

                using var child = ChildProcessScope.Start(psi, "Failed to start ffmpeg.");
                var p = child.Process;
                var stdoutDrain = ChildProcessPipes.DrainAsync(p.StandardOutput);
                var stderrDrain = ChildProcessPipes.DrainAsync(p.StandardError);
                await p.WaitForExitAsync();
                await Task.WhenAll(stdoutDrain, stderrDrain);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public async Task<IFFmpegProcess> StartAsync(string outputPath, string? audioPcmFilePath = null)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path is required.", nameof(outputPath));

            if (_fps <= 0)
                throw new InvalidOperationException($"FFmpeg rawvideo FPS must be greater than zero. Current value: {_fps}.");

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            // Use a container-compatible H.264 output for real mp4 smoke tests. The project keeps its
            // custom binary frame protocol and decoder tolerance; the real FFmpeg layer only needs a valid
            // codec/container pair so the encoded stream can be decoded back for end-to-end validation.
            string args;
            if (!string.IsNullOrWhiteSpace(audioPcmFilePath))
            {
                // Audio input is a second, already-fully-written raw PCM file (mono 16-bit,
                // see AudioSampleRate) rather than a second stdin stream; ffmpeg only accepts
                // one pipe input per process. -shortest trims either track to the shorter one
                // in case audio/video duration rounding differs by a fraction of a frame.
                // -strict -2 is required by some older ffmpeg builds where the native AAC
                // encoder is still marked experimental.
                args = $"-y -f rawvideo -pix_fmt rgb24 -s {_width}x{_height} -r {_fps} -i - " +
                       $"-f s16le -ar {AudioSampleRate} -ac 1 -i \"{audioPcmFilePath}\" " +
                       $"-c:v libx264 -pix_fmt yuv420p -c:a aac -strict -2 -shortest \"{outputPath}\"";
            }
            else
            {
                args = $"-y -f rawvideo -pix_fmt rgb24 -s {_width}x{_height} -r {_fps} -i - -c:v libx264 -pix_fmt yuv420p -an \"{outputPath}\"";
            }

            var psi = new ProcessStartInfo(_ffmpegExecutablePath, args)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                // stdout is deliberately not redirected: media goes to the output file, and a
                // redirected-but-unread pipe is a deadlock waiting for a buffer to fill.
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg");

            // Consume stderr concurrently, on a dedicated thread: reading this pipe is a blocking
            // operation, so draining it from the thread pool would park a pool thread for the whole
            // lifetime of the encode and starve the pool the pipeline itself runs on.
            _ = ChildProcessPipes.DrainAsync(_process.StandardError);

            return new ProcessWrapper(_process);
        }

        public async Task<byte[]?> TryExtractAudioPcmAsync(string inputVideo)
        {
            if (string.IsNullOrWhiteSpace(inputVideo) || !File.Exists(inputVideo))
                return null;

            try
            {
                var args = $"-v error -i \"{inputVideo}\" -vn -f s16le -ar {AudioSampleRate} -ac 1 -";
                var psi = new ProcessStartInfo(_ffmpegExecutablePath, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var child = ChildProcessScope.Start(psi, "Failed to start ffmpeg.");
                var process = child.Process;

                // The pipe is pumped on a dedicated thread; reading it from the pool would park a
                // pool thread for the whole duration of the extraction.
                using var stdout = new ChildPipeStream(process.StandardOutput.BaseStream);
                using var pcmStream = new MemoryStream();
                var stderrDrain = ChildProcessPipes.DrainAsync(process.StandardError);
                await stdout.CopyToAsync(pcmStream);
                await process.WaitForExitAsync();
                await stderrDrain;

                if (process.ExitCode != 0 || pcmStream.Length == 0)
                    return null;

                return pcmStream.ToArray();
            }
            catch
            {
                return null;
            }
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
                    // Bounded wait, not a bare Kill: an encode that was cancelled mid-flight must not leave
                    // the child still writing the output video once the call has returned to the caller.
                    ChildProcessLifetime.KillAndWait(_p);
                }
                finally { _p.Dispose(); }
            }
        }

        public void Dispose()
        {
            try
            {
                if (_process != null)
                {
                    ChildProcessLifetime.KillAndWait(_process);
                }
            }
            finally
            {
                _process?.Dispose();
            }
        }
    }
}
