using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using YTAHD.Core.Modulation;

namespace YTAHD.Core.Core
{
    public interface IFrameBitDecoder
    {
        void Decode(ReadOnlySpan<byte> frame, int width, int height, int macroblockSize, int rowBytes, int frameBytes, Span<byte> packet, int borderWidth = 0);

        void DecodeMemory(ReadOnlyMemory<byte> frame, int width, int height, int macroblockSize, int rowBytes, int frameBytes, Memory<byte> packet, int borderWidth = 0)
            => Decode(frame.Span, width, height, macroblockSize, rowBytes, frameBytes, packet.Span, borderWidth);

        /// <summary>
        /// True if this frame is a "no data" marker that the pipeline should skip rather than
        /// treat as an invalid/corrupted packet. Modulators without a separator concept never
        /// produce one, so the default is always false.
        /// </summary>
        bool IsCanonicalFrame(ReadOnlySpan<byte> frame, int width, int height, int borderWidth) => false;

        bool IsCanonicalFrameMemory(ReadOnlyMemory<byte> frame, int width, int height, int borderWidth)
            => IsCanonicalFrame(frame.Span, width, height, borderWidth);
    }

    public static class FrameBitDecoderFactory
    {
        /// <summary>
        /// Creates the frame bit decoder matching <paramref name="modulator"/>, inheriting the
        /// modulator's <see cref="IParallelismConfigurable.InnerDegreeOfParallelism"/> so a single
        /// assignment on the modulator reaches every decoder the pipeline creates for it.
        /// </summary>
        public static IFrameBitDecoder CreateForModulator(IModulator modulator)
        {
            if (modulator == null)
            {
                throw new ArgumentNullException(nameof(modulator));
            }

            // Resolve the decoder for the effective modulator, so a decorating wrapper (for example a
            // test double that varies per-frame decode cost) resolves to the decoder of what it wraps.
            while (modulator is IModulatorDecorator decorator)
            {
                modulator = decorator.Inner ?? throw new ArgumentException("A modulator decorator must expose an inner modulator.", nameof(modulator));
            }

            IFrameBitDecoder decoder;

            if (modulator is BinaryGridModulator)
            {
                decoder = new BinaryGridFrameBitDecoder();
            }
            else if (modulator is PseudoQamModulator)
            {
                decoder = new PseudoQamFrameBitDecoder();
            }
            else if (modulator is DctModulator)
            {
                decoder = new DctFrameBitDecoder();
            }
            else if (modulator is MotionVectorModulator motion)
            {
                decoder = new MotionFrameBitDecoder(motion.Profile);
            }
            else
            {
                throw new NotSupportedException($"No frame bit decoder available for modulator '{modulator.GetType().Name}'.");
            }

            if (modulator is IParallelismConfigurable modulatorParallelism
                && decoder is IParallelismConfigurable decoderParallelism)
            {
                decoderParallelism.InnerDegreeOfParallelism = modulatorParallelism.InnerDegreeOfParallelism;
            }

            return decoder;
        }
    }

    public sealed class BinaryGridFrameBitDecoder : IFrameBitDecoder
    {
        public void Decode(ReadOnlySpan<byte> frame, int width, int height, int macroblockSize, int rowBytes, int frameBytes, Span<byte> packet, int borderWidth = 0)
        {
            packet.Clear();

            bool isRgba = frame.Length == width * height * 4;
            int bytesPerPixel = isRgba ? 4 : 3;
            int effectiveRowBytes = isRgba ? width * 4 : rowBytes;
            int blocksX = Math.Max(1, width / macroblockSize);
            int blocksY = Math.Max(1, height / macroblockSize);

            var blockLuminance = new int[blocksX * blocksY];
            int index = 0;
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int sampleXStart = bx * macroblockSize;
                    int sampleYStart = by * macroblockSize;
                    int sampleCount = 0;
                    long luminanceTotal = 0;

                    for (int sy = 0; sy < macroblockSize; sy++)
                    {
                        for (int sx = 0; sx < macroblockSize; sx++)
                        {
                            int pixelX = sampleXStart + sx;
                            int pixelY = sampleYStart + sy;
                            if (pixelX >= width || pixelY >= height)
                            {
                                continue;
                            }

                            int idx = (pixelY * effectiveRowBytes) + (pixelX * bytesPerPixel);
                            if (idx + 2 >= frameBytes)
                            {
                                continue;
                            }

                            byte r = frame[idx];
                            byte g = frame[idx + 1];
                            byte b = frame[idx + 2];
                            luminanceTotal += (r + g + b) / 3;
                            sampleCount++;
                        }
                    }

                    blockLuminance[index++] = sampleCount > 0 ? (int)(luminanceTotal / sampleCount) : 0;
                }
            }

            int[] thresholds = BuildThresholdCandidates(blockLuminance);
            var bestCandidate = new byte[packet.Length];
            int bestScore = int.MinValue;
            int bestThreshold = 128;

            foreach (int threshold in thresholds)
            {
                var candidate = new byte[packet.Length];
                for (int by = 0; by < blocksY; by++)
                {
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        int frameBitIndex = by * blocksX + bx;
                        if (frameBitIndex >= candidate.Length * 8)
                        {
                            continue;
                        }

                        int luminance = blockLuminance[by * blocksX + bx];
                        if (luminance >= threshold)
                        {
                            int byteIdx = frameBitIndex / 8;
                            int bitInByte = 7 - (frameBitIndex % 8);
                            candidate[byteIdx] |= (byte)(1 << bitInByte);
                        }
                    }
                }

                int score = ScoreCandidate(candidate);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestThreshold = threshold;
                    candidate.AsSpan().CopyTo(bestCandidate);
                }
            }

            if (bestScore > int.MinValue)
            {
                bestCandidate.CopyTo(packet);
                return;
            }

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int frameBitIndex = by * blocksX + bx;
                    if (frameBitIndex >= packet.Length * 8)
                    {
                        continue;
                    }

                    int luminance = blockLuminance[by * blocksX + bx];
                    if (luminance >= bestThreshold)
                    {
                        int byteIdx = frameBitIndex / 8;
                        int bitInByte = 7 - (frameBitIndex % 8);
                        packet[byteIdx] |= (byte)(1 << bitInByte);
                    }
                }
            }
        }

        private static int ScoreCandidate(ReadOnlySpan<byte> candidate)
        {
            int score = PacketQualityScorer.Score(candidate);

            if (candidate.Length >= FramePacket.HeaderBytes)
            {
                if (candidate[0] == 0x59 && candidate[1] == 0x54)
                {
                    score += 4096;
                }

                if (candidate[2] == FramePacket.FrameVersion || candidate[2] == FramePacket.LegacyFrameVersion)
                {
                    score += 256;
                }

                if (candidate[3] == FramePacket.FrameTypeData || candidate[3] == FramePacket.FrameTypeParity)
                {
                    score += 128;
                }

                int payloadLength = candidate[2] == FramePacket.LegacyFrameVersion
                    ? (candidate[17] << 8) | candidate[18]
                    : candidate.Length >= FramePacket.HeaderBytes ? ((candidate[17] << 24) | (candidate[18] << 16) | (candidate[19] << 8) | candidate[20]) : -1;
                int headerBytes = candidate[2] == FramePacket.LegacyFrameVersion ? FramePacket.LegacyHeaderBytes : FramePacket.HeaderBytes;
                if (payloadLength >= 0 && payloadLength <= candidate.Length - headerBytes)
                {
                    score += 128;
                }
            }

            return score;
        }

        private static int[] BuildThresholdCandidates(int[] blockLuminance)
        {
            if (blockLuminance.Length == 0)
            {
                return new[] { 128 };
            }

            var sorted = (int[])blockLuminance.Clone();
            Array.Sort(sorted);

            int median = sorted[sorted.Length / 2];
            int average = 0;
            foreach (int value in sorted)
            {
                average += value;
            }
            average /= Math.Max(1, sorted.Length);

            var values = new SortedSet<int>
            {
                0, 16, 32, 48, 64, 80, 96, 112, 128, 144, 160, 176, 192, 208, 224, 240, 255
            };

            foreach (int value in new[] { median, average, Math.Min(96, median), Math.Max(160, median), sorted[0], sorted[^1] })
            {
                values.Add(value);
            }

            return values.ToArray();
        }
    }

    public sealed class PseudoQamFrameBitDecoder : IFrameBitDecoder
    {
        public void Decode(ReadOnlySpan<byte> frame, int width, int height, int macroblockSize, int rowBytes, int frameBytes, Span<byte> packet, int borderWidth = 0)
        {
            packet.Clear();

            int blocksX = Math.Max(1, (width - (borderWidth * 2)) / macroblockSize);
            int blocksY = Math.Max(1, (height - (borderWidth * 2)) / macroblockSize);
            bool isRgba = frame.Length == width * height * 4;

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int byteIndex = by * blocksX + bx;
                    if (byteIndex >= packet.Length)
                    {
                        continue;
                    }

                    int sampleX = borderWidth + (bx * macroblockSize) + (macroblockSize / 2);
                    int sampleY = borderWidth + (by * macroblockSize) + (macroblockSize / 2);
                    int pixelIndex = sampleY * width + sampleX;
                    int channelOffset = isRgba ? pixelIndex * 4 : pixelIndex * 3;

                    if (channelOffset + 2 >= frame.Length)
                    {
                        continue;
                    }

                    byte r = frame[channelOffset + 0];
                    byte g = frame[channelOffset + 1];

                    int lowNibble = DequantizeNibble(r);
                    int highNibble = DequantizeNibble(g);
                    packet[byteIndex] = (byte)((highNibble << 4) | lowNibble);
                }
            }
        }

        private static int DequantizeNibble(byte pamValue)
        {
            const int pamStep = 17;
            int half = pamStep / 2;
            int level = (pamValue + half) / pamStep;
            if (level < 0) level = 0;
            if (level > 15) level = 15;
            return level;
        }
    }

    public sealed class DctFrameBitDecoder : IFrameBitDecoder, IParallelismConfigurable
    {
        private const int BlockSize = DctCarrierBasis.BlockSize; // 8

        /// <inheritdoc />
        public int InnerDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

        /// <summary>
        /// Decode a frame produced by <see cref="DctModulator"/>.
        /// For each 8×8 block, a forward 2-D DCT is applied and the sign of each
        /// carrier coefficient recovers 1 bit.  8 bits (1 byte) per block.
        /// </summary>
        public void Decode(ReadOnlySpan<byte> frame, int width, int height, int macroblockSize, int rowBytes, int frameBytes, Span<byte> packet, int borderWidth = 0)
        {
            packet.Clear();

            bool isRgba = frame.Length == width * height * 4;
            int bytesPerPixel = isRgba ? 4 : 3;
            int effectiveRowBytes = isRgba ? width * 4 : rowBytes;

            int blocksX = Math.Max(1, (width - borderWidth * 2) / BlockSize);
            int blocksY = Math.Max(1, (height - borderWidth * 2) / BlockSize);
            var carriers = DctCarrierBasis.CarrierPositions;
            var cos = DctCarrierBasis.CosTable;

            int packetBitIndex = 0;
            int totalBits = packet.Length * 8;

            for (int blockY = 0; blockY < blocksY && packetBitIndex < totalBits; blockY++)
            {
                for (int blockX = 0; blockX < blocksX && packetBitIndex < totalBits; blockX++)
                {
                    int bx = borderWidth + blockX * BlockSize;
                    int by = borderWidth + blockY * BlockSize;

                    for (int ci = 0; ci < carriers.Length && packetBitIndex < totalBits; ci++)
                    {
                        var (u, v) = carriers[ci];
                        double sum = 0;

                        for (int py = 0; py < BlockSize; py++)
                        {
                            for (int px = 0; px < BlockSize; px++)
                            {
                                int x = bx + px;
                                int y = by + py;
                                if (x >= width || y >= height) continue;
                                int idx = y * effectiveRowBytes + x * bytesPerPixel;
                                if (idx + 2 >= frameBytes) continue;

                                // luminance average (R=G=B for Phase 3 frames; tolerant of drift)
                                double luma = (frame[idx] + frame[idx + 1] + frame[idx + 2]) / 3.0;
                                sum += luma * cos[px, u] * cos[py, v];
                            }
                        }

                        // Coefficient sign encodes the bit; DC (mean) has zero net contribution
                        // to AC coefficients by orthogonality, so no bias correction is needed.
                        double coeff = DctCarrierBasis.C(u) * DctCarrierBasis.C(v) / 4.0 * sum;
                        int bit = coeff > 0 ? 1 : 0;

                        int bytePos = packetBitIndex / 8;
                        int bitPos = 7 - (packetBitIndex % 8);
                        if (bytePos < packet.Length)
                            packet[bytePos] = (byte)(packet[bytePos] | (bit << bitPos));

                        packetBitIndex++;
                    }
                }
            }
        }

        public void DecodeMemory(ReadOnlyMemory<byte> frame, int width, int height, int macroblockSize, int rowBytes, int frameBytes, Memory<byte> packet, int borderWidth = 0)
        {
            packet.Span.Clear();

            bool isRgba = frame.Length == width * height * 4;
            int bytesPerPixel = isRgba ? 4 : 3;
            int effectiveRowBytes = isRgba ? width * 4 : rowBytes;

            int blocksX = Math.Max(1, (width - borderWidth * 2) / BlockSize);
            int blocksY = Math.Max(1, (height - borderWidth * 2) / BlockSize);
            var carriers = DctCarrierBasis.CarrierPositions;
            var cos = DctCarrierBasis.CosTable;
            int totalPacketBytes = Math.Min(packet.Length, blocksX * blocksY);

            InnerLoopParallelism.ForEachRow(blocksY, InnerDegreeOfParallelism, blockY =>
            {
                for (int blockX = 0; blockX < blocksX; blockX++)
                {
                    int bytePos = (blockY * blocksX) + blockX;
                    if (bytePos >= totalPacketBytes) continue;

                    int bx = borderWidth + blockX * BlockSize;
                    int by = borderWidth + blockY * BlockSize;
                    byte decodedByte = 0;

                    for (int ci = 0; ci < carriers.Length; ci++)
                    {
                        var (u, v) = carriers[ci];
                        double sum = 0;

                        for (int py = 0; py < BlockSize; py++)
                        {
                            for (int px = 0; px < BlockSize; px++)
                            {
                                int x = bx + px;
                                int y = by + py;
                                if (x >= width || y >= height) continue;
                                int idx = y * effectiveRowBytes + x * bytesPerPixel;
                                if (idx + 2 >= frameBytes) continue;

                                var frameSpan = frame.Span;
                                double luma = (frameSpan[idx] + frameSpan[idx + 1] + frameSpan[idx + 2]) / 3.0;
                                sum += luma * cos[px, u] * cos[py, v];
                            }
                        }

                        double coeff = DctCarrierBasis.C(u) * DctCarrierBasis.C(v) / 4.0 * sum;
                        int bit = coeff > 0 ? 1 : 0;
                        int bitPos = 7 - (ci % 8);
                        decodedByte = (byte)(decodedByte | (bit << bitPos));
                    }

                    packet.Span[bytePos] = decodedByte;
                }
            });
        }
    }
}
