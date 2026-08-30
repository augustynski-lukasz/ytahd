namespace YTAHD.Core.Application;

public sealed class DecodeOptions
{
    public required string InputVideo { get; init; }
    public required string OutputFile { get; init; }
    public int MacroblockSize { get; init; } = 16;
    public int Width { get; init; } = 3840;
    public int Height { get; init; } = 2160;
    public int Fps { get; init; } = 60;
    public bool VerifyFfmpeg { get; init; } = true;
}
