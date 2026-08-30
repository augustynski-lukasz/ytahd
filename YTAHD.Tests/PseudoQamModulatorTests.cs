using System;
using System.Linq;
using Xunit;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    public class PseudoQamModulatorTests
    {
        [Fact]
        public void Encode_Uses_16_Level_Pam_States()
        {
            var mod = new PseudoQamModulator();
            byte[] input = { 0x5A, 0xA5 };
            var pixelBuffer = new byte[mod.MacroblockWidth * mod.MacroblockHeight * 3];

            mod.Encode(input, pixelBuffer);

            Assert.True(pixelBuffer[0] % 17 == 0, "The first channel should use 16-level PAM amplitudes rather than raw byte values.");
            Assert.True(pixelBuffer[1] % 17 == 0, "The second channel should use 16-level PAM amplitudes rather than raw byte values.");
        }

        [Fact]
        public void Roundtrip_Preserves_Bytes()
        {
            var mod = new PseudoQamModulator();
            byte[] input = { 0x00, 0x01, 0x2A, 0x3C, 0x5A, 0x7F, 0xA5, 0xFF };
            var pixelBuffer = new byte[mod.MacroblockWidth * mod.MacroblockHeight * 3];

            mod.Encode(input, pixelBuffer);

            var output = new byte[input.Length];
            mod.Decode(pixelBuffer, output);

            Assert.Equal(input, output);
        }

        [Fact]
        public void Encode_Decode_RoundTrips_All_Byte_Values_And_Threshold_Boundaries()
        {
            var mod = new PseudoQamModulator();
            var input = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            var pixelBuffer = new byte[256 * 3 + 16];

            mod.Encode(input, pixelBuffer);

            var output = new byte[input.Length];
            mod.Decode(pixelBuffer, output);

            Assert.Equal(input, output);

            foreach (var boundary in new[] { (byte)0x00, (byte)0x0F, (byte)0x10, (byte)0x1F, (byte)0x55, (byte)0xAA, (byte)0xF0, (byte)0xFF })
            {
                var single = new[] { boundary };
                var singlePixelBuffer = new byte[16];
                mod.Encode(single, singlePixelBuffer);
                var decoded = new byte[1];
                mod.Decode(singlePixelBuffer, decoded);
                Assert.Equal(single, decoded);
            }
        }

        [Fact]
        public void Encode_Handles_Empty_Input_Gracefully()
        {
            var mod = new PseudoQamModulator();
            var pixelBuffer = new byte[32];
            var output = new byte[0];

            mod.Encode(ReadOnlySpan<byte>.Empty, pixelBuffer);
            mod.Decode(ReadOnlySpan<byte>.Empty, output);

            Assert.Empty(output);
        }

        [Fact]
        public void Phase2_FrameDecoder_Recovers_The_Original_Bytes_From_The_Quantized_Block()
        {
            var input = new byte[] { 0x00, 0x01, 0x2A, 0x3C, 0x5A, 0x7F, 0xA5, 0xFF };
            var frame = PseudoQamModulator.CreatePhase2Frame(64, 64, 8, input);
            var packet = new byte[input.Length];
            var decoder = FrameBitDecoderFactory.CreateForModulator(new PseudoQamModulator());

            decoder.Decode(frame, 64, 64, 16, 64 * 3, 64 * 64 * 4, packet, 8);

            Assert.Equal(input, packet);
        }

        [Fact]
        public void Phase2_Calibration_Profile_Is_Stable_For_Reference_Palette_Values()
        {
            var width = 64;
            var height = 64;
            var borderWidth = 8;
            var frame = new byte[width * height * 4];
            PseudoQamModulator.PaintCalibrationBorder(frame, width, height, borderWidth);

            var profile = PseudoQamModulator.EstimateCalibrationProfile(frame, width, height, borderWidth);

            Assert.Equal(PseudoQamModulator.CreateReferencePalette().Length, profile.Length);
            Assert.Equal(PseudoQamModulator.CreateReferencePalette(), profile);
        }
    }
}
