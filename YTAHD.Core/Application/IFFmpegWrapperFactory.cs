using YTAHD.Core.Infrastructure;

namespace YTAHD.Core.Application;

public interface IFFmpegWrapperFactory
{
    IFFmpegWrapper CreateForEncode(int width, int height, int fps);
    IFFmpegWrapper CreateForDecode();
}
