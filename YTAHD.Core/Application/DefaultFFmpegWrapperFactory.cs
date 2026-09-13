using YTAHD.Core.Infrastructure;

namespace YTAHD.Core.Application;

public sealed class DefaultFFmpegWrapperFactory : IFFmpegWrapperFactory
{
    private readonly string? _ffmpegPath;
    private readonly VideoEncoder _videoEncoder;

    public DefaultFFmpegWrapperFactory(string? ffmpegPath = null, VideoEncoder videoEncoder = VideoEncoder.LibX264)
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? null : ffmpegPath;
        _videoEncoder = videoEncoder;
    }

    public IFFmpegWrapper CreateForEncode(int width, int height, int fps, string? ffmpegPath = null, VideoEncoder? videoEncoder = null)
    {
        return new FFmpegWrapper(width, height, fps, ffmpegPath ?? _ffmpegPath, videoEncoder ?? _videoEncoder);
    }

    public IFFmpegWrapper CreateForDecode(string? ffmpegPath = null)
    {
        return new FFmpegWrapper(ffmpegExecutablePath: ffmpegPath ?? _ffmpegPath);
    }
}
