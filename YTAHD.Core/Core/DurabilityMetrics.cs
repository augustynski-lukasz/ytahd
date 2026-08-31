namespace YTAHD.Core.Core;

public sealed class DurabilityMetrics
{
    public int TotalGroups { get; set; }
    public int SymbolsReceived { get; set; }
    public int SymbolsRecovered { get; set; }
    public int ParitySymbolsReceived { get; set; }
    public int MinimumRequiredSymbols { get; set; } = 1;

    public double RecoveryRatio => TotalGroups > 0 ? SymbolsRecovered / (double)TotalGroups : 0d;
}
