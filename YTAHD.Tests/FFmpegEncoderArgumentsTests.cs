using System;
using Xunit;
using YTAHD.Core.Application;
using YTAHD.Core.Infrastructure;

namespace YTAHD.Tests;

/// <summary>
/// Regression coverage for the GPU codec option model and FFmpeg argument generation
/// (CR-20260912-06 stages 1-2): the CPU baseline arguments are unchanged, each GPU profile has
/// its owned argument set, and the hwaccel mapping is explicit.
/// </summary>
public class FFmpegEncoderArgumentsTests
{
    [Fact]
    public void LibX264_Keeps_The_Exact_Production_Baseline_Arguments()
    {
        Assert.Equal("-c:v libx264 -crf 23 -pix_fmt yuv420p", FFmpegEncoderArguments.For(VideoEncoder.LibX264));
    }

    [Fact]
    public void Qsv_Profile_Matches_The_Cpu_Quality_Baseline()
    {
        var args = FFmpegEncoderArguments.For(VideoEncoder.H264Qsv);
        Assert.Contains("-c:v h264_qsv", args);
        Assert.Contains("-global_quality 23", args);
        Assert.Contains("-pix_fmt yuv420p", args);
    }

    [Fact]
    public void Nvenc_And_Amf_Profiles_Are_Declared()
    {
        Assert.Contains("-c:v h264_nvenc", FFmpegEncoderArguments.For(VideoEncoder.H264Nvenc));
        Assert.Contains("-c:v h264_amf", FFmpegEncoderArguments.For(VideoEncoder.H264Amf));
    }

    [Fact]
    public void CodecName_Maps_Every_Encoder()
    {
        Assert.Equal("libx264", FFmpegEncoderArguments.CodecName(VideoEncoder.LibX264));
        Assert.Equal("h264_qsv", FFmpegEncoderArguments.CodecName(VideoEncoder.H264Qsv));
        Assert.Equal("h264_nvenc", FFmpegEncoderArguments.CodecName(VideoEncoder.H264Nvenc));
        Assert.Equal("h264_amf", FFmpegEncoderArguments.CodecName(VideoEncoder.H264Amf));
    }

    [Fact]
    public void HwaccelValue_Maps_Modes_And_None_Emits_No_Flag()
    {
        Assert.Null(FFmpegEncoderArguments.HwaccelValue(HardwareAcceleration.None));
        Assert.Equal("qsv", FFmpegEncoderArguments.HwaccelValue(HardwareAcceleration.Qsv));
        Assert.Equal("cuda", FFmpegEncoderArguments.HwaccelValue(HardwareAcceleration.Cuda));
        Assert.Equal("d3d11va", FFmpegEncoderArguments.HwaccelValue(HardwareAcceleration.D3D11Va));
    }

    [Fact]
    public void Unknown_Enum_Values_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FFmpegEncoderArguments.For((VideoEncoder)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => FFmpegEncoderArguments.CodecName((VideoEncoder)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => FFmpegEncoderArguments.HwaccelValue((HardwareAcceleration)99));
    }

    [Fact]
    public void Options_Default_To_The_Cpu_Baseline_And_No_Acceleration()
    {
        var options = new VideoCodecOptions();
        Assert.Equal(VideoEncoder.LibX264, options.VideoEncoder);
        Assert.Equal(HardwareAcceleration.None, options.HardwareAcceleration);
    }
}