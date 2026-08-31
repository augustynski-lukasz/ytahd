using System;
using System.Collections.Generic;

namespace YTAHD.Core.Core;

public sealed class DurabilityMatrixEnvelope
{
    public DurabilityMatrixEnvelope(IReadOnlyList<DurabilitySymbol> symbols)
    {
        Symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
    }

    public IReadOnlyList<DurabilitySymbol> Symbols { get; }
}
