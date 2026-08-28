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
        private const int Width = 128;
        private const int Height = 64;
        private const int Macroblock = 1;
        private const int HeaderBytes = 2 + 1 + 1 + 4 + 4 + 4 + 1 + 2 + 32;

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
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.VerifyAsync();
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
        public async Task DecodeFromRgbStream_Throws_When_FrameHashInvalid()
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
                int bitIndex = HeaderBytes * 8; // first payload bit in packet
                int bx = bitIndex % Width;
                int by = bitIndex / Width;
                int pixelOffsetInFrame = by * Width * 3 + bx * 3;

                // Corrupt the same bit across all 3 repeated copies of:
                // - logical frame 0 (data)
                // - logical frame 1 (parity)
                // so parity recovery cannot reconstruct the missing/corrupt data frame.
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

                var ex = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                    await decoder.DecodeFromRgbStreamAsync(corruptedStream, Width, Height, Macroblock, data.Length, tmpOut));

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

        [Fact]
        public async Task DecodeFromRgbStream_Recovers_When_OneDataFrameLostInParityGroup()
        {
            var tmpIn = Path.GetTempFileName();
            var tmpOut = Path.GetTempFileName();
            try
            {
                // Ensure multiple data frames so parity recovery is exercised.
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
                int payloadBytesPerFrame = (bitsPerFrame - (HeaderBytes * 8)) / 8;
                int totalDataFrames = (data.Length + payloadBytesPerFrame - 1) / payloadBytesPerFrame;

                // Layout per group: data frames followed by one parity frame, each repeated 3x.
                // Drop the 3 repeated physical frames of logical data frame #1.
                int droppedDataFrameIndex = 1;
                byte[] dropped = RemoveLogicalFrameCopies(raw, frameBytes, repeatsPerLogicalFrame: 3, droppedDataFrameIndex);

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
                byte[] dropped = RemoveLogicalFrameCopies(raw, frameBytes, repeatsPerLogicalFrame: 3, 0, 1);

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

                // Drop 2 of the 3 repeated physical frames for logical data frame #0.
                // Remaining single copy should still decode correctly.
                byte[] dropped = RemovePhysicalFrames(raw, frameBytes, 1, 2);

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

        private static byte[] RemoveLogicalFrameCopies(byte[] raw, int frameBytes, int repeatsPerLogicalFrame, params int[] logicalFrameIndexes)
        {
            if (logicalFrameIndexes == null || logicalFrameIndexes.Length == 0)
            {
                return raw;
            }

            int logicalSize = frameBytes * repeatsPerLogicalFrame;
            Array.Sort(logicalFrameIndexes);

            var output = new byte[raw.Length - (logicalSize * logicalFrameIndexes.Length)];
            int srcPos = 0;
            int dstPos = 0;

            foreach (int logicalFrameIndex in logicalFrameIndexes)
            {
                int start = logicalFrameIndex * logicalSize;
                if (start < srcPos || start + logicalSize > raw.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(logicalFrameIndexes));
                }

                int copyLen = start - srcPos;
                if (copyLen > 0)
                {
                    Buffer.BlockCopy(raw, srcPos, output, dstPos, copyLen);
                    dstPos += copyLen;
                }

                srcPos = start + logicalSize;
            }

            if (srcPos < raw.Length)
            {
                Buffer.BlockCopy(raw, srcPos, output, dstPos, raw.Length - srcPos);
            }

            return output;
        }

        private static byte[] RemovePhysicalFrames(byte[] raw, int frameBytes, params int[] physicalFrameIndexes)
        {
            if (physicalFrameIndexes == null || physicalFrameIndexes.Length == 0)
            {
                return raw;
            }

            Array.Sort(physicalFrameIndexes);
            var output = new byte[raw.Length - (frameBytes * physicalFrameIndexes.Length)];
            int srcPos = 0;
            int dstPos = 0;

            foreach (int physicalFrameIndex in physicalFrameIndexes)
            {
                int start = physicalFrameIndex * frameBytes;
                if (start < srcPos || start + frameBytes > raw.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(physicalFrameIndexes));
                }

                int copyLen = start - srcPos;
                if (copyLen > 0)
                {
                    Buffer.BlockCopy(raw, srcPos, output, dstPos, copyLen);
                    dstPos += copyLen;
                }

                srcPos = start + frameBytes;
            }

            if (srcPos < raw.Length)
            {
                Buffer.BlockCopy(raw, srcPos, output, dstPos, raw.Length - srcPos);
            }

            return output;
        }
    }
}
