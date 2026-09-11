using System;
using YTAHD.Core.Core;

namespace YTAHD.Core.Application;

public sealed class DecodeOptions : VideoCodecOptions
{
    public required string InputVideo { get; init; }
    public required string OutputFile { get; init; }
    public IProgress<DecodeProgress>? Progress { get; init; }
}
