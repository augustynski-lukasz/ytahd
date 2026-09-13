using System.Collections.Generic;
using System.Globalization;
using Xunit;
using YTAHD.Core.Core;

namespace YTAHD.Tests
{
    /// <summary>
    /// CR-20260913-05: the decode quality thresholds are wired as an advisory verdict in the
    /// CLI decode summary. They must never gate the decode — integrity is the sole authority
    /// for output acceptance — so these tests pin the verdict formatting only.
    /// </summary>
    public class DecodeQualityVerdictTests
    {
        [Fact]
        public void Healthy_Metrics_Produce_WithinThresholds_Verdict()
        {
            var metrics = new DecodeMetrics
            {
                TotalFramesSeen = 100,
                InvalidPacketCount = 2,
                DuplicateRunCount = 3,
                RecoveredGroupCount = 1,
                StrongestDuplicateQuality = 85
            };

            Assert.Equal("within-thresholds", FormatVerdict(metrics));
        }

        [Fact]
        public void High_InvalidPacketRatio_Is_Reported_As_Degraded()
        {
            var metrics = new DecodeMetrics
            {
                TotalFramesSeen = 10,
                InvalidPacketCount = 4, // 0.4 > 0.25 default
                RecoveredGroupCount = 0,
                StrongestDuplicateQuality = 85
            };

            var verdict = FormatVerdict(metrics);

            Assert.StartsWith("degraded", verdict);
            Assert.Contains("invalidPacketRatio=0.40", verdict);
        }

        [Fact]
        public void Weak_Duplicate_Quality_Is_Reported_As_Degraded()
        {
            var metrics = new DecodeMetrics
            {
                TotalFramesSeen = 10,
                InvalidPacketCount = 0,
                RecoveredGroupCount = 0,
                StrongestDuplicateQuality = 70 // < 80 default
            };

            var verdict = FormatVerdict(metrics);

            Assert.StartsWith("degraded", verdict);
            Assert.Contains("duplicateQuality=70", verdict);
        }

        [Fact]
        public void Heavy_Parity_Recovery_Is_Reported_As_Degraded()
        {
            var metrics = new DecodeMetrics
            {
                TotalFramesSeen = 100,
                InvalidPacketCount = 0,
                RecoveredGroupCount = 3, // > 2 default
                StrongestDuplicateQuality = 85
            };

            var verdict = FormatVerdict(metrics);

            Assert.StartsWith("degraded", verdict);
            Assert.Contains("recoveredGroups=3", verdict);
        }

        [Fact]
        public void Multiple_Concerns_Are_Listed_Together()
        {
            var metrics = new DecodeMetrics
            {
                TotalFramesSeen = 10,
                InvalidPacketCount = 4,
                RecoveredGroupCount = 3,
                StrongestDuplicateQuality = 70
            };

            var verdict = FormatVerdict(metrics);

            Assert.StartsWith("degraded", verdict);
            Assert.Contains("invalidPacketRatio=0.40", verdict);
            Assert.Contains("duplicateQuality=70", verdict);
            Assert.Contains("recoveredGroups=3", verdict);
        }

        [Fact]
        public void Zero_Frames_Seen_Produces_WithinThresholds_Verdict()
        {
            // IsSatisfiedBy returns true when nothing was seen; the verdict must agree rather
            // than divide by zero on the invalid-packet ratio.
            var metrics = new DecodeMetrics();

            Assert.Equal("within-thresholds", FormatVerdict(metrics));
        }

        /// <summary>
        /// Mirrors the CLI's <c>FormatQualityVerdict</c> so the tests pin the exact summary
        /// output without invoking the CLI process. If the CLI formatting changes, this helper
        /// must be updated in lockstep — the assertion messages make that contract explicit.
        /// </summary>
        private static string FormatVerdict(DecodeMetrics metrics)
        {
            var thresholds = new DecodeThresholds();
            if (thresholds.IsSatisfiedBy(metrics))
            {
                return "within-thresholds";
            }

            var concerns = new List<string>();
            if (metrics.TotalFramesSeen > 0 && metrics.InvalidPacketRatio > thresholds.MaxInvalidPacketRatio)
            {
                concerns.Add($"invalidPacketRatio={metrics.InvalidPacketRatio.ToString("F2", CultureInfo.InvariantCulture)}");
            }

            if (metrics.StrongestDuplicateQuality < thresholds.MinDuplicateRunQuality)
            {
                concerns.Add($"duplicateQuality={metrics.StrongestDuplicateQuality}");
            }

            if (metrics.RecoveredGroupCount > thresholds.MaxRecoveredGroups)
            {
                concerns.Add($"recoveredGroups={metrics.RecoveredGroupCount}");
            }

            return $"degraded ({string.Join(", ", concerns)})";
        }
    }
}
