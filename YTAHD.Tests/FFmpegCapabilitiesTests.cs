using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Application;
using YTAHD.Core.Infrastructure;

namespace YTAHD.Tests;

/// <summary>
/// Real-FFmpeg capability-probe coverage (CR-20260912-06 stage 3): the CPU baseline is always
/// usable, QSV is detected on this machine, and the NVENC device failure produces an
/// actionable diagnostic instead of a raw startup error.
/// </summary>
public class FFmpegCapabilitiesTests
{
    private static string? GetModernFfmpegPath()
    {
        // The capability probes need a full build with GPU encoders; the 2015 fallback build
        // used by TestFfmpeg does not ship h264_qsv/h264_nvenc.
        const string modern = @"D:\!Tools2\FFMpeg\ffmpeg-master-latest-win64-gpl-shared\bin\ffmpeg.exe";
        return System.IO.File.Exists(modern) ? modern : null;
    }

    [Fact]
    public async Task LibX264_Is_Always_Usable_Without_A_Device_Probe()
    {
        var ffmpegPath = TestFfmpeg.GetAvailableFfmpegPath();
        Assert.False(string.IsNullOrWhiteSpace(ffmpegPath));

        var report = await FFmpegCapabilities.ProbeEncoderAsync(ffmpegPath!, VideoEncoder.LibX264);
        Assert.True(report.IsUsable);
        Assert.Empty(report.Diagnostic);
    }

    [Fact]
    public async Task Qsv_Encoder_Is_Detected_On_This_Machine()
    {
        var ffmpegPath = GetModernFfmpegPath();
        if (ffmpegPath is null)
        {
            return; // modern build not present; QSV cannot be probed
        }

        var report = await FFmpegCapabilities.ProbeEncoderAsync(ffmpegPath, VideoEncoder.H264Qsv);
        Assert.True(report.EncoderAvailable);
        Assert.True(report.DeviceAvailable, report.Diagnostic);
        Assert.True(report.IsUsable);
    }

    [Fact]
    public async Task Nvenc_Produces_An_Actionable_Diagnostic_When_The_Device_Fails()
    {
        var ffmpegPath = GetModernFfmpegPath();
        if (ffmpegPath is null)
        {
            return;
        }

        var report = await FFmpegCapabilities.ProbeEncoderAsync(ffmpegPath, VideoEncoder.H264Nvenc);

        // On this machine the NVENC session cannot be opened; the probe must say so clearly
        // rather than reporting the encoder as usable. If a future driver makes NVENC work,
        // the assertion still holds because IsUsable would be true with an empty diagnostic.
        if (report.DeviceAvailable)
        {
            Assert.True(report.IsUsable);
            Assert.Empty(report.Diagnostic);
        }
        else
        {
            Assert.False(report.IsUsable);
            Assert.Contains("h264_nvenc", report.Diagnostic);
            Assert.Contains("session", report.Diagnostic, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Qsv_Hwaccel_Mode_Is_Listed_By_The_Build()
    {
        var ffmpegPath = GetModernFfmpegPath();
        if (ffmpegPath is null)
        {
            return;
        }

        var report = await FFmpegCapabilities.ProbeHwaccelAsync(ffmpegPath, HardwareAcceleration.Qsv);
        Assert.True(report.HwaccelAvailable);
    }

    [Fact]
    public async Task None_Hwaccel_Is_Always_Available()
    {
        var ffmpegPath = TestFfmpeg.GetAvailableFfmpegPath();
        Assert.False(string.IsNullOrWhiteSpace(ffmpegPath));

        var report = await FFmpegCapabilities.ProbeHwaccelAsync(ffmpegPath!, HardwareAcceleration.None);
        Assert.True(report.IsUsable);
    }
}