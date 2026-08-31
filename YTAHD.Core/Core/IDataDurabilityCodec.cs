using System.Collections.Generic;

namespace YTAHD.Core.Core;

public interface IDataDurabilityCodec
{
    DurabilityMatrixEnvelope Encode(byte[] payload);
    bool TryDecode(IEnumerable<DurabilitySymbol> symbols, int expectedLength, out byte[] payload, out int recoveredBytes);
}
