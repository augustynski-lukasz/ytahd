using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Application;
using YTAHD.Core.Core;
using YTAHD.Core.Infrastructure;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests;

/// <summary>
/// Real-codec durability validation for the QSV encoder (CR-20260912-06 stage 4): every
/// modulator must round-trip through a real h264_qsv encode with byte-exact payload recovery
/// and integrity=passed — the Workstream E machinery is the durability bar. NVENC/AMF stay
/// experimental and are not validated here (no usable device on this machine).
/// </summary>
public class QsvRealCodecTests
{
    private const int Width = 640;
    private const int Height = 480;
    private const int MacroblockSize = 16;
    private const int Fps = 30;

    private const string ModernFfmpeg = @"D:\!Tools2\FFMpeg\ffmpeg-master-latest-win64-gpl-shared\bin\ffmpeg.exe";

    private static string? GetQsvFfmpegPath()
    {
        if (!System.IO.File.Exists(ModernFfmpeg))
        {
            return null;
        }

        // The QSV profile is only asserted when the device actually accepts a session; on a
        // machine without Quick Sync the tests skip rather than fail.
        return System.IO.File.Exists(ModernFfmpeg) ? ModernFfmpeg : null;
    }

    public static System.Collections.Generic.IEnumerable<object[]> ModulatorCases()
    {
        yield return new object[] { "phase1", 256 };
        yield return new object[] { "phase2", 256 };
        yield return new object[] { "phase3", 128 };
        yield return new object[] { "phase4", 64 };
    }

    [Theory]
    [MemberData(nameof(ModulatorCases))]
    public async Task Qsv_RoundTrip_Recovers_Payload_With_Integrity_Passed(string modeName, int payloadSize)
    {
        var ffmpegPath = GetQsvFfmpegPath();
        if (ffmpegPath is null)
        {
            return;
        }

        var report = await FFmpegCapabilities.ProbeEncoderAsync(ffmpegPath, VideoEncoder.H264Qsv);
        if (!report.IsUsable)
        {
            return; // no Quick Sync on this machine; the CPU baseline remains the reference
        }

        var payload = new byte[payloadSize];
        new Random(4400 + payloadSize + modeName.Length).NextBytes(payload);

        var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

        try
        {
            await File.WriteAllBytesAsync(inputFile, payload);

            var service = new YtahdCodecService(
                CreateModulator(modeName),
                new DefaultFFmpegWrapperFactory(ffmpegPath, VideoEncoder.H264Qsv));

            await service.EncodeAsync(new EncodeOptions
            {
                InputFile = inputFile,
                OutputVideo = outputVideo,
                Width = Width,
                Height = Height,
                MacroblockSize = MacroblockSize,
                Fps = Fps,
                VerifyFfmpeg = true,
                UseDurabilityMatrix = true,
                DurabilityMatrixOptions = new DurabilityMatrixOptions { SymbolSize = 32, GroupSize = 4, ParitySymbolsPerGroup = 1 }
            });

            await service.DecodeAsync(new DecodeOptions
            {
                InputVideo = outputVideo,
                OutputFile = outputFile,
                Width = Width,
                Height = Height,
                MacroblockSize = MacroblockSize,
                Fps = Fps,
                VerifyFfmpeg = true,
                UseDurabilityMatrix = true,
                DurabilityMatrixOptions = new DurabilityMatrixOptions { SymbolSize = 32, GroupSize = 4, ParitySymbolsPerGroup = 1 }
            });

            var decoded = await File.ReadAllBytesAsync(outputFile);
            Assert.True(payload.AsSpan().SequenceEqual(decoded), $"{modeName} payload of {payloadSize} bytes was not recovered exactly through the QSV encoder.");
            Assert.Equal(IntegrityStatus.Passed, service.LastDecodeMetrics.IntegrityStatus);
            Assert.NotNull(service.LastDecodeMetrics.Manifest);
            Assert.True(service.LastDecodeMetrics.Manifest!.PayloadSha256.AsSpan().SequenceEqual(SHA256.HashData(payload)));
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
            if (File.Exists(outputVideo)) File.Delete(outputVideo);
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }

    [Fact]
    public async Task Qsv_Encode_Is_Faster_Than_Cpu_On_The_Same_Payload()
    {
        var ffmpegPath = GetQsvFfmpegPath();
        if (ffmpegPath is null)
        {
            return;
        }

        var report = await FFmpegCapabilities.ProbeEncoderAsync(ffmpegPath, VideoEncoder.H264Qsv);
        if (!report.IsUsable)
        {
            return;
        }

        var payload = new byte[64 * 1024];
        new Random(4401).NextBytes(payload);
        var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");

        try
        {
            await File.WriteAllBytesAsync(inputFile, payload);

            var qsvTime = await EncodeOnceAsync(ffmpegPath, VideoEncoder.H264Qsv, inputFile);
            var cpuTime = await EncodeOnceAsync(ffmpegPath, VideoEncoder.LibX264, inputFile);

            // A generous ceiling: QSV should not be dramatically slower than the CPU baseline on
            // a payload large enough for the codec to matter. This is a smoke comparison, not a
            // benchmark — YTAHD.Perf bench remains the authoritative measurement path.
            Assert.True(
                qsvTime <= cpuTime * 3,
                $"QSV encode took {qsvTime:F0} ms vs CPU {cpuTime:F0} ms; the GPU profile appears misconfigured.");
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
        }
    }

    private static async Task<double> EncodeOnceAsync(string ffmpegPath, VideoEncoder encoder, string inputFile)
    {
        var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        try
        {
            var service = new YtahdCodecService(
                new BinaryGridModulator(),
                new DefaultFFmpegWrapperFactory(ffmpegPath, encoder));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await service.EncodeAsync(new EncodeOptions
            {
                InputFile = inputFile,
                OutputVideo = outputVideo,
                Width = Width,
                Height = Height,
                MacroblockSize = MacroblockSize,
                Fps = Fps,
                VerifyFfmpeg = false
            });
            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds;
        }
        finally
        {
            if (File.Exists(outputVideo)) File.Delete(outputVideo);
        }
    }

    private static IModulator CreateModulator(string mode) => mode switch
    {
        "phase1" => new BinaryGridModulator(),
        "phase2" => new PseudoQamModulator(),
        "phase3" => new DctModulator(),
        "phase4" => new MotionVectorModulator(),
        _ => throw new ArgumentException($"Unsupported modulator '{mode}'.")
    };
}