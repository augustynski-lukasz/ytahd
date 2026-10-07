using System;

namespace YTAHD.Core.Core;

/// <summary>
/// Options for the XOR-parity durability matrix (CR-20261006-01): the only implemented
/// redundancy scheme. The recovery guarantee is exactly
/// <see cref="ComputeRecoveryThreshold"/> symbols per group — with the CLI defaults
/// (GroupSize 4, one parity symbol) that is one missing symbol per group. Fountain codes,
/// Reed–Solomon, and other erasure-coding schemes are future extension paths behind
/// <c>IDataDurabilityCodec</c> (ARCHITECTURE.md §12.3), not selectable modes here.
/// </summary>
public sealed class DurabilityMatrixOptions
{
    public int SymbolSize { get; init; } = 32;
    public int GroupSize { get; init; } = 8;
    public int ParitySymbolsPerGroup { get; init; } = 1;

    public int ComputeRecoveryThreshold()
    {
        if (GroupSize <= 0)
        {
            throw new InvalidOperationException("GroupSize must be greater than zero.");
        }

        return Math.Max(1, GroupSize - ParitySymbolsPerGroup);
    }
}
