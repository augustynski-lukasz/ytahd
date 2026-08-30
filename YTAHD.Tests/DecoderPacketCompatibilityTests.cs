using System;
using System.IO;
using System.Security.Cryptography;
using Xunit;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    public class DecoderPacketCompatibilityTests
    {
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
        public void PacketQualityScore_Prefers_Stronger_Valid_Frame_Within_Duplicate_Run()
        {
            var strongPayload = new byte[32];
            var weakPayload = new byte[32];
            for (int i = 0; i < strongPayload.Length; i++)
            {
                strongPayload[i] = (byte)((i * 17 + 3) % 251);
                weakPayload[i] = (byte)(i % 3);
            }

            var strongPacket = EncoderEngine.CreateDataFramePacket(0, 2, 0, 2, strongPayload.Length, strongPayload, strongPayload.Length);
            var weakPacket = EncoderEngine.CreateDataFramePacket(0, 2, 0, 2, weakPayload.Length, weakPayload, weakPayload.Length);

            var strongScore = DecoderEngine.GetPacketQualityScore(strongPacket);
            var weakScore = DecoderEngine.GetPacketQualityScore(weakPacket);

            Assert.True(strongScore > weakScore);
            Assert.True(strongScore > 0);
            Assert.True(weakScore > 0);
        }
    }
}
