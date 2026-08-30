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

        public double InvalidPacketRatio => TotalFramesSeen > 0 ? InvalidPacketCount / (double)TotalFramesSeen : 0d;
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
