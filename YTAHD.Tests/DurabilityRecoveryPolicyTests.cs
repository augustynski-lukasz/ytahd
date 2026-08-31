using System.Linq;
using Xunit;
using YTAHD.Core.Core;

namespace YTAHD.Tests;

public class DurabilityRecoveryPolicyTests
{
    [Fact]
    public void SelectBestSubset_PrefersHighestQualitySymbols_AndDeduplicatesDuplicates()
    {
        var options = new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        };

        var validA = new DurabilitySymbol(0, 0, false, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())
        {
            RedundancyLevel = 1,
            SourceLength = 32
        };

        var validB = new DurabilitySymbol(0, 0, false, Enumerable.Range(32, 64).Select(i => (byte)i).ToArray())
        {
            RedundancyLevel = 2,
            SourceLength = 32
        };

        var parity = new DurabilitySymbol(0, 4, true, new byte[32])
        {
            RedundancyLevel = 1,
            SourceLength = 32
        };

        var selected = DurabilityRecoveryPolicy.SelectBestSubset(new[] { validA, validB, parity }, options);

        Assert.Equal(2, selected.Count);
        Assert.Contains(selected, s => s.SymbolId == 0 && !s.IsParity);
        Assert.Contains(selected, s => s.SymbolId == 4 && s.IsParity);
    }
}
