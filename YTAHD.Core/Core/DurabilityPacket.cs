using System;

namespace YTAHD.Core.Core;

public sealed class DurabilityPacket
{
    public int GroupId { get; init; }
    public int SymbolId { get; init; }
    public bool IsParity { get; init; }
    public int SourceLength { get; init; }
    public int RedundancyLevel { get; init; }
    public byte[] Hash { get; init; } = Array.Empty<byte>();
    public byte[] Data { get; init; } = Array.Empty<byte>();
}
