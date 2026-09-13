using YTAHD.Core.Core;

namespace YTAHD.Core.Application;

/// <summary>
/// FFmpeg video encoder selection (CR-20260912-06 stage 1). The CPU <see cref="LibX264"/>
/// baseline is the default and the durability reference; GPU profiles are opt-in and must pass
/// the real-codec durability bar (Workstream E integrity verification) before being treated as
/// production-ready.
/// </summary>
public enum VideoEncoder
{
    /// <summary>CPU libx264, the production baseline (CRF 23).</summary>
    LibX264 = 0,

    /// <summary>Intel Quick Sync H.264. Hardware-validated on this machine.</summary>
    H264Qsv = 1,

    /// <summary>NVIDIA NVENC H.264. Declared but experimental: not hardware-validated here
    /// (NVENC session unavailable on the GTX 1060 driver/build).</summary>
    H264Nvenc = 2,

    /// <summary>AMD AMF H.264. Declared but experimental: no AMD GPU on this machine.</summary>
    H264Amf = 3
}

/// <summary>
/// Decode-side hardware acceleration mode (CR-20260912-06 stage 1). Exposed for experiments;
/// raw RGB24 must still reach the decoder, so decode gains are expected to be smaller than
/// encode gains.
/// </summary>
public enum HardwareAcceleration
{
    /// <summary>No decode-side acceleration (default).</summary>
    None = 0,

    /// <summary>Intel Quick Sync Video decode acceleration.</summary>
    Qsv = 1,

    /// <summary>NVIDIA CUDA decode acceleration (experimental).</summary>
    Cuda = 2,

    /// <summary>Direct3D 11 video acceleration (experimental).</summary>
    D3D11Va = 3
}

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
    /// Video encoder for the FFmpeg container layer. Defaults to the CPU
    /// <see cref="VideoEncoder.LibX264"/> baseline; GPU encoders are opt-in
    /// (CR-20260912-06 stage 1).
    /// </summary>
    public VideoEncoder VideoEncoder { get; init; } = VideoEncoder.LibX264;

    /// <summary>
    /// Decode-side hardware acceleration mode. Defaults to
    /// <see cref="HardwareAcceleration.None"/>; exposed for experiments
    /// (CR-20260912-06 stage 1).
    /// </summary>
    public HardwareAcceleration HardwareAcceleration { get; init; } = HardwareAcceleration.None;

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
