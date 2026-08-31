using System;

namespace YTAHD.Core.Core;

public static class DurabilityRecoveryPolicy
{
    public static int ResolveMinimumRequiredSymbols(DurabilityMatrixOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return options.ComputeRecoveryThreshold();
    }

    public static IReadOnlyList<DurabilitySymbol> SelectBestSubset(IEnumerable<DurabilitySymbol> symbols, DurabilityMatrixOptions? options = null)
    {
        if (symbols is null)
        {
            throw new ArgumentNullException(nameof(symbols));
        }

        var candidateSymbols = symbols
            .Where(s => s != null && s.Data != null && s.HasValidHash())
            .GroupBy(s => (s.GroupId, s.SymbolId, s.IsParity))
            .Select(g => g.OrderByDescending(s => s.GetQualityScore()).First())
            .OrderBy(s => s.GroupId)
            .ThenBy(s => s.SymbolId)
            .ToList();

        if (options is null || options.GroupSize <= 0)
        {
            return candidateSymbols;
        }

        return candidateSymbols
            .Where(s => s.GroupId >= 0)
            .ToList();
    }

    public static bool CanRecover(DurabilityMetrics metrics)
    {
        if (metrics is null)
        {
            throw new ArgumentNullException(nameof(metrics));
        }

        if (metrics.MinimumRequiredSymbols <= 0)
        {
            return true;
        }

        return metrics.SymbolsRecovered >= metrics.MinimumRequiredSymbols;
    }
}
