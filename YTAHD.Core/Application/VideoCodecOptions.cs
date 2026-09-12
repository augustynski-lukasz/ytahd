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

    /// <summary>
    /// Adds an audio FSK datagram clock (see ADR F-20260903-02-audio-fsk-clock-design.md).
    /// Optional end-to-end: decode of videos without an audio track is unaffected.
    /// </summary>
    public bool UseAudioClock { get; init; } = false;

    /// <summary>
    /// Requested degree of parallelism for the encode/decode frame pipeline, resolved by
    /// <see cref="ParallelismPolicy"/> (see docs/decisions/CR-20260912-01-degree-of-parallelism-controls.md).
    /// <c>0</c> is the unconfigured default: frames are processed sequentially and modulator/decoder
    /// inner loops still use the machine's processors. <see cref="ParallelismPolicy.Auto"/> (-1)
    /// chooses a conservative worker count, higher values request that many frame workers
    /// (clamped to <see cref="ParallelismPolicy.MaxWorkerLimit"/>), and <c>1</c> runs fully serial
    /// — frame workers and inner loops — for deterministic debugging.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 0;
}
