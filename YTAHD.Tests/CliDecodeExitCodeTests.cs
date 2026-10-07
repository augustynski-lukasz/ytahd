using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace YTAHD.Tests;

/// <summary>
/// RM-03 regression coverage (CR-20261007-01): the CLI decode must exit non-zero when the
/// recovery is untrustworthy instead of silently returning exit 0.
///
/// Found by the 2026-09-14 real-user CLI validation: the plain decode path (no manifest)
/// can return a silently truncated payload with exit 0 and integrity=unknown. The CLI now
/// gates on three signals:
/// - integrity=failed (durability path, manifest mismatch);
/// - outputBytes vs --expected-bytes mismatch (plain path, the only truncation guard);
/// - audio-clock datagram mismatch (frames lost beyond parity repair).
/// The summary stays on stdout; the failure reason goes to stderr and the exit code is 1.
/// </summary>
public class CliDecodeExitCodeTests
{
    private static string? GetAvailableFfmpegPath() => TestFfmpeg.GetAvailableFfmpegPath();

    private static (string RepoRoot, string CliProject) LocateCli()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var cliProject = Path.Combine(repoRoot, "YTAHD.Cli", "YTAHD.Cli.csproj");
        Assert.True(File.Exists(cliProject), $"CLI project not found at '{cliProject}'.");
        return (repoRoot, cliProject);
    }

    private static ProcessStartInfo CliStartInfo(string repoRoot, string arguments) => new()
    {
        FileName = "dotnet",
        Arguments = $"run --project \"{Path.Combine(repoRoot, "YTAHD.Cli", "YTAHD.Cli.csproj")}\" -- {arguments}",
        WorkingDirectory = repoRoot,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        // The CLI child must not inherit the test host's never-closing stdin pipe
        // (CR-20260913-08): ffmpeg under it monitors stdin for interactive commands.
        RedirectStandardInput = true
    };

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(ProcessStartInfo psi)
    {
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdOut = await process.StandardOutput.ReadToEndAsync();
        var stdErr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdOut, stdErr);
    }

    private static async Task<string> EncodeAsync(string repoRoot, string cliProject, string ffmpegPath, string inputFile, string outputVideo)
    {
        var (exitCode, stdOut, stdErr) = await RunCliAsync(CliStartInfo(repoRoot,
            $"encode \"{inputFile}\" \"{outputVideo}\" --modulator phase1 --ffmpeg-path \"{ffmpegPath}\" --width 640 --height 480 --fps 30"));
        Assert.True(exitCode == 0, $"CLI encode failed. StdOut: {stdOut} StdErr: {stdErr}");
        return stdOut;
    }

    [Fact]
    public async Task Decode_With_ExpectedBytes_Mismatch_Exits_NonZero()
    {
        var ffmpegPath = GetAvailableFfmpegPath();
        Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be available for the CLI exit-code test.");
        var (repoRoot, cliProject) = LocateCli();

        var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

        try
        {
            var payload = new byte[1024];
            new Random(3301).NextBytes(payload);
            await File.WriteAllBytesAsync(inputFile, payload);

            await EncodeAsync(repoRoot, cliProject, ffmpegPath!, inputFile, outputVideo);

            // A wrong expected size must turn into a loud non-zero exit, not a silent pass.
            var (exitCode, stdOut, stdErr) = await RunCliAsync(CliStartInfo(repoRoot,
                $"decode \"{outputVideo}\" \"{outputFile}\" --modulator phase1 --ffmpeg-path \"{ffmpegPath}\" --expected-bytes 999999"));

            Assert.True(exitCode != 0, $"Decode with a wrong --expected-bytes must exit non-zero. StdOut: {stdOut} StdErr: {stdErr}");
            Assert.Contains("expected payload size", stdErr);
            Assert.Matches(@"outputBytes=1024\b", stdOut);
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
            if (File.Exists(outputVideo)) File.Delete(outputVideo);
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }

    [Fact]
    public async Task Decode_With_Correct_ExpectedBytes_Exits_Zero()
    {
        var ffmpegPath = GetAvailableFfmpegPath();
        Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be available for the CLI exit-code test.");
        var (repoRoot, cliProject) = LocateCli();

        var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

        try
        {
            var payload = new byte[1024];
            new Random(3302).NextBytes(payload);
            await File.WriteAllBytesAsync(inputFile, payload);

            await EncodeAsync(repoRoot, cliProject, ffmpegPath!, inputFile, outputVideo);

            var (exitCode, stdOut, stdErr) = await RunCliAsync(CliStartInfo(repoRoot,
                $"decode \"{outputVideo}\" \"{outputFile}\" --modulator phase1 --ffmpeg-path \"{ffmpegPath}\" --expected-bytes 1024"));

            Assert.True(exitCode == 0, $"Decode with the correct --expected-bytes must exit zero. StdOut: {stdOut} StdErr: {stdErr}");
            Assert.True(File.Exists(outputFile), "Decode did not write the output file.");
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
            if (File.Exists(outputVideo)) File.Delete(outputVideo);
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }

    [Fact]
    public async Task Decode_Durability_Integrity_Passed_Exits_Zero()
    {
        var ffmpegPath = GetAvailableFfmpegPath();
        Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be available for the CLI exit-code test.");
        var (repoRoot, cliProject) = LocateCli();

        var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

        try
        {
            var payload = new byte[2048];
            new Random(3303).NextBytes(payload);
            await File.WriteAllBytesAsync(inputFile, payload);

            var (encodeExit, encOut, encErr) = await RunCliAsync(CliStartInfo(repoRoot,
                $"encode \"{inputFile}\" \"{outputVideo}\" --modulator phase1 --durability --ffmpeg-path \"{ffmpegPath}\" --width 640 --height 480 --fps 30"));
            Assert.True(encodeExit == 0, $"CLI durability encode failed. StdOut: {encOut} StdErr: {encErr}");

            var (exitCode, stdOut, stdErr) = await RunCliAsync(CliStartInfo(repoRoot,
                $"decode \"{outputVideo}\" \"{outputFile}\" --modulator phase1 --durability --ffmpeg-path \"{ffmpegPath}\""));

            Assert.True(exitCode == 0, $"Durability decode with integrity=passed must exit zero. StdOut: {stdOut} StdErr: {stdErr}");
            Assert.Contains("integrity=passed", stdOut);
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
            if (File.Exists(outputVideo)) File.Delete(outputVideo);
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }
}
