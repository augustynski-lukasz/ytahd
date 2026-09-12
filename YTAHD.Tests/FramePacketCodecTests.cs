using System;
using System.Security.Cryptography;
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
        public void TryDecode_Rejects_Invalid_Magic_Or_length()
        {
            var badHeader = new byte[FramePacket.HeaderBytes];
            badHeader[0] = 0x00;
            badHeader[1] = 0x00;
            badHeader[2] = 0xFF;

            Assert.False(FramePacketCodec.TryDecode(badHeader, out _, out _, out _, out _, out _, out _, out _));

            var shortPacket = new byte[FramePacket.HeaderBytes - 1];
            Assert.False(FramePacketCodec.TryDecode(shortPacket, out _, out _, out _, out _, out _, out _, out _));
        }

        [Fact]
        public void CreateDataFramePacket_RoundTrips_PayloadLength_Above_V1_HeaderLimit()
        {
            var payload = new byte[ushort.MaxValue + 1234];
            new Random(7).NextBytes(payload);

            var packet = FramePacketCodec.CreateDataFramePacket(0, 1, 0, 1, payload.Length, payload, payload.Length);

            Assert.True(FramePacketCodec.TryDecode(packet, out _, out _, out _, out _, out _, out var payloadLength, out var decodedPayload));
            Assert.Equal(FramePacket.FrameVersion, packet[2]);
            Assert.Equal(payload.Length, payloadLength);
            Assert.Equal(payload, decodedPayload);
        }

        [Fact]
        public void TryDecode_Recovers_LegacyOversizedPayloadLength_From_Hash()
        {
            var payload = new byte[ushort.MaxValue + 1234];
            new Random(42).NextBytes(payload);

            var packet = new byte[FramePacket.LegacyHeaderBytes + payload.Length];
            packet[0] = (byte)((FramePacket.FrameMagic >> 8) & 0xFF);
            packet[1] = (byte)(FramePacket.FrameMagic & 0xFF);
            packet[2] = FramePacket.LegacyFrameVersion;
            packet[3] = FramePacket.FrameTypeData;
            packet[11] = 1;
            packet[16] = 1;
            int truncatedPayloadLength = payload.Length & 0xFFFF;
            packet[17] = (byte)((truncatedPayloadLength >> 8) & 0xFF);
            packet[18] = (byte)(truncatedPayloadLength & 0xFF);
            var hash = SHA256.HashData(payload);
            Buffer.BlockCopy(hash, 0, packet, 19, hash.Length);
            Buffer.BlockCopy(payload, 0, packet, FramePacket.LegacyHeaderBytes, payload.Length);

            Assert.True(FramePacketCodec.TryDecode(packet, out _, out _, out _, out _, out _, out var payloadLength, out var decodedPayload));
            Assert.Equal(payload.Length, payloadLength);
            Assert.Equal(payload, decodedPayload);
        }

        [Fact]
        public void TryDecode_Accepts_V2Packet_With_Matching_Payload_Hash()
        {
            var payload = new byte[256];
            new Random(11).NextBytes(payload);

            var packet = FramePacketCodec.CreateDataFramePacket(3, 10, 0, 4, payload.Length, payload, payload.Length);

            Assert.True(FramePacketCodec.TryDecode(packet, out _, out _, out _, out _, out _, out var payloadLength, out var decodedPayload));
            Assert.Equal(payload.Length, payloadLength);
            Assert.Equal(payload, decodedPayload);
        }

        [Fact]
        public void TryDecode_Rejects_V2Packet_With_Corrupted_Payload()
        {
            var payload = new byte[256];
            new Random(12).NextBytes(payload);

            var packet = FramePacketCodec.CreateDataFramePacket(3, 10, 0, 4, payload.Length, payload, payload.Length);

            // Flip one payload byte after the header hash was written: the header parses, the
            // declared length is intact, but the payload no longer matches the stored hash.
            packet[FramePacket.HeaderBytes + 100] ^= 0xFF;

            Assert.False(FramePacketCodec.TryDecode(packet, out _, out _, out _, out _, out _, out _, out _));
        }

        [Fact]
        public void TryDecode_Rejects_V2Packet_With_Corrupted_Declared_length()
        {
            var payload = new byte[256];
            new Random(13).NextBytes(payload);

            var packet = FramePacketCodec.CreateDataFramePacket(3, 10, 0, 4, payload.Length, payload, payload.Length);

            // A corrupted declared length must not silently truncate the payload: the hash is
            // computed over the declared slice, so a wrong length fails the hash check.
            packet[20] ^= 0x01;

            Assert.False(FramePacketCodec.TryDecode(packet, out _, out _, out _, out _, out _, out _, out _));
        }

        [Fact]
        public void TryDecode_Rejects_V2ParityPacket_With_Corrupted_Payload()
        {
            var parity = new byte[64];
            new Random(14).NextBytes(parity);

            var packet = FramePacketCodec.CreateParityFramePacket(0, 4, 8, parity);
            packet[FramePacket.HeaderBytes + 10] ^= 0xFF;

            Assert.False(FramePacketCodec.TryDecode(packet, out _, out _, out _, out _, out _, out _, out _));
        }

        [Fact]
        public void TryDecodeWithTolerance_Rejects_V2Packet_With_Corrupted_Payload()
        {
            var payload = new byte[256];
            new Random(15).NextBytes(payload);

            var packet = FramePacketCodec.CreateDataFramePacket(3, 10, 0, 4, payload.Length, payload, payload.Length);
            packet[FramePacket.HeaderBytes + 100] ^= 0xFF;

            Assert.False(FramePacketCodec.TryDecodeWithTolerance(packet, out _, out _, out _, out _, out _, out _, out _));
        }
    }
}
