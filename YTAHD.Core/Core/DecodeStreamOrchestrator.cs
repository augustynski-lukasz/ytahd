using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using YTAHD.Core.Application;
using YTAHD.Core.Infrastructure;
using YTAHD.Core.Modulation;

namespace YTAHD.Core.Core
{
    public sealed class DecodeStreamOrchestrator
    {
        private readonly IModulator _modulator;
        private readonly int _width;
        private readonly int _height;
        private readonly int _macroblockSize;
        private readonly bool _useDurabilityMatrix;
        private readonly DurabilityMatrixOptions? _durabilityMatrixOptions;
        /// <summary>Resolved worker count from <see cref="ParallelismPolicy"/>; <c>1</c> selects the serial path.</summary>
        private readonly int _maxDegreeOfParallelism;

        public DecodeStreamOrchestrator(int width, int height, int macroblockSize)
            : this(new BinaryGridModulator(macroblockSize, macroblockSize), width, height, macroblockSize, false, null)
        {
        }

        public DecodeStreamOrchestrator(IModulator modulator, int width, int height, int macroblockSize)
            : this(modulator, width, height, macroblockSize, false, null)
        {
        }

        public DecodeStreamOrchestrator(IModulator modulator, int width, int height, int macroblockSize, bool useDurabilityMatrix, DurabilityMatrixOptions? durabilityMatrixOptions, int maxDegreeOfParallelism = 0)
        {
            _modulator = modulator ?? throw new ArgumentNullException(nameof(modulator));
            _width = width;
            _height = height;
            _macroblockSize = macroblockSize;
            _useDurabilityMatrix = useDurabilityMatrix;
            _durabilityMatrixOptions = durabilityMatrixOptions ?? new DurabilityMatrixOptions();
            _maxDegreeOfParallelism = ParallelismPolicy.Resolve(maxDegreeOfParallelism);

            // Split the resolved CPU budget with the modulator's inner loop instead of granting it
            // to both layers; decoders created from this modulator inherit the value.
            if (_modulator is IParallelismConfigurable configurable)
            {
                configurable.InnerDegreeOfParallelism = ParallelismPolicy.ResolveInnerDegree(maxDegreeOfParallelism, _maxDegreeOfParallelism);
            }
        }

        public DecodeMetrics LastDecodeMetrics { get; private set; } = new();

        public async Task<byte[]> ProcessAsync(Stream rgbStream, int expectedOutputBytes, int? audioDatagramCount = null, int totalVideoFrames = 0, IProgress<DecodeProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var totalStopwatch = Stopwatch.StartNew();
            double frameReadMilliseconds = 0d;
            double packetDecodeMilliseconds = 0d;
            double aggregationMilliseconds = 0d;

            if (rgbStream == null) throw new ArgumentNullException(nameof(rgbStream));
            if (!rgbStream.CanRead) throw new ArgumentException("Stream is not readable", nameof(rgbStream));

            DebugTrace.Log("DecodeStreamOrchestrator", $"ProcessAsync start expectedOutputBytes={expectedOutputBytes} useDurability={_useDurabilityMatrix} modulator={_modulator.GetType().Name}");

            var geometry = new ModulatorGeometry(_width, _height, _macroblockSize, FramePacket.HeaderBytes, BitsPerFrame: 0);
            int borderWidth = _modulator.GetBorderWidth(geometry);
            geometry = geometry with { BorderWidth = borderWidth };
            int payloadBytesPerFrame = _modulator.GetPayloadBytesPerFrame(geometry);
            if (payloadBytesPerFrame <= 0)
                throw new InvalidOperationException("Frame capacity too small for metadata header and payload.");

            int rowBytes = _width * 3;
            int frameBytes = rowBytes * _height;
            int blocksX = _width / _macroblockSize;
            int blocksY = _height / _macroblockSize;
            int bitsPerFrame = blocksX * blocksY;
            var packetGeometry = geometry with { BitsPerFrame = bitsPerFrame };
            int packetByteLength = _modulator.GetPacketBufferLength(packetGeometry, payloadBytesPerFrame);

            if (_maxDegreeOfParallelism > 1)
            {
                return await ProcessParallelAsync(rgbStream, expectedOutputBytes, audioDatagramCount, totalVideoFrames, progress, borderWidth, payloadBytesPerFrame, rowBytes, frameBytes, bitsPerFrame, packetByteLength, totalStopwatch, cancellationToken);
            }

            if (_useDurabilityMatrix)
            {
                var packets = new List<byte[]>();
                byte[] frameBuf = new byte[frameBytes];
                while (true)
                {
                    var readStopwatch = Stopwatch.StartNew();
                    int read = 0;
                    while (read < frameBytes)
                    {
                        int r = await rgbStream.ReadAsync(frameBuf, read, frameBytes - read, cancellationToken);
                        if (r == 0) break;
                        read += r;
                    }
                    readStopwatch.Stop();
                    frameReadMilliseconds += readStopwatch.Elapsed.TotalMilliseconds;

                    if (read < frameBytes) break;
                    progress?.Report(new DecodeProgress(packets.Count + 1, totalVideoFrames));

                    var decodeStopwatch = Stopwatch.StartNew();
                    if (DecoderEngine.TryReadDecodedPacket(frameBuf, _width, _height, _macroblockSize, rowBytes, frameBytes, bitsPerFrame, _modulator, out var packet))
                    {
                        packets.Add(packet);
                    }
                    decodeStopwatch.Stop();
                    packetDecodeMilliseconds += decodeStopwatch.Elapsed.TotalMilliseconds;
                }

                var aggregationStopwatch = Stopwatch.StartNew();
                var durabilityCodec = new DurabilityTransportCodec(_durabilityMatrixOptions ?? new DurabilityMatrixOptions());
                var uniqueDataLengths = new Dictionary<(int GroupId, int SymbolId), int>();
                foreach (var packet in packets)
                {
                    if (!FramePacketCodec.TryDecodeWithTolerance(packet, out var frameType, out var frameIndex, out _, out var groupStart, out _, out var payloadLength, out _))
                    {
                        continue;
                    }

                    if (frameType != FramePacket.FrameTypeData)
                    {
                        continue;
                    }

                    var groupId = groupStart / Math.Max(1, _durabilityMatrixOptions?.GroupSize ?? 1);
                    var symbolId = Math.Max(0, frameIndex - groupStart);
                    var key = (groupId, symbolId);
                    if (!uniqueDataLengths.ContainsKey(key))
                    {
                        uniqueDataLengths[key] = payloadLength;
                    }
                }

                int recoveredLength = uniqueDataLengths.Values.Sum();
                if (recoveredLength <= 0)
                {
                    throw new InvalidDataException("Decoded durability payload is incomplete. No valid frame packets were recovered.");
                }

                // Hole-tolerant reconstruction (CR-20260913-02): a wholly-missing parity group
                // becomes a zero-filled hole plus a loss-map entry instead of aborting the
                // decode. Integrity verification below still fails loudly for any hole.
                if (!durabilityCodec.TryDecodeFramePacketsWithHoles(packets, recoveredLength, out var payload, out var decodedBytes, out var manifest, out var missingGroupIds))
                {
                    throw new InvalidDataException("Durability matrix could not reconstruct the payload from the recovered packets.");
                }
                aggregationStopwatch.Stop();
                aggregationMilliseconds += aggregationStopwatch.Elapsed.TotalMilliseconds;
                totalStopwatch.Stop();

                var serialMetrics = new DecodeMetrics
                {
                    TotalDecodedPayloadBytes = decodedBytes,
                    TotalFramesDecoded = packets.Count,
                    TotalFramesSeen = packets.Count,
                    RecoveredGroupCount = packets.Count,
                    RecoveredDataFrameCount = packets.Count,
                    AudioDatagramCount = audioDatagramCount,
                    TotalElapsedMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds,
                    FrameReadMilliseconds = frameReadMilliseconds,
                    PacketDecodeMilliseconds = packetDecodeMilliseconds,
                    AggregationMilliseconds = aggregationMilliseconds,
                    Manifest = manifest,
                    MissingDatagramIds = missingGroupIds
                };
                serialMetrics.IntegrityStatus = VerifyAgainstManifest(payload, manifest, serialMetrics);
                LastDecodeMetrics = serialMetrics;

                return payload;
            }

            var accumulator = new DecodedFrameAccumulator();
            const int repeatedFrameCount = 3;
            byte[] frameBufLegacy = new byte[frameBytes];
            var duplicateTracker = new DuplicateFrameRunTracker();
            var metrics = new DecodeMetrics();
            var frameBitDecoder = FrameBitDecoderFactory.CreateForModulator(_modulator);

            byte[] CreateLogicalSignature(byte[] frame)
            {
                var packet = new byte[packetByteLength];
                var decodeStopwatch = Stopwatch.StartNew();
                frameBitDecoder.DecodeMemory(frame, _width, _height, _macroblockSize, rowBytes, frameBytes, packet, borderWidth);
                decodeStopwatch.Stop();
                packetDecodeMilliseconds += decodeStopwatch.Elapsed.TotalMilliseconds;
                return packet;
            }

            bool DecodePayloadFrame(byte[] frame)
            {
                var aggregationStopwatch = Stopwatch.StartNew();
                var added = accumulator.TryAddDecodedFrame(frame, _width, _height, _macroblockSize, rowBytes, frameBytes, payloadBytesPerFrame, bitsPerFrame, _modulator);
                aggregationStopwatch.Stop();
                aggregationMilliseconds += aggregationStopwatch.Elapsed.TotalMilliseconds;
                return added;
            }

            void FlushPendingRun()
            {
                if (!duplicateTracker.HasCurrentRun) return;

                metrics.DuplicateRunCount++;
                metrics.StrongestDuplicateQuality = Math.Max(metrics.StrongestDuplicateQuality, duplicateTracker.BestQuality);
                duplicateTracker.FlushCurrentRun(repeatedFrameCount, frame => DecodePayloadFrame(frame));
            }

            int legacyFrameIndex = 0;
            while (true)
            {
                var readStopwatch = Stopwatch.StartNew();
                int read = 0;
                while (read < frameBytes)
                {
                    int r = await rgbStream.ReadAsync(frameBufLegacy, read, frameBytes - read, cancellationToken);
                    if (r == 0) break;
                    read += r;
                }
                readStopwatch.Stop();
                frameReadMilliseconds += readStopwatch.Elapsed.TotalMilliseconds;

                if (read < frameBytes) break;

                legacyFrameIndex++;
                metrics.TotalFramesSeen++;
                progress?.Report(new DecodeProgress(metrics.TotalFramesSeen, totalVideoFrames));
                DebugTrace.Log("DecodeStreamOrchestrator", $"Read frame #{legacyFrameIndex} ({read} bytes) for legacy decode path. invalid packets so far={metrics.InvalidPacketCount}");

                // Canonical separator frames (Phase 4) mark a datagram boundary; they carry no
                // payload of their own and must not be counted as invalid/corrupted packets.
                if (frameBitDecoder.IsCanonicalFrameMemory(frameBufLegacy, _width, _height, borderWidth))
                {
                    metrics.CanonicalFrameCount++;
                    FlushPendingRun();

                    if (DecodeRecoveryPolicy.ShouldStopDecoding(accumulator, expectedOutputBytes))
                    {
                        DebugTrace.Log("DecodeStreamOrchestrator", $"Stopping decode after canonical frame #{legacyFrameIndex}; accumulator bytes={accumulator.TotalDataFrames} expected={expectedOutputBytes}");
                        break;
                    }

                    continue;
                }

                var decodeStopwatch = Stopwatch.StartNew();
                var packetIsValid = DecoderEngine.TryReadDecodedPacket(frameBufLegacy, _width, _height, _macroblockSize, rowBytes, frameBytes, bitsPerFrame, _modulator, out var packet);
                decodeStopwatch.Stop();
                packetDecodeMilliseconds += decodeStopwatch.Elapsed.TotalMilliseconds;
                if (!packetIsValid)
                {
                    metrics.InvalidPacketCount++;
                    DebugTrace.Log("DecodeStreamOrchestrator", $"Invalid packet on frame #{legacyFrameIndex}; invalidPacketCount={metrics.InvalidPacketCount}");
                    continue;
                }

                var currentLogicalSignature = CreateLogicalSignature(frameBufLegacy);
                int currentQuality = DecoderEngine.GetPacketQualityScore(packet);

                var aggregationStopwatch = Stopwatch.StartNew();
                var completedFrame = duplicateTracker.Update(frameBufLegacy, currentLogicalSignature, currentQuality);
                if (completedFrame is not null)
                {
                    metrics.DuplicateRunCount++;
                    metrics.StrongestDuplicateQuality = Math.Max(metrics.StrongestDuplicateQuality, completedFrame.BestQuality);
                    duplicateTracker.Flush(completedFrame, repeatedFrameCount, frame => DecodePayloadFrame(frame));
                }

                var shouldStop = DecodeRecoveryPolicy.ShouldStopDecoding(accumulator, expectedOutputBytes);
                aggregationStopwatch.Stop();
                aggregationMilliseconds += aggregationStopwatch.Elapsed.TotalMilliseconds;

                if (shouldStop)
                {
                    DebugTrace.Log("DecodeStreamOrchestrator", $"Stopping decode after frame #{legacyFrameIndex}; accumulator bytes={accumulator.TotalDataFrames} expected={expectedOutputBytes}");
                    break;
                }
            }

            var finalAggregationStopwatch = Stopwatch.StartNew();
            duplicateTracker.FlushCurrentRun(repeatedFrameCount, frame => DecodePayloadFrame(frame));

            if (accumulator.TotalDataFrames < 0 || accumulator.OrderedPayload.Count == 0)
                throw new InvalidDataException("Decoded payload is incomplete. No valid frames were decoded.");

            int resolvedExpectedBytes = DecodeRecoveryPolicy.ResolveExpectedOutputBytes(accumulator, expectedOutputBytes);
            accumulator.RecoverMissingPayloadFrames(accumulator.TotalDataFrames, payloadBytesPerFrame, resolvedExpectedBytes);
            metrics.RecoveredGroupCount = accumulator.RecoveredGroupCount;
            metrics.StrongestDuplicateQuality = Math.Max(metrics.StrongestDuplicateQuality, duplicateTracker.BestQuality);
            metrics.TotalDecodedPayloadBytes = resolvedExpectedBytes;
            metrics.TotalFramesDecoded = accumulator.TotalDataFrames;
            metrics.RecoveredDataFrameCount = accumulator.OrderedPayload.Count;
            metrics.RecoveredParityFrameCount = accumulator.ParityPayloadByGroup.Count;
            metrics.AudioDatagramCount = audioDatagramCount;
            finalAggregationStopwatch.Stop();
            aggregationMilliseconds += finalAggregationStopwatch.Elapsed.TotalMilliseconds;
            totalStopwatch.Stop();
            metrics.TotalElapsedMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds;
            metrics.FrameReadMilliseconds = frameReadMilliseconds;
            metrics.PacketDecodeMilliseconds = packetDecodeMilliseconds;
            metrics.AggregationMilliseconds = aggregationMilliseconds;
            LastDecodeMetrics = metrics;

            return accumulator.AssembleOutput(resolvedExpectedBytes);
        }

        private async Task<byte[]> ProcessParallelAsync(
            Stream rgbStream,
            int expectedOutputBytes,
            int? audioDatagramCount,
            int totalVideoFrames,
            IProgress<DecodeProgress>? progress,
            int borderWidth,
            int payloadBytesPerFrame,
            int rowBytes,
            int frameBytes,
            int bitsPerFrame,
            int packetByteLength,
            Stopwatch totalStopwatch,
            CancellationToken cancellationToken)
        {
            int workerCount = _maxDegreeOfParallelism;
            int channelCapacity = Math.Max(2, workerCount * 2);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = linkedCts.Token;
            var work = Channel.CreateBounded<DecodeWorkItem>(new BoundedChannelOptions(channelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = false
            });
            var results = Channel.CreateBounded<DecodedFrame>(new BoundedChannelOptions(channelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = true
            });
            using var resultSlots = new SemaphoreSlim(channelCapacity, channelCapacity);

            double frameReadMilliseconds = 0d;
            var reader = Task.Run(async () =>
            {
                try
                {
                    long sequence = 0;
                    while (true)
                    {
                        var frame = new byte[frameBytes];
                        var readStopwatch = Stopwatch.StartNew();
                        int read = 0;
                        while (read < frameBytes)
                        {
                            int count = await rgbStream.ReadAsync(frame, read, frameBytes - read, token);
                            if (count == 0) break;
                            read += count;
                        }
                        readStopwatch.Stop();
                        InterlockedAdd(ref frameReadMilliseconds, readStopwatch.Elapsed.TotalMilliseconds);
                        if (read < frameBytes) break;

                        // Reserve the result slot here, in sequence order, before dispatching the frame.
                        // Slots are released by the aggregator only on in-order consumption, so if a
                        // worker acquired its own slot after decoding, a slow head frame could see every
                        // slot taken by later frames that the aggregator is still holding out of order -
                        // a circular wait that froze the whole decode. Acquiring in the reader guarantees
                        // the head item always holds a slot before any later item can, so the aggregator
                        // can always consume the head and release.
                        await resultSlots.WaitAsync(token);
                        try
                        {
                            await work.Writer.WriteAsync(new DecodeWorkItem(sequence++, frame), token);
                        }
                        catch
                        {
                            resultSlots.Release();
                            throw;
                        }
                    }
                    work.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    work.Writer.TryComplete(ex);
                    linkedCts.Cancel();
                    throw;
                }
            }, token);

            async Task WorkerAsync()
            {
                try
                {
                    await foreach (var item in work.Reader.ReadAllAsync(token))
                    {
                        var decodeStopwatch = Stopwatch.StartNew();
                        var decoder = FrameBitDecoderFactory.CreateForModulator(_modulator);
                        bool canonical = decoder.IsCanonicalFrameMemory(item.Frame, _width, _height, borderWidth);
                        byte[]? packet = null;
                        bool packetIsValid = false;
                        if (!canonical)
                        {
                            packetIsValid = DecoderEngine.TryReadDecodedPacket(item.Frame, _width, _height, _macroblockSize, rowBytes, frameBytes, bitsPerFrame, _modulator, out packet);
                        }
                        byte[]? signature = null;
                        if (packetIsValid)
                        {
                            signature = new byte[packetByteLength];
                            decoder.DecodeMemory(item.Frame, _width, _height, _macroblockSize, rowBytes, frameBytes, signature, borderWidth);
                        }
                        decodeStopwatch.Stop();

                        // The slot for this frame was already reserved by the reader in sequence order;
                        // the worker only produces the result. The aggregator releases the slot when it
                        // consumes the frame in order.
                        await results.Writer.WriteAsync(new DecodedFrame(item.Sequence, item.Frame, canonical, packetIsValid, packet, signature, packetIsValid ? DecoderEngine.GetPacketQualityScore(packet!) : 0, decodeStopwatch.Elapsed.TotalMilliseconds), token);
                    }
                }
                catch (Exception ex)
                {
                    linkedCts.Cancel();
                    throw new InvalidOperationException("Parallel frame decode failed.", ex);
                }
            }

            var workers = Enumerable.Range(0, workerCount).Select(_ => WorkerAsync()).ToArray();
            var workersCompletion = Task.WhenAll(workers);
            _ = workersCompletion.ContinueWith(
                completed => results.Writer.TryComplete(completed.IsFaulted ? completed.Exception : null),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            var aggregator = Task.Run(async () =>
            {
                var pending = new SortedDictionary<long, DecodedFrame>();
                var packets = new List<byte[]>();
                var accumulator = new DecodedFrameAccumulator();
                var duplicateTracker = new DuplicateFrameRunTracker();
                var metrics = new DecodeMetrics();
                const int repeatedFrameCount = 3;
                double packetDecodeMilliseconds = 0d;
                double aggregationMilliseconds = 0d;
                long nextSequence = 0;
                bool stopRequested = false;

                bool DecodePayloadFrame(byte[] frame)
                {
                    return accumulator.TryAddDecodedFrame(frame, _width, _height, _macroblockSize, rowBytes, frameBytes, payloadBytesPerFrame, bitsPerFrame, _modulator);
                }

                void FlushPendingRun()
                {
                    if (!duplicateTracker.HasCurrentRun) return;
                    metrics.DuplicateRunCount++;
                    metrics.StrongestDuplicateQuality = Math.Max(metrics.StrongestDuplicateQuality, duplicateTracker.BestQuality);
                    duplicateTracker.FlushCurrentRun(repeatedFrameCount, DecodePayloadFrame);
                }

                void Aggregate(DecodedFrame result)
                {
                    var aggregationStopwatch = Stopwatch.StartNew();
                    packetDecodeMilliseconds += result.DecodeMilliseconds;
                    metrics.TotalFramesSeen++;
                    progress?.Report(new DecodeProgress(metrics.TotalFramesSeen, totalVideoFrames));
                    if (result.Canonical)
                    {
                        metrics.CanonicalFrameCount++;
                        FlushPendingRun();
                    }
                    else if (!result.PacketIsValid)
                    {
                        metrics.InvalidPacketCount++;
                    }
                    else if (_useDurabilityMatrix)
                    {
                        packets.Add(result.Packet!);
                    }
                    else
                    {
                        var completed = duplicateTracker.Update(result.Frame, result.Signature!, result.Quality);
                        if (completed is not null)
                        {
                            metrics.DuplicateRunCount++;
                            metrics.StrongestDuplicateQuality = Math.Max(metrics.StrongestDuplicateQuality, completed.BestQuality);
                            duplicateTracker.Flush(completed, repeatedFrameCount, DecodePayloadFrame);
                        }
                    }
                    stopRequested |= !_useDurabilityMatrix && DecodeRecoveryPolicy.ShouldStopDecoding(accumulator, expectedOutputBytes);
                    aggregationStopwatch.Stop();
                    aggregationMilliseconds += aggregationStopwatch.Elapsed.TotalMilliseconds;
                }

                try
                {
                    await foreach (var result in results.Reader.ReadAllAsync(token))
                    {
                        pending.Add(result.Sequence, result);
                        while (pending.Remove(nextSequence, out var ordered))
                        {
                            if (!stopRequested) Aggregate(ordered);
                            resultSlots.Release();
                            nextSequence++;
                        }
                    }
                    while (pending.Remove(nextSequence, out var finalResult))
                    {
                        if (!stopRequested) Aggregate(finalResult);
                        resultSlots.Release();
                        nextSequence++;
                    }

                    if (_useDurabilityMatrix)
                    {
                        var durabilityCodec = new DurabilityTransportCodec(_durabilityMatrixOptions ?? new DurabilityMatrixOptions());
                        var uniqueDataLengths = new Dictionary<(int GroupId, int SymbolId), int>();
                        foreach (var packet in packets)
                        {
                            if (!FramePacketCodec.TryDecodeWithTolerance(packet, out var frameType, out var frameIndex, out _, out var groupStart, out _, out var payloadLength, out _) || frameType != FramePacket.FrameTypeData)
                                continue;
                            uniqueDataLengths.TryAdd((groupStart / Math.Max(1, _durabilityMatrixOptions?.GroupSize ?? 1), Math.Max(0, frameIndex - groupStart)), payloadLength);
                        }
                        int recoveredLength = uniqueDataLengths.Values.Sum();
                        if (recoveredLength <= 0)
                            throw new InvalidDataException("Decoded durability payload is incomplete. No valid frame packets were recovered.");

                        // Hole-tolerant reconstruction (CR-20260913-02): a wholly-missing parity
                        // group becomes a zero-filled hole plus a loss-map entry instead of
                        // aborting the decode. Integrity verification below still fails loudly.
                        if (!durabilityCodec.TryDecodeFramePacketsWithHoles(packets, recoveredLength, out var payload, out var decodedBytes, out var manifest, out var missingGroupIds))
                            throw new InvalidDataException("Durability matrix could not reconstruct the payload from the recovered packets.");

                        metrics.Manifest = manifest;
                        metrics.MissingDatagramIds = missingGroupIds;
                        metrics.IntegrityStatus = VerifyAgainstManifest(payload, manifest, metrics);
                        if (metrics.IntegrityStatus == IntegrityStatus.Failed)
                        {
                            metrics.TotalDecodedPayloadBytes = decodedBytes;
                            metrics.TotalFramesDecoded = packets.Count;
                            metrics.TotalFramesSeen = packets.Count;
                            metrics.RecoveredGroupCount = packets.Count;
                            metrics.RecoveredDataFrameCount = packets.Count;
                            metrics.AudioDatagramCount = audioDatagramCount;
                            metrics.TotalElapsedMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds;
                            metrics.FrameReadMilliseconds = frameReadMilliseconds;
                            metrics.PacketDecodeMilliseconds = packetDecodeMilliseconds;
                            metrics.AggregationMilliseconds = aggregationMilliseconds;
                            LastDecodeMetrics = metrics;
                            throw new InvalidDataException(
                                manifest is not null && manifest.TotalPayloadBytes != decodedBytes
                                    ? $"Recovered payload length {decodedBytes} does not match the stream manifest's declared {manifest.TotalPayloadBytes} bytes."
                                    : "Recovered payload hash does not match the stream manifest's SHA-256. The output would be silently corrupt.");
                        }

                        metrics.TotalDecodedPayloadBytes = decodedBytes;
                        metrics.TotalFramesDecoded = packets.Count;
                        metrics.TotalFramesSeen = packets.Count;
                        metrics.RecoveredGroupCount = packets.Count;
                        metrics.RecoveredDataFrameCount = packets.Count;
                        metrics.AudioDatagramCount = audioDatagramCount;
                        metrics.TotalElapsedMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds;
                        metrics.FrameReadMilliseconds = frameReadMilliseconds;
                        metrics.PacketDecodeMilliseconds = packetDecodeMilliseconds;
                        metrics.AggregationMilliseconds = aggregationMilliseconds;
                        LastDecodeMetrics = metrics;
                        return payload;
                    }

                    duplicateTracker.FlushCurrentRun(repeatedFrameCount, DecodePayloadFrame);
                    if (accumulator.OrderedPayload.Count == 0)
                        throw new InvalidDataException("Decoded payload is incomplete. No valid frames were decoded.");
                    int resolvedExpectedBytes = DecodeRecoveryPolicy.ResolveExpectedOutputBytes(accumulator, expectedOutputBytes);
                    accumulator.RecoverMissingPayloadFrames(accumulator.TotalDataFrames, payloadBytesPerFrame, resolvedExpectedBytes);
                    metrics.RecoveredGroupCount = accumulator.RecoveredGroupCount;
                    metrics.StrongestDuplicateQuality = Math.Max(metrics.StrongestDuplicateQuality, duplicateTracker.BestQuality);
                    metrics.TotalDecodedPayloadBytes = resolvedExpectedBytes;
                    metrics.TotalFramesDecoded = accumulator.TotalDataFrames;
                    metrics.RecoveredDataFrameCount = accumulator.OrderedPayload.Count;
                    metrics.RecoveredParityFrameCount = accumulator.ParityPayloadByGroup.Count;
                    metrics.AudioDatagramCount = audioDatagramCount;
                    metrics.TotalElapsedMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds;
                    metrics.FrameReadMilliseconds = frameReadMilliseconds;
                    metrics.PacketDecodeMilliseconds = packetDecodeMilliseconds;
                    metrics.AggregationMilliseconds = aggregationMilliseconds;
                    LastDecodeMetrics = metrics;
                    return accumulator.AssembleOutput(resolvedExpectedBytes);
                }
                catch
                {
                    linkedCts.Cancel();
                    throw;
                }
            }, token);

            try
            {
                var output = await aggregator;
                await reader;
                await workersCompletion;
                return output;
            }
            finally
            {
                linkedCts.Cancel();
                work.Writer.TryComplete();
                results.Writer.TryComplete();
                try { await reader; } catch { }
                try { await workersCompletion; } catch { }
            }
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

        private static void InterlockedAdd(ref double location, double value)
        {
            double current;
            do
            {
                current = location;
            }
            while (Interlocked.CompareExchange(ref location, current + value, current) != current);
        }

        private sealed record DecodeWorkItem(long Sequence, byte[] Frame);

        private sealed record DecodedFrame(long Sequence, byte[] Frame, bool Canonical, bool PacketIsValid, byte[]? Packet, byte[]? Signature, int Quality, double DecodeMilliseconds);

        public async Task WriteToFileAsync(Stream rgbStream, string outputFile, int expectedOutputBytes, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(outputFile))
                throw new ArgumentException("Output path is required.", nameof(outputFile));

            var output = await ProcessAsync(rgbStream, expectedOutputBytes, cancellationToken: cancellationToken);
            await File.WriteAllBytesAsync(outputFile, output, cancellationToken);
        }
    }
}
