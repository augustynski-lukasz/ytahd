using Xunit;
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
    }
}
