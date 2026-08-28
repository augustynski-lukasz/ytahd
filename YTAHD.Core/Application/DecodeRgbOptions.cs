using System.IO;

namespace YTAHD.Core.Application;

public sealed class DecodeRgbOptions
{
    public required Stream RgbStream { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int MacroblockSize { get; init; }
    public required int ExpectedOutputBytes { get; init; }
    public required string OutputFile { get; init; }
}
