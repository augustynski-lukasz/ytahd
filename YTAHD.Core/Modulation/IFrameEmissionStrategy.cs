namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Optional capability for modulators that need a custom physical-frame emission
    /// pattern instead of the default fixed 3x repeat per logical frame. Used by Phase 4
    /// (motion-vector modulation) to interleave a canonical "no data" separator frame
    /// between datagrams instead of repeating the same displaced frame. Modulators that do
    /// not implement this keep the historical fixed 3x repeat behavior unchanged.
    /// </summary>
    public interface IFrameEmissionStrategy
    {
        /// <summary>Physical repeats written to the video stream for each logical (data/parity) frame.</summary>
        int RepeatCount { get; }

        /// <summary>True if a canonical (empty-payload) separator frame should follow each logical frame's repeats.</summary>
        bool UsesCanonicalSeparator { get; }
    }
}
