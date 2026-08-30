using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Application;
using YTAHD.Core.Infrastructure;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    public class YtahdCodecServiceTests
    {
        [Fact]
        public async Task FFmpegWrapper_UsesExplicitExecutablePath_WhenProvided()
        {
            var ffmpegPath = "D:\\!Tools\\ffmpeg-20151019\\bin\\ffmpeg.exe";
            var wrapper = new FFmpegWrapper(ffmpegExecutablePath: ffmpegPath);

            var isAvailable = await wrapper.IsAvailableAsync();

            Assert.True(isAvailable);
        }

        [Fact]
        public async Task EncodeAsync_WritesData_UsingServiceApi()
        {
            var tmpIn = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[64];
                new Random(123).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(128, 64, 30);
                var service = new YtahdCodecService(mod, new FakeFFmpegWrapperFactory(fake));

                await service.EncodeAsync(new EncodeOptions
                {
                    InputFile = tmpIn,
                    OutputVideo = "out.mp4",
                    Width = 128,
                    Height = 64,
                    MacroblockSize = 1,
                    Fps = 30,
                    VerifyFfmpeg = true
                });

                Assert.True(fake.WrittenBytes > 0);
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }

        [Fact]
        public async Task DecodeFromRgbStreamAsync_RoundTrips_UsingServiceApi()
        {
            var tmpIn = Path.GetTempFileName();
            var tmpOut = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[256];
                new Random(222).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(128, 64, 30);
                var service = new YtahdCodecService(mod, new FakeFFmpegWrapperFactory(fake));

                await service.EncodeAsync(new EncodeOptions
                {
                    InputFile = tmpIn,
                    OutputVideo = "out.mp4",
                    Width = 128,
                    Height = 64,
                    MacroblockSize = 1,
                    Fps = 30,
                    VerifyFfmpeg = false
                });

                var raw = fake.Process?.Buffer;
                Assert.NotNull(raw);
                raw.Position = 0;

                await service.DecodeFromRgbStreamAsync(new DecodeRgbOptions
                {
                    RgbStream = raw,
                    Width = 128,
                    Height = 64,
                    MacroblockSize = 1,
                    ExpectedOutputBytes = data.Length,
                    OutputFile = tmpOut
                });

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
}
