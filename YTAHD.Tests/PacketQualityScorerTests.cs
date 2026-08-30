using System;
using Xunit;
using YTAHD.Core.Core;

namespace YTAHD.Tests
{
    public class PacketQualityScorerTests
    {
        [Fact]
        public void Score_Returns_Zero_For_Invalid_Frame_Header()
        {
            var score = PacketQualityScorer.Score(new byte[] { 0x00, 0x00, 0x00, 0x00 });

            Assert.Equal(0, score);
        }

        [Fact]
        public void Score_Prefers_Stronger_Valid_Packet_Over_Weaker_One()
        {
            var strong = new byte[FramePacket.HeaderBytes + 8];
            var weak = new byte[FramePacket.HeaderBytes + 8];

            FillPacket(strong, 0, 2, 4, 0, 8, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
            FillPacket(weak, 0, 2, 4, 0, 8, new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

            Assert.True(PacketQualityScorer.Score(strong) > PacketQualityScorer.Score(weak));
        }

        [Fact]
        public void IsFramePacketValid_Fails_When_Length_Or_Metadata_Is_Invalid()
        {
            var packet = new byte[FramePacket.HeaderBytes + 4];
            FillPacket(packet, 0, 0, 0, 0, 4, new byte[] { 0x01, 0x02, 0x03, 0x04 });

            Assert.False(PacketQualityScorer.IsFramePacketValid(packet));
        }

        private static void FillPacket(byte[] packet, byte frameType, int frameIndex, int totalDataFrames, int groupStart, int payloadLength, byte[] payload)
        {
            packet[0] = (byte)((FramePacket.FrameMagic >> 8) & 0xFF);
            packet[1] = (byte)(FramePacket.FrameMagic & 0xFF);
            packet[2] = FramePacket.FrameVersion;
            packet[3] = frameType;
            packet[4] = (byte)((frameIndex >> 24) & 0xFF);
            packet[5] = (byte)((frameIndex >> 16) & 0xFF);
            packet[6] = (byte)((frameIndex >> 8) & 0xFF);
            packet[7] = (byte)(frameIndex & 0xFF);
            packet[8] = (byte)((totalDataFrames >> 24) & 0xFF);
            packet[9] = (byte)((totalDataFrames >> 16) & 0xFF);
            packet[10] = (byte)((totalDataFrames >> 8) & 0xFF);
            packet[11] = (byte)(totalDataFrames & 0xFF);
            packet[12] = (byte)((groupStart >> 24) & 0xFF);
            packet[13] = (byte)((groupStart >> 16) & 0xFF);
            packet[14] = (byte)((groupStart >> 8) & 0xFF);
            packet[15] = (byte)(groupStart & 0xFF);
            packet[16] = (byte)1;
            packet[17] = (byte)((payloadLength >> 8) & 0xFF);
            packet[18] = (byte)(payloadLength & 0xFF);

            Array.Copy(payload, 0, packet, FramePacket.HeaderBytes, payload.Length);
        }
    }
}
