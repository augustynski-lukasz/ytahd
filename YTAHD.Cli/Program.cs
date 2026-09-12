using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using YTAHD.Core.Application;
using YTAHD.Core.Core;
using YTAHD.Core.Infrastructure;
using YTAHD.Core.Modulation;

// Shared help text for --jobs on both the encode and decode commands.
const string JobsOptionHelp = "Degree of parallelism: 'auto' (default, conservative core-count based workers), 'serial' (fully single-threaded for deterministic runs), '0' (sequential frames; inner loops still use the machine's processors), or an explicit frame worker count. See docs/decisions/CR-20260912-01-degree-of-parallelism-controls.md.";

static IModulator CreateModulator(string mode)
{
    return mode.Trim().ToLowerInvariant() switch
    {
        "phase4" or "motion" or "motion-vector" => new MotionVectorModulator(),
        "phase3" or "dct" or "dct-domain" => new DctModulator(),
        "phase2" or "pseudo-qam" or "qam" => new PseudoQamModulator(),
        "phase1" or "binary" or "grid" or "default" or "" => new BinaryGridModulator(),
        _ => throw new ArgumentException($"Unsupported modulation mode '{mode}'. Use 'phase1', 'phase2', 'phase3', or 'phase4'.", nameof(mode))
    };
}

// Build a simple command line with extensible options (future-friendly)
static int TryGetVideoFrameCount(string videoPath, string? ffmpegPath)
{
    return FFmpegProbe.GetVideoFrameCountAsync(videoPath, ffmpegPath).GetAwaiter().GetResult();
}

static string FormatMilliseconds(double milliseconds)
{
    return milliseconds.ToString("F1", CultureInfo.InvariantCulture);
}

static string FormatParallelism(int requested, int resolved)
{
    if (requested == ParallelismPolicy.Auto)
    {
        return resolved == 1
            ? "auto->1 (serial fallback: low core count)"
            : $"auto->{resolved}";
    }

    if (requested == 1)
    {
        // One worker everywhere, including the modulator/decoder inner loops.
        return "serial";
    }

    if (requested == 0)
    {
        return resolved == 1
            ? "default->1 (no frame parallelism)"
            : $"default->{resolved}";
    }

    return $"{requested}->{resolved}";
}

static bool TryParseJobs(string? value, out int requested, out string? error)
{
    requested = 0;
    error = null;

    var trimmed = (value ?? string.Empty).Trim();
    if (trimmed.Length == 0 || string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
    {
        requested = ParallelismPolicy.Auto;
        return true;
    }

    if (string.Equals(trimmed, "serial", StringComparison.OrdinalIgnoreCase))
    {
        // Fully serial: one frame worker and serial inner loops, for deterministic debugging.
        requested = 1;
        return true;
    }

    if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
    {
        // Values above ParallelismPolicy.MaxWorkerLimit are clamped by the policy and reported
        // in the resolved count rather than rejected.
        requested = parsed;
        return true;
    }

    error = $"Invalid --jobs value '{value}'. Use 'auto', 'serial', or a non-negative worker count.";
    return false;
}

static IProgress<DecodeProgress> CreateDecodeProgressReporter()
{
    var lastReportedPercent = -1;

    return new Progress<DecodeProgress>(progress =>
    {
        if (progress.Percentage is not { } percentage)
        {
            return;
        }

        var roundedPercent = (int)Math.Floor(percentage);
        if (roundedPercent < 100 && roundedPercent - lastReportedPercent < 5)
        {
            return;
        }

        lastReportedPercent = roundedPercent;
        Console.WriteLine($"Decode progress: {percentage.ToString("F1", CultureInfo.InvariantCulture)}% ({progress.FramesSeen}/{progress.TotalVideoFrames} frames)");
    });
}

var root = new RootCommand("YTAHD - encode/decode binary data into resilient video frames");

var argIn = new Argument<FileInfo>("input") { Arity = ArgumentArity.ExactlyOne };
var argOut = new Argument<FileInfo>("output") { Arity = ArgumentArity.ExactlyOne };
var encodeCommand = new Command("encode", "Encode an input file into a resilient video");
encodeCommand.AddArgument(argIn);
encodeCommand.AddArgument(argOut);

var optMacro = new Option<int>(new[] { "--macroblock-size", "-m" }, () => 16, "Macroblock size in pixels (default:16)");
var optWidth = new Option<int>(new[] { "--width", "-w" }, () => 3840, "Output video width");
var optHeight = new Option<int>(new[] { "--height", "-H" }, () => 2160, "Output video height");
var optFps = new Option<int>(new[] { "--fps", "-r" }, () => 60, "Output framerate");
var optModulator = new Option<string>(new[] { "--modulator", "-M" }, () => "phase3", "Modulation mode: 'phase1', 'phase2', 'phase3', or 'phase4'");
var optFfmpegPath = new Option<string?>(new[] { "--ffmpeg-path", "-F" }, () => null, "Optional explicit path to ffmpeg.exe or its directory; defaults to PATH lookup when omitted.");
var optAudioClock = new Option<bool>(new[] { "--audio-clock", "-A" }, () => false, "Add an audio FSK datagram clock track alongside the video (see docs/decisions/F-20260903-02-audio-fsk-clock-design.md).");
var optJobs = new Option<string>(new[] { "--jobs", "-j" }, () => "auto", JobsOptionHelp);
encodeCommand.AddOption(optMacro);
encodeCommand.AddOption(optWidth);
encodeCommand.AddOption(optHeight);
encodeCommand.AddOption(optFps);
encodeCommand.AddOption(optModulator);
encodeCommand.AddOption(optFfmpegPath);
encodeCommand.AddOption(optAudioClock);
encodeCommand.AddOption(optJobs);

encodeCommand.SetHandler(async (InvocationContext ctx) =>
{
    var input = ctx.ParseResult.GetValueForArgument(argIn);
    var output = ctx.ParseResult.GetValueForArgument(argOut);
    var macroblockSize = ctx.ParseResult.GetValueForOption(optMacro);
    var width = ctx.ParseResult.GetValueForOption(optWidth);
    var height = ctx.ParseResult.GetValueForOption(optHeight);
    var fps = ctx.ParseResult.GetValueForOption(optFps);
    var modulatorName = ctx.ParseResult.GetValueForOption(optModulator) ?? "phase1";
    var ffmpegPath = ctx.ParseResult.GetValueForOption(optFfmpegPath);
    var useAudioClock = ctx.ParseResult.GetValueForOption(optAudioClock);
    var jobs = ctx.ParseResult.GetValueForOption(optJobs);

    if (!TryParseJobs(jobs, out var requestedJobs, out var jobsError))
    {
        Console.Error.WriteLine(jobsError);
        ctx.ExitCode = 1;
        return;
    }

    var parallelism = FormatParallelism(requestedJobs, ParallelismPolicy.Resolve(requestedJobs));

    var modulator = CreateModulator(modulatorName);
    Console.WriteLine($"Encode: {input} -> {output} [{width}x{height}@{fps}, MB={macroblockSize}, mode={modulatorName}, ffmpeg={ffmpegPath ?? "PATH"}, audioClock={useAudioClock}, parallelism={parallelism}] ");
    var service = new YtahdCodecService(modulator, new DefaultFFmpegWrapperFactory(ffmpegPath));
    var payloadBytes = File.Exists(input.FullName) ? new FileInfo(input.FullName).Length : 0;
    await service.EncodeAsync(new EncodeOptions
    {
        InputFile = input.FullName,
        OutputVideo = output.FullName,
        MacroblockSize = macroblockSize,
        Width = width,
        Height = height,
        Fps = fps,
        UseAudioClock = useAudioClock,
        MaxDegreeOfParallelism = requestedJobs,
        VerifyFfmpeg = true
    });

    var metrics = service.LastEncodeMetrics;
    var actualVideoFrames = TryGetVideoFrameCount(output.FullName, ffmpegPath);
    Console.WriteLine($"Encode summary: payload={payloadBytes} bytes, payloadPerFrame={metrics.PayloadBytesPerFrame}, dataFrames={metrics.TotalDataFrames}, framesWritten={metrics.TotalFramesWritten}, actualVideoFrames={actualVideoFrames}, parallelism={parallelism}, timingMs={{total={FormatMilliseconds(metrics.TotalElapsedMilliseconds)}, packetBuild={FormatMilliseconds(metrics.PacketBuildMilliseconds)}, render={FormatMilliseconds(metrics.FrameRenderMilliseconds)}, rgb={FormatMilliseconds(metrics.RgbConversionMilliseconds)}, ffmpegWrite={FormatMilliseconds(metrics.FfmpegWriteMilliseconds)}}}");
});

var decodeIn = new Argument<FileInfo>("input") { Arity = ArgumentArity.ExactlyOne };
var decodeOut = new Argument<FileInfo>("output") { Arity = ArgumentArity.ExactlyOne };
var decodeCommand = new Command("decode", "Decode a video back into a binary file");
decodeCommand.AddArgument(decodeIn);
decodeCommand.AddArgument(decodeOut);
var decodeModulator = new Option<string>(new[] { "--modulator", "-M" }, () => "phase1", "Modulation mode: 'phase1', 'phase2', 'phase3', or 'phase4'");
var decodeFfmpegPath = new Option<string?>(new[] { "--ffmpeg-path", "--ffpmeg-path" }, () => null, "Optional explicit path to ffmpeg.exe or its directory; defaults to PATH lookup when omitted.");
var decodeAudioClock = new Option<bool>(new[] { "--audio-clock" }, () => false, "Cross-check the audio FSK datagram clock against the decoded video frame count (see docs/decisions/F-20260903-02-audio-fsk-clock-design.md).");
var decodeJobs = new Option<string>(new[] { "--jobs", "-j" }, () => "auto", JobsOptionHelp);
decodeCommand.AddOption(decodeModulator);
decodeCommand.AddOption(decodeFfmpegPath);
decodeCommand.AddOption(decodeAudioClock);
decodeCommand.AddOption(decodeJobs);
decodeCommand.SetHandler(async (InvocationContext ctx) =>
{
    var input = ctx.ParseResult.GetValueForArgument(decodeIn);
    var output = ctx.ParseResult.GetValueForArgument(decodeOut);
    var modulatorName = ctx.ParseResult.GetValueForOption(decodeModulator) ?? "phase1";
    var ffmpegPath = ctx.ParseResult.GetValueForOption(decodeFfmpegPath);
    var useAudioClock = ctx.ParseResult.GetValueForOption(decodeAudioClock);
    var jobs = ctx.ParseResult.GetValueForOption(decodeJobs);

    if (!TryParseJobs(jobs, out var requestedJobs, out var jobsError))
    {
        Console.Error.WriteLine(jobsError);
        ctx.ExitCode = 1;
        return;
    }

    var parallelism = FormatParallelism(requestedJobs, ParallelismPolicy.Resolve(requestedJobs));

    var modulator = CreateModulator(modulatorName);
    Console.WriteLine($"Decode: {input} -> {output} [mode={modulatorName}, ffmpeg={ffmpegPath ?? "PATH"}, audioClock={useAudioClock}, parallelism={parallelism}]");
    var service = new YtahdCodecService(modulator, new DefaultFFmpegWrapperFactory(ffmpegPath));
    await service.DecodeAsync(new DecodeOptions
    {
        InputVideo = input.FullName,
        OutputFile = output.FullName,
        UseAudioClock = useAudioClock,
        MaxDegreeOfParallelism = requestedJobs,
        VerifyFfmpeg = true,
        Progress = CreateDecodeProgressReporter()
    });

    var decodeMetrics = service.LastDecodeMetrics;
    var outputBytes = File.Exists(output.FullName) ? new FileInfo(output.FullName).Length : 0;
    var totalVideoFrames = TryGetVideoFrameCount(input.FullName, ffmpegPath);
    var completionPercentage = totalVideoFrames > 0 ? Math.Min(100d, decodeMetrics.TotalFramesSeen * 100d / totalVideoFrames).ToString("F1", CultureInfo.InvariantCulture) + "%" : "n/a";
    Console.WriteLine($"Decode summary: framesSeen={decodeMetrics.TotalFramesSeen}, totalVideoFrames={totalVideoFrames}, completion={completionPercentage}, framesDecoded={decodeMetrics.TotalFramesDecoded}, payloadRecovered={decodeMetrics.TotalDecodedPayloadBytes} bytes, outputBytes={outputBytes}, parallelism={parallelism}, integrity={decodeMetrics.IntegrityStatus.ToString().ToLowerInvariant()}, audioDatagramCount={decodeMetrics.AudioDatagramCount?.ToString() ?? "n/a"}, audioVideoMismatch={decodeMetrics.HasAudioVideoDatagramMismatch()?.ToString() ?? "n/a"}, timingMs={{total={FormatMilliseconds(decodeMetrics.TotalElapsedMilliseconds)}, read={FormatMilliseconds(decodeMetrics.FrameReadMilliseconds)}, packetDecode={FormatMilliseconds(decodeMetrics.PacketDecodeMilliseconds)}, aggregation={FormatMilliseconds(decodeMetrics.AggregationMilliseconds)}}}");
});

root.AddCommand(encodeCommand);
root.AddCommand(decodeCommand);

return await root.InvokeAsync(args);
