using Xunit;
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
    }
}
