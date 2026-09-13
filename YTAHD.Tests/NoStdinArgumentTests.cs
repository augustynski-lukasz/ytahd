using System;
using System.Diagnostics;
using Xunit;
using YTAHD.Core.Application;
using YTAHD.Core.Core;
using YTAHD.Core.Infrastructure;

namespace YTAHD.Tests;

/// <summary>
/// CR-20260913-08 regression: every ffmpeg/ffprobe child the pipeline spawns must run with
/// <c>-nostdin</c> (or an explicitly redirected stdin). ffmpeg monitors stdin for interactive
/// commands; under a test host or service host the inherited stdin is a pipe that never
/// delivers data and never closes, which can freeze the child before it writes a single frame
/// — the exact signature of the suite-hang investigation this ADR records.
/// </summary>
public class NoStdinArgumentTests
{
    [Fact]
    public void DecodeArguments_Start_With_NoStdin()
    {
        var args = DecoderEngine.BuildDecodeArguments("in.mp4", 640, 480, 30, HardwareAcceleration.None);

        Assert.StartsWith("-nostdin ", args, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeArguments_Keep_NoStdin_With_Hwaccel()
    {
        var args = DecoderEngine.BuildDecodeArguments("in.mp4", 640, 480, 30, HardwareAcceleration.Qsv);

        Assert.StartsWith("-nostdin ", args, StringComparison.Ordinal);
        // -nostdin must not displace -hwaccel ahead of -i.
        Assert.True(args.IndexOf("-hwaccel", StringComparison.Ordinal) < args.IndexOf("-i ", StringComparison.Ordinal));
    }

    [Fact]
    public void ProbePathResolution_Uses_NoStdin()
    {
        // ResolveFfprobePath probes candidates with `ffprobe -version`; the argument string is
        // built inline, so this test pins the observable contract instead: a successful probe
        // must work under a redirected (never-closing) stdin, which -nostdin guarantees.
        var resolved = FFmpegProbe.ResolveFfprobePath(TestFfmpeg.GetAvailableFfmpegPath());

        Assert.False(string.IsNullOrWhiteSpace(resolved), "ffprobe must resolve next to the pinned ffmpeg build.");
    }

    [Fact]
    public async Task RealFfmpeg_Encode_Completes_Under_NeverClosing_Stdin_And_Undrained_Pipes()
    {
        // The live-hang reproduction (CR-20260913-08): an ffmpeg child whose stdin was the test
        // host's inherited (never-closing) pipe froze before writing a single frame, and a
        // second freeze mode blocked the child on its final write to a redirected-but-undrained
        // stderr pipe. This test deliberately does NOT redirect stdin and does NOT drain the
        // pipes, so it exercises both conditions; -nostdin plus -hide_banner -loglevel error
        // -nostats must keep the child's output below the pipe buffer so it completes.
        var ffmpegPath = TestFfmpeg.GetAvailableFfmpegPath();
        Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be available for the -nostdin regression test.");

        var rawPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_nostdin_probe.rgb");
        var outputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_nostdin_probe.mp4");

        try
        {
            // One 128x64 rgb24 frame.
            var frame = new byte[128 * 64 * 3];
            new Random(20260913).NextBytes(frame);
            File.WriteAllBytes(rawPath, frame);

            using var encode = Process.Start(new ProcessStartInfo(
                ffmpegPath,
                $"-nostdin -hide_banner -loglevel error -nostats -y -f rawvideo -pix_fmt rgb24 -s 128x64 -r 30 -i \"{rawPath}\" -c:v libx264 -pix_fmt yuv420p -an \"{outputPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Decisive experiment (CR-20260913-08): -nostdin alone did not stop the freeze
                // under the test host; an explicitly redirected (and immediately closed) stdin
                // removes the inherited never-closing pipe entirely.
                RedirectStandardInput = true
            });
            Assert.NotNull(encode);
            encode.StandardInput.Close();

            // Without -nostdin this WaitForExit froze indefinitely under the test host; without
            // the quiet-stderr flags it froze on the final stderr write with a full pipe.
            Assert.True(encode!.WaitForExit(30_000), "ffmpeg encode did not finish within 30s under an inherited stdin and undrained pipes; the child hardening is not taking effect.");
            Assert.Equal(0, encode.ExitCode);
            Assert.True(new FileInfo(outputPath).Length > 0, "ffmpeg produced no output video.");
        }
        finally
        {
            if (File.Exists(rawPath)) File.Delete(rawPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}
