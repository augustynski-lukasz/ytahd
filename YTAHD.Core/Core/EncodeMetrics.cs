namespace YTAHD.Core.Core
{
    public sealed class EncodeMetrics
    {
        public int InputPayloadBytes { get; set; }
        public int PayloadBytesPerFrame { get; set; }
        public int TotalDataFrames { get; set; }
        public int TotalFramesWritten { get; set; }
        public int TotalFramesInVideo { get; set; }
    }
}
