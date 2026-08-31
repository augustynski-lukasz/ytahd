using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    public class Phase3DctCarrierTests
    {
        [Fact]
        public void LowFrequencyMask_Uses_Only_TopLeft_Coefficients()
        {
            var mask = DctCarrierBasis.CreateLowFrequencyMask(8, cutoff: 4);

            Assert.Equal(8, mask.GetLength(0));
            Assert.Equal(8, mask.GetLength(1));
            Assert.True(mask[0, 0]);
            Assert.True(mask[3, 3]);
            Assert.False(mask[7, 7]);
            Assert.Equal(16, mask.Cast<bool>().Count(v => v));
        }

        [Fact]
        public void Basis_Generates_Stable_Low_Frequency_Values()
        {
            var basis = DctCarrierBasis.GenerateBasis(8, cutoff: 4);

            Assert.Equal(8, basis.GetLength(0));
            Assert.Equal(8, basis.GetLength(1));
            Assert.True(Math.Abs(basis[0, 0] - 1.0d) < 0.0001d);
            Assert.True(basis[0, 1] < 0.999d);
            Assert.True(basis[3, 3] > 0.0d);
            Assert.True(Math.Abs(basis[7, 7]) < 0.0001d);
        }

        [Fact]
        public void DctModulator_RoundTrips_BytePayload()
        {
            var mod = new DctModulator();
            byte[] payload = { 0, 17, 42, 85, 127, 170, 200, 255 };
            var encoded = new byte[64];

            mod.Encode(payload, encoded);

            var decoded = new byte[payload.Length];
            mod.Decode(encoded, decoded);

            Assert.Equal(payload, decoded);
        }

        [Fact]
        public void DctModulator_Compensates_For_Linear_Coefficient_Drift()
        {
            var mod = new DctModulator();
            byte[] payload = { 0, 17, 42, 85, 127, 170, 200, 255 };
            var encoded = new byte[64];

            mod.Encode(payload, encoded);

            var drifted = new byte[encoded.Length];
            for (int i = 0; i < payload.Length; i++)
            {
                int y = i / 4;
                int x = i % 4;
                int index = y * 8 + x;
                drifted[index] = (byte)Math.Clamp(Math.Round(encoded[index] * 0.75d + 12d), 0, 255);
            }

            var decoded = new byte[payload.Length];
            mod.Decode(drifted, decoded);

            for (int i = 0; i < payload.Length; i++)
            {
                Assert.True(Math.Abs(decoded[i] - payload[i]) <= 1, $"Byte {i} drifted beyond tolerance: expected {payload[i]}, actual {decoded[i]}");
            }
        }

        [Fact]
        public void DctModulator_CreatePhase3Frame_Uses_LowFrequency_Carrier_Only()
        {
            byte[] payload = { 1, 2, 3, 4, 5, 6, 7, 8 };
            var frame = DctModulator.CreatePhase3Frame(32, 32, borderWidth: 4, payload);

            Assert.Equal(32 * 32 * 4, frame.Length);
            Assert.True(frame[(4 * 32 + 4) * 4 + 0] > 0);
            Assert.True(frame[(4 * 32 + 4) * 4 + 1] == frame[(4 * 32 + 4) * 4 + 0]);
            Assert.Equal(0, frame[(12 * 32 + 12) * 4 + 0]);
        }

        [Fact]
        public async Task EncoderEngine_Uses_DctFramePath_For_Phase3()
        {
            var fake = new FakeFFmpegWrapper(128, 64, 30);
            var mod = new DctModulator();
            var engine = new EncoderEngine(mod, fake, macroblockSize: 1, width: 128, height: 64, fps: 30);

            byte[] payload = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            var input = Path.GetTempFileName();
            await File.WriteAllBytesAsync(input, payload);

            try
            {
                await engine.EncodeAsync(input, "out.mp4");
                var buf = fake.Process?.Buffer;
                Assert.NotNull(buf);
                var raw = buf.ToArray();

                Assert.NotEmpty(raw);
                Assert.NotEqual(0, raw[0]);
                Assert.True(raw.Any(v => v != 0), "Encoded stream should contain non-zero carrier bytes for Phase 3 output.");
            }
            finally
            {
                File.Delete(input);
            }
        }

        [Fact]
        public void DctModulator_Phase3_Capacity_Matches_Separate_PerFrame_Calculation()
        {
            const int width = 3840;
            const int height = 2160;
            const int borderWidth = 32;

            int usableWidth = Math.Max(0, width - borderWidth * 2);
            int usableHeight = Math.Max(0, height - borderWidth * 2);
            int dctBlocksX = usableWidth / 8;
            int dctBlocksY = usableHeight / 8;
            int expectedPayloadBytesPerFrame = dctBlocksX * dctBlocksY * 16;

            var payload = Enumerable.Range(0, expectedPayloadBytesPerFrame)
                .Select(i => (byte)(i % 251))
                .ToArray();

            var frame = DctModulator.CreatePhase3Frame(width, height, borderWidth, payload);

            Assert.Equal(width * height * 4, frame.Length);
            Assert.Equal(expectedPayloadBytesPerFrame, payload.Length);
            Assert.Equal(payload[0], frame[((borderWidth + 0) * width + (borderWidth + 0)) * 4 + 0]);
            Assert.Equal(payload[1], frame[((borderWidth + 0) * width + (borderWidth + 1)) * 4 + 0]);
            Assert.Equal(payload[15], frame[((borderWidth + 3) * width + (borderWidth + 3)) * 4 + 0]);
            Assert.Equal(0, frame[((borderWidth + 6) * width + (borderWidth + 6)) * 4 + 0]);
        }

        [Fact]
        public void DctFrameBitDecoder_Recovers_Packet_From_Encoded_Frame()
        {
            var payload = new byte[96];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)((i * 17) % 251);
            }

            var packetBytes = FrameProtocolHelpers.CreateDataFramePacket(0, 1, 0, 1, payload.Length, payload, payload.Length);
            var frame = DctModulator.CreatePhase3Frame(640, 480, borderWidth: 32, packetBytes);
            var rgb = FrameProtocolHelpers.ConvertRgbaToRgb(frame, 640, 480);

            var decodedPacket = new byte[FramePacket.HeaderBytes + payload.Length];
            var decoder = new DctFrameBitDecoder();
            decoder.Decode(rgb, 640, 480, 16, 640 * 3, 640 * 480 * 3, decodedPacket, borderWidth: 32);

            Assert.True(FramePacketCodec.TryDecode(decodedPacket, out var frameType, out var frameIndex, out var totalFrames, out var groupStart, out var groupCount, out var payloadLength, out var decodedPayload));
            Assert.Equal(FramePacket.FrameTypeData, frameType);
            Assert.Equal(0, frameIndex);
            Assert.Equal(1, totalFrames);
            Assert.Equal(0, groupStart);
            Assert.Equal(1, groupCount);
            Assert.Equal(payload.Length, payloadLength);
            Assert.Equal(payload, decodedPayload);
        }

        [Fact]
        public void DctFrameBitDecoder_Recovers_Packet_After_Luminance_Drift()
        {
            var payload = new byte[96];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)((i * 29) % 251);
            }

            var packetBytes = FrameProtocolHelpers.CreateDataFramePacket(0, 1, 0, 1, payload.Length, payload, payload.Length);
            var frame = DctModulator.CreatePhase3Frame(640, 480, borderWidth: 32, packetBytes);
            var rgb = FrameProtocolHelpers.ConvertRgbaToRgb(frame, 640, 480);

            var drifted = new byte[rgb.Length];
            for (int i = 0; i < rgb.Length; i++)
            {
                drifted[i] = (byte)Math.Clamp(rgb[i] + 12, 0, 255);
            }

            var decodedPacket = new byte[FramePacket.HeaderBytes + payload.Length];
            var decoder = new DctFrameBitDecoder();
            decoder.Decode(drifted, 640, 480, 16, 640 * 3, 640 * 480 * 3, decodedPacket, borderWidth: 32);

            Assert.True(FramePacketCodec.TryDecode(decodedPacket, out _, out _, out _, out _, out _, out _, out _), "DCT decoder should recover the packet header despite luminance drift.");
            Assert.True(PacketQualityScorer.IsFramePacketValid(decodedPacket), "DCT decoder should produce a valid packet quality score after luminance drift.");
        }

        [Fact]
        public void DctFrameBitDecoder_Recovers_Packet_After_RGB_Channel_Drift()
        {
            var payload = new byte[96];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)((i * 11 + 37) % 251);
            }

            var packetBytes = FrameProtocolHelpers.CreateDataFramePacket(0, 1, 0, 1, payload.Length, payload, payload.Length);
            var frame = DctModulator.CreatePhase3Frame(640, 480, borderWidth: 32, packetBytes);
            var rgb = FrameProtocolHelpers.ConvertRgbaToRgb(frame, 640, 480);

            var drifted = new byte[rgb.Length];
            for (int i = 0; i < rgb.Length; i += 3)
            {
                drifted[i] = (byte)Math.Clamp(rgb[i] + 18, 0, 255);
                drifted[i + 1] = (byte)Math.Clamp(rgb[i + 1] - 8, 0, 255);
                drifted[i + 2] = (byte)Math.Clamp(rgb[i + 2] + 12, 0, 255);
            }

            var decodedPacket = new byte[FramePacket.HeaderBytes + payload.Length];
            var decoder = new DctFrameBitDecoder();
            decoder.Decode(drifted, 640, 480, 16, 640 * 3, 640 * 480 * 3, decodedPacket, borderWidth: 32);

            Assert.True(FramePacketCodec.TryDecode(decodedPacket, out _, out _, out _, out _, out _, out _, out _), "DCT decoder should recover from channel imbalance seen in lossy H.264 decode.");
            Assert.True(PacketQualityScorer.IsFramePacketValid(decodedPacket), "DCT decoder should still produce a valid frame packet under uneven RGB drift.");
        }

        [Fact]
        public void DecodeRecoveryPolicy_DoesNotStop_When_Leading_Frame_Is_Missing()
        {
            var payload = new byte[96];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)((i * 19 + 7) % 251);
            }

            var packetBytes = FrameProtocolHelpers.CreateDataFramePacket(1, 2, 0, 2, payload.Length, payload, payload.Length);
            var frame = DctModulator.CreatePhase3Frame(640, 480, borderWidth: 32, packetBytes);
            var rgb = FrameProtocolHelpers.ConvertRgbaToRgb(frame, 640, 480);

            var accumulator = new DecodedFrameAccumulator();
            var geometry = new ModulatorGeometry(640, 480, 16, FramePacket.HeaderBytes, BitsPerFrame: 0);
            geometry = geometry with { BorderWidth = 32 };
            int payloadBytesPerFrame = new DctModulator().GetPayloadBytesPerFrame(geometry);
            int rowBytes = 640 * 3;
            int frameBytes = rowBytes * 480;

            Assert.True(accumulator.TryAddDecodedFrame(rgb, 640, 480, 16, rowBytes, frameBytes, payloadBytesPerFrame, 0, new DctModulator()));
            Assert.False(DecodeRecoveryPolicy.ShouldStopDecoding(accumulator, 64), "Decode should not stop when the required leading frame 0 is not present yet.");
        }

        [Fact]
        public void DctModulator_Uses_BorderWidth_When_Calculating_Frame_Capacity()
        {
            var modulator = new DctModulator();
            var geometry = new ModulatorGeometry(640, 480, 16, FramePacket.HeaderBytes, BorderWidth: 0, BitsPerFrame: 0);

            var borderAware = geometry with { BorderWidth = modulator.GetBorderWidth(geometry) };
            var payloadBytes = modulator.GetPayloadBytesPerFrame(borderAware);
            var packetLength = modulator.GetPacketBufferLength(borderAware, payloadBytes);

            Assert.Equal(32, borderAware.BorderWidth);
            Assert.True(packetLength > 0);
            Assert.Equal(72 * 52 * 16, payloadBytes + FramePacket.HeaderBytes);
        }
    }
}
