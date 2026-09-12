using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace YTAHD.Core.Infrastructure
{
    public static class FFmpegProbe
    {
        public static string? ResolveFfprobePath(string? ffmpegExecutablePath = null)
        {
            if (!string.IsNullOrWhiteSpace(ffmpegExecutablePath))
            {
                var resolvedFfmpegPath = FfmpegToolPathResolver.ResolveFfmpegPath(ffmpegExecutablePath);
                var ffmpegDir = Path.GetDirectoryName(resolvedFfmpegPath);
                if (!string.IsNullOrWhiteSpace(ffmpegDir))
                {
                    var candidates = new[]
                    {
                        Path.Combine(ffmpegDir, "ffprobe.exe"),
                        Path.Combine(ffmpegDir, "ffprobe"),
                    };

                    foreach (var candidate in candidates)
                    {
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }

            var pathCandidates = new[]
            {
                "ffprobe.exe",
                "ffprobe",
            };

            foreach (var candidate in pathCandidates)
            {
                try
                {
                    var psi = new ProcessStartInfo(candidate, "-version")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using var child = ChildProcessScope.Start(psi, "Failed to start ffprobe.");
                    var process = child.Process;

                    // Both pipes are redirected, so both must be consumed before waiting;
                    // ffprobe -version is short, but an unwritten rule here would be a latent
                    // deadlock the moment a build emits a longer banner.
                    var stdout = ChildProcessPipes.ReadToEndAsync(process.StandardOutput);
                    var stderr = ChildProcessPipes.ReadToEndAsync(process.StandardError);
                    process.WaitForExit();
                    System.Threading.Tasks.Task.WaitAll(stdout, stderr);
                    if (process.ExitCode == 0)
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Try next candidate.
                }
            }

            return null;
        }

        public static int ParseFrameCountFromProbeOutput(string? output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return 0;
            }

            var values = output
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var value in values)
            {
                var trimmed = value.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nbFrames) && nbFrames > 0)
                {
                    return nbFrames;
                }
            }

            double? duration = null;
            double? fps = null;

            foreach (var value in values)
            {
                var trimmed = value.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                if (trimmed.Contains("avg_frame_rate", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.Split('=');
                    if (parts.Length == 2)
                    {
                        var rate = parts[1].Trim();
                        if (rate.Contains('/'))
                        {
                            var rateParts = rate.Split('/');
                            if (rateParts.Length == 2 && double.TryParse(rateParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) && double.TryParse(rateParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator > 0)
                            {
                                fps = numerator / denominator;
                            }
                        }
                        else if (double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var rateValue) && rateValue > 0)
                        {
                            fps = rateValue;
                        }
                    }
                }
                else if (trimmed.Contains("DURATION", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("duration", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.Split('=');
                    if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var durationValue) && durationValue >= 0)
                    {
                        duration = durationValue;
                    }
                }
            }

            if (duration.HasValue && fps.HasValue && fps.Value > 0)
            {
                return (int)Math.Round(duration.Value * fps.Value);
            }

            return 0;
        }

        public static async Task<int> GetVideoFrameCountAsync(string videoPath, string? ffmpegExecutablePath = null)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            {
                return 0;
            }

            var ffprobePath = ResolveFfprobePath(ffmpegExecutablePath);
            if (string.IsNullOrWhiteSpace(ffprobePath))
            {
                return 0;
            }

            var psi = new ProcessStartInfo(ffprobePath,
                $"-v error -select_streams v:0 -show_entries stream=nb_frames,avg_frame_rate,duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                using var child = ChildProcessScope.Start(psi, "Failed to start ffprobe.");
                var process = child.Process;

                var stderrDrain = ChildProcessPipes.DrainAsync(process.StandardError);
                var output = await ChildProcessPipes.ReadToEndAsync(process.StandardOutput);
                await process.WaitForExitAsync();
                await stderrDrain;
                return ParseFrameCountFromProbeOutput(output);
            }
            catch
            {
                return 0;
            }
        }
    }
}
