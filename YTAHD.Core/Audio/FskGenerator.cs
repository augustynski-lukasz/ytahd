using System;
using System.IO;
using System.Threading.Tasks;

namespace YTAHD.Core.Audio
{
    /// <summary>
    /// Generates the audio-assisted datagram clock: a continuous 16-bit PCM mono tone that
    /// holds at <see cref="HoldFrequencyHz"/> and briefly shifts to <see cref="PulseFrequencyHz"/>
    /// for <see cref="PulseDurationVideoFrames"/> video frames whenever a new datagram starts.
    /// Phase is tracked continuously across calls and each segment is amplitude-ramped at its
    /// edges, so switching frequency never introduces a sample discontinuity or click.
    /// See ADR F-20260903-02-audio-fsk-clock-design.md.
    /// </summary>
    public sealed class FskGenerator
    {
        public const int SampleRate = 44100;
        public const double HoldFrequencyHz = 1000.0;
        public const double PulseFrequencyHz = 1500.0;

        // AAC frames are 1024 samples (~23ms); a single 60fps video frame is only 735 samples
        // (~16.7ms). A 1-frame pulse cannot survive re-encode sample-accurately, so the pulse
        // spans 2 video frames per the ADR's corrected design.
        public const int PulseDurationVideoFrames = 2;

        private const double RampSeconds = 0.003; // 3ms fade in/out avoids clicks at segment edges
        private const short Amplitude = 12000;    // headroom below Int16 full scale (32767)

        private double _phaseRadians;

        /// <summary>Writes a hold-tone segment covering <paramref name="videoFrameCount"/> video frames.</summary>
        public Task WriteHoldToneAsync(Stream outStream, int videoFrameCount, int fps)
        {
            if (videoFrameCount <= 0) throw new ArgumentOutOfRangeException(nameof(videoFrameCount));
            if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
            return WriteToneSegmentAsync(outStream, HoldFrequencyHz, videoFrameCount / (double)fps);
        }

        /// <summary>Writes one datagram-start pulse, <paramref name="videoFrameCount"/> video frames long
        /// (defaults to <see cref="PulseDurationVideoFrames"/>; callers may cap it to fit a shorter gap).</summary>
        public Task WritePulseAsync(Stream outStream, int fps, int videoFrameCount = PulseDurationVideoFrames)
        {
            if (videoFrameCount <= 0) throw new ArgumentOutOfRangeException(nameof(videoFrameCount));
            if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
            return WriteToneSegmentAsync(outStream, PulseFrequencyHz, videoFrameCount / (double)fps);
        }

        private async Task WriteToneSegmentAsync(Stream outStream, double frequencyHz, double durationSeconds)
        {
            int sampleCount = (int)Math.Round(durationSeconds * SampleRate);
            if (sampleCount <= 0) return;

            int rampSamples = Math.Min(sampleCount / 2, (int)Math.Round(RampSeconds * SampleRate));
            double angularStep = 2.0 * Math.PI * frequencyHz / SampleRate;

            var buffer = new byte[sampleCount * 2];
            for (int i = 0; i < sampleCount; i++)
            {
                double envelope = 1.0;
                if (rampSamples > 0)
                {
                    if (i < rampSamples) envelope = i / (double)rampSamples;
                    else if (i >= sampleCount - rampSamples) envelope = (sampleCount - 1 - i) / (double)rampSamples;
                }

                short sample = (short)Math.Round(Amplitude * envelope * Math.Sin(_phaseRadians));
                buffer[i * 2] = (byte)(sample & 0xFF);
                buffer[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);

                _phaseRadians += angularStep;
                if (_phaseRadians > Math.PI * 2) _phaseRadians -= Math.PI * 2;
            }

            await outStream.WriteAsync(buffer, 0, buffer.Length);
        }
    }
}
