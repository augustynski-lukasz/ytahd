namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Implemented by modulators and frame bit decoders whose inner loops can run in parallel
    /// (Phase 3 block-row render/decode, Phase 4 motion tile search).
    /// </summary>
    /// <remarks>
    /// The pipeline assigns <see cref="InnerDegreeOfParallelism"/> from
    /// <see cref="YTAHD.Core.Application.ParallelismPolicy.ResolveInnerDegree(int, int)"/> so the
    /// configured CPU budget is shared with the frame-level workers rather than granted twice.
    /// Decoders inherit the value from the modulator that produced them, so a single assignment
    /// on the modulator covers every decoder instance the pipeline creates.
    /// A value of one keeps the inner loop serial; see
    /// <see cref="InnerLoopParallelism.ForEachRow(int, int, System.Action{int})"/>.
    /// The default is the machine's logical processor count, which preserves the historical
    /// behaviour when no pipeline configured the value.
    /// </remarks>
    public interface IParallelismConfigurable
    {
        /// <summary>Worker count for the modulator's or decoder's inner loop; one means serial.</summary>
        int InnerDegreeOfParallelism { get; set; }
    }
}
