using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Application;
using YTAHD.Core.Audio;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    /// <summary>
    /// B2: audio FSK datagram-clock plumbing (mux on encode, extraction on decode).
    /// See ADR F-20260903-02-audio-fsk-clock-design.md.
    /// </summary>
    public class AudioClockPipelineTests
    {
        [Fact]
        public async Task WriteAudioClockTrackAsync_Writes_PulsePlusHold_Per_LogicalFrame()
        {
            using var ms = new MemoryStream();
            const int totalLogicalFrames = 3;
            const int physicalFramesPerLogicalFrame = 3; // Phase 1-3 default: 1 pulse frame + 2 hold frames
            const int fps = 30;

            await EncoderEngine.WriteAudioClockTrackAsync(ms, totalLogicalFrames, physicalFramesPerLogicalFrame, fps);

            int pulseSamples = (int)Math.Round(FskGenerator.SampleRate * (FskGenerator.PulseDurationVideoFrames / (double)fps));
            int holdSamples = (int)Math.Round(FskGenerator.SampleRate * ((physicalFramesPerLogicalFrame - FskGenerator.PulseDurationVideoFrames) / (double)fps));
            long expectedBytes = (long)totalLogicalFrames * (pulseSamples + holdSamples) * 2;

            Assert.Equal(expectedBytes, ms.Length);
        }

        [Fact]
        public async Task WriteAudioClockTrackAsync_Caps_Pulse_When_LogicalFrame_Shorter_Than_PulseDuration()
        {
            using var ms = new MemoryStream();
            const int totalLogicalFrames = 2;
            const int physicalFramesPerLogicalFrame = 1; // shorter than the 2-frame pulse (e.g. degenerate cadence)
            const int fps = 30;

            await EncoderEngine.WriteAudioClockTrackAsync(ms, totalLogicalFrames, physicalFramesPerLogicalFrame, fps);

            int cappedPulseSamples = (int)Math.Round(FskGenerator.SampleRate * (physicalFramesPerLogicalFrame / (double)fps));
            long expectedBytes = (long)totalLogicalFrames * cappedPulseSamples * 2;

            Assert.Equal(expectedBytes, ms.Length);
        }

        [Fact]
        public async Task EncoderEngine_Passes_AudioPcmFilePath_When_UseAudioClock_Enabled()
        {
            var tmpIn = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(tmpIn, new byte[] { 1, 2, 3, 4 });
                var fake = new FakeFFmpegWrapper(640, 480, 30);
                var options = new VideoCodecOptions { MacroblockSize = 16, Width = 640, Height = 480, Fps = 30, UseAudioClock = true };
                var engine = new EncoderEngine(new BinaryGridModulator(), fake, options);

                await engine.EncodeAsync(tmpIn, "out.mp4");

                Assert.NotNull(fake.LastAudioPcmFilePath);
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }

        [Fact]
        public async Task EncoderEngine_Passes_No_AudioPcmFilePath_When_UseAudioClock_Disabled()
        {
            var tmpIn = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(tmpIn, new byte[] { 1, 2, 3, 4 });
                var fake = new FakeFFmpegWrapper(640, 480, 30);
                var engine = new EncoderEngine(new BinaryGridModulator(), fake, 16, 640, 480, 30);

                await engine.EncodeAsync(tmpIn, "out.mp4");

                Assert.Null(fake.LastAudioPcmFilePath);
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }

        [Fact]
        public async Task TryExtractAudioPcmAsync_Returns_Configured_Bytes_On_Fake_Wrapper()
        {
            var fake = new FakeFFmpegWrapper(640, 480, 30) { AudioPcmToReturn = new byte[] { 1, 2, 3, 4 } };
            var result = await fake.TryExtractAudioPcmAsync("anything.mp4");
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, result);
        }

        [Fact]
        public async Task TryExtractAudioPcmAsync_Returns_Null_By_Default_On_Fake_Wrapper()
        {
            var fake = new FakeFFmpegWrapper(640, 480, 30);
            var result = await fake.TryExtractAudioPcmAsync("anything.mp4");
            Assert.Null(result);
        }
    }
}
