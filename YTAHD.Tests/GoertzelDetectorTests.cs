using System;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Audio;
using YTAHD.Core.Core;

namespace YTAHD.Tests
{
    public class GoertzelDetectorTests
    {
        private const int Fps = 30;
        private const int SampleRate = FskGenerator.SampleRate;

        private static short[] BuildCleanClockTrack(int totalLogicalFrames, int physicalFramesPerLogicalFrame)
        {
            using var ms = new System.IO.MemoryStream();
            EncoderEngine.WriteAudioClockTrackAsync(ms, totalLogicalFrames, physicalFramesPerLogicalFrame, Fps).GetAwaiter().GetResult();
            return GoertzelDetector.ToInt16Samples(ms.ToArray());
        }

        [Fact]
        public void DetectDatagramBoundaries_Finds_One_Boundary_Per_LogicalFrame_On_Clean_Signal()
        {
            const int totalLogicalFrames = 4;
            const int physicalFramesPerLogicalFrame = 3; // Phase 1-3 style cadence: pulse + 2 hold frames
            var samples = BuildCleanClockTrack(totalLogicalFrames, physicalFramesPerLogicalFrame);

            var boundaries = GoertzelDetector.DetectDatagramBoundaries(samples, SampleRate, Fps);

            var expected = new[] { 0, 3, 6, 9 };
            Assert.Equal(expected, boundaries);
        }

        [Fact]
        public void DetectDatagramBoundaries_Handles_Phase4Style_BackToBack_Pulses()
        {
            // Phase 4 cadence: 2 physical frames/logical frame, pulse fully occupies the span
            // (no hold gap) — every window is "pulse", so every window is its own boundary
            // only if the classifier still sees a hold->pulse *edge*; back-to-back pulses
            // with no hold in between will NOT produce a rising edge per logical frame here,
            // which is the documented Phase 4/FSK cadence conflict (see ADR).
            const int totalLogicalFrames = 3;
            const int physicalFramesPerLogicalFrame = 2;
            var samples = BuildCleanClockTrack(totalLogicalFrames, physicalFramesPerLogicalFrame);

            var boundaries = GoertzelDetector.DetectDatagramBoundaries(samples, SampleRate, Fps);

            // Only the very first rising edge (hold -> pulse at the stream start) is detected;
            // subsequent logical frames are indistinguishable because the tone never returns
            // to "hold" between them.
            Assert.Equal(new[] { 0 }, boundaries);
        }

        [Fact]
        public void DetectDatagramBoundaries_Tolerates_LowPass_Smeared_Signal()
        {
            const int totalLogicalFrames = 4;
            const int physicalFramesPerLogicalFrame = 3;
            var samples = BuildCleanClockTrack(totalLogicalFrames, physicalFramesPerLogicalFrame);

            var smeared = ApplyMovingAverageLowPass(samples, tapCount: 5);
            var boundaries = GoertzelDetector.DetectDatagramBoundaries(smeared, SampleRate, Fps);

            var expected = new[] { 0, 3, 6, 9 };
            Assert.Equal(expected.Length, boundaries.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.True(Math.Abs(expected[i] - boundaries[i]) <= 1, $"Boundary {i}: expected ~{expected[i]}, got {boundaries[i]}.");
            }
        }

        [Fact]
        public async Task ClassifyWindow_Distinguishes_Pure_Hold_From_Pure_Pulse_Tones()
        {
            var generator = new FskGenerator();
            using var holdStream = new System.IO.MemoryStream();
            await generator.WriteHoldToneAsync(holdStream, videoFrameCount: 5, Fps);
            var holdSamples = GoertzelDetector.ToInt16Samples(holdStream.ToArray());

            using var pulseStream = new System.IO.MemoryStream();
            await generator.WritePulseAsync(pulseStream, Fps, videoFrameCount: 5);
            var pulseSamples = GoertzelDetector.ToInt16Samples(pulseStream.ToArray());

            Assert.False(GoertzelDetector.ClassifyWindow(holdSamples, SampleRate));
            Assert.True(GoertzelDetector.ClassifyWindow(pulseSamples, SampleRate));
        }

        private static short[] ApplyMovingAverageLowPass(short[] samples, int tapCount)
        {
            var result = new short[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                long sum = 0;
                int count = 0;
                for (int t = -(tapCount / 2); t <= tapCount / 2; t++)
                {
                    int idx = i + t;
                    if (idx < 0 || idx >= samples.Length) continue;
                    sum += samples[idx];
                    count++;
                }

                result[i] = (short)(sum / count);
            }

            return result;
        }
    }
}
