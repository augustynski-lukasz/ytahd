using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    public class DecoderRecoveryTests
    {
        private const int Width = 128;
        private const int Height = 64;
        private const int Macroblock = 1;

        [Fact]
        public async Task DecodeFromRgbStream_Dedup_PreservesIdenticalAdjacentPayloadFrames()
        {
            var tmpIn = Path.GetTempFileName();
            var tmpOut = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[16];
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var buf = fake.Process?.Buffer;
                Assert.NotNull(buf);
                buf.Position = 0;

                var decoder = new DecoderEngine(mod, fake);
                await decoder.DecodeFromRgbStreamAsync(buf, Width, Height, Macroblock, data.Length, tmpOut);

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
        public async Task DecodeFromRgbStream_Tolerates_Lossy_BitFlips()
        {
            var tmpIn = Path.GetTempFileName();
            var tmpOut = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[16];
                new Random(42).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var raw = fake.Process?.Buffer?.ToArray();
                Assert.NotNull(raw);

                int frameBytes = Width * Height * 3;
                int bitIndex = (2 + 1 + 1 + 4 + 4 + 4 + 1 + 2 + 32) * 8;
                int bx = bitIndex % Width;
                int by = bitIndex / Width;
                int pixelOffsetInFrame = by * Width * 3 + bx * 3;

                for (int logicalFrame = 0; logicalFrame <= 1; logicalFrame++)
                {
                    for (int rep = 0; rep < 3; rep++)
                    {
                        int idx = (logicalFrame * 3 * frameBytes) + (rep * frameBytes) + pixelOffsetInFrame;
                        raw[idx] = raw[idx] > 128 ? (byte)0 : (byte)255;
                    }
                }

                using var corruptedStream = new MemoryStream(raw, writable: false);
                var decoder = new DecoderEngine(mod, fake);

                await decoder.DecodeFromRgbStreamAsync(corruptedStream, Width, Height, Macroblock, data.Length, tmpOut);

                var outData = await File.ReadAllBytesAsync(tmpOut);
                Assert.Equal(data.Length, outData.Length);
            }
            finally
            {
                File.Delete(tmpIn);
                File.Delete(tmpOut);
            }
        }

        [Fact]
        public async Task DecodeFromRgbStream_Skips_Invalid_Packets_And_Continues()
        {
            var tmpIn = Path.GetTempFileName();
            var tmpOut = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[16];
                new Random(101).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var raw = fake.Process?.Buffer?.ToArray();
                Assert.NotNull(raw);

                int frameBytes = Width * Height * 3;
                int invalidFrameOffset = 2 * frameBytes;
                Array.Clear(raw, invalidFrameOffset, frameBytes);

                using var stream = new MemoryStream(raw, writable: false);
                var decoder = new DecoderEngine(mod, fake);

                await decoder.DecodeFromRgbStreamAsync(stream, Width, Height, Macroblock, data.Length, tmpOut);

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
        public async Task DecodeFromRgbStream_Recovers_When_OneDataFrameLostInParityGroup()
        {
            var tmpIn = Path.GetTempFileName();
            var tmpOut = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[3000];
                new Random(7).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var raw = fake.Process?.Buffer?.ToArray();
                Assert.NotNull(raw);

                int frameBytes = Width * Height * 3;
                int bitsPerFrame = Width * Height;
                int payloadBytesPerFrame = (bitsPerFrame - ((2 + 1 + 1 + 4 + 4 + 4 + 1 + 2 + 32) * 8)) / 8;
                int totalDataFrames = (data.Length + payloadBytesPerFrame - 1) / payloadBytesPerFrame;

                int droppedDataFrameIndex = 1;
                byte[] dropped = DecoderTestHelpers.RemoveLogicalFrameCopies(raw, frameBytes, repeatsPerLogicalFrame: 3, droppedDataFrameIndex);

                using var droppedStream = new MemoryStream(dropped, writable: false);
                var decoder = new DecoderEngine(mod, fake);
                await decoder.DecodeFromRgbStreamAsync(droppedStream, Width, Height, Macroblock, data.Length, tmpOut);

                var outData = await File.ReadAllBytesAsync(tmpOut);
                Assert.Equal(data.Length, outData.Length);
                Assert.Equal(data, outData);
                Assert.True(totalDataFrames >= 4);
            }
            finally
            {
                File.Delete(tmpIn);
                File.Delete(tmpOut);
            }
        }

        [Fact]
        public async Task DecodeFromRgbStream_Throws_When_TwoDataFramesLostInSameParityGroup()
        {
            var tmpIn = Path.GetTempFileName();
            var tmpOut = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[3000];
                new Random(17).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var raw = fake.Process?.Buffer?.ToArray();
                Assert.NotNull(raw);

                int frameBytes = Width * Height * 3;
                byte[] dropped = DecoderTestHelpers.RemoveLogicalFrameCopies(raw, frameBytes, repeatsPerLogicalFrame: 3, 0, 1);

                using var droppedStream = new MemoryStream(dropped, writable: false);
                var decoder = new DecoderEngine(mod, fake);

                var ex = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                    await decoder.DecodeFromRgbStreamAsync(droppedStream, Width, Height, Macroblock, data.Length, tmpOut));

                Assert.Contains("cannot recover more than one loss per group", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                File.Delete(tmpIn);
                File.Delete(tmpOut);
            }
        }

        [Fact]
        public async Task DecodeFromRgbStream_Recovers_When_TwoPhysicalCopiesLostOfSameLogicalFrame()
        {
            var tmpIn = Path.GetTempFileName();
            var tmpOut = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[3000];
                new Random(23).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var raw = fake.Process?.Buffer?.ToArray();
                Assert.NotNull(raw);

                int frameBytes = Width * Height * 3;
                byte[] dropped = DecoderTestHelpers.RemovePhysicalFrames(raw, frameBytes, 1, 2);

                using var droppedStream = new MemoryStream(dropped, writable: false);
                var decoder = new DecoderEngine(mod, fake);
                await decoder.DecodeFromRgbStreamAsync(droppedStream, Width, Height, Macroblock, data.Length, tmpOut);

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
