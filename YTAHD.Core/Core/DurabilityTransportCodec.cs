using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace YTAHD.Core.Core;

public sealed class DurabilityTransportCodec
{
    private readonly DurabilityMatrixOptions _options;

    public DurabilityTransportCodec(DurabilityMatrixOptions? options = null)
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
    }

    public List<byte[]> EncodeToFramePackets(byte[] payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        var symbols = new DurabilityMatrixCodec(_options).Encode(payload).Symbols;
        if (symbols.Count == 0)
        {
            return new List<byte[]>();
        }

        var totalDataSymbols = symbols.Count(s => !s.IsParity);
        var packets = new List<byte[]>();

        foreach (var group in symbols.GroupBy(s => s.GroupId).OrderBy(g => g.Key))
        {
            var totalGroupSymbols = Math.Min(_options.GroupSize, totalDataSymbols - (group.Key * _options.GroupSize));
            var groupStart = group.Key * _options.GroupSize;
            var parity = group.FirstOrDefault(s => s.IsParity);

            foreach (var symbol in group.Where(s => !s.IsParity).OrderBy(s => s.SymbolId))
            {
                var frameIndex = groupStart + symbol.SymbolId;
                packets.Add(FramePacketCodec.CreateDataFramePacket(
                    frameIndex,
                    totalDataSymbols,
                    groupStart,
                    Math.Max(1, totalGroupSymbols),
                    symbol.SourceLength,
                    symbol.Data,
                    symbol.Data.Length));
            }

            if (parity is not null)
            {
                packets.Add(FramePacketCodec.CreateParityFramePacket(
                    groupStart,
                    Math.Max(1, totalGroupSymbols),
                    totalDataSymbols,
                    parity.Data));
            }
        }

        return packets;
    }

    public bool TryDecodeFramePackets(IEnumerable<byte[]> packets, int expectedLength, out byte[] payload, out int recoveredBytes)
    {
        payload = Array.Empty<byte>();
        recoveredBytes = 0;

        if (packets is null)
        {
            throw new ArgumentNullException(nameof(packets));
        }

        var bestSymbolsByKey = new Dictionary<(int GroupId, int SymbolId, bool IsParity), DurabilitySymbol>();
        foreach (var packet in packets)
        {
            if (packet is null || packet.Length < FramePacket.HeaderBytes)
            {
                continue;
            }

            if (!FramePacketCodec.TryDecode(packet, out var frameType, out var frameIndex, out var totalDataFrames, out var groupStart, out var groupCount, out var payloadLength, out var payloadBytes))
            {
                continue;
            }

            DurabilitySymbol symbol;
            if (frameType == FramePacket.FrameTypeData)
            {
                var groupId = (groupStart / _options.GroupSize);
                var symbolId = Math.Max(0, frameIndex - groupStart);
                symbol = new DurabilitySymbol(groupId, symbolId, false, payloadBytes)
                {
                    SourceLength = payloadLength,
                    RedundancyLevel = 1,
                    Hash = SHA256.HashData(payloadBytes)
                };
            }
            else if (frameType == FramePacket.FrameTypeParity)
            {
                var groupId = groupStart / Math.Max(1, _options.GroupSize);
                symbol = new DurabilitySymbol(groupId, _options.GroupSize, true, payloadBytes)
                {
                    SourceLength = payloadLength,
                    RedundancyLevel = 1,
                    Hash = SHA256.HashData(payloadBytes)
                };
            }
            else
            {
                continue;
            }

            var key = (symbol.GroupId, symbol.SymbolId, symbol.IsParity);
            if (!bestSymbolsByKey.TryGetValue(key, out var existing) || symbol.GetQualityScore() > existing.GetQualityScore())
            {
                bestSymbolsByKey[key] = symbol;
            }
        }

        var codec = new DurabilityMatrixCodec(_options);
        return codec.TryDecode(bestSymbolsByKey.Values, expectedLength, out payload, out recoveredBytes);
    }
}
