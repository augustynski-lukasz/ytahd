using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Modulation;
using YTAHD.Core.Core;
using YTAHD.Core.Infrastructure;

namespace YTAHD.Tests
{
    public class EncoderEngineTests
    {
        [Fact]
        public async Task Writes_Frames_To_FakeFFmpeg()
        {
            var tmp = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[32];
                new Random(1).NextBytes(data);
                await File.WriteAllBytesAsync(tmp, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(128, 64, 30);
                var engine = new EncoderEngine(mod, fake, 1, 128, 64, 30);
                await engine.VerifyAsync();
                await engine.EncodeAsync(tmp, "out.mp4");

                Assert.True(fake.WrittenBytes > 0);
            }
            finally
            {
                File.Delete(tmp);
            }
        }
    }
}
