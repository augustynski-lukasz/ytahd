using Xunit;
using YTAHD.Cli.Modulation;

namespace YTAHD.Tests
{
    public class BinaryGridModulatorTests
    {
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
