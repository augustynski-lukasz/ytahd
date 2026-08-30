using System;
using Xunit;
using YTAHD.Core.Core;

namespace YTAHD.Tests
{
    public class DecodeRecoveryPolicyTests
    {
        [Fact]
        public void ShouldStopDecoding_WhenAccumulatorHasReachedTarget()
        {
            var accumulator = new DecodedFrameAccumulator();
            accumulator.OrderedPayload[0] = new byte[] { 1, 2, 3 };
            accumulator.OrderedPayload[1] = new byte[] { 4, 5, 6 };

            Assert.True(DecodeRecoveryPolicy.ShouldStopDecoding(accumulator, 6));
            Assert.False(DecodeRecoveryPolicy.ShouldStopDecoding(accumulator, 7));
        }

        [Fact]
        public void ResolveExpectedOutputBytes_UsesAccumulatorLength_WhenTargetIsZero()
        {
            var accumulator = new DecodedFrameAccumulator();
            accumulator.OrderedPayload[0] = new byte[] { 9, 9, 9 };
            accumulator.OrderedPayload[1] = new byte[] { 1, 2, 3, 4 };

            Assert.Equal(7, DecodeRecoveryPolicy.ResolveExpectedOutputBytes(accumulator, 0));
            Assert.Equal(9, DecodeRecoveryPolicy.ResolveExpectedOutputBytes(accumulator, 9));
        }
    }
}
