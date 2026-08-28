using YTAHD.Core.Infrastructure;

namespace YTAHD.Core.Application;

public sealed class DefaultFFmpegWrapperFactory : IFFmpegWrapperFactory
{
    public IFFmpegWrapper CreateForEncode(int width, int height, int fps)
    {
        return new FFmpegWrapper(width, height, fps);
    }

    public IFFmpegWrapper CreateForDecode()
    {
        return new FFmpegWrapper();
    }
}
