using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    public class Phase2CalibrationTests
    {
        [Fact]
        public void CreateReferencePalette_Contains_16_Pam_Levels()
        {
            var palette = PseudoQamModulator.CreateReferencePalette();

            Assert.Equal(16, palette.Length);
            for (int i = 0; i < palette.Length; i++)
            {
                Assert.Equal((byte)(i * 17), palette[i]);
            }
        }

        [Fact]
        public void PaintCalibrationBorder_Creates_Border_And_Palette_Markers()
        {
            const int width = 64;
            const int height = 64;
            var rgba = new byte[width * height * 4];

            PseudoQamModulator.PaintCalibrationBorder(rgba, width, height, borderWidth: 8);

            bool foundNonZeroBorder = false;
            bool foundNonZeroPalette = false;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = (y * width + x) * 4;
                    bool onBorder = x < 8 || x >= width - 8 || y < 8 || y >= height - 8;
                    if (onBorder && (rgba[idx] != 0 || rgba[idx + 1] != 0 || rgba[idx + 2] != 0))
                    {
                        foundNonZeroBorder = true;
                    }

                    if (y == 4 && x >= 12 && x <= 12 + 15 * 4 && (rgba[idx] != 0 || rgba[idx + 1] != 0 || rgba[idx + 2] != 0))
                    {
                        foundNonZeroPalette = true;
                    }
                }
            }

            Assert.True(foundNonZeroBorder, "The calibration border should paint visible non-zero samples.");
            Assert.True(foundNonZeroPalette, "The reference palette row should include visible PAM markers.");
        }

        [Fact]
        public void CreatePhase2Frame_Combines_Border_With_Payload_Data()
        {
            const int width = 64;
            const int height = 64;
            var payload = new byte[] { 0x00, 0x0F, 0x5A, 0xA5, 0xFF };

            var frame = PseudoQamModulator.CreatePhase2Frame(width, height, borderWidth: 8, payload);

            Assert.Equal(width * height * 4, frame.Length);
            Assert.True(frame.Any(v => v != 0), "The frame should contain visible calibration or payload data.");

            int centerIndex = ((height / 2) * width + (width / 2)) * 4;
            Assert.True(frame[centerIndex] != 0 || frame[centerIndex + 1] != 0 || frame[centerIndex + 2] != 0,
                "The payload area should carry encoded data rather than staying completely empty.");
        }

        [Fact]
        public async Task EncoderEngine_Emits_Phase2_Pilot_Values_For_PseudoQam()
        {
            var tmp = Path.GetTempFileName();
            try
            {
                var data = new byte[] { 0x01, 0x02, 0x03, 0x04 };
                await File.WriteAllBytesAsync(tmp, data);

                var mod = new PseudoQamModulator();
                var fake = new FakeFFmpegWrapper(3840, 2160, 60);
                var engine = new EncoderEngine(mod, fake, 16, 3840, 2160, 60);

                await engine.EncodeAsync(tmp, "out.mp4");

                var rgbBytes = fake.Process!.Buffer.ToArray();
                int x = 32 + 4;
                int y = 32 + 4;
                int idx = (y * 3840 + x) * 3;

                Assert.NotEqual(0, rgbBytes[idx]);
                Assert.NotEqual(0, rgbBytes[idx + 1]);
                Assert.NotEqual(0, rgbBytes[idx + 2]);
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        [Fact]
        public void EstimateCalibrationProfile_Produces_16_Thresholds_From_Pilot_Row()
        {
            const int width = 64;
            const int height = 64;
            var rgba = new byte[width * height * 4];
            PseudoQamModulator.PaintCalibrationBorder(rgba, width, height, borderWidth: 8);

            var levels = PseudoQamModulator.EstimateCalibrationProfile(rgba, width, height, borderWidth: 8);

            Assert.Equal(16, levels.Length);
            Assert.Equal(0, levels[0]);
            Assert.NotEqual(0, levels[15]);
        }

        [Fact]
        public void Phase2_Frame_Roundtrip_Recovers_Original_Bytes()
        {
            const int width = 128;
            const int height = 128;
            const int borderWidth = 8;
            var payload = new byte[] { 0x00, 0x01, 0x2A, 0x3C, 0x5A, 0x7F, 0xA5, 0xFF, 0x10, 0x20, 0x40, 0x80 };

            var frame = PseudoQamModulator.CreatePhase2Frame(width, height, borderWidth, payload);

            var recovered = new List<byte>();
            const int macroblockSize = 16;
            int blocksX = (width - borderWidth * 2) / macroblockSize;
            int blocksY = (height - borderWidth * 2) / macroblockSize;

            for (int by = 0; by < blocksY && recovered.Count < payload.Length; by++)
            {
                for (int bx = 0; bx < blocksX && recovered.Count < payload.Length; bx++)
                {
                    int px = borderWidth + (bx * macroblockSize) + (macroblockSize / 2);
                    int py = borderWidth + (by * macroblockSize) + (macroblockSize / 2);
                    int idx = (py * width + px) * 4;

                    byte r = frame[idx + 0];
                    byte g = frame[idx + 1];

                    byte low = (byte)((r + 8) / 17);
                    byte high = (byte)((g + 8) / 17);
                    recovered.Add((byte)((high << 4) | low));
                }
            }

            Assert.Equal(payload, recovered.ToArray());
        }
    }
}
