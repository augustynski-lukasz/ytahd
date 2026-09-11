using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    public class DecoderStreamOrchestratorTests
    {
        private const int Width = 128;
        private const int Height = 64;
        private const int Macroblock = 1;

        [Fact]
        public async Task DecodeStreamOrchestrator_Reproduces_Output_From_Rgb_Stream()
        {
            var tmpIn = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[16];
                new Random(2).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var buffer = fake.Process?.Buffer;
                Assert.NotNull(buffer);
                buffer.Position = 0;

                var orchestrator = new DecodeStreamOrchestrator(Width, Height, Macroblock);
                var output = await orchestrator.ProcessAsync(buffer, data.Length);

                Assert.Equal(data, output);
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }

        [Fact]
        public async Task DecodeStreamOrchestrator_Reports_Completion_Percentage()
        {
            var tmpIn = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[16];
                new Random(3).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var buffer = fake.Process?.Buffer;
                Assert.NotNull(buffer);
                buffer.Position = 0;

                var frameBytes = Width * Height * 3;
                var totalFrames = (int)(buffer.Length / frameBytes);
                var progressReports = new List<DecodeProgress>();
                var progress = new CaptureProgress(progressReports);

                var orchestrator = new DecodeStreamOrchestrator(Width, Height, Macroblock);
                var output = await orchestrator.ProcessAsync(buffer, 0, totalVideoFrames: totalFrames, progress: progress);

                Assert.Equal(data, output[..data.Length]);
                Assert.NotEmpty(progressReports);
                Assert.Equal(totalFrames, progressReports[^1].FramesSeen);
                Assert.Equal(100d, progressReports[^1].Percentage);
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }

        [Fact]
        public async Task EncoderDecoder_RoundTrip_FakeFFmpeg()
        {
            var tmpIn = Path.GetTempFileName();
            var tmpOut = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[16];
                new Random(2).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.VerifyAsync();
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var buf = fake.Process?.Buffer;
                Assert.NotNull(buf);
                buf.Position = 0;

                var decoder = new DecoderEngine(mod, fake);
                await decoder.DecodeFromRgbStreamAsync(buf, Width, Height, Macroblock, data.Length, tmpOut);

                var outData = await File.ReadAllBytesAsync(tmpOut);
                Assert.Equal(data, outData);
            }
            finally
            {
                File.Delete(tmpIn);
                File.Delete(tmpOut);
            }
        }

        private sealed class CaptureProgress : IProgress<DecodeProgress>
        {
            private readonly List<DecodeProgress> _reports;

            public CaptureProgress(List<DecodeProgress> reports)
            {
                _reports = reports;
            }

            public void Report(DecodeProgress value)
            {
                _reports.Add(value);
            }
        }
    }
}
