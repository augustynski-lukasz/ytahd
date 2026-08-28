using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Cli.Modulation;
using YTAHD.Cli.Audio;
using YTAHD.Cli.Core;
using YTAHD.Cli.Infrastructure;

namespace YTAHD.Tests
{
    public class HappyPathTests
    {
        [Fact]
        public void BinaryGridModulator_Roundtrip_Simple()
        {
            var mod = new BinaryGridModulator();
            byte[] input = { 0x5A };
            var pixelBuffer = new byte[mod.MacroblockWidth * mod.MacroblockHeight * 1];
            mod.Encode(input, pixelBuffer);
            var output = new byte[1];
            mod.Decode(pixelBuffer, output);
            Assert.Equal(input[0], output[0]);
        }

        [Fact]
        public async Task FskGenerator_Generates_Silence_Length()
        {
            using var ms = new MemoryStream();
            var duration = TimeSpan.FromSeconds(0.1); // 0.1s
            await FskGenerator.GenerateSilenceAsync(ms, duration);
            // expected bytes = samples * 2
            int expectedSamples = (int)(FskGenerator.SampleRate * duration.TotalSeconds);
            Assert.Equal(expectedSamples * 2, (int)ms.Length);
        }

        [Fact]
        public async Task EncoderEngine_Writes_Frames_To_FakeFFmpeg()
        {
            // create a tiny input file
            var tmp = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[32];
                new Random(1).NextBytes(data);
                await File.WriteAllBytesAsync(tmp, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(128, 64, 30);
                var engine = new EncoderEngine(mod, fake, 16, 128, 64, 30);
                await engine.VerifyAsync();
                await engine.EncodeAsync(tmp, "out.mp4");

                // ensure some bytes were written to the fake process
                Assert.True(fake.WrittenBytes > 0);
            }
            finally
            {
                File.Delete(tmp);
            }
        }
    }

    // Test fake implementations
    internal class FakeFFmpegWrapper : IFFmpegWrapper
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _fps;
        public long WrittenBytes => _process?.WrittenBytes ?? 0;
        public FakeFFmpegProcess? Process => _process;
        private FakeFFmpegProcess? _process;

        public FakeFFmpegWrapper(int width, int height, int fps)
        {
            _width = width; _height = height; _fps = fps;
        }

        public Task<bool> IsAvailableAsync() => Task.FromResult(true);

        public Task<IFFmpegProcess> StartAsync(string outputPath)
        {
            _process = new FakeFFmpegProcess();
            return Task.FromResult<IFFmpegProcess>(_process);
        }
    }

    internal class FakeFFmpegProcess : IFFmpegProcess
    {
        private readonly MemoryStream _ms = new MemoryStream();
        public Stream StandardInput => _ms;
        public MemoryStream Buffer => _ms;
        public long WrittenBytes => _ms.Length;
        public Task WaitForExitAsync() => Task.CompletedTask;
        public void Dispose()
        {
            // Intentionally do not dispose the memory stream so tests can inspect it after encoding completes.
        }
    }

    [Fact]
    public async Task EncoderDecoder_RoundTrip_FakeFFmpeg()
    {
        var tmpIn = Path.GetTempFileName();
        var tmpOut = Path.GetTempFileName();
        try
        {
            byte[] data = new byte[16];
            new Random(2).NextBytes(data);
            await File.WriteAllBytesAsync(tmpIn, data);

            var mod = new BinaryGridModulator();
            var fake = new FakeFFmpegWrapper(128, 64, 30);
            var encoder = new EncoderEngine(mod, fake, 16, 128, 64, 30);
            await encoder.VerifyAsync();
            await encoder.EncodeAsync(tmpIn, "out.mp4");

            // get the raw RGB bytes from fake process
            var buf = fake.Process?.Buffer;
            Assert.NotNull(buf);
            buf.Position = 0;

            var decoder = new DecoderEngine(mod, fake);
            await decoder.DecodeFromRgbStreamAsync(buf, 128, 64, 16, data.Length, tmpOut);

            var outData = await File.ReadAllBytesAsync(tmpOut);
            Assert.Equal(data, outData);
        }
        finally
        {
            File.Delete(tmpIn);
            File.Delete(tmpOut);
        }
    }
}
