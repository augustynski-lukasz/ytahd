using System;
using Xunit;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    public class BinaryGridModulatorTests
    {
        [Fact]
        public void Default_Macroblock_Size_Matches_Phase1_Reference()
        {
            var mod = new BinaryGridModulator();
            Assert.Equal(16, mod.MacroblockWidth);
            Assert.Equal(16, mod.MacroblockHeight);
        }

        [Fact]
        public void Roundtrip_Simple()
        {
            var mod = new BinaryGridModulator();
            byte[] input = { 0x5A };
            var pixelBuffer = new byte[mod.MacroblockWidth * mod.MacroblockHeight * 1];
            mod.Encode(input, pixelBuffer);
            var output = new byte[1];
            mod.Decode(pixelBuffer, output);
            Assert.Equal(input[0], output[0]);
        }

        [Fact]
        public void Phase1_Decoder_RoundTrips_A_Valid_Packet_From_A_Rgba_Frame()
        {
            var payload = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80 };
            var packet = FramePacketCodec.CreateDataFramePacket(0, 1, 0, 1, payload.Length, payload, payload.Length);
            var mod = new BinaryGridModulator(16, 16);
            var frame = mod.CreateFrame(640, 480, 0, packet);

            var decoded = new byte[FramePacket.HeaderBytes + payload.Length];
            var decoder = FrameBitDecoderFactory.CreateForModulator(mod);
            decoder.Decode(frame, 640, 480, 16, 640 * 4, frame.Length, decoded, 0);

            Assert.True(FramePacket.TryParse(decoded, out _, out _, out _, out _, out _, out var payloadLength, out var actualPayload));
            Assert.Equal(payload.Length, payloadLength);
            Assert.Equal(payload, actualPayload);
        }

        [Fact]
        public void Phase1_Decoder_Still_Recovers_A_Valid_Packet_When_Luminance_Is_Lossy_And_Threshold_Shifts()
        {
            var payload = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA };
            var packet = FramePacketCodec.CreateDataFramePacket(0, 1, 0, 1, payload.Length, payload, payload.Length);
            var mod = new BinaryGridModulator(1, 1);
            var frame = mod.CreateFrame(32, 32, 0, packet);

            for (int i = 0; i < frame.Length; i += 4)
            {
                byte current = frame[i];
                if (current == 0)
                {
                    frame[i] = (byte)Math.Clamp(current + 18, 0, 255);
                    frame[i + 1] = (byte)Math.Clamp(frame[i + 1] + 18, 0, 255);
                    frame[i + 2] = (byte)Math.Clamp(frame[i + 2] + 18, 0, 255);
                }
                else if (current == 255)
                {
                    frame[i] = (byte)Math.Clamp(current - 18, 0, 255);
                    frame[i + 1] = (byte)Math.Clamp(frame[i + 1] - 18, 0, 255);
                    frame[i + 2] = (byte)Math.Clamp(frame[i + 2] - 18, 0, 255);
                }
            }

            var decoded = new byte[FramePacket.HeaderBytes + payload.Length];
            var decoder = FrameBitDecoderFactory.CreateForModulator(mod);
            decoder.Decode(frame, 32, 32, 1, 32 * 4, frame.Length, decoded, 0);

            Assert.True(FramePacket.TryParse(decoded, out _, out _, out _, out _, out _, out var payloadLength, out var actualPayload));
            Assert.Equal(payload.Length, payloadLength);
            Assert.Equal(payload, actualPayload);
        }

        [Fact]
        public async System.Threading.Tasks.Task Phase1_Decoder_Uses_The_Actual_Rgb24_Stream_Contract_When_Parsing_Frame_Data()
        {
            var payload = new byte[] { 0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01 };
            var packet = FramePacketCodec.CreateDataFramePacket(0, 1, 0, 1, payload.Length, payload, payload.Length);
            var mod = new BinaryGridModulator(16, 16);
            var rgbaFrame = mod.CreateFrame(640, 480, 0, packet);
            var rgbFrame = FrameProtocolHelpers.ConvertRgbaToRgb(rgbaFrame, 640, 480);

            var decoded = new byte[FramePacket.HeaderBytes + payload.Length];
            var decoder = FrameBitDecoderFactory.CreateForModulator(mod);
            decoder.Decode(rgbFrame, 640, 480, 16, 640 * 3, rgbFrame.Length, decoded, 0);

            Assert.True(FramePacket.TryParse(decoded, out _, out _, out _, out _, out _, out var decodedLength, out var actualPayload));
            Assert.Equal(payload.Length, decodedLength);
            Assert.Equal(payload, actualPayload);

            var repeated = new byte[rgbFrame.Length * 3];
            Buffer.BlockCopy(rgbFrame, 0, repeated, 0, rgbFrame.Length);
            Buffer.BlockCopy(rgbFrame, 0, repeated, rgbFrame.Length, rgbFrame.Length);
            Buffer.BlockCopy(rgbFrame, 0, repeated, rgbFrame.Length * 2, rgbFrame.Length);

            using var stream = new System.IO.MemoryStream(repeated, writable: false);
            var orchestrator = new DecodeStreamOrchestrator(mod, 640, 480, 16);
            var output = await orchestrator.ProcessAsync(stream, payload.Length);
            Assert.Equal(payload, output);
        }
    }
}
