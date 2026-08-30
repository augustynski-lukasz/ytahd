using Xunit;
using YTAHD.Core.Core;

namespace YTAHD.Tests
{
    public class FrameLayoutCalculatorTests
    {
        [Fact]
        public void CalculatePayloadBytes_Uses_Usable_Area_And_Header_Size()
        {
            var result = FrameLayoutCalculator.CalculatePayloadBytesPerFrame(640, 480, 16, 16, 16, 0);

            Assert.Equal(134, result);
        }

        [Fact]
        public void CalculatePayloadBytes_Respects_Border_Width()
        {
            var result = FrameLayoutCalculator.CalculatePayloadBytesPerFrame(640, 480, 16, 16, 16, 32);

            Assert.Equal(101, result);
        }

        [Fact]
        public void CalculatePayloadBytes_Returns_Zero_When_Too_Small_For_Header()
        {
            var result = FrameLayoutCalculator.CalculatePayloadBytesPerFrame(16, 16, 16, 16, 16, 0);

            Assert.Equal(0, result);
        }
    }
}
