using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Threading.Tasks;
using YTAHD.Core.Application;
using YTAHD.Core.Modulation;

// Build a simple command line with extensible options (future-friendly)
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
encodeCommand.AddOption(optMacro);
encodeCommand.AddOption(optWidth);
encodeCommand.AddOption(optHeight);
encodeCommand.AddOption(optFps);

encodeCommand.SetHandler(async (FileInfo input, FileInfo output, int macroblockSize, int width, int height, int fps) =>
{
    Console.WriteLine($"Encode: {input} -> {output} [{width}x{height}@{fps}, MB={macroblockSize}]");
    IModulator modulator = new BinaryGridModulator();
    var service = new YtahdCodecService(modulator, new DefaultFFmpegWrapperFactory());
    await service.EncodeAsync(new EncodeOptions
    {
        InputFile = input.FullName,
        OutputVideo = output.FullName,
        MacroblockSize = macroblockSize,
        Width = width,
        Height = height,
        Fps = fps,
        VerifyFfmpeg = true
    });
}, argIn, argOut, optMacro, optWidth, optHeight, optFps);

var decodeIn = new Argument<FileInfo>("input") { Arity = ArgumentArity.ExactlyOne };
var decodeOut = new Argument<FileInfo>("output") { Arity = ArgumentArity.ExactlyOne };
var decodeCommand = new Command("decode", "Decode a video back into a binary file");
decodeCommand.AddArgument(decodeIn);
decodeCommand.AddArgument(decodeOut);
decodeCommand.SetHandler(async (FileInfo input, FileInfo output) =>
{
    Console.WriteLine($"Decode: {input} -> {output}");
    IModulator modulator = new BinaryGridModulator();
    var service = new YtahdCodecService(modulator, new DefaultFFmpegWrapperFactory());
    await service.DecodeAsync(new DecodeOptions
    {
        InputVideo = input.FullName,
        OutputFile = output.FullName,
        VerifyFfmpeg = true
    });
}, decodeIn, decodeOut);

root.AddCommand(encodeCommand);
root.AddCommand(decodeCommand);

await root.InvokeAsync(args);
