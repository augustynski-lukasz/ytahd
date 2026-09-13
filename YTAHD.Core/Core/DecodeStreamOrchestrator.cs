using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

            // Both serial branches (durability and legacy) feed the same aggregator in stream
            // order (CR-20260913-03); only the finalization differs.
            var aggregator = new DecodeAggregator(
                _useDurabilityMatrix,
                _durabilityMatrixOptions!,
                _width,
                _height,
                _macroblockSize,
                rowBytes,
                frameBytes,
                bitsPerFrame,
                payloadBytesPerFrame,
                expectedOutputBytes,
                audioDatagramCount,
                _modulator);
            var frameBitDecoder = FrameBitDecoderFactory.CreateForModulator(_modulator);
            byte[] frameBuf = new byte[frameBytes];
            int framesRead = 0;

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
                framesRead++;

                // Canonical separator frames (Phase 4) mark a datagram boundary; they carry no
                // payload of their own and must not be counted as invalid/corrupted packets.
                bool canonical = frameBitDecoder.IsCanonicalFrameMemory(frameBuf, _width, _height, borderWidth);

                byte[]? packet = null;
                bool packetIsValid = false;
                byte[]? signature = null;
                int quality = 0;

                var decodeStopwatch = Stopwatch.StartNew();
                if (!canonical)
                {
                    packetIsValid = DecoderEngine.TryReadDecodedPacket(frameBuf, _width, _height, _macroblockSize, rowBytes, frameBytes, bitsPerFrame, _modulator, out packet);
                    if (packetIsValid && !_useDurabilityMatrix)
                    {
                        // Duplicate-run arbitration needs the logical signature; the durability
                        // path never compares frames, so skip the extra decode pass there.
                        signature = new byte[packetByteLength];
                        frameBitDecoder.DecodeMemory(frameBuf, _width, _height, _macroblockSize, rowBytes, frameBytes, signature, borderWidth);
                        quality = DecoderEngine.GetPacketQualityScore(packet!);
                    }
                }
                decodeStopwatch.Stop();
                packetDecodeMilliseconds += decodeStopwatch.Elapsed.TotalMilliseconds;

                aggregator.Aggregate(new DecodedFrameResult(frameBuf, canonical, packetIsValid, packet, signature, quality));
                progress?.Report(new DecodeProgress(aggregator.Metrics.TotalFramesSeen, totalVideoFrames));

                if (aggregator.StopRequested)
                {
                    DebugTrace.Log("DecodeStreamOrchestrator", $"Stopping decode after frame #{framesRead}; expected={expectedOutputBytes}");
                    break;
                }
            }

            var aggregationStopwatch = Stopwatch.StartNew();
            byte[] payload = _useDurabilityMatrix ? aggregator.FinalizeDurability() : aggregator.FinalizeLegacy();
            aggregationStopwatch.Stop();
            aggregationMilliseconds += aggregationStopwatch.Elapsed.TotalMilliseconds;
            totalStopwatch.Stop();

            aggregator.Complete(totalStopwatch.Elapsed.TotalMilliseconds, frameReadMilliseconds, packetDecodeMilliseconds, aggregationMilliseconds);
            LastDecodeMetrics = aggregator.Metrics;

            return payload;
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

            double packetDecodeMilliseconds = 0d;
            double aggregationMilliseconds = 0d;
            var aggregator = Task.Run(async () =>
            {
                // Aggregation semantics are shared with the serial path (CR-20260913-03); results
                // are fed strictly in sequence order so the aggregator stays single-threaded.
                var shared = new DecodeAggregator(
                    _useDurabilityMatrix,
                    _durabilityMatrixOptions!,
                    _width,
                    _height,
                    _macroblockSize,
                    rowBytes,
                    frameBytes,
                    bitsPerFrame,
                    payloadBytesPerFrame,
                    expectedOutputBytes,
                    audioDatagramCount,
                    _modulator);
                var pending = new SortedDictionary<long, DecodedFrame>();
                long nextSequence = 0;

                void Aggregate(DecodedFrame result)
                {
                    shared.Aggregate(new DecodedFrameResult(result.Frame, result.Canonical, result.PacketIsValid, result.Packet, result.Signature, result.Quality));
                    packetDecodeMilliseconds += result.DecodeMilliseconds;
                    progress?.Report(new DecodeProgress(shared.Metrics.TotalFramesSeen, totalVideoFrames));
                }

                try
                {
                    await foreach (var result in results.Reader.ReadAllAsync(token))
                    {
                        pending.Add(result.Sequence, result);
                        while (pending.Remove(nextSequence, out var ordered))
                        {
                            if (!shared.StopRequested) Aggregate(ordered);
                            resultSlots.Release();
                            nextSequence++;
                        }
                    }
                    while (pending.Remove(nextSequence, out var finalResult))
                    {
                        if (!shared.StopRequested) Aggregate(finalResult);
                        resultSlots.Release();
                        nextSequence++;
                    }

                    byte[] payload = _useDurabilityMatrix ? shared.FinalizeDurability() : shared.FinalizeLegacy();
                    totalStopwatch.Stop();
                    shared.Complete(totalStopwatch.Elapsed.TotalMilliseconds, frameReadMilliseconds, packetDecodeMilliseconds, aggregationMilliseconds);
                    LastDecodeMetrics = shared.Metrics;
                    return payload;
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
