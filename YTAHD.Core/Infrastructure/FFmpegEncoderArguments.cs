using System;
using YTAHD.Core.Application;

namespace YTAHD.Core.Infrastructure;

/// <summary>
/// Produces the FFmpeg video-encoder arguments for the container layer
/// (CR-20260912-06 stage 2). Encoder-specific flags live here rather than in the CLI so the
/// CPU baseline and each GPU profile are owned, testable, and matched as closely as possible
/// to the CRF-23 durability baseline.
/// </summary>
public static class FFmpegEncoderArguments
{
    /// <summary>
    /// Returns the video-encoder arguments for <paramref name="encoder"/>, e.g.
    /// <c>-c:v libx264 -crf 23 -pix_fmt yuv420p</c>. The CPU baseline keeps the exact
    /// arguments the pipeline has always used.
    /// </summary>
    public static string For(VideoEncoder encoder)
    {
        return encoder switch
        {
            VideoEncoder.LibX264 => "-c:v libx264 -crf 23 -pix_fmt yuv420p",
            VideoEncoder.H264Qsv => "-c:v h264_qsv -global_quality 23 -pix_fmt yuv420p",
            VideoEncoder.H264Nvenc => "-c:v h264_nvenc -preset p4 -cq 23 -pix_fmt yuv420p",
            VideoEncoder.H264Amf => "-c:v h264_amf -quality balanced -qp_i 23 -qp_p 23 -pix_fmt yuv420p",
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Unknown video encoder.")
        };
    }

    /// <summary>
    /// Returns the FFmpeg codec name for <paramref name="encoder"/> (e.g. <c>libx264</c>),
    /// used by capability probing and diagnostics.
    /// </summary>
    public static string CodecName(VideoEncoder encoder)
    {
        return encoder switch
        {
            VideoEncoder.LibX264 => "libx264",
            VideoEncoder.H264Qsv => "h264_qsv",
            VideoEncoder.H264Nvenc => "h264_nvenc",
            VideoEncoder.H264Amf => "h264_amf",
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Unknown video encoder.")
        };
    }

    /// <summary>
    /// Returns the FFmpeg <c>-hwaccel</c> value for <paramref name="acceleration"/>, or null
    /// for <see cref="HardwareAcceleration.None"/> (no flag is emitted).
    /// </summary>
    public static string? HwaccelValue(HardwareAcceleration acceleration)
    {
        return acceleration switch
        {
            HardwareAcceleration.None => null,
            HardwareAcceleration.Qsv => "qsv",
            HardwareAcceleration.Cuda => "cuda",
            HardwareAcceleration.D3D11Va => "d3d11va",
            _ => throw new ArgumentOutOfRangeException(nameof(acceleration), acceleration, "Unknown hardware acceleration mode.")
        };
    }
}
