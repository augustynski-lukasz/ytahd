using System;
using System.Collections.Generic;

namespace YTAHD.Core.Audio
{
    /// <summary>
    /// Detects FSK datagram-start pulses (brief 1500 Hz bursts against a 1000 Hz hold tone,
    /// see <see cref="FskGenerator"/>) in a raw PCM mono 16-bit stream via per-window two-bin
    /// Goertzel magnitude comparison. Frequency-ratio based, never amplitude-based, so it
    /// stays correct under loudness normalization. See ADR F-20260903-02-audio-fsk-clock-design.md.
    /// </summary>
    public static class GoertzelDetector
    {
        public static short[] ToInt16Samples(ReadOnlySpan<byte> pcm)
        {
            var samples = new short[pcm.Length / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            return samples;
        }

        public static double GoertzelMagnitude(ReadOnlySpan<short> samples, double targetFrequencyHz, int sampleRate)
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

        /// <summary>True if the pulse frequency's Goertzel magnitude dominates the hold frequency's in this window.</summary>
        public static bool ClassifyWindow(ReadOnlySpan<short> window, int sampleRate)
        {
            double pulseMagnitude = GoertzelMagnitude(window, FskGenerator.PulseFrequencyHz, sampleRate);
            double holdMagnitude = GoertzelMagnitude(window, FskGenerator.HoldFrequencyHz, sampleRate);
            return pulseMagnitude > holdMagnitude;
        }

        /// <summary>
        /// Classifies fixed-size, non-overlapping windows across the whole sample buffer.
        /// An incomplete trailing window shorter than <paramref name="windowSamples"/> is
        /// dropped rather than padded, since it cannot represent a full video frame.
        /// </summary>
        public static bool[] DetectPulseWindows(ReadOnlySpan<short> samples, int sampleRate, int windowSamples)
        {
            if (windowSamples <= 0) throw new ArgumentOutOfRangeException(nameof(windowSamples));

            int windowCount = samples.Length / windowSamples;
            var result = new bool[windowCount];
            for (int i = 0; i < windowCount; i++)
            {
                result[i] = ClassifyWindow(samples.Slice(i * windowSamples, windowSamples), sampleRate);
            }

            return result;
        }

        /// <summary>
        /// Converts a per-window pulse/hold sequence into datagram-start window indices: a
        /// rising edge from hold to pulse marks a new datagram boundary (adjacent pulse
        /// windows belonging to the same multi-frame pulse merge into a single boundary).
        /// The stream is assumed to start in the "hold" state before the first pulse.
        /// </summary>
        public static int[] FindDatagramBoundaries(bool[] pulseWindows)
        {
            var boundaries = new List<int>();
            bool previous = false;
            for (int i = 0; i < pulseWindows.Length; i++)
            {
                if (pulseWindows[i] && !previous)
                {
                    boundaries.Add(i);
                }

                previous = pulseWindows[i];
            }

            return boundaries.ToArray();
        }

        /// <summary>
        /// One-shot helper: classifies <paramref name="samples"/> into fps-aligned windows
        /// (one window per video frame, optionally shifted by <paramref name="sampleOffset"/>
        /// to compensate for AAC encoder priming delay) and returns datagram-start frame
        /// indices.
        /// </summary>
        public static int[] DetectDatagramBoundaries(ReadOnlySpan<short> samples, int sampleRate, int fps, int sampleOffset = 0)
        {
            if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));

            if (sampleOffset > 0)
            {
                samples = sampleOffset < samples.Length ? samples.Slice(sampleOffset) : ReadOnlySpan<short>.Empty;
            }

            int windowSamples = sampleRate / fps;
            var windows = DetectPulseWindows(samples, sampleRate, windowSamples);
            return FindDatagramBoundaries(windows);
        }
    }
}
