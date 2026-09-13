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

    public List<byte[]> EncodeToFramePackets(byte[] payload, StreamManifest? manifest = null, int maxPayloadBytesPerFrame = 0)
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
            foreach (var chunk in SplitManifestIntoChunks(manifest, totalDataSymbols, maxPayloadBytesPerFrame))
            {
                packets.Add(chunk);
            }
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
            foreach (var chunk in SplitManifestIntoChunks(manifest, totalDataSymbols, maxPayloadBytesPerFrame))
            {
                packets.Add(chunk);
            }
        }

        return packets;
    }

    /// <summary>
    /// Serialises the manifest and splits it into manifest frames whose payload fits one frame's
    /// payload capacity. With no capacity limit (or a fitting body) this yields exactly one
    /// byte-identical manifest frame, preserving the pre-chunking wire format.
    /// </summary>
    private static IEnumerable<byte[]> SplitManifestIntoChunks(StreamManifest manifest, int totalDataSymbols, int maxPayloadBytesPerFrame)
    {
        var manifestPayload = StreamManifestCodec.Serialize(manifest);
        if (maxPayloadBytesPerFrame <= 0 || manifestPayload.Length <= maxPayloadBytesPerFrame)
        {
            yield return FramePacketCodec.CreateManifestFramePacket(totalDataSymbols, 0, 1, manifestPayload);
            yield break;
        }

        int chunkCount = (manifestPayload.Length + maxPayloadBytesPerFrame - 1) / maxPayloadBytesPerFrame;
        for (int i = 0; i < chunkCount; i++)
        {
            int chunkStart = i * maxPayloadBytesPerFrame;
            int chunkLength = Math.Min(maxPayloadBytesPerFrame, manifestPayload.Length - chunkStart);
            yield return FramePacketCodec.CreateManifestFramePacket(
                totalDataSymbols,
                i,
                chunkCount,
                manifestPayload.AsSpan(chunkStart, chunkLength));
        }
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
        var manifestChunks = new Dictionary<bool, Dictionary<int, byte[]>>();
        bool seenSymbolFrame = false;
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
                    GroupCount = groupCount,
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
                    GroupCount = groupCount,
                    RedundancyLevel = 1,
                    Hash = SHA256.HashData(payloadBytes)
                };
            }
            else
            {
                if (frameType != FramePacket.FrameTypeManifest)
                {
                    continue; // unknown frame type: not a symbol, not a manifest
                }

                // Manifest frames are not durability symbols. A manifest body larger than one
                // frame's payload capacity (e.g. phase4's 88-byte frames) is split into chunks;
                // chunk index/count ride in the otherwise-unused frameIndex/groupCount header
                // fields. The start and end copies must be reassembled separately so a
                // conflicting end copy is still detected by the reconciliation below instead of
                // being deduplicated away (CR-20260912-05 stage 3 + phase4 chunking fix).
                // Packets arrive in stream order: manifest frames seen before any data/parity
                // frame belong to the start copy, everything after to the end copy.
                var copyKey = !seenSymbolFrame;
                if (!manifestChunks.TryGetValue(copyKey, out var chunkList))
                {
                    chunkList = new Dictionary<int, byte[]>();
                    manifestChunks[copyKey] = chunkList;
                }

                chunkList.TryAdd(frameIndex, payloadBytes);
                continue;
            }

            seenSymbolFrame = true;

            var key = (symbol.GroupId, symbol.SymbolId, symbol.IsParity);
            if (!bestSymbolsByKey.TryGetValue(key, out var existing) || symbol.GetQualityScore() > existing.GetQualityScore())
            {
                bestSymbolsByKey[key] = symbol;
            }
        }

        foreach (var chunkList in manifestChunks.Values)
        {
            // Reassemble every copy: a conflicting copy must reach the reconciliation below so
            // the decode fails loudly instead of silently picking the start copy.
            TryReassembleManifestChunks(chunkList, manifests);
        }

        if (!ReconcileManifests(manifests, out manifest))
        {
            return false;
        }

        var codec = new DurabilityMatrixCodec(_options);
        return codec.TryDecode(bestSymbolsByKey.Values, expectedLength, out payload, out recoveredBytes);
    }

    /// <summary>
    /// Hole-tolerant frame-packet decode (CR-20260913-02): like <see cref="TryDecodeFramePackets"/>
    /// but a wholly-missing parity group becomes a zero-filled hole instead of failing the decode.
    /// Manifest reconciliation is unchanged (conflicting manifests still fail). Callers must treat
    /// a non-empty <paramref name="missingGroupIds"/> as failed integrity.
    /// </summary>
    public bool TryDecodeFramePacketsWithHoles(IEnumerable<byte[]> packets, int expectedLength, out byte[] payload, out int recoveredBytes, out StreamManifest? manifest, out IReadOnlyList<int> missingGroupIds)
    {
        payload = Array.Empty<byte>();
        recoveredBytes = 0;
        manifest = null;
        missingGroupIds = Array.Empty<int>();

        var bestSymbolsByKey = CollectSymbols(packets, out var manifests);
        if (!ReconcileManifests(manifests, out manifest))
        {
            return false;
        }

        var codec = new DurabilityMatrixCodec(_options);

        // A recovered manifest is authoritative for the schedule length (CR-20260913-02):
        // sizing holes from the sum of recovered per-frame lengths would truncate the output
        // at the hole boundary instead of spanning the missing groups.
        int scheduleLength = manifest is not null ? (int)Math.Min(int.MaxValue, manifest.TotalPayloadBytes) : expectedLength;
        return codec.TryDecodeWithHoles(bestSymbolsByKey.Values, scheduleLength, out payload, out recoveredBytes, out missingGroupIds);
    }

    /// <summary>
    /// Shared packet-walk for the strict and hole-tolerant decodes: converts frame packets into
    /// the best symbol per (group, symbol, parity) key and collects manifest chunks.
    /// </summary>
    private Dictionary<(int GroupId, int SymbolId, bool IsParity), DurabilitySymbol> CollectSymbols(IEnumerable<byte[]> packets, out List<StreamManifest> manifests)
    {
        var bestSymbolsByKey = new Dictionary<(int GroupId, int SymbolId, bool IsParity), DurabilitySymbol>();
        manifests = new List<StreamManifest>();
        var manifestChunks = new Dictionary<bool, Dictionary<int, byte[]>>();
        bool seenSymbolFrame = false;
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
                    GroupCount = groupCount,
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
                    GroupCount = groupCount,
                    RedundancyLevel = 1,
                    Hash = SHA256.HashData(payloadBytes)
                };
            }
            else
            {
                if (frameType != FramePacket.FrameTypeManifest)
                {
                    continue; // unknown frame type: not a symbol, not a manifest
                }

                // Manifest frames are not durability symbols. A manifest body larger than one
                // frame's payload capacity (e.g. phase4's 88-byte frames) is split into chunks;
                // chunk index/count ride in the otherwise-unused frameIndex/groupCount header
                // fields. The start and end copies must be reassembled separately so a
                // conflicting end copy is still detected by the reconciliation below instead of
                // being deduplicated away (CR-20260912-05 stage 3 + phase4 chunking fix).
                // Packets arrive in stream order: manifest frames seen before any data/parity
                // frame belong to the start copy, everything after to the end copy.
                var copyKey = !seenSymbolFrame;
                if (!manifestChunks.TryGetValue(copyKey, out var chunkList))
                {
                    chunkList = new Dictionary<int, byte[]>();
                    manifestChunks[copyKey] = chunkList;
                }

                chunkList.TryAdd(frameIndex, payloadBytes);
                continue;
            }

            seenSymbolFrame = true;

            var key = (symbol.GroupId, symbol.SymbolId, symbol.IsParity);
            if (!bestSymbolsByKey.TryGetValue(key, out var existing) || symbol.GetQualityScore() > existing.GetQualityScore())
            {
                bestSymbolsByKey[key] = symbol;
            }
        }

        foreach (var chunkList in manifestChunks.Values)
        {
            // Reassemble every copy: a conflicting copy must reach the reconciliation below so
            // the decode fails loudly instead of silently picking the start copy.
            TryReassembleManifestChunks(chunkList, manifests);
        }

        return bestSymbolsByKey;
    }

    /// <summary>
    /// Reassembles one copy's manifest chunk frames: concatenates chunks in contiguous index
    /// order starting at 0 and deserializes the body. Returns true when a valid manifest was
    /// recovered; a gap (both copies corrupt for the same chunk) simply yields false and the
    /// decode proceeds manifest-less under the frame-only policy.
    /// </summary>
    private static bool TryReassembleManifestChunks(Dictionary<int, byte[]> chunks, List<StreamManifest> manifests)
    {
        if (chunks.Count == 0 || !chunks.TryGetValue(0, out _))
        {
            return false;
        }

        var body = new byte[chunks.Values.Sum(c => c.Length)];
        int bodyOffset = 0;
        for (int i = 0; chunks.TryGetValue(i, out var chunk); i++)
        {
            Buffer.BlockCopy(chunk, 0, body, bodyOffset, chunk.Length);
            bodyOffset += chunk.Length;
        }

        if (bodyOffset != body.Length || bodyOffset == 0)
        {
            return false; // index gap: incomplete sequence
        }

        if (StreamManifestCodec.TryDeserialize(body, out var candidate) && candidate is not null)
        {
            manifests.Add(candidate);
            return true;
        }

        return false;
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
