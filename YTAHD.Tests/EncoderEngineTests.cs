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
        public void Modulator_Capacity_Is_Specific_To_Modulation_Mode()
        {
            const int width = 3840;
            const int height = 2160;
            const int headerBytes = 51;

            var binary = new BinaryGridModulator();
            var pseudo = new PseudoQamModulator();
            var dct = new DctModulator();

            int binaryBytes = binary.GetPayloadBytesPerFrame(width, height, headerBytes, borderWidth: 0);
            int pseudoBytes = pseudo.GetPayloadBytesPerFrame(width, height, headerBytes, borderWidth: 32);
            int dctBytes = dct.GetPayloadBytesPerFrame(width, height, headerBytes, borderWidth: 32);

            Assert.True(binaryBytes > 0);
            Assert.True(pseudoBytes > 0);
            Assert.True(dctBytes > 0);
            Assert.True(binaryBytes != pseudoBytes);
            Assert.True(pseudoBytes != dctBytes);
        }

        [Fact]
        public void BinaryGrid_PerFrame_Capacity_Matches_FrameLayout_Contract()
        {
            const int width = 640;
            const int height = 480;
            const int headerBytes = 51;

            var mod = new BinaryGridModulator();
            int expected = FrameLayoutCalculator.CalculatePayloadBytesPerFrame(width, height, mod.MacroblockWidth, mod.MacroblockHeight, headerBytes, borderWidth: 0);
            int actual = mod.GetPayloadBytesPerFrame(width, height, headerBytes, borderWidth: 0, macroblockSize: mod.MacroblockWidth);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void PacketBufferLength_Is_Derived_From_Modulator_Contract()
        {
            const int width = 640;
            const int height = 480;
            const int headerBytes = 51;

            var binary = new BinaryGridModulator();
            var pseudo = new PseudoQamModulator();

            int binaryPayload = binary.GetPayloadBytesPerFrame(width, height, headerBytes, borderWidth: 0, macroblockSize: binary.MacroblockWidth);
            int pseudoPayload = pseudo.GetPayloadBytesPerFrame(width, height, headerBytes, borderWidth: 0, macroblockSize: pseudo.MacroblockWidth);

            Assert.Equal(headerBytes + binaryPayload, binary.GetPacketBufferLength(width, height, headerBytes, binaryPayload, 0, binary.MacroblockWidth));
            Assert.Equal(Math.Max((width / pseudo.MacroblockWidth) * (height / pseudo.MacroblockHeight), headerBytes), pseudo.GetPacketBufferLength(width, height, headerBytes, pseudoPayload, (width / pseudo.MacroblockWidth) * (height / pseudo.MacroblockHeight), pseudo.MacroblockWidth));
        }

        [Fact]
        public void PseudoQam_PerFrame_Capacity_Matches_Block_Count_Contract()
        {
            const int width = 640;
            const int height = 480;
            const int headerBytes = 51;

            var mod = new PseudoQamModulator();
            int expected = ((width / mod.MacroblockWidth) * (height / mod.MacroblockHeight)) - headerBytes;
            int actual = mod.GetPayloadBytesPerFrame(width, height, headerBytes, borderWidth: 0, macroblockSize: mod.MacroblockWidth);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void CreateDataFramePacket_Uses_Expected_Header_Layout()
        {
            var payload = new byte[] { 10, 20, 30, 40 };
            var packet = EncoderEngine.CreateDataFramePacket(7, 12, 3, 4, payload.Length, payload);

            Assert.Equal(0x59, packet[0]);
            Assert.Equal(0x54, packet[1]);
            Assert.Equal(2, packet[2]);
            Assert.Equal(0, packet[3]);
            Assert.Equal(7, ((packet[4] << 24) | (packet[5] << 16) | (packet[6] << 8) | packet[7]));
            Assert.Equal(12, ((packet[8] << 24) | (packet[9] << 16) | (packet[10] << 8) | packet[11]));
            Assert.Equal(3, ((packet[12] << 24) | (packet[13] << 16) | (packet[14] << 8) | packet[15]));
            Assert.Equal(4, packet[16]);
            Assert.Equal(0, packet[17]);
            Assert.Equal(0, packet[18]);
            Assert.Equal(0, packet[19]);
            Assert.Equal(4, packet[20]);
            Assert.Equal(4, packet.Length - FramePacket.HeaderBytes);
            Assert.Equal(payload, packet.AsSpan(FramePacket.HeaderBytes, payload.Length).ToArray());
        }

        [Fact]
        public void ConvertRgbaToRgb_Preserves_Channel_Order()
        {
            var rgba = new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 };
            var rgb = EncoderEngine.ConvertRgbaToRgb(rgba, width: 2, height: 1);

            Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60 }, rgb);
        }

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

        [Fact]
        public async Task Encode_Allows_PayloadBytesPerFrame_Above_V1_HeaderLimit()
        {
            var tmp = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[ushort.MaxValue + 1];
                new Random(5).NextBytes(data);
                await File.WriteAllBytesAsync(tmp, data);

                var fake = new FakeFFmpegWrapper(1, 1, 30);
                var engine = new EncoderEngine(new HighCapacityModulator(), fake, 1, 1, 1, 30);
                await engine.EncodeAsync(tmp, "out.mp4");

                Assert.True(engine.LastEncodeMetrics.PayloadBytesPerFrame > ushort.MaxValue);
                Assert.Equal(1, engine.LastEncodeMetrics.TotalDataFrames);
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        private sealed class HighCapacityModulator : IModulator
        {
            public int MacroblockWidth => 1;
            public int MacroblockHeight => 1;

            public void Encode(ReadOnlySpan<byte> input, Span<byte> pixelBuffer)
            {
                if (!input.IsEmpty && !pixelBuffer.IsEmpty)
                {
                    pixelBuffer[0] = input[0];
                }
            }

            public void Decode(ReadOnlySpan<byte> pixelBuffer, Span<byte> output)
            {
                if (!pixelBuffer.IsEmpty && !output.IsEmpty)
                {
                    output[0] = pixelBuffer[0];
                }
            }

            public int GetPayloadBytesPerFrame(int width, int height, int headerBytes, int borderWidth = 0, int macroblockSize = 0)
            {
                return ushort.MaxValue + 1024;
            }

            public int GetPayloadBytesPerFrame(ModulatorGeometry geometry)
            {
                return ushort.MaxValue + 1024;
            }

            public int GetPacketBufferLength(int width, int height, int headerBytes, int payloadBytesPerFrame, int bitsPerFrame, int macroblockSize = 0)
            {
                return headerBytes + payloadBytesPerFrame;
            }

            public int GetPacketBufferLength(ModulatorGeometry geometry, int payloadBytesPerFrame)
            {
                return geometry.HeaderBytes + payloadBytesPerFrame;
            }

            public int GetBorderWidth(int width, int height, int macroblockSize = 0)
            {
                return 0;
            }

            public int GetBorderWidth(ModulatorGeometry geometry)
            {
                return 0;
            }

            public byte[] CreateFrame(int width, int height, int borderWidth, ReadOnlySpan<byte> payload)
            {
                return new byte[width * height * 4];
            }

            public byte[] CreateFrame(ModulatorGeometry geometry, ReadOnlySpan<byte> payload)
            {
                return CreateFrame(geometry.Width, geometry.Height, geometry.BorderWidth, payload);
            }
        }
    }
}
