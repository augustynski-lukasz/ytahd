using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using YTAHD.Core.Application;
using YTAHD.Core.Infrastructure;
using YTAHD.Core.Modulation;

var ffmpeg = "ffmpeg";
var mods = new (string Name, IModulator Modulator)[]
{
    ("phase1", new BinaryGridModulator()),
    ("phase2", new PseudoQamModulator()),
    ("phase3", new DctModulator())
};
foreach (var (name, mod) in mods)
{
    var input = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
    var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
    var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");
    var payload = new byte[1024];
    new Random(123 + name.Length).NextBytes(payload);
    await File.WriteAllBytesAsync(input, payload);
    var service = new YtahdCodecService(mod, new DefaultFFmpegWrapperFactory(ffmpeg));
    try
    {
        await service.EncodeAsync(new EncodeOptions { InputFile = input, OutputVideo = outputVideo, Width = 640, Height = 480, MacroblockSize = 16, Fps = 30, VerifyFfmpeg = true });
        await service.DecodeAsync(new DecodeOptions { InputVideo = outputVideo, OutputFile = outputFile, Width = 640, Height = 480, MacroblockSize = 16, Fps = 30, VerifyFfmpeg = true });
        var decoded = await File.ReadAllBytesAsync(outputFile);
        Console.WriteLine($"{name}: success={payload.SequenceEqual(decoded)} payload={payload.Length} decoded={decoded.Length} frames={service.LastDecodeMetrics.TotalFramesDecoded} totalBytes={service.LastDecodeMetrics.TotalDecodedPayloadBytes}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{name}: EXCEPTION {ex.GetType().Name}: {ex.Message}");
    }
    finally
    {
        if (File.Exists(input)) File.Delete(input);
        if (File.Exists(outputVideo)) File.Delete(outputVideo);
        if (File.Exists(outputFile)) File.Delete(outputFile);
    }
}
