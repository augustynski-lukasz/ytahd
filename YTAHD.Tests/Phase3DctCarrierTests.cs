using System;
using System.Linq;
using Xunit;
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
    }
}
