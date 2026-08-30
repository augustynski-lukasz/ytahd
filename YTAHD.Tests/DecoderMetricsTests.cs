using Xunit;
using YTAHD.Core.Core;

namespace YTAHD.Tests
{
    public class DecoderMetricsTests
    {
        [Fact]
        public void DecodeThresholds_Rejects_Over_Threshold_Recovery_And_Invalid_Rate()
        {
            var metrics = new DecodeMetrics
            {
                TotalFramesSeen = 10,
                InvalidPacketCount = 2,
                DuplicateRunCount = 3,
                RecoveredGroupCount = 1,
                StrongestDuplicateQuality = 85
            };

            var thresholds = new DecodeThresholds
            {
                MaxInvalidPacketRatio = 0.25,
                MinDuplicateRunQuality = 80,
                MaxRecoveredGroups = 2
            };

            Assert.True(thresholds.IsSatisfiedBy(metrics));

            metrics.InvalidPacketCount = 4;
            metrics.RecoveredGroupCount = 3;
            metrics.StrongestDuplicateQuality = 70;

            Assert.False(thresholds.IsSatisfiedBy(metrics));
        }
    }
}
