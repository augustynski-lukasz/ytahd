using System;
using System.Diagnostics;
using System.IO;

namespace YTAHD.Tests
{
    /// <summary>
    /// Shared ffmpeg discovery for the real-codec integration tests. Set the
    /// <c>YTAHD_FFMPEG_PATH</c> environment variable (file or directory) to pin the suite to a
    /// specific build; otherwise the known local installs and finally PATH are probed.
    /// </summary>
    internal static class TestFfmpeg
    {
        /// <summary>Returns a usable ffmpeg executable path, or null when none was found.</summary>
        public static string? GetAvailableFfmpegPath()
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("YTAHD_FFMPEG_PATH"),
                @"D:\!Tools\ffmpeg-20151019\bin\ffmpeg.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
                "ffmpeg.exe",
                "ffmpeg"
            };

            foreach (var candidate in candidates)
            {
                var resolved = ResolveCandidate(candidate);
                if (resolved != null)
                {
                    return resolved;
                }
            }

            return null;
        }

        private static string? ResolveCandidate(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            // An explicit directory is resolved to ffmpeg.exe inside it.
            if (Directory.Exists(candidate))
            {
                var inDirectory = Path.Combine(candidate, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
                return File.Exists(inDirectory) ? inDirectory : null;
            }

            if (Path.IsPathRooted(candidate) || candidate.Contains(Path.DirectorySeparatorChar))
            {
                return File.Exists(candidate) ? candidate : null;
            }

            return IsRunnableFromPath(candidate) ? candidate : null;
        }

        private static bool IsRunnableFromPath(string executable)
        {
            try
            {
                var psi = new ProcessStartInfo(executable, "-version")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return false;
                }

                process.StandardInput.Close();

                process.WaitForExit();
                return process.ExitCode == 0;
            }
            catch
            {
                // Not on PATH (or not executable); try the next candidate.
                return false;
            }
        }
    }
}
