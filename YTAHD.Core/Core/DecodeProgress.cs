namespace YTAHD.Core.Core
{
    public sealed class DecodeProgress
    {
        public DecodeProgress(int framesSeen, int totalVideoFrames)
        {
            FramesSeen = framesSeen;
            TotalVideoFrames = totalVideoFrames;
        }

        public int FramesSeen { get; }
        public int TotalVideoFrames { get; }
        public double? Percentage => TotalVideoFrames > 0
            ? System.Math.Min(100d, FramesSeen * 100d / TotalVideoFrames)
            : null;
    }
}