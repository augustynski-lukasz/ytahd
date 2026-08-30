using YTAHD.Core.Infrastructure;

namespace YTAHD.Core.Application;

public sealed class DefaultFFmpegWrapperFactory : IFFmpegWrapperFactory
{
    private readonly string? _ffmpegPath;

    public DefaultFFmpegWrapperFactory(string? ffmpegPath = null)
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? null : ffmpegPath;
    }

    public IFFmpegWrapper CreateForEncode(int width, int height, int fps, string? ffmpegPath = null)
    {
        return new FFmpegWrapper(width, height, fps, ffmpegPath ?? _ffmpegPath);
    }

    public IFFmpegWrapper CreateForDecode(string? ffmpegPath = null)
    {
        return new FFmpegWrapper(ffmpegExecutablePath: ffmpegPath ?? _ffmpegPath);
    }
}
