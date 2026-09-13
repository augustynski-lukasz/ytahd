using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using YTAHD.Core.Application;

namespace YTAHD.Core.Infrastructure;

/// <summary>
/// Result of probing an FFmpeg build for a requested encoder/hwaccel combination
/// (CR-20260912-06 stage 3).
/// </summary>
public sealed class FFmpegCapabilityReport
{
    /// <summary>True when the requested encoder is present in the FFmpeg build.</summary>
    public bool EncoderAvailable { get; init; }

    /// <summary>True when the requested hwaccel mode is present in the FFmpeg build.</summary>
    public bool HwaccelAvailable { get; init; }

    /// <summary>True when a real encode session could be opened (device/driver check).</summary>
    public bool DeviceAvailable { get; init; }

    /// <summary>Actionable diagnostics when any check failed; empty when everything is available.</summary>
    public string Diagnostic { get; init; } = string.Empty;

    public bool IsUsable => EncoderAvailable && HwaccelAvailable && DeviceAvailable;
}

/// <summary>
/// Probes an FFmpeg build for GPU encoder/hwaccel support before a long encode/decode starts
/// (CR-20260912-06 stage 3). The motivating failure is NVENC's
/// <c>OpenEncodeSessionEx: unsupported device</c>: the encoder is listed by
/// <c>-encoders</c> but no session can be opened, so a build-level check alone is not enough —
/// a tiny real encode is the only reliable device check.
/// </summary>
public static class FFmpegCapabilities
{
    /// <summary>
    /// Probes whether <paramref name="ffmpegExecutablePath"/> can actually encode with
    /// <paramref name="encoder"/>. Runs <c>-encoders</c> first (cheap, no device needed), then
    /// a 1-frame real encode to confirm the device/driver accepts a session.
    /// </summary>
    public static async Task<FFmpegCapabilityReport> ProbeEncoderAsync(
        string ffmpegExecutablePath,
        VideoEncoder encoder,
        int width = 64,
        int height = 64)
    {
        if (encoder == VideoEncoder.LibX264)
        {
            // The CPU baseline needs no device; its presence is checked by IsAvailableAsync.
            return new FFmpegCapabilityReport { EncoderAvailable = true, HwaccelAvailable = true, DeviceAvailable = true };
        }

        var codecName = FFmpegEncoderArguments.CodecName(encoder);
        var encodersOutput = await RunAndCaptureAsync(ffmpegExecutablePath, "-hide_banner -encoders");
        if (!encodersOutput.Contains(codecName, StringComparison.Ordinal))
        {
            return new FFmpegCapabilityReport
            {
                EncoderAvailable = false,
                HwaccelAvailable = false,
                DeviceAvailable = false,
                Diagnostic = $"FFmpeg build at '{ffmpegExecutablePath}' does not include the '{codecName}' encoder. Use a full/gpl build or fall back to libx264."
            };
        }

        // A real 1-frame encode is the device check: it fails fast with the driver's own
        // message when no session can be opened (e.g. NVENC "unsupported device").
        var deviceProbe = await TryTinyEncodeAsync(ffmpegExecutablePath, encoder, width, height);
        if (!deviceProbe.Success)
        {
            return new FFmpegCapabilityReport
            {
                EncoderAvailable = true,
                HwaccelAvailable = true,
                DeviceAvailable = false,
                Diagnostic = $"The '{codecName}' encoder is present but no encode session could be opened: {deviceProbe.Error}. Check the GPU driver, or fall back to libx264."
            };
        }

        return new FFmpegCapabilityReport { EncoderAvailable = true, HwaccelAvailable = true, DeviceAvailable = true };
    }

    /// <summary>
    /// Probes whether <paramref name="ffmpegExecutablePath"/> lists the requested hwaccel mode.
    /// </summary>
    public static async Task<FFmpegCapabilityReport> ProbeHwaccelAsync(string ffmpegExecutablePath, HardwareAcceleration acceleration)
    {
        if (acceleration == HardwareAcceleration.None)
        {
            return new FFmpegCapabilityReport { EncoderAvailable = true, HwaccelAvailable = true, DeviceAvailable = true };
        }

        var hwaccelValue = FFmpegEncoderArguments.HwaccelValue(acceleration);
        var hwaccelsOutput = await RunAndCaptureAsync(ffmpegExecutablePath, "-hide_banner -hwaccels");
        if (!hwaccelsOutput.Contains(hwaccelValue!, StringComparison.Ordinal))
        {
            return new FFmpegCapabilityReport
            {
                EncoderAvailable = true,
                HwaccelAvailable = false,
                DeviceAvailable = false,
                Diagnostic = $"FFmpeg build at '{ffmpegExecutablePath}' does not list the '{hwaccelValue}' hwaccel mode."
            };
        }

        return new FFmpegCapabilityReport { EncoderAvailable = true, HwaccelAvailable = true, DeviceAvailable = true };
    }

    private static async Task<string> RunAndCaptureAsync(string executable, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(executable, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var child = ChildProcessScope.Start(psi, "Failed to start ffmpeg for capability probe.");
            var stdoutTask = ChildProcessPipes.ReadToEndAsync(child.Process.StandardOutput);
            var stderrDrain = ChildProcessPipes.DrainAsync(child.Process.StandardError);
            await child.Process.WaitForExitAsync();
            await stderrDrain;
            return await stdoutTask;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static async Task<(bool Success, string Error)> TryTinyEncodeAsync(string executable, VideoEncoder encoder, int width, int height)
    {
        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ytahd_cap_{Guid.NewGuid():N}.mp4");
        try
        {
            var args = $"-y -loglevel error -f lavfi -i color=c=black:s={width}x{height}:d=0.1 -frames:v 1 {FFmpegEncoderArguments.For(encoder)} \"{tempFile}\"";
            var psi = new ProcessStartInfo(executable, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = true
            };

            using var child = ChildProcessScope.Start(psi, "Failed to start ffmpeg for the device probe.");
            var stderrTask = ChildProcessPipes.ReadToEndAsync(child.Process.StandardError);
            await child.Process.WaitForExitAsync();
            var error = await stderrTask;

            if (child.Process.ExitCode == 0 && System.IO.File.Exists(tempFile))
            {
                return (true, string.Empty);
            }

            return (false, string.IsNullOrWhiteSpace(error) ? $"exit code {child.Process.ExitCode}" : error.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile); } catch { }
        }
    }
}
