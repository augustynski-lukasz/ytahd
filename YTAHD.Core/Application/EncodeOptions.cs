namespace YTAHD.Core.Application;

public sealed class EncodeOptions : VideoCodecOptions
{
    public required string InputFile { get; init; }
    public required string OutputVideo { get; init; }
}
