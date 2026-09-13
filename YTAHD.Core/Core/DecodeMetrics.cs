using System;

namespace YTAHD.Core.Core
{
    public sealed class DecodeMetrics
    {
        public int TotalFramesSeen { get; set; }
        public int InvalidPacketCount { get; set; }
        public int DuplicateRunCount { get; set; }
        public int RecoveredGroupCount { get; set; }
        public int StrongestDuplicateQuality { get; set; }
        public int TotalDecodedPayloadBytes { get; set; }
        public int TotalFramesDecoded { get; set; }
        public int CanonicalFrameCount { get; set; }
        public double TotalElapsedMilliseconds { get; set; }
        public double FrameReadMilliseconds { get; set; }
        public double PacketDecodeMilliseconds { get; set; }
        public double AggregationMilliseconds { get; set; }

        /// <summary>
        /// Datagram count derived from the audio FSK clock (see ADR
        /// F-20260903-02-audio-fsk-clock-design.md), or null if no audio track was present /
        /// the audio clock was not used. A mismatch against <see cref="TotalFramesDecoded"/>
        /// signals dropped or corrupted video frames the video-only pipeline didn't detect.
        /// </summary>
        public int? AudioDatagramCount { get; set; }

        /// <summary>Data frames actually present after XOR-parity recovery (not just the protocol-declared total).</summary>
        public int RecoveredDataFrameCount { get; set; }

        /// <summary>Parity frames actually decoded (each covers up to one recovered data-frame loss).</summary>
        public int RecoveredParityFrameCount { get; set; }

        /// <summary>Total logical (data + parity) frames actually reconstructed from the video stream.</summary>
        public int TotalRecoveredLogicalFrames => RecoveredDataFrameCount + RecoveredParityFrameCount;

        /// <summary>
        /// Whole-payload integrity status (CR-20260912-05 stage 3): <c>Passed</c> when the assembled
        /// payload matched the stream manifest's SHA-256, <c>Failed</c> when it did not,
        /// <c>FrameOnly</c> for legacy streams without a manifest, <c>Unknown</c> when the manifest
        /// was never consulted (e.g. the durability matrix was not used).
        /// </summary>
        public IntegrityStatus IntegrityStatus { get; set; } = IntegrityStatus.Unknown;

        /// <summary>The stream manifest recovered from intact manifest frames, if any.</summary>
        public StreamManifest? Manifest { get; set; }

        /// <summary>
        /// Parity-group ids that could not be reconstructed (whole-group losses, CR-20260913-02).
        /// Empty when every group recovered; non-empty implies <see cref="IntegrityStatus"/> Failed.
        /// </summary>
        public IReadOnlyList<int> MissingDatagramIds { get; set; } = Array.Empty<int>();
        /// True when the audio clock indicates more datagrams were sent than the video
        /// pipeline could reconstruct (beyond a small tolerance for detector/AAC noise) —
        /// e.g. a whole parity group silently dropped, which XOR-parity alone cannot detect
        /// since it only recovers a single missing frame *within* a known group. Null when
        /// the audio clock was not used. See ADR F-20260903-02-audio-fsk-clock-design.md.
        /// </summary>
        public bool? HasAudioVideoDatagramMismatch(int tolerance = 2)
            => AudioDatagramCount.HasValue ? Math.Abs(AudioDatagramCount.Value - TotalRecoveredLogicalFrames) > tolerance : (bool?)null;

        public double InvalidPacketRatio => TotalFramesSeen > 0 ? InvalidPacketCount / (double)TotalFramesSeen : 0d;
    }

    /// <summary>Whole-payload integrity status reported by a decode (CR-20260912-05 stage 3).</summary>
    public enum IntegrityStatus
    {
        /// <summary>No manifest was consulted (durability matrix off, or decode never reached verification).</summary>
        Unknown = 0,

        /// <summary>Assembled payload matched the manifest's SHA-256.</summary>
        Passed = 1,

        /// <summary>Assembled payload did not match the manifest's SHA-256.</summary>
        Failed = 2,

        /// <summary>Legacy stream without a manifest: only per-frame hashes were available.</summary>
        FrameOnly = 3
    }

    public sealed class DecodeThresholds
    {
        public double MaxInvalidPacketRatio { get; set; } = 0.25d;
        public int MinDuplicateRunQuality { get; set; } = 80;
        public int MaxRecoveredGroups { get; set; } = 2;

        public bool IsSatisfiedBy(DecodeMetrics metrics)
        {
            if (metrics == null)
                throw new ArgumentNullException(nameof(metrics));

            if (metrics.TotalFramesSeen <= 0)
            {
                return true;
            }

            if (metrics.InvalidPacketRatio > MaxInvalidPacketRatio)
            {
                return false;
            }

            if (metrics.StrongestDuplicateQuality < MinDuplicateRunQuality)
            {
                return false;
            }

            if (metrics.RecoveredGroupCount > MaxRecoveredGroups)
            {
                return false;
            }

            return true;
        }
    }
}
