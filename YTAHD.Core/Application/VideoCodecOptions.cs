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
}
