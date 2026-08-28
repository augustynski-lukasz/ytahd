namespace YTAHD.Core.Application;

public sealed class DecodeOptions
{
    public required string InputVideo { get; init; }
    public required string OutputFile { get; init; }
    public bool VerifyFfmpeg { get; init; } = true;
}
