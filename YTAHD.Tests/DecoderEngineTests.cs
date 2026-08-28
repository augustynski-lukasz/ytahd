using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Cli.Modulation;
using YTAHD.Cli.Core;

namespace YTAHD.Tests
{
    public class DecoderEngineTests
    {
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
                var encoder = new EncoderEngine(mod, fake, 1, 128, 64, 30);
                await encoder.VerifyAsync();
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var buf = fake.Process?.Buffer;
                Assert.NotNull(buf);
                buf.Position = 0;

                var decoder = new DecoderEngine(mod, fake);
                await decoder.DecodeFromRgbStreamAsync(buf, 128, 64, 1, data.Length, tmpOut);

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
        public async Task DecodeFromRgbStream_Dedup_PreservesIdenticalAdjacentPayloadFrames()
        {
            var tmpIn = Path.GetTempFileName();
            var tmpOut = Path.GetTempFileName();
            try
            {
                // 16 bytes with the current 128x64/16 geometry => 4 payload frames.
                // All-zero bytes produce identical payload frames, which de-dup must still expand correctly.
                byte[] data = new byte[16];
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(128, 64, 30);
                var encoder = new EncoderEngine(mod, fake, 1, 128, 64, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var buf = fake.Process?.Buffer;
                Assert.NotNull(buf);
                buf.Position = 0;

                var decoder = new DecoderEngine(mod, fake);
                await decoder.DecodeFromRgbStreamAsync(buf, 128, 64, 1, data.Length, tmpOut);

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
        public async Task DecodeFromRgbStream_Throws_When_FrameHashInvalid()
        {
            var tmpIn = Path.GetTempFileName();
            var tmpOut = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[16];
                new Random(42).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                const int width = 128;
                const int height = 64;
                const int macroblock = 1;
                const int headerBytes = 2 + 1 + 4 + 2 + 32;

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(width, height, 30);
                var encoder = new EncoderEngine(mod, fake, macroblock, width, height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var raw = fake.Process?.Buffer?.ToArray();
                Assert.NotNull(raw);

                int frameBytes = width * height * 3;
                int bitIndex = headerBytes * 8; // first payload bit in packet
                int bx = bitIndex % width;
                int by = bitIndex / width;
                int pixelOffsetInFrame = by * width * 3 + bx * 3;

                // Corrupt the same bit across all 3 repeated copies of the first logical frame.
                for (int rep = 0; rep < 3; rep++)
                {
                    int idx = rep * frameBytes + pixelOffsetInFrame;
                    raw[idx] = raw[idx] > 128 ? (byte)0 : (byte)255;
                }

                using var corruptedStream = new MemoryStream(raw, writable: false);
                var decoder = new DecoderEngine(mod, fake);

                var ex = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                    await decoder.DecodeFromRgbStreamAsync(corruptedStream, width, height, macroblock, data.Length, tmpOut));

                Assert.True(
                    ex.Message.Contains("Missing frame index", StringComparison.Ordinal) ||
                    ex.Message.Contains("incomplete", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                File.Delete(tmpIn);
                File.Delete(tmpOut);
            }
        }
    }
}
