using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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

        public DecodeStreamOrchestrator(int width, int height, int macroblockSize)
            : this(new BinaryGridModulator(macroblockSize, macroblockSize), width, height, macroblockSize, false, null)
        {
        }

        public DecodeStreamOrchestrator(IModulator modulator, int width, int height, int macroblockSize)
            : this(modulator, width, height, macroblockSize, false, null)
        {
        }

        public DecodeStreamOrchestrator(IModulator modulator, int width, int height, int macroblockSize, bool useDurabilityMatrix, DurabilityMatrixOptions? durabilityMatrixOptions)
        {
            _modulator = modulator ?? throw new ArgumentNullException(nameof(modulator));
            _width = width;
            _height = height;
            _macroblockSize = macroblockSize;
            _useDurabilityMatrix = useDurabilityMatrix;
            _durabilityMatrixOptions = durabilityMatrixOptions ?? new DurabilityMatrixOptions();
        }

        public DecodeMetrics LastDecodeMetrics { get; private set; } = new();

        public async Task<byte[]> ProcessAsync(Stream rgbStream, int expectedOutputBytes, int? audioDatagramCount = null, int totalVideoFrames = 0, IProgress<DecodeProgress>? progress = null)
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
                        int r = await rgbStream.ReadAsync(frameBuf, read, frameBytes - read);
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

                if (!durabilityCodec.TryDecodeFramePackets(packets, recoveredLength, out var payload, out var decodedBytes))
                {
                    throw new InvalidDataException("Durability matrix could not reconstruct the payload from the recovered packets.");
                }
                aggregationStopwatch.Stop();
                aggregationMilliseconds += aggregationStopwatch.Elapsed.TotalMilliseconds;
                totalStopwatch.Stop();

                LastDecodeMetrics = new DecodeMetrics
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
                    AggregationMilliseconds = aggregationMilliseconds
                };

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
                    int r = await rgbStream.ReadAsync(frameBufLegacy, read, frameBytes - read);
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

        public async Task WriteToFileAsync(Stream rgbStream, string outputFile, int expectedOutputBytes)
        {
            if (string.IsNullOrWhiteSpace(outputFile))
                throw new ArgumentException("Output path is required.", nameof(outputFile));

            var output = await ProcessAsync(rgbStream, expectedOutputBytes);
            await File.WriteAllBytesAsync(outputFile, output);
        }
    }
}
