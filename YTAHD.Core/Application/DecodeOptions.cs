using System;
using YTAHD.Core.Core;

namespace YTAHD.Core.Application;

public sealed class DecodeOptions : VideoCodecOptions
{
    public required string InputVideo { get; init; }
    public required string OutputFile { get; init; }
    public IProgress<DecodeProgress>? Progress { get; init; }

    /// <summary>
    /// Stream the output to disk incrementally (RM-10, F-20260914-02): on the durability path
    /// each finalized group is written as soon as it is complete, so decode memory stays
    /// bounded by the parity-group size instead of the payload size. Default false preserves
    /// the historical batch behavior.
    /// </summary>
    public bool StreamingOutput { get; init; } = false;
}
