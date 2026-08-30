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
    }
}
