using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Threading.Tasks;
using YTAHD.Core.Application;
using YTAHD.Core.Infrastructure;
using YTAHD.Core.Modulation;

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
static int TryGetVideoFrameCount(string videoPath)
{
    return FFmpegProbe.GetVideoFrameCountAsync(videoPath).GetAwaiter().GetResult();
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
var optModulator = new Option<string>(new[] { "--modulator", "-M" }, () => "phase1", "Modulation mode: 'phase1', 'phase2', 'phase3', or 'phase4'");
var optFfmpegPath = new Option<string?>(new[] { "--ffmpeg-path" }, () => null, "Optional explicit path to ffmpeg.exe; defaults to PATH lookup when omitted.");
var optAudioClock = new Option<bool>(new[] { "--audio-clock" }, () => false, "Add an audio FSK datagram clock track alongside the video (see docs/decisions/F-20260903-02-audio-fsk-clock-design.md).");
encodeCommand.AddOption(optMacro);
encodeCommand.AddOption(optWidth);
encodeCommand.AddOption(optHeight);
encodeCommand.AddOption(optFps);
encodeCommand.AddOption(optModulator);
encodeCommand.AddOption(optFfmpegPath);
encodeCommand.AddOption(optAudioClock);

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

    var modulator = CreateModulator(modulatorName);
    Console.WriteLine($"Encode: {input} -> {output} [{width}x{height}@{fps}, MB={macroblockSize}, mode={modulatorName}, ffmpeg={ffmpegPath ?? "PATH"}, audioClock={useAudioClock}] ");
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
        VerifyFfmpeg = true
    });

    var metrics = service.LastEncodeMetrics;
    var actualVideoFrames = TryGetVideoFrameCount(output.FullName);
    Console.WriteLine($"Encode summary: payload={payloadBytes} bytes, payloadPerFrame={metrics.PayloadBytesPerFrame}, dataFrames={metrics.TotalDataFrames}, framesWritten={metrics.TotalFramesWritten}, actualVideoFrames={actualVideoFrames}");
});

var decodeIn = new Argument<FileInfo>("input") { Arity = ArgumentArity.ExactlyOne };
var decodeOut = new Argument<FileInfo>("output") { Arity = ArgumentArity.ExactlyOne };
var decodeCommand = new Command("decode", "Decode a video back into a binary file");
decodeCommand.AddArgument(decodeIn);
decodeCommand.AddArgument(decodeOut);
var decodeModulator = new Option<string>(new[] { "--modulator", "-M" }, () => "phase1", "Modulation mode: 'phase1', 'phase2', 'phase3', or 'phase4'");
var decodeFfmpegPath = new Option<string?>(new[] { "--ffmpeg-path" }, () => null, "Optional explicit path to ffmpeg.exe; defaults to PATH lookup when omitted.");
var decodeAudioClock = new Option<bool>(new[] { "--audio-clock" }, () => false, "Cross-check the audio FSK datagram clock against the decoded video frame count (see docs/decisions/F-20260903-02-audio-fsk-clock-design.md).");
decodeCommand.AddOption(decodeModulator);
decodeCommand.AddOption(decodeFfmpegPath);
decodeCommand.AddOption(decodeAudioClock);
decodeCommand.SetHandler(async (FileInfo input, FileInfo output, string modulatorName, string? ffmpegPath, bool useAudioClock) =>
{
    var modulator = CreateModulator(modulatorName);
    Console.WriteLine($"Decode: {input} -> {output} [mode={modulatorName}, ffmpeg={ffmpegPath ?? "PATH"}, audioClock={useAudioClock}]");
    var service = new YtahdCodecService(modulator, new DefaultFFmpegWrapperFactory(ffmpegPath));
    await service.DecodeAsync(new DecodeOptions
    {
        InputVideo = input.FullName,
        OutputFile = output.FullName,
        UseAudioClock = useAudioClock,
        VerifyFfmpeg = true
    });

    var decodeMetrics = service.LastDecodeMetrics;
    var outputBytes = File.Exists(output.FullName) ? new FileInfo(output.FullName).Length : 0;
    Console.WriteLine($"Decode summary: framesSeen={decodeMetrics.TotalFramesSeen}, framesDecoded={decodeMetrics.TotalFramesDecoded}, payloadRecovered={decodeMetrics.TotalDecodedPayloadBytes} bytes, outputBytes={outputBytes}, audioDatagramCount={decodeMetrics.AudioDatagramCount?.ToString() ?? "n/a"}");
}, decodeIn, decodeOut, decodeModulator, decodeFfmpegPath, decodeAudioClock);

root.AddCommand(encodeCommand);
root.AddCommand(decodeCommand);

await root.InvokeAsync(args);
