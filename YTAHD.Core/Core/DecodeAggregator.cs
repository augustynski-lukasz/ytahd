using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using YTAHD.Core.Modulation;

namespace YTAHD.Core.Core
{
    /// <summary>
    /// Shared aggregation semantics for the serial and parallel decode paths
    /// (CR-20260913-03). Owns the duplicate-run tracker, the decoded-frame accumulator,
    /// the durability packet list, and the decode metrics; both orchestrator paths feed it
    /// per-frame results and read the final output and metrics from it.
    /// </summary>
    /// <remarks>
    /// The aggregator is single-threaded by contract: the parallel orchestrator serializes
    /// results into sequence order before feeding them, so no internal synchronization is
    /// needed. The <c>stopRequested</c> latch lives here because early-stop is an aggregation
    /// decision (the contiguous prefix of the accumulator covers the expected output), not an
    /// orchestration one.
    /// </remarks>
    internal sealed class DecodeAggregator
    {
        private readonly bool _useDurabilityMatrix;
        private readonly DurabilityMatrixOptions _durabilityMatrixOptions;
        private readonly int _width;
        private readonly int _height;
        private readonly int _macroblockSize;
        private readonly int _rowBytes;
        private readonly int _frameBytes;
        private readonly int _bitsPerFrame;
        private readonly int _payloadBytesPerFrame;
        private readonly int _expectedOutputBytes;
        private readonly int? _audioDatagramCount;
        private readonly IModulator _modulator;

        private readonly DecodedFrameAccumulator _accumulator = new();
        private readonly DuplicateFrameRunTracker _duplicateTracker = new();
        private readonly List<byte[]> _packets = new();
        private readonly DecodeMetrics _metrics = new();
        private const int RepeatedFrameCount = 3;

        private bool _stopRequested;

        public DecodeAggregator(
            bool useDurabilityMatrix,
            DurabilityMatrixOptions durabilityMatrixOptions,
            int width,
            int height,
            int macroblockSize,
            int rowBytes,
            int frameBytes,
            int bitsPerFrame,
            int payloadBytesPerFrame,
            int expectedOutputBytes,
            int? audioDatagramCount,
            IModulator modulator)
        {
            _useDurabilityMatrix = useDurabilityMatrix;
            _durabilityMatrixOptions = durabilityMatrixOptions ?? new DurabilityMatrixOptions();
            _width = width;
            _height = height;
            _macroblockSize = macroblockSize;
            _rowBytes = rowBytes;
            _frameBytes = frameBytes;
            _bitsPerFrame = bitsPerFrame;
            _payloadBytesPerFrame = payloadBytesPerFrame;
            _expectedOutputBytes = expectedOutputBytes;
            _audioDatagramCount = audioDatagramCount;
            _modulator = modulator;
        }

        public DecodeMetrics Metrics => _metrics;

        /// <summary>True once the legacy path's contiguous prefix covers the expected output.</summary>
        public bool StopRequested => _stopRequested;

        /// <summary>
        /// Feed one decoded frame result in stream order. The frame buffer may be reused by the
        /// caller afterwards; the aggregator clones anything it retains.
        /// </summary>
        public void Aggregate(DecodedFrameResult result)
        {
            _metrics.TotalFramesSeen++;
            if (result.Canonical)
            {
                _metrics.CanonicalFrameCount++;
                FlushPendingRun();
            }
            else if (!result.PacketIsValid)
            {
                _metrics.InvalidPacketCount++;
            }
            else if (_useDurabilityMatrix)
            {
                _packets.Add(result.Packet!);
            }
            else
            {
                var completed = _duplicateTracker.Update(result.Frame, result.Signature!, result.Quality);
                if (completed is not null)
                {
                    _metrics.DuplicateRunCount++;
                    _metrics.StrongestDuplicateQuality = Math.Max(_metrics.StrongestDuplicateQuality, completed.BestQuality);
                    _duplicateTracker.Flush(completed, RepeatedFrameCount, DecodePayloadFrame);
                }
            }

            _stopRequested |= !_useDurabilityMatrix && DecodeRecoveryPolicy.ShouldStopDecoding(_accumulator, _expectedOutputBytes);
        }

        /// <summary>
        /// Finalize a durability-matrix decode: walk the recovered data packets, reconstruct the
        /// payload hole-tolerantly (CR-20260913-02), verify against the manifest, and populate
        /// the metrics. Returns the payload bytes.
        /// </summary>
        /// <remarks>
        /// Integrity semantics (unified per CR-20260913-03): a <see cref="IntegrityStatus.Failed"/>
        /// verification still returns the reconstructed payload — including documented zero-filled
        /// holes with their loss map — so callers can inspect what was recovered. The manifest is
        /// authoritative; consumers must treat <c>Failed</c> as "output is not trustworthy" and
        /// surface it loudly (the CLI reports integrity and exit status from the metrics).
        /// </remarks>
        public byte[] FinalizeDurability()
        {
            var durabilityCodec = new DurabilityTransportCodec(_durabilityMatrixOptions);
            var uniqueDataLengths = new Dictionary<(int GroupId, int SymbolId), int>();
            foreach (var packet in _packets)
            {
                if (!FramePacketCodec.TryDecodeWithTolerance(packet, out var frameType, out var frameIndex, out _, out var groupStart, out _, out var payloadLength, out _) || frameType != FramePacket.FrameTypeData)
                {
                    continue;
                }

                var groupId = groupStart / Math.Max(1, _durabilityMatrixOptions.GroupSize);
                var symbolId = Math.Max(0, frameIndex - groupStart);
                uniqueDataLengths.TryAdd((groupId, symbolId), payloadLength);
            }

            int recoveredLength = uniqueDataLengths.Values.Sum();
            if (recoveredLength <= 0)
            {
                throw new InvalidDataException("Decoded durability payload is incomplete. No valid frame packets were recovered.");
            }

            // Hole-tolerant reconstruction (CR-20260913-02): a wholly-missing parity group
            // becomes a zero-filled hole plus a loss-map entry instead of aborting the
            // decode. Integrity verification below still fails loudly for any hole.
            if (!durabilityCodec.TryDecodeFramePacketsWithHoles(_packets, recoveredLength, out var payload, out var decodedBytes, out var manifest, out var missingGroupIds))
            {
                throw new InvalidDataException("Durability matrix could not reconstruct the payload from the recovered packets.");
            }

            _metrics.Manifest = manifest;
            _metrics.MissingDatagramIds = missingGroupIds;
            _metrics.IntegrityStatus = VerifyAgainstManifest(payload, manifest, _metrics);

            _metrics.TotalDecodedPayloadBytes = decodedBytes;
            _metrics.TotalFramesDecoded = _packets.Count;
            _metrics.TotalFramesSeen = _packets.Count;
            _metrics.RecoveredGroupCount = _packets.Count;
            _metrics.RecoveredDataFrameCount = _packets.Count;
            _metrics.AudioDatagramCount = _audioDatagramCount;

            return payload;
        }

        /// <summary>
        /// Finalize a legacy (non-durability) decode: flush the trailing duplicate run, recover
        /// single-frame losses via parity, and assemble the output. Returns the payload bytes.
        /// </summary>
        public byte[] FinalizeLegacy()
        {
            _duplicateTracker.FlushCurrentRun(RepeatedFrameCount, DecodePayloadFrame);

            if (_accumulator.TotalDataFrames < 0 || _accumulator.OrderedPayload.Count == 0)
            {
                throw new InvalidDataException("Decoded payload is incomplete. No valid frames were decoded.");
            }

            int resolvedExpectedBytes = DecodeRecoveryPolicy.ResolveExpectedOutputBytes(_accumulator, _expectedOutputBytes);
            _accumulator.RecoverMissingPayloadFrames(_accumulator.TotalDataFrames, _payloadBytesPerFrame, resolvedExpectedBytes);
            _metrics.RecoveredGroupCount = _accumulator.RecoveredGroupCount;
            _metrics.StrongestDuplicateQuality = Math.Max(_metrics.StrongestDuplicateQuality, _duplicateTracker.BestQuality);
            _metrics.TotalDecodedPayloadBytes = resolvedExpectedBytes;
            _metrics.TotalFramesDecoded = _accumulator.TotalDataFrames;
            _metrics.RecoveredDataFrameCount = _accumulator.OrderedPayload.Count;
            _metrics.RecoveredParityFrameCount = _accumulator.ParityPayloadByGroup.Count;
            _metrics.AudioDatagramCount = _audioDatagramCount;

            return _accumulator.AssembleOutput(resolvedExpectedBytes);
        }

        /// <summary>Record the timing metrics captured outside the aggregator and publish them.</summary>
        public void Complete(double totalElapsedMilliseconds, double frameReadMilliseconds, double packetDecodeMilliseconds, double aggregationMilliseconds)
        {
            _metrics.TotalElapsedMilliseconds = totalElapsedMilliseconds;
            _metrics.FrameReadMilliseconds = frameReadMilliseconds;
            _metrics.PacketDecodeMilliseconds = packetDecodeMilliseconds;
            _metrics.AggregationMilliseconds = aggregationMilliseconds;
        }

        private bool DecodePayloadFrame(byte[] frame)
        {
            return _accumulator.TryAddDecodedFrame(frame, _width, _height, _macroblockSize, _rowBytes, _frameBytes, _payloadBytesPerFrame, _bitsPerFrame, _modulator);
        }

        private void FlushPendingRun()
        {
            if (!_duplicateTracker.HasCurrentRun)
            {
                return;
            }

            _metrics.DuplicateRunCount++;
            _metrics.StrongestDuplicateQuality = Math.Max(_metrics.StrongestDuplicateQuality, _duplicateTracker.BestQuality);
            _duplicateTracker.FlushCurrentRun(RepeatedFrameCount, DecodePayloadFrame);
        }

        /// <summary>
        /// Whole-payload verification against a recovered stream manifest (CR-20260912-05 stage 3):
        /// the manifest's declared length and SHA-256 are authoritative. Returns the integrity
        /// status to report; <see cref="IntegrityStatus.FrameOnly"/> when no manifest is present
        /// (legacy durability stream), <see cref="IntegrityStatus.Failed"/> on a length or hash
        /// mismatch, <see cref="IntegrityStatus.Passed"/> when both agree.
        /// </summary>
        private static IntegrityStatus VerifyAgainstManifest(byte[] payload, StreamManifest? manifest, DecodeMetrics metrics)
        {
            metrics.Manifest = manifest;
            if (manifest is null)
            {
                // Legacy durability stream without a manifest: per-frame hashes were enforced,
                // but whole-payload integrity cannot be proven.
                return IntegrityStatus.FrameOnly;
            }

            if (manifest.TotalPayloadBytes != payload.LongLength)
            {
                return IntegrityStatus.Failed;
            }

            var actualHash = SHA256.HashData(payload);
            return actualHash.AsSpan().SequenceEqual(manifest.PayloadSha256)
                ? IntegrityStatus.Passed
                : IntegrityStatus.Failed;
        }
    }

    /// <summary>
    /// Per-frame decode result handed to <see cref="DecodeAggregator.Aggregate"/>. Mirrors the
    /// parallel path's internal <c>DecodedFrame</c> record so both paths feed identical shapes.
    /// </summary>
    internal readonly record struct DecodedFrameResult(
        byte[] Frame,
        bool Canonical,
        bool PacketIsValid,
        byte[]? Packet,
        byte[]? Signature,
        int Quality);
}
