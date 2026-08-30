using System;
using System.Threading.Tasks;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Core.Application;

public sealed class YtahdCodecService
{
    private readonly IModulator _modulator;
    private readonly IFFmpegWrapperFactory _ffmpegFactory;

    public EncodeMetrics LastEncodeMetrics { get; private set; } = new();
    public DecodeMetrics LastDecodeMetrics { get; private set; } = new();

    public YtahdCodecService(IModulator modulator, IFFmpegWrapperFactory ffmpegFactory)
    {
        _modulator = modulator ?? throw new ArgumentNullException(nameof(modulator));
        _ffmpegFactory = ffmpegFactory ?? throw new ArgumentNullException(nameof(ffmpegFactory));
    }

    public async Task EncodeAsync(EncodeOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.InputFile)) throw new ArgumentException("InputFile is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.OutputVideo)) throw new ArgumentException("OutputVideo is required.", nameof(options));

        var ffmpeg = _ffmpegFactory.CreateForEncode(options.Width, options.Height, options.Fps);
        var engine = new EncoderEngine(_modulator, ffmpeg, options);

        if (options.VerifyFfmpeg)
        {
            await engine.VerifyAsync();
        }

        await engine.EncodeAsync(options.InputFile, options.OutputVideo);
        LastEncodeMetrics = engine.LastEncodeMetrics;
    }

    public async Task DecodeAsync(DecodeOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.InputVideo)) throw new ArgumentException("InputVideo is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.OutputFile)) throw new ArgumentException("OutputFile is required.", nameof(options));

        var ffmpeg = _ffmpegFactory.CreateForDecode();
        var engine = new DecoderEngine(_modulator, ffmpeg, options);

        if (options.VerifyFfmpeg)
        {
            await engine.VerifyAsync();
        }

        await engine.DecodeAsync(options.InputVideo, options.OutputFile);
        LastDecodeMetrics = engine.LastDecodeMetrics;
    }

    public async Task DecodeFromRgbStreamAsync(DecodeRgbOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));

        var ffmpeg = _ffmpegFactory.CreateForDecode();
        var engine = new DecoderEngine(_modulator, ffmpeg);
        await engine.DecodeFromRgbStreamAsync(
            options.RgbStream,
            options.Width,
            options.Height,
            options.MacroblockSize,
            options.ExpectedOutputBytes,
            options.OutputFile);
        LastDecodeMetrics = engine.LastDecodeMetrics;
    }
}
