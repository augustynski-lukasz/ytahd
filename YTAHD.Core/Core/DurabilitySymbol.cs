using System;
using System.Linq;
using System.Security.Cryptography;

namespace YTAHD.Core.Core;

public sealed class DurabilitySymbol
{
    public DurabilitySymbol(int groupId, int symbolId, bool isParity, byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        GroupId = groupId;
        SymbolId = symbolId;
        IsParity = isParity;
        Data = data;
        SourceLength = data.Length;
        RedundancyLevel = 1;
        Hash = ComputeHash(data);
    }

    public int GroupId { get; }
    public int SymbolId { get; }
    public bool IsParity { get; }
    public int SourceLength { get; set; }

    /// <summary>
    /// Number of source symbols the owning group actually contains. Zero when unknown; the
    /// recovery policy then falls back to the parity symbol's identifier. Needed because the
    /// final group of a payload is often partial while the parity symbol's id is always the
    /// full group size — inferring the group size from that id would treat never-existing
    /// symbols as losses and fail every short payload.
    /// </summary>
    public int GroupCount { get; set; }

    public int RedundancyLevel { get; set; }
    public byte[] Hash { get; set; }
    public byte[] Data { get; }

    public bool HasValidHash()
    {
        return Hash != null && Hash.Length == 32 && ComputeHash(Data).SequenceEqual(Hash);
    }

    public int GetQualityScore()
    {
        if (Data is null || Data.Length == 0 || !HasValidHash())
        {
            return 0;
        }

        var score = 100;
        score += SourceLength * 2;
        score += IsParity ? 40 : 80;
        score += RedundancyLevel * 25;
        score += (GroupId + 1) * 3;
        score += (SymbolId + 1) * 2;
        return score;
    }

    public static byte[] ComputeHash(byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }
}
