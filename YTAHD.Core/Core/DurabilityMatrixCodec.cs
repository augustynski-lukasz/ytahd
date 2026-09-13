using System;
using System.Collections.Generic;
using System.Linq;

namespace YTAHD.Core.Core;

public sealed class DurabilityMatrixCodec : IDataDurabilityCodec
{
    private readonly DurabilityMatrixOptions _options;

    public DurabilityMatrixCodec(DurabilityMatrixOptions? options = null)
    {
        _options = options ?? new DurabilityMatrixOptions();

        if (_options.SymbolSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SymbolSize must be greater than zero.");
        }

        if (_options.GroupSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "GroupSize must be greater than zero.");
        }

        if (_options.ParitySymbolsPerGroup < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ParitySymbolsPerGroup must be zero or greater.");
        }
    }

    public DurabilityMatrixEnvelope Encode(byte[] payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        var symbols = new List<DurabilitySymbol>();
        if (payload.Length == 0)
        {
            return new DurabilityMatrixEnvelope(symbols);
        }

        var totalSymbols = (int)Math.Ceiling(payload.Length / (double)_options.SymbolSize);
        var totalGroups = (int)Math.Ceiling(totalSymbols / (double)_options.GroupSize);

        for (var groupIndex = 0; groupIndex < totalGroups; groupIndex++)
        {
            var groupSourceSymbols = new List<DurabilitySymbol>();
            var sourceCount = Math.Min(_options.GroupSize, totalSymbols - (groupIndex * _options.GroupSize));

            for (var symbolIndex = 0; symbolIndex < sourceCount; symbolIndex++)
            {
                var symbolOffset = ((groupIndex * _options.GroupSize) + symbolIndex) * _options.SymbolSize;
                var symbolLength = Math.Min(_options.SymbolSize, payload.Length - symbolOffset);
                var chunk = new byte[_options.SymbolSize];
                Array.Copy(payload, symbolOffset, chunk, 0, symbolLength);

                groupSourceSymbols.Add(new DurabilitySymbol(groupIndex, symbolIndex, false, chunk)
                {
                    SourceLength = symbolLength,
                    GroupCount = sourceCount,
                    RedundancyLevel = 1
                });
            }

            symbols.AddRange(groupSourceSymbols);

            if (_options.ParitySymbolsPerGroup <= 0)
            {
                continue;
            }

            var parity = new byte[_options.SymbolSize];
            foreach (var symbol in groupSourceSymbols)
            {
                for (var i = 0; i < parity.Length; i++)
                {
                    parity[i] ^= symbol.Data[i];
                }
            }

            symbols.Add(new DurabilitySymbol(groupIndex, _options.GroupSize, true, parity)
            {
                SourceLength = parity.Length,
                GroupCount = sourceCount,
                RedundancyLevel = 1
            });
        }

        return new DurabilityMatrixEnvelope(symbols);
    }

    public List<DurabilityPacket> EncodeToPackets(byte[] payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        return Encode(payload).Symbols
            .Select(s => new DurabilityPacket
            {
                GroupId = s.GroupId,
                SymbolId = s.SymbolId,
                IsParity = s.IsParity,
                SourceLength = s.SourceLength,
                GroupCount = s.GroupCount,
                RedundancyLevel = s.RedundancyLevel,
                Hash = s.Hash.ToArray(),
                Data = s.Data.ToArray()
            })
            .ToList();
    }

    public bool TryDecode(IEnumerable<DurabilitySymbol> symbols, int expectedLength, out byte[] payload, out int recoveredBytes)
    {
        payload = Array.Empty<byte>();
        recoveredBytes = 0;

        if (symbols is null)
        {
            throw new ArgumentNullException(nameof(symbols));
        }

        if (expectedLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLength));
        }

        var validSymbols = DurabilityRecoveryPolicy.SelectBestSubset(symbols, _options)
            .GroupBy(s => (s.GroupId, s.SymbolId, s.IsParity))
            .Select(g => g.OrderByDescending(s => s.GetQualityScore()).First())
            .ToList();

        if (validSymbols.Count == 0)
        {
            return false;
        }

        var groups = validSymbols
            .GroupBy(s => s.GroupId)
            .OrderBy(g => g.Key)
            .ToList();

        var reconstructed = new List<byte>();

        foreach (var group in groups)
        {
            if (!TryRecoverGroup(group.ToList(), out var recoveredGroupSymbols))
            {
                return false;
            }

            foreach (var symbol in recoveredGroupSymbols.OrderBy(s => s.SymbolId))
            {
                reconstructed.AddRange(symbol.Data);
            }
        }

        payload = reconstructed.Take(expectedLength).ToArray();
        recoveredBytes = payload.Length;
        return payload.Length == expectedLength;
    }

    /// <summary>
    /// Hole-tolerant decode (CR-20260913-02): recovers each parity group independently.
    /// A group whose symbols and parity are both absent (or whose loss count exceeds the
    /// parity budget) becomes a zero-filled hole instead of aborting the whole decode.
    /// Returns true when reconstruction produced the expected length — even with holes —
    /// and reports the group ids that could not be recovered. Callers must treat a
    /// non-empty hole map as failed integrity (the output is not byte-faithful).
    /// </summary>
    public bool TryDecodeWithHoles(IEnumerable<DurabilitySymbol> symbols, int expectedLength, out byte[] payload, out int recoveredBytes, out IReadOnlyList<int> missingGroupIds)
    {
        payload = Array.Empty<byte>();
        recoveredBytes = 0;
        missingGroupIds = Array.Empty<int>();

        if (symbols is null)
        {
            throw new ArgumentNullException(nameof(symbols));
        }

        if (expectedLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLength));
        }

        var validSymbols = DurabilityRecoveryPolicy.SelectBestSubset(symbols, _options)
            .GroupBy(s => (s.GroupId, s.SymbolId, s.IsParity))
            .Select(g => g.OrderByDescending(s => s.GetQualityScore()).First())
            .ToList();

        // The expected schedule covers the whole declared length, so leading, interior, and
        // trailing losses all fall out of one uniform iteration (CR-20260913-02).
        int groupsCovered = (int)Math.Ceiling(expectedLength / (double)(_options.GroupSize * _options.SymbolSize));
        var groupsById = validSymbols
            .GroupBy(s => s.GroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var reconstructed = new List<byte>();
        var holes = new List<int>();
        var symbolSize = _options.SymbolSize;

        for (int groupId = 0; groupId < groupsCovered; groupId++)
        {
            if (!groupsById.TryGetValue(groupId, out var group) ||
                !TryRecoverGroup(group, out var recoveredGroupSymbols))
            {
                // Group entirely absent (nothing arrived) or unreconstructable: zero-filled
                // hole sized from the schedule. Hash verification fails loudly downstream.
                holes.Add(groupId);
                reconstructed.AddRange(new byte[_options.GroupSize * symbolSize]);
                continue;
            }

            foreach (var symbol in recoveredGroupSymbols.OrderBy(s => s.SymbolId))
            {
                reconstructed.AddRange(symbol.Data);
            }
        }

        missingGroupIds = holes;

        payload = reconstructed.Take(expectedLength).ToArray();
        if (payload.Length < expectedLength)
        {
            // The schedule may overshoot the declared length at the tail; trim exactly.
            var trimmed = new byte[expectedLength];
            Buffer.BlockCopy(payload, 0, trimmed, 0, payload.Length);
            payload = trimmed;
        }

        recoveredBytes = payload.Length;
        return true;
    }

    public bool TryDecodePackets(IEnumerable<DurabilityPacket> packets, int expectedLength, out byte[] payload, out int recoveredBytes)
    {
        if (packets is null)
        {
            throw new ArgumentNullException(nameof(packets));
        }

        return TryDecode(
            packets.Select(p => new DurabilitySymbol(p.GroupId, p.SymbolId, p.IsParity, p.Data.ToArray())
            {
                SourceLength = p.SourceLength,
                GroupCount = p.GroupCount,
                RedundancyLevel = p.RedundancyLevel,
                Hash = p.Hash.ToArray()
            }),
            expectedLength,
            out payload,
            out recoveredBytes);
    }

    private bool TryRecoverGroup(IReadOnlyList<DurabilitySymbol> groupSymbols, out List<DurabilitySymbol> recoveredSymbols)
    {
        recoveredSymbols = new List<DurabilitySymbol>();

        if (groupSymbols.Count == 0)
        {
            return false;
        }

        var deduplicated = groupSymbols
            .GroupBy(s => (s.GroupId, s.SymbolId, s.IsParity))
            .Select(g => g.OrderByDescending(s => s.GetQualityScore()).First())
            .ToList();

        var sourceSymbols = deduplicated
            .Where(s => !s.IsParity)
            .GroupBy(s => s.SymbolId)
            .Select(g => g.OrderByDescending(s => s.GetQualityScore()).First())
            .OrderBy(s => s.SymbolId)
            .ToList();

        var paritySymbols = deduplicated
            .Where(s => s.IsParity)
            .GroupBy(s => s.SymbolId)
            .Select(g => g.OrderByDescending(s => s.GetQualityScore()).First())
            .OrderBy(s => s.SymbolId)
            .ToList();

        if (sourceSymbols.Count == 0)
        {
            return false;
        }

        var actualSourceIds = sourceSymbols.Select(s => s.SymbolId).ToHashSet();

        // The group's real size comes from the packet metadata when available. The parity
        // symbol's id is the full GroupSize even for a partial final group, so falling back to
        // it would count never-existing symbols as losses and fail every short payload. Data
        // symbols carry the same GroupCount header, so they must be consulted too: without
        // parity there is otherwise no authoritative group size and a partial group would be
        // silently truncated instead of reported as a hole (CR-20260913-02).
        var declaredGroupCounts = deduplicated
            .Where(s => !s.IsParity)
            .Select(s => s.GroupCount)
            .Where(c => c > 0)
            .ToList();
        var expectedSourceCount = paritySymbols.Count > 0
            ? (paritySymbols[0].GroupCount > 0
                ? paritySymbols[0].GroupCount
                : Math.Max(1, paritySymbols[0].SymbolId))
            : declaredGroupCounts.Count > 0
                ? declaredGroupCounts.Max()
                : (actualSourceIds.Count > 0 ? actualSourceIds.Max() + 1 : 0);

        var missingSourceIds = Enumerable.Range(0, expectedSourceCount)
            .Where(i => !actualSourceIds.Contains(i))
            .ToList();

        if (missingSourceIds.Count == 0)
        {
            recoveredSymbols.AddRange(sourceSymbols);
            return true;
        }

        if (paritySymbols.Count == 0 || missingSourceIds.Count > _options.ParitySymbolsPerGroup)
        {
            return false;
        }

        var parity = paritySymbols[0];
        var recoveredSource = sourceSymbols.ToDictionary(s => s.SymbolId, s => s.Data.ToArray());

        foreach (var missingId in missingSourceIds)
        {
            var candidate = new byte[_options.SymbolSize];
            var remainingXor = new byte[_options.SymbolSize];

            foreach (var symbol in sourceSymbols)
            {
                if (symbol.SymbolId == missingId)
                {
                    continue;
                }

                for (var i = 0; i < remainingXor.Length; i++)
                {
                    remainingXor[i] ^= symbol.Data[i];
                }
            }

            for (var i = 0; i < candidate.Length; i++)
            {
                candidate[i] = (byte)(parity.Data[i] ^ remainingXor[i]);
            }

            recoveredSource[missingId] = candidate;
        }

        for (var symbolId = 0; symbolId < expectedSourceCount; symbolId++)
        {
            if (recoveredSource.TryGetValue(symbolId, out var data))
            {
                recoveredSymbols.Add(new DurabilitySymbol(groupSymbols[0].GroupId, symbolId, false, data));
            }
        }

        // The recovery threshold is derived from the full group size; a partial final group has
        // fewer symbols by construction, so its budget scales with the real group size.
        var requiredSymbols = Math.Max(1, expectedSourceCount - _options.ParitySymbolsPerGroup);
        return recoveredSymbols.Count >= requiredSymbols;
    }
}
