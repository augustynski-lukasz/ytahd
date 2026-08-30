using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using YTAHD.Core.Modulation;

namespace YTAHD.Core.Core
{
    public sealed class DecodeStreamOrchestrator
    {
        private readonly IModulator _modulator;
        private readonly int _width;
        private readonly int _height;
        private readonly int _macroblockSize;

        public DecodeStreamOrchestrator(int width, int height, int macroblockSize)
            : this(new BinaryGridModulator(macroblockSize, macroblockSize), width, height, macroblockSize)
        {
        }

        public DecodeStreamOrchestrator(IModulator modulator, int width, int height, int macroblockSize)
        {
            _modulator = modulator ?? throw new ArgumentNullException(nameof(modulator));
            _width = width;
            _height = height;
            _macroblockSize = macroblockSize;
        }

        public DecodeMetrics LastDecodeMetrics { get; private set; } = new();

        public async Task<byte[]> ProcessAsync(Stream rgbStream, int expectedOutputBytes)
        {
            if (rgbStream == null) throw new ArgumentNullException(nameof(rgbStream));
            if (!rgbStream.CanRead) throw new ArgumentException("Stream is not readable", nameof(rgbStream));

            var geometry = new ModulatorGeometry(_width, _height, _macroblockSize, FramePacket.HeaderBytes, BitsPerFrame: 0);
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

            var accumulator = new DecodedFrameAccumulator();
            const int repeatedFrameCount = 3;
            byte[] frameBuf = new byte[frameBytes];
            var duplicateTracker = new DuplicateFrameRunTracker();
            var metrics = new DecodeMetrics();

            byte[] CreateLogicalSignature(ReadOnlySpan<byte> frame)
            {
                var packet = new byte[packetByteLength];
                var strategy = FrameBitDecoderFactory.CreateForModulator(_modulator);
                int borderWidth = _modulator.GetBorderWidth(packetGeometry with { BorderWidth = 0 });
                strategy.Decode(frame, _width, _height, _macroblockSize, rowBytes, frameBytes, packet, borderWidth);
                return packet;
            }

            bool DecodePayloadFrame(ReadOnlySpan<byte> frame)
            {
                return accumulator.TryAddDecodedFrame(frame, _width, _height, _macroblockSize, rowBytes, frameBytes, payloadBytesPerFrame, packetByteLength, _modulator);
            }

            while (true)
            {
                int read = 0;
                while (read < frameBytes)
                {
                    int r = await rgbStream.ReadAsync(frameBuf, read, frameBytes - read);
                    if (r == 0) break;
                    read += r;
                }

                if (read < frameBytes) break;

                metrics.TotalFramesSeen++;

                if (!DecoderEngine.TryReadDecodedPacket(frameBuf, _width, _height, _macroblockSize, rowBytes, frameBytes, bitsPerFrame, _modulator, out var packet))
                {
                    metrics.InvalidPacketCount++;
                    continue;
                }

                var currentLogicalSignature = CreateLogicalSignature(frameBuf);
                int currentQuality = DecoderEngine.GetPacketQualityScore(packet);

                var completedFrame = duplicateTracker.Update(frameBuf, currentLogicalSignature, currentQuality);
                if (completedFrame is not null)
                {
                    metrics.DuplicateRunCount++;
                    metrics.StrongestDuplicateQuality = Math.Max(metrics.StrongestDuplicateQuality, completedFrame.BestQuality);
                    duplicateTracker.Flush(completedFrame, repeatedFrameCount, frame => DecodePayloadFrame(frame));
                }

                if (DecodeRecoveryPolicy.ShouldStopDecoding(accumulator, expectedOutputBytes))
                {
                    break;
                }
            }

            duplicateTracker.FlushCurrentRun(repeatedFrameCount, frame => DecodePayloadFrame(frame));

            if (accumulator.TotalDataFrames < 0 || accumulator.OrderedPayload.Count == 0)
                throw new InvalidDataException("Decoded payload is incomplete. No valid frames were decoded.");

            int resolvedExpectedBytes = DecodeRecoveryPolicy.ResolveExpectedOutputBytes(accumulator, expectedOutputBytes);
            accumulator.RecoverMissingPayloadFrames(accumulator.TotalDataFrames, payloadBytesPerFrame, resolvedExpectedBytes);
            metrics.RecoveredGroupCount = accumulator.RecoveredGroupCount;
            metrics.StrongestDuplicateQuality = Math.Max(metrics.StrongestDuplicateQuality, duplicateTracker.BestQuality);
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
