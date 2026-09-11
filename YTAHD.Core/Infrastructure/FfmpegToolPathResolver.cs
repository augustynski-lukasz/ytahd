using System.IO;

namespace YTAHD.Core.Infrastructure
{
    internal static class FfmpegToolPathResolver
    {
        public static string ResolveFfmpegPath(string? ffmpegPath)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                return "ffmpeg";
            }

            var trimmed = ffmpegPath.Trim();
            if (!Directory.Exists(trimmed))
            {
                return trimmed;
            }

            foreach (var fileName in new[] { "ffmpeg.exe", "ffmpeg" })
            {
                var candidate = Path.Combine(trimmed, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return trimmed;
        }
    }
}