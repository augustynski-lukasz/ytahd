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

    public List<byte[]> EncodeToFramePackets(byte[] payload, StreamManifest? manifest = null)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        var symbols = new DurabilityMatrixCodec(_options).Encode(payload).Symbols;
        if (symbols.Count == 0 && manifest is null)
        {
            return new List<byte[]>();
        }

        var totalDataSymbols = symbols.Count(s => !s.IsParity);
        var packets = new List<byte[]>();

        // Redundant manifest copies at stream start and end (CR-20260912-05 stage 2): at least
        // one copy survives typical head/tail loss, and each copy is additionally protected by
        // its own per-frame SHA-256.
        if (manifest is not null)
        {
            var manifestPayload = StreamManifestCodec.Serialize(manifest);
            packets.Add(FramePacketCodec.CreateManifestFramePacket(totalDataSymbols, manifestPayload));
        }

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

        if (manifest is not null)
        {
            var manifestPayload = StreamManifestCodec.Serialize(manifest);
            packets.Add(FramePacketCodec.CreateManifestFramePacket(totalDataSymbols, manifestPayload));
        }

        return packets;
    }

    public bool TryDecodeFramePackets(IEnumerable<byte[]> packets, int expectedLength, out byte[] payload, out int recoveredBytes)
    {
        var result = TryDecodeFramePackets(packets, expectedLength, out payload, out recoveredBytes, out _);
        return result;
    }

    /// <summary>
    /// Decodes frame packets, optionally reconciling the stream manifests carried by intact
    /// manifest frames (CR-20260912-05 stage 3). When at least one manifest is recovered it is
    /// returned through <paramref name="manifest"/>; conflicting manifests make the decode fail.
    /// </summary>
    public bool TryDecodeFramePackets(IEnumerable<byte[]> packets, int expectedLength, out byte[] payload, out int recoveredBytes, out StreamManifest? manifest)
    {
        payload = Array.Empty<byte>();
        recoveredBytes = 0;
        manifest = null;

        if (packets is null)
        {
            throw new ArgumentNullException(nameof(packets));
        }

        var bestSymbolsByKey = new Dictionary<(int GroupId, int SymbolId, bool IsParity), DurabilitySymbol>();
        var manifests = new List<StreamManifest>();
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
                if (frameType == FramePacket.FrameTypeManifest
                    && StreamManifestCodec.TryDeserialize(payloadBytes, out var candidate)
                    && candidate is not null)
                {
                    // Manifest frames are not durability symbols; collect every intact copy so the
                    // manifests can be reconciled below (CR-20260912-05 stage 3).
                    manifests.Add(candidate);
                }

                continue;
            }

            var key = (symbol.GroupId, symbol.SymbolId, symbol.IsParity);
            if (!bestSymbolsByKey.TryGetValue(key, out var existing) || symbol.GetQualityScore() > existing.GetQualityScore())
            {
                bestSymbolsByKey[key] = symbol;
            }
        }

        if (!ReconcileManifests(manifests, out manifest))
        {
            return false;
        }

        var codec = new DurabilityMatrixCodec(_options);
        return codec.TryDecode(bestSymbolsByKey.Values, expectedLength, out payload, out recoveredBytes);
    }

    /// <summary>
    /// Reconciles multiple intact manifest copies. Duplicate identical manifests are fine
    /// (redundant emission); genuinely conflicting manifests mean the stream is inconsistent and
    /// the decode must fail loudly rather than pick a winner.
    /// </summary>
    private static bool ReconcileManifests(IReadOnlyList<StreamManifest> manifests, out StreamManifest? reconciled)
    {
        reconciled = null;
        if (manifests.Count == 0)
        {
            return true;
        }

        var first = manifests[0];
        foreach (var candidate in manifests.Skip(1))
        {
            if (!ManifestsEqual(first, candidate))
            {
                return false;
            }
        }

        reconciled = first;
        return true;
    }

    private static bool ManifestsEqual(StreamManifest left, StreamManifest right)
    {
        return left.Version == right.Version
            && left.TotalPayloadBytes == right.TotalPayloadBytes
            && left.PayloadSha256.AsSpan().SequenceEqual(right.PayloadSha256)
            && string.Equals(left.ModulatorId, right.ModulatorId, StringComparison.Ordinal)
            && left.Width == right.Width
            && left.Height == right.Height
            && left.MacroblockSize == right.MacroblockSize
            && left.Fps == right.Fps
            && left.DataFrameCount == right.DataFrameCount
            && left.ParityGroupSize == right.ParityGroupSize
            && left.ParitySymbolsPerGroup == right.ParitySymbolsPerGroup
            && left.UseDurabilityMatrix == right.UseDurabilityMatrix;
    }
}
