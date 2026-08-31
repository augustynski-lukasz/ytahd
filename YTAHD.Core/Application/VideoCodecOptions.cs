using YTAHD.Core.Core;

namespace YTAHD.Core.Application;

public class VideoCodecOptions
{
    public int MacroblockSize { get; init; } = 16;
    public int Width { get; init; } = 3840;
    public int Height { get; init; } = 2160;
    public int Fps { get; init; } = 60;
    public bool VerifyFfmpeg { get; init; } = true;
    public bool UseDurabilityMatrix { get; init; } = false;
    public DurabilityMatrixOptions? DurabilityMatrixOptions { get; init; }
}
