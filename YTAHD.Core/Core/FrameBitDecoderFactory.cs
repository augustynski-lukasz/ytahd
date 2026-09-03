using System;
using System.Collections.Generic;
using YTAHD.Core.Modulation;

namespace YTAHD.Core.Core
{
    public interface IFrameBitDecoder
    {
        void Decode(ReadOnlySpan<byte> frame, int width, int height, int macroblockSize, int rowBytes, int frameBytes, Span<byte> packet, int borderWidth = 0);

        /// <summary>
        /// True if this frame is a "no data" marker that the pipeline should skip rather than
        /// treat as an invalid/corrupted packet. Modulators without a separator concept never
        /// produce one, so the default is always false.
        /// </summary>
        bool IsCanonicalFrame(ReadOnlySpan<byte> frame, int width, int height, int borderWidth) => false;
    }

    public static class FrameBitDecoderFactory
    {
        public static IFrameBitDecoder CreateForModulator(IModulator modulator)
        {
            if (modulator == null)
            {
                throw new ArgumentNullException(nameof(modulator));
            }

            if (modulator is BinaryGridModulator)
            {
                return new BinaryGridFrameBitDecoder();
            }

            if (modulator is PseudoQamModulator)
            {
                return new PseudoQamFrameBitDecoder();
            }

            if (modulator is DctModulator)
            {
                return new DctFrameBitDecoder();
            }

            if (modulator is MotionVectorModulator)
            {
                return new MotionFrameBitDecoder();
            }

            throw new NotSupportedException($"No frame bit decoder available for modulator '{modulator.GetType().Name}'.");
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

                if (candidate[2] == FramePacket.FrameVersion)
                {
                    score += 256;
                }

                if (candidate[3] == FramePacket.FrameTypeData || candidate[3] == FramePacket.FrameTypeParity)
                {
                    score += 128;
                }

                int payloadLength = (candidate[17] << 8) | candidate[18];
                if (payloadLength >= 0 && payloadLength <= candidate.Length - FramePacket.HeaderBytes)
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

    public sealed class DctFrameBitDecoder : IFrameBitDecoder
    {
        private const int BlockSize = DctCarrierBasis.BlockSize; // 8

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
    }
}
