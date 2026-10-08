using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace YTAHD.Core.Core;

/// <summary>
/// Streaming, memory-bounded durability decode (RM-10, CR-20261007-04; design in
/// F-20260914-02). Consumes frame packets in stream order and flushes every finalized
/// payload byte to the sink as soon as its position is final, so peak memory is
/// O(one parity group), not O(payload).
/// </summary>
/// <remarks>
/// <para>
/// A group becomes <em>final</em> when every one of its data symbols has arrived (then its
/// bytes are flushed in symbol order), or when its parity symbol has arrived and exactly one
/// data symbol is missing (XOR parity repairs it, then the group flushes). A group with more
/// than one missing symbol after its parity arrived is beyond the XOR budget — reported via
/// <see cref="MissingGroups"/> and its zero-filled hole is written immediately (the parity
/// symbol is the group's last packet in stream order, so the verdict is final the moment the
/// hole is detected), keeping the sink in payload order.
/// </para>
/// <para>
/// Hole semantics deliberately match the retroactive hole-tolerant decode (CR-20260913-02):
/// the output spans the manifest schedule and the loss map is reported. Memory stays bounded
/// by the group size, not the payload size. Requires in-order group arrival (true for all
/// current encoders); a group finalizing out of position fails loudly. Conflicting manifest
/// copies fail loudly, exactly like the batch decode.
/// </para>
/// </remarks>
internal sealed class StreamingDurabilityDecoder
{
    private readonly DurabilityMatrixOptions _options;
    private readonly Action<byte[], int, int> _writeToSink;
    private readonly IncrementalHash _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    private readonly Dictionary<int, GroupBuffer> _groups = new();
    private readonly HashSet<int> _resolvedGroupIds = new();
    private readonly Dictionary<bool, Dictionary<int, byte[]>> _manifestChunks = new();
    private bool _seenSymbolFrame;
    private bool _manifestConflict;
    private long _flushedBytes;
    private bool _sealed;

    public StreamingDurabilityDecoder(DurabilityMatrixOptions options, Action<byte[], int, int> writeToSink)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _writeToSink = writeToSink ?? throw new ArgumentNullException(nameof(writeToSink));

        if (_options.SymbolSize <= 0) throw new ArgumentOutOfRangeException(nameof(options), "SymbolSize must be greater than zero.");
        if (_options.GroupSize <= 0) throw new ArgumentOutOfRangeException(nameof(options), "GroupSize must be greater than zero.");
    }

    /// <summary>Bytes flushed to the sink so far.</summary>
    public long FlushedBytes => _flushedBytes;

    /// <summary>The reconciled manifest, when at least one intact copy has arrived.</summary>
    public StreamManifest? Manifest { get; private set; }

    /// <summary>Groups that could not be reconstructed (beyond the XOR budget or wholly absent).</summary>
    public List<int> MissingGroups { get; } = new();

    /// <summary>The streaming SHA-256 over every flushed byte.</summary>
    public byte[] ComputeFlushedHash() => _sha256.GetHashAndReset();

    /// <summary>Feed one strictly-valid frame packet in stream order.</summary>
    public void Add(ReadOnlySpan<byte> packet)
    {
        if (_sealed) throw new InvalidOperationException("The streaming decode is already finalized.");

        if (!FramePacketCodec.TryDecode(packet, out var frameType, out var frameIndex, out _, out var groupStart, out var groupCount, out var payloadLength, out var payload))
        {
            return; // strict decode: tolerance is upstream's job (same as the batch path)
        }

        if (frameType == FramePacket.FrameTypeManifest)
        {
            // Same copy-key discipline as the batch decode: packets before the first
            // data/parity frame belong to the start copy, everything after to the end copy.
            var copyKey = !_seenSymbolFrame;
            if (!_manifestChunks.TryGetValue(copyKey, out var chunkList))
            {
                chunkList = new Dictionary<int, byte[]>();
                _manifestChunks[copyKey] = chunkList;
            }

            chunkList.TryAdd(frameIndex, payload.ToArray());
            TryReconcileManifests();
            return;
        }

        if (frameType != FramePacket.FrameTypeData && frameType != FramePacket.FrameTypeParity)
        {
            return;
        }

        _seenSymbolFrame = true;
        var groupId = groupStart / Math.Max(1, _options.GroupSize);

        // Physical repeats: the encoder writes every logical packet several times. Once a group
        // has finalized (flushed or holed), later copies of its packets must be ignored —
        // re-buffering them would re-create the group and mis-read the duplicates as losses.
        if (_resolvedGroupIds.Contains(groupId))
        {
            return;
        }

        var group = GetGroup(groupId);
        group.GroupCount = Math.Max(group.GroupCount, groupCount);

        if (frameType == FramePacket.FrameTypeParity)
        {
            group.AddParity(payload);
        }
        else
        {
            var symbolId = Math.Max(0, frameIndex - groupStart);
            group.AddData(symbolId, payload, payloadLength);
        }

        TryFlushGroup(groupId);
    }

    /// <summary>
    /// Finalize the decode: flush any tail groups whose schedule length is now known,
    /// zero-fill unrecoverable groups from the manifest schedule, and return the integrity
    /// verdict against the streaming hash. Returns the total bytes written to the sink.
    /// </summary>
    public long Finalize(StreamManifest? authoritativeManifest, out IntegrityStatus integrityStatus, out IReadOnlyList<int> missingGroups)
    {
        if (_sealed) throw new InvalidOperationException("The streaming decode is already finalized.");
        _sealed = true;

        var manifest = authoritativeManifest ?? Manifest;

        if (manifest is not null && _flushedBytes < manifest.TotalPayloadBytes)
        {
            // Wholly-absent tail groups never received a parity symbol, so TryFlushGroup never
            // ran for them (and MarkHole only covers groups that reached it with parity).
            // Materialize their zero-filled holes here, in schedule order, from the manifest.
            long groupBytes = (long)_options.GroupSize * _options.SymbolSize;
            int firstMissing = (int)(_flushedBytes / groupBytes);
            int lastGroup = (int)((manifest.TotalPayloadBytes - 1) / groupBytes);
            for (int groupId = firstMissing; groupId <= lastGroup; groupId++)
            {
                var groupStartByte = (long)groupId * groupBytes;
                var holeBytes = Math.Min(manifest.TotalPayloadBytes, groupStartByte + groupBytes)
                                - Math.Max(groupStartByte, _flushedBytes);
                if (holeBytes <= 0)
                {
                    continue;
                }

                MissingGroups.Add(groupId);
                WriteHole((int)Math.Min(int.MaxValue, holeBytes));
            }
        }

        var actualHash = ComputeFlushedHash();
        if (manifest is null)
        {
            // Legacy durability stream without any intact manifest: per-frame hashes were
            // enforced, but whole-payload integrity cannot be proven (same verdict as batch).
            integrityStatus = IntegrityStatus.FrameOnly;
        }
        else if (manifest.TotalPayloadBytes != _flushedBytes
                 || !actualHash.AsSpan().SequenceEqual(manifest.PayloadSha256))
        {
            integrityStatus = IntegrityStatus.Failed;
        }
        else
        {
            integrityStatus = IntegrityStatus.Passed;
        }

        missingGroups = MissingGroups;
        return _flushedBytes;
    }

    private GroupBuffer GetGroup(int groupId)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            group = new GroupBuffer(groupId, _options);
            _groups[groupId] = group;
        }

        return group;
    }

    private void TryFlushGroup(int groupId)
    {
        var group = _groups[groupId];
        if (group.State == GroupState.Flushed)
        {
            return;
        }

        if (group.IsComplete)
        {
            FlushGroup(group);
            return;
        }

        if (group.HasParity && group.MissingCount == 1)
        {
            group.RecoverMissingViaParity();
            FlushGroup(group);
            return;
        }

        if (group.HasParity && group.MissingCount > _options.ParitySymbolsPerGroup)
        {
            // Beyond the XOR budget: a documented hole, not a silent failure. The parity symbol
            // is the group's last packet in stream order, so no further data for this group can
            // arrive (only duplicate copies, ignored above) — the verdict is final NOW.
            // Materialize the hole immediately so the sink stays in payload order.
            MarkHole(groupId);
            return;
        }

        // Still waiting on symbols: keep the (bounded) group buffer.
    }

    private void FlushGroup(GroupBuffer group)
    {
        foreach (var (data, realLength) in group.SymbolsInOrder())
        {
            // The wire packet carries the full symbol buffer; the header's declared length is
            // the real payload slice (partial final group). Flush exactly that slice.
            WriteToSink(data.AsSpan(0, Math.Min(realLength, data.Length)).ToArray());
        }

        group.MarkFlushed();
        _resolvedGroupIds.Add(group.GroupId);
        _groups.Remove(group.GroupId);
    }

    /// <summary>
    /// Records a group as unrecoverable and writes its zero-filled hole to the sink immediately,
    /// keeping the sink in payload order. Requires in-order group arrival (all current encoders):
    /// every previous group must have flushed before this hole's position is written.
    /// </summary>
    private void MarkHole(int groupId)
    {
        var groupStartByte = (long)groupId * _options.GroupSize * _options.SymbolSize;
        if (_flushedBytes != groupStartByte)
        {
            throw new InvalidDataException($"Group {groupId} finalized out of order at byte {_flushedBytes} (expected {groupStartByte}) — streaming decode requires in-order group arrival.");
        }

        long holeBytes;
        if (Manifest is not null)
        {
            var groupEndByte = Math.Min(Manifest.TotalPayloadBytes, groupStartByte + (long)_options.GroupSize * _options.SymbolSize);
            holeBytes = Math.Max(0, groupEndByte - groupStartByte);
        }
        else
        {
            holeBytes = (long)_options.GroupSize * _options.SymbolSize; // no manifest: full schedule size
        }

        MissingGroups.Add(groupId);
        _resolvedGroupIds.Add(groupId);
        _groups.Remove(groupId);

        if (holeBytes > 0)
        {
            WriteHole((int)Math.Min(int.MaxValue, holeBytes));
        }
    }


    private void WriteToSink(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        _writeToSink(bytes, 0, bytes.Length);
        _sha256.AppendData(bytes, 0, bytes.Length);
        _flushedBytes += bytes.Length;
    }

    private void WriteHole(int bytes)
    {
        var hole = new byte[bytes];
        _writeToSink(hole, 0, hole.Length);
        _sha256.AppendData(hole, 0, hole.Length);
        _flushedBytes += hole.Length;
    }

    private void TryReconcileManifests()
    {
        foreach (var chunkList in _manifestChunks.Values)
        {
            if (chunkList.Count == 0 || !chunkList.TryGetValue(0, out _))
            {
                continue;
            }

            var body = new byte[chunkList.Values.Sum(c => c.Length)];
            int bodyOffset = 0;
            for (int i = 0; chunkList.TryGetValue(i, out var chunk); i++)
            {
                Buffer.BlockCopy(chunk, 0, body, bodyOffset, chunk.Length);
                bodyOffset += chunk.Length;
            }

            if (bodyOffset != body.Length || bodyOffset == 0)
            {
                continue; // index gap: incomplete sequence, wait for more chunks
            }

            if (StreamManifestCodec.TryDeserialize(body, out var candidate) && candidate is not null)
            {
                if (Manifest is null)
                {
                    Manifest = candidate;
                }
                else if (!ManifestsEqual(Manifest, candidate))
                {
                    _manifestConflict = true;
                }
            }
        }

        if (_manifestConflict)
        {
            throw new InvalidDataException("Conflicting stream manifests recovered — the stream is inconsistent.");
        }
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

    private enum GroupState
    {
        Collecting,
        Flushed
    }

    /// <summary>Bounded per-group buffer: at most GroupSize data symbols plus the parity symbol.</summary>
    private sealed class GroupBuffer
    {
        private readonly DurabilityMatrixOptions _options;
        private readonly SortedDictionary<int, (byte[] Data, int RealLength)> _data = new();
        private byte[]? _parity;

        public GroupBuffer(int groupId, DurabilityMatrixOptions options)
        {
            GroupId = groupId;
            _options = options;
        }

        public int GroupId { get; }
        public int GroupCount { get; set; }
        public bool HasParity => _parity is not null;
        public int DataCount => _data.Count;
        public GroupState State { get; private set; }

        /// <summary>True when every declared group member has arrived (partial final group aware).</summary>
        public bool IsComplete => GroupCount > 0 && DataCount >= GroupCount;

        public int MissingCount => Math.Max(0, GroupCount - DataCount);

        public void AddData(int symbolId, byte[] payload, int realLength) => _data[symbolId] = (payload, realLength);

        public void AddParity(byte[] payload) => _parity = payload;

        public void RecoverMissingViaParity()
        {
            if (_parity is null || MissingCount != 1)
            {
                throw new InvalidOperationException("Parity recovery requires exactly one missing symbol and a parity symbol.");
            }

            var missingId = Enumerable.Range(0, GroupCount).First(id => !_data.ContainsKey(id));
            var recovered = new byte[_options.SymbolSize];
            Buffer.BlockCopy(_parity, 0, recovered, 0, Math.Min(_parity.Length, recovered.Length));

            foreach (var symbol in _data.Values)
            {
                for (var i = 0; i < recovered.Length; i++)
                {
                    recovered[i] ^= symbol.Data[i];
                }
            }

            // A recovered symbol's real length is only known when it is the final symbol of the
            // payload (the manifest trims the tail); interior symbols are always full-size.
            _data[missingId] = (recovered, _options.SymbolSize);
        }

        public IEnumerable<(byte[] Data, int RealLength)> SymbolsInOrder() => _data.Values;

        public void MarkFlushed() => State = GroupState.Flushed;
    }
}
