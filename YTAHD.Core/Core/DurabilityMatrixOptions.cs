using System;

namespace YTAHD.Core.Core;

public sealed class DurabilityMatrixOptions
{
    public int SymbolSize { get; init; } = 32;
    public int GroupSize { get; init; } = 8;
    public int ParitySymbolsPerGroup { get; init; } = 1;
    public DurabilityRedundancyMode RedundancyMode { get; init; } = DurabilityRedundancyMode.XorParity;

    public int ComputeRecoveryThreshold()
    {
        if (GroupSize <= 0)
        {
            throw new InvalidOperationException("GroupSize must be greater than zero.");
        }

        return Math.Max(1, GroupSize - ParitySymbolsPerGroup);
    }
}
