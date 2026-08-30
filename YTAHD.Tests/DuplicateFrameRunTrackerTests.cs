using System;
using System.Linq;
using Xunit;
using YTAHD.Core.Core;

namespace YTAHD.Tests
{
    public class DuplicateFrameRunTrackerTests
    {
        [Fact]
        public void DuplicateFrameRunTracker_Prefers_Stronger_Frame_Within_Run()
        {
            var tracker = new DuplicateFrameRunTracker();
            var signature = new byte[] { 1, 2, 3, 4 };
            var strongFrame = new byte[] { 9, 9, 9, 9 };
            var weakFrame = new byte[] { 1, 1, 1, 1 };
            var nextFrame = new byte[] { 2, 2, 2, 2 };
            var nextSignature = new byte[] { 5, 6, 7, 8 };

            Assert.Null(tracker.Update(strongFrame, signature, 10));
            Assert.Null(tracker.Update(weakFrame, signature, 30));

            Assert.Equal(30, tracker.BestQuality);
            Assert.True(tracker.BestFrame.SequenceEqual(weakFrame));

            var completed = tracker.Update(nextFrame, nextSignature, 15);
            Assert.NotNull(completed);
            Assert.True(completed!.BestFrame.SequenceEqual(weakFrame));
            Assert.Equal(1, tracker.RunLength);
        }
    }
}
