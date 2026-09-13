using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Application;
using YTAHD.Core.Core;
using YTAHD.Core.Infrastructure;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests;

/// <summary>
/// Decode-side <c>--hwaccel</c> wiring (CR-20260913-04): the decode argument builder must
/// prepend <c>-hwaccel &lt;value&gt;</c> before <c>-i</c> when a mode is selected, emit no
/// flag for <see cref="HardwareAcceleration.None"/> (byte-identical default arguments), and
/// the CLI option must flow through <see cref="DecodeOptions"/>. The real-FFmpeg qsv decode
/// smoke test is probe-guarded (pattern from QsvRealCodecTests) and skips on stacks without
/// Quick Sync.
/// </summary>
public class DecodeHwaccelTests
{
    private const int Width = 640;
    private const int Height = 480;
    private const int MacroblockSize = 16;
    private const int Fps = 30;

    private const string ModernFfmpeg = @"D:\!Tools2\FFMpeg\ffmpeg-master-latest-win64-gpl-shared\bin\ffmpeg.exe";

    [Fact]
    public void BuildDecodeArguments_With_None_Emits_No_Hwaccel_Flag()
    {
        var args = DecoderEngine.BuildDecodeArguments("in.mp4", Width, Height, Fps, HardwareAcceleration.None);

        Assert.DoesNotContain("-hwaccel", args);
        // Default arguments stay byte-identical to the pre-CR-20260913-04 behavior apart from
        // -nostdin (CR-20260913-08), which closes the child's inherited stdin.
        Assert.Equal($"-nostdin -hide_banner -loglevel error -i \"in.mp4\" -f rawvideo -pix_fmt rgb24 -s {Width}x{Height} -r {Fps} -", args);
    }

    [Theory]
    [InlineData(HardwareAcceleration.Qsv, "qsv")]
    [InlineData(HardwareAcceleration.Cuda, "cuda")]
    [InlineData(HardwareAcceleration.D3D11Va, "d3d11va")]
    public void BuildDecodeArguments_With_Acceleration_Prepends_Hwaccel_Before_Input(HardwareAcceleration acceleration, string expectedValue)
    {
        var args = DecoderEngine.BuildDecodeArguments("in.mp4", Width, Height, Fps, acceleration);

        // -hwaccel_output_format nv12 is required so frames leave GPU surface memory and the
        // rgb24 conversion works (qsv surfaces otherwise break swscale with exit -40).
        Assert.Contains($"-hwaccel {expectedValue} -hwaccel_output_format nv12 -i", args);
        // -hwaccel must precede -i to affect input decoding.
        Assert.True(args.IndexOf("-hwaccel", StringComparison.Ordinal) < args.IndexOf("-i ", StringComparison.Ordinal));
    }

    [Fact]
    public void DecodeOptions_Defaults_To_None()
    {
        var options = new DecodeOptions { InputVideo = string.Empty, OutputFile = string.Empty };
        Assert.Equal(HardwareAcceleration.None, options.HardwareAcceleration);
        Assert.Equal(HardwareAcceleration.None, new VideoCodecOptions().HardwareAcceleration);
    }

    [Fact]
    public async Task Decode_With_Default_Options_Still_RoundTrips_Through_The_Fake_Wrapper()
    {
        var tmpIn = Path.GetTempFileName();
        var tmpOut = Path.GetTempFileName();
        try
        {
            byte[] data = new byte[16];
            new Random(7).NextBytes(data);
            await File.WriteAllBytesAsync(tmpIn, data);

            var mod = new BinaryGridModulator();
            var fake = new FakeFFmpegWrapper(Width, Height, Fps);
            var encoder = new EncoderEngine(mod, fake, MacroblockSize, Width, Height, Fps);
            await encoder.EncodeAsync(tmpIn, "out.mp4");

            var buf = fake.Process?.Buffer;
            Assert.NotNull(buf);
            buf!.Position = 0;

            var decoder = new DecoderEngine(mod, fake, new DecodeOptions
            {
                InputVideo = "in.mp4",
                OutputFile = tmpOut,
                Width = Width,
                Height = Height,
                Fps = Fps,
                MacroblockSize = MacroblockSize
            });
            await decoder.DecodeFromRgbStreamAsync(buf, Width, Height, MacroblockSize, data.Length, tmpOut);

            var outData = await File.ReadAllBytesAsync(tmpOut);
            Assert.Equal(data, outData);

        }
        finally
        {
            File.Delete(tmpIn);
            File.Delete(tmpOut);
        }
    }

    [Fact]
    public async Task RealFfmpeg_Qsv_Decode_RoundTrips_When_Probe_Succeeds()
    {
        if (!File.Exists(ModernFfmpeg))
        {
            return; // no modern build on this machine; the CPU baseline is the reference
        }

        var report = await FFmpegCapabilities.ProbeHwaccelAsync(ModernFfmpeg, HardwareAcceleration.Qsv);
        if (!report.HwaccelAvailable)
        {
            return; // build does not list qsv; skip rather than fail
        }

        var payload = new byte[256];
        new Random(9901).NextBytes(payload);
        var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

        try
        {
            await File.WriteAllBytesAsync(inputFile, payload);

            var service = new YtahdCodecService(
                new BinaryGridModulator(),
                new DefaultFFmpegWrapperFactory(ModernFfmpeg));

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
                HardwareAcceleration = HardwareAcceleration.Qsv,
                DurabilityMatrixOptions = new DurabilityMatrixOptions { SymbolSize = 32, GroupSize = 4, ParitySymbolsPerGroup = 1 }
            });

            var decoded = await File.ReadAllBytesAsync(outputFile);
            Assert.True(payload.AsSpan().SequenceEqual(decoded), "qsv-decoded payload was not recovered exactly.");
            Assert.Equal(IntegrityStatus.Passed, service.LastDecodeMetrics.IntegrityStatus);
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
            if (File.Exists(outputVideo)) File.Delete(outputVideo);
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }
}
