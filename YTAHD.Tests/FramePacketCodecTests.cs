using System;
using Xunit;
using YTAHD.Core.Core;

namespace YTAHD.Tests
{
    public class FramePacketCodecTests
    {
        [Fact]
        public void CreateDataFramePacket_RoundTrips_Header_And_Payload()
        {
            var payload = new byte[] { 0x10, 0x20, 0x30, 0x40 };
            var packet = FramePacketCodec.CreateDataFramePacket(7, 42, 8, 3, payload.Length, payload, payload.Length);

            Assert.True(FramePacketCodec.TryDecode(packet, out var frameType, out var frameIndex, out var totalDataFrames, out var groupStart, out var groupCount, out var payloadLength, out var decodedPayload));
            Assert.Equal(FramePacket.FrameTypeData, frameType);
            Assert.Equal(7, frameIndex);
            Assert.Equal(42, totalDataFrames);
            Assert.Equal(8, groupStart);
            Assert.Equal(3, groupCount);
            Assert.Equal(payload.Length, payloadLength);
            Assert.Equal(payload, decodedPayload);
        }

        [Fact]
        public void TryDecode_Rejects_Invalid_Magic_Or_Length()
        {
            var badHeader = new byte[FramePacket.HeaderBytes];
            badHeader[0] = 0x00;
            badHeader[1] = 0x00;
            badHeader[2] = 0xFF;

            Assert.False(FramePacketCodec.TryDecode(badHeader, out _, out _, out _, out _, out _, out _, out _));

            var shortPacket = new byte[FramePacket.HeaderBytes - 1];
            Assert.False(FramePacketCodec.TryDecode(shortPacket, out _, out _, out _, out _, out _, out _, out _));
        }
    }
}
