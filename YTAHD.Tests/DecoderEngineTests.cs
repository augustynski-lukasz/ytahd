using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Modulation;
using YTAHD.Core.Core;

namespace YTAHD.Tests
{
    public class DecoderEngineTests
    {
        private const int Width = 128;
        private const int Height = 64;
        private const int Macroblock = 1;
        private const int HeaderBytes = 2 + 1 + 1 + 4 + 4 + 4 + 1 + 2 + 32;

        [Fact]
        public void TryParseFramePacket_Parses_Header_And_Payload()
        {
            var payload = new byte[] { 10, 20, 30, 40 };
            var packet = new byte[51 + payload.Length];
            packet[0] = 0x59;
            packet[1] = 0x54;
            packet[2] = 1;
            packet[3] = 0;
            packet[4] = 0;
            packet[5] = 0;
            packet[6] = 0;
            packet[7] = 7;
            packet[8] = 0;
            packet[9] = 0;
            packet[10] = 0;
            packet[11] = 12;
            packet[12] = 0;
            packet[13] = 0;
            packet[14] = 0;
            packet[15] = 3;
            packet[16] = 4;
            packet[17] = 0;
            packet[18] = 4;
            var hash = SHA256.HashData(payload);
            Buffer.BlockCopy(hash, 0, packet, 19, hash.Length);
            Buffer.BlockCopy(payload, 0, packet, 51, payload.Length);

            var parsed = DecoderEngine.TryParseFramePacket(packet, out var frameType, out var frameIndex, out var totalDataFrames, out var groupStart, out var groupCount, out var payloadLength, out var parsedPayload);

            Assert.True(parsed);
            Assert.Equal(0, frameType);
            Assert.Equal(7, frameIndex);
            Assert.Equal(12, totalDataFrames);
            Assert.Equal(3, groupStart);
            Assert.Equal(4, groupCount);
            Assert.Equal(4, payloadLength);
            Assert.Equal(payload, parsedPayload);
        }

        [Fact]
        public void FrameBitDecoderFactory_Uses_BinaryGrid_Strategy_For_Phase1()
        {
            var decoder = FrameBitDecoderFactory.CreateForModulator(new BinaryGridModulator());

            var frame = new byte[128 * 64 * 3];
            for (int i = 0; i < frame.Length; i += 3)
            {
                frame[i] = 255;
                frame[i + 1] = 255;
                frame[i + 2] = 255;
            }

            var packet = new byte[(128 * 64) / 8];
            decoder.Decode(frame, 128, 64, 1, 128 * 3, frame.Length, packet);

            Assert.NotEmpty(packet);
            Assert.All(packet, b => Assert.Equal((byte)0xFF, b));
        }

        [Fact]
        public void FramePacket_TryParse_Rejects_Invalid_Magic_And_Length_Values()
        {
            var packet = new byte[HeaderBytes + 4];
            packet[0] = 0x00;
            packet[1] = 0x00;
            packet[2] = 1;
            packet[3] = 0;
            packet[4] = 0;
            packet[5] = 0;
            packet[6] = 0;
            packet[7] = 0;
            packet[8] = 0;
            packet[9] = 0;
            packet[10] = 0;
            packet[11] = 1;
            packet[12] = 0;
            packet[13] = 0;
            packet[14] = 0;
            packet[15] = 0;
            packet[16] = 0;
            packet[17] = 0;
            packet[18] = 8;

            var result = FramePacket.TryParse(packet, out _, out _, out _, out _, out _, out _, out _);
            Assert.False(result);

            var invalidFrameType = new byte[HeaderBytes];
            invalidFrameType[0] = 0x59;
            invalidFrameType[1] = 0x54;
            invalidFrameType[2] = 1;
            invalidFrameType[3] = 2;
            invalidFrameType[8] = 0;
            invalidFrameType[9] = 0;
            invalidFrameType[10] = 0;
            invalidFrameType[11] = 1;
            invalidFrameType[12] = 0;
            invalidFrameType[13] = 0;
            invalidFrameType[14] = 0;
            invalidFrameType[15] = 0;
            invalidFrameType[16] = 1;
            invalidFrameType[17] = 0;
            invalidFrameType[18] = 0;

            var invalidFrameTypeResult = FramePacket.TryParse(invalidFrameType, out _, out _, out _, out _, out _, out _, out _);
            Assert.False(invalidFrameTypeResult);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(4)]
        [InlineData(16)]
        [InlineData(127)]
        public void EncoderPacketCompatibility_Matrix_Parses_And_Validates_Data_And_Parity_Packets(int payloadLength)
        {
            var payload = new byte[payloadLength];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)((i * 31 + 7) % 251);
            }

            var dataPacket = EncoderEngine.CreateDataFramePacket(7, 12, 3, 4, payload.Length, payload, payloadLength);
            var parsedData = DecoderEngine.TryParseFramePacket(dataPacket, out var dataType, out var dataFrameIndex, out var totalFrames, out var groupStart, out var groupCount, out var dataLen, out var parsedPayload);

            Assert.True(parsedData);
            Assert.Equal((byte)0, dataType);
            Assert.Equal(7, dataFrameIndex);
            Assert.Equal(12, totalFrames);
            Assert.Equal(3, groupStart);
            Assert.Equal(4, groupCount);
            Assert.Equal(payloadLength, dataLen);
            Assert.Equal(payload, parsedPayload);

            var parityPacket = EncoderEngine.CreateParityFramePacket(3, 4, 12, payload);
            var parsedParity = DecoderEngine.TryParseFramePacket(parityPacket, out var parityType, out var parityFrameIndex, out var parityTotalFrames, out var parityGroupStart, out var parityGroupCount, out var parityLength, out var parityPayload);

            Assert.True(parsedParity);
            Assert.Equal((byte)1, parityType);
            Assert.Equal(0, parityFrameIndex);
            Assert.Equal(12, parityTotalFrames);
            Assert.Equal(3, parityGroupStart);
            Assert.Equal(4, parityGroupCount);
            Assert.Equal(payloadLength, parityLength);
            Assert.Equal(payload, parityPayload);
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
                int bitIndex = HeaderBytes * 8; // first payload bit in packet
                int bx = bitIndex % Width;
                int by = bitIndex / Width;
                int pixelOffsetInFrame = by * Width * 3 + bx * 3;

                // A real lossy phase-1 stream may flip bits in the carrier without the decoder being able
                // to use the exact SHA-256 packet hash as a hard rejection boundary.
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
