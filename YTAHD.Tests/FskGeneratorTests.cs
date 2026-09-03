using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Audio;

namespace YTAHD.Tests
{
    public class FskGeneratorTests
    {
        private const int Fps = 60;

        [Fact]
        public async Task WriteHoldToneAsync_Writes_Expected_Sample_Count()
        {
            using var ms = new MemoryStream();
            var generator = new FskGenerator();
            await generator.WriteHoldToneAsync(ms, videoFrameCount: 30, Fps);

            int expectedSamples = (int)Math.Round(FskGenerator.SampleRate * (30.0 / Fps));
            Assert.Equal(expectedSamples * 2, (int)ms.Length);
        }

        [Fact]
        public async Task WritePulseAsync_Writes_TwoVideoFrames_Worth_Of_Samples()
        {
            using var ms = new MemoryStream();
            var generator = new FskGenerator();
            await generator.WritePulseAsync(ms, Fps);

            int expectedSamples = (int)Math.Round(FskGenerator.SampleRate * (FskGenerator.PulseDurationVideoFrames / (double)Fps));
            Assert.Equal(expectedSamples * 2, (int)ms.Length);
        }

        [Fact]
        public async Task Consecutive_Segments_Have_No_Sample_Discontinuity_At_The_Boundary()
        {
            using var ms = new MemoryStream();
            var generator = new FskGenerator();
            await generator.WriteHoldToneAsync(ms, videoFrameCount: 10, Fps);
            long boundaryPosition = ms.Length;
            await generator.WritePulseAsync(ms, Fps);

            var samples = ToInt16Samples(ms.ToArray());
            int boundarySampleIndex = (int)(boundaryPosition / 2);

            // Amplitude ramps mean the last hold sample and first pulse sample are both
            // near-zero; the jump between them must stay small relative to full scale.
            int jump = Math.Abs(samples[boundarySampleIndex] - samples[boundarySampleIndex - 1]);
            Assert.True(jump < 2000, $"Discontinuity of {jump} at segment boundary exceeds click threshold.");
        }

        [Fact]
        public async Task Phase_Is_Continuous_Across_Multiple_HoldTone_Calls()
        {
            // Each call ramps its own edges by design (so a hold segment fades in after a
            // pulse and fades out before the next one), so comparing raw samples across a
            // split-call boundary isn't valid. Instead, predict the phase analytically from
            // segment 1's known sample count and check it against a probe point safely
            // inside segment 2's steady (non-ramped) region.
            var generator = new FskGenerator();
            using var ms = new MemoryStream();

            await generator.WriteHoldToneAsync(ms, videoFrameCount: 10, Fps);
            long segment1Samples = ms.Length / 2;

            await generator.WriteHoldToneAsync(ms, videoFrameCount: 10, Fps);

            var samples = ToInt16Samples(ms.ToArray());

            const double amplitude = 12000.0;
            const double rampSeconds = 0.003;
            double angularStep = 2.0 * Math.PI * FskGenerator.HoldFrequencyHz / FskGenerator.SampleRate;
            double phaseAfterSegment1 = (angularStep * segment1Samples) % (2.0 * Math.PI);

            int rampSamples = (int)Math.Round(rampSeconds * FskGenerator.SampleRate);
            int probeOffsetInSegment2 = rampSamples + 500; // clear of segment 2's fade-in
            int probeIndex = (int)segment1Samples + probeOffsetInSegment2;

            double expectedPhase = phaseAfterSegment1 + probeOffsetInSegment2 * angularStep;
            double expectedSample = amplitude * Math.Sin(expectedPhase);

            Assert.True(Math.Abs(samples[probeIndex] - expectedSample) < 5,
                $"Sample {probeIndex}: expected ~{expectedSample:F1}, got {samples[probeIndex]}.");
        }

        [Theory]
        [InlineData(1000.0)]
        [InlineData(1500.0)]
        public async Task Tone_Segment_Has_Dominant_Energy_At_Its_Target_Frequency(double frequencyHz)
        {
            using var ms = new MemoryStream();
            var generator = new FskGenerator();
            if (frequencyHz == FskGenerator.HoldFrequencyHz)
                await generator.WriteHoldToneAsync(ms, videoFrameCount: 30, Fps);
            else
                await generator.WritePulseAsync(ms, Fps);

            var samples = ToInt16Samples(ms.ToArray());
            double targetMagnitude = GoertzelMagnitude(samples, frequencyHz, FskGenerator.SampleRate);
            double otherFrequency = frequencyHz == FskGenerator.HoldFrequencyHz ? FskGenerator.PulseFrequencyHz : FskGenerator.HoldFrequencyHz;
            double otherMagnitude = GoertzelMagnitude(samples, otherFrequency, FskGenerator.SampleRate);

            Assert.True(targetMagnitude > otherMagnitude * 5, $"Target magnitude {targetMagnitude} not dominant over {otherMagnitude} at {otherFrequency}Hz.");
        }

        private static short[] ToInt16Samples(byte[] pcm)
        {
            var samples = new short[pcm.Length / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            return samples;
        }

        // Minimal Goertzel magnitude estimator used only to validate spectral content in tests;
        // the production detector (Goertzel-based too) lands separately in workstream B stage B3.
        private static double GoertzelMagnitude(short[] samples, double targetFrequencyHz, int sampleRate)
        {
            double omega = 2.0 * Math.PI * targetFrequencyHz / sampleRate;
            double coeff = 2.0 * Math.Cos(omega);
            double q1 = 0, q2 = 0;

            foreach (short sample in samples)
            {
                double q0 = coeff * q1 - q2 + sample;
                q2 = q1;
                q1 = q0;
            }

            return Math.Sqrt(q1 * q1 + q2 * q2 - q1 * q2 * coeff);
        }
    }
}
