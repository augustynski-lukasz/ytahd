using System;
using System.Collections.Generic;
using YTAHD.Core.Modulation;

namespace YTAHD.Core.Core
{
    public interface IFrameBitDecoder
    {
        void Decode(ReadOnlySpan<byte> frame, int width, int height, int macroblockSize, int rowBytes, int frameBytes, Span<byte> packet, int borderWidth = 0);
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
        public void Decode(ReadOnlySpan<byte> frame, int width, int height, int macroblockSize, int rowBytes, int frameBytes, Span<byte> packet, int borderWidth = 0)
        {
            packet.Clear();

            const int blockSize = 8;
            const int lowFrequencySize = 4;
            bool isRgba = frame.Length == width * height * 4;
            int bytesPerPixel = isRgba ? 4 : 3;
            int effectiveRowBytes = isRgba ? width * 4 : rowBytes;

            var signalVariants = new[]
            {
                new List<int>(),
                new List<int>(),
                new List<int>(),
                new List<int>()
            };

            int blocksX = Math.Max(1, (width - (borderWidth * 2)) / blockSize);
            int blocksY = Math.Max(1, (height - (borderWidth * 2)) / blockSize);

            for (int blockY = 0; blockY < blocksY; blockY++)
            {
                for (int blockX = 0; blockX < blocksX; blockX++)
                {
                    int baseX = borderWidth + (blockX * blockSize);
                    int baseY = borderWidth + (blockY * blockSize);

                    for (int py = 0; py < lowFrequencySize; py++)
                    {
                        for (int px = 0; px < lowFrequencySize; px++)
                        {
                            int x = baseX + px;
                            int y = baseY + py;
                            if (x < 0 || y < 0 || x >= width || y >= height)
                            {
                                continue;
                            }

                            int idx = (y * effectiveRowBytes) + (x * bytesPerPixel);
                            if (idx + 2 >= frameBytes)
                            {
                                continue;
                            }

                            byte r = frame[idx];
                            byte g = frame[idx + 1];
                            byte b = frame[idx + 2];
                            signalVariants[0].Add(r);
                            signalVariants[1].Add(g);
                            signalVariants[2].Add(b);
                            signalVariants[3].Add((byte)Math.Clamp((r + g + b) / 3, 0, 255));
                        }
                    }
                }
            }

            int totalSamples = signalVariants[0].Count;
            if (totalSamples == 0)
            {
                return;
            }

            byte[] bestCandidate = new byte[packet.Length];
            int bestScore = int.MinValue;
            bool hasValidHeaderCandidate = false;

            foreach (int[] signal in signalVariants.Select(values => values.ToArray()))
            {
                if (signal.Length == 0)
                {
                    continue;
                }

                for (int startOffset = 0; startOffset <= Math.Max(0, signal.Length - FramePacket.HeaderBytes); startOffset++)
                {
                    int candidateBytes = Math.Min(packet.Length, signal.Length - startOffset);
                    if (candidateBytes < FramePacket.HeaderBytes)
                    {
                        continue;
                    }

                    for (int delta = -32; delta <= 32; delta += 4)
                    {
                        var candidate = new byte[candidateBytes];
                        for (int i = 0; i < candidateBytes; i++)
                        {
                            int adjusted = signal[startOffset + i] - delta;
                            candidate[i] = (byte)Math.Clamp(adjusted, 0, 255);
                        }

                        if (FramePacketCodec.TryDecodeWithTolerance(candidate, out _, out _, out _, out _, out _, out _, out _))
                        {
                            candidate.AsSpan().CopyTo(packet);
                            return;
                        }

                        if (candidate.Length >= FramePacket.HeaderBytes && FramePacketCodec.TryDecodeWithTolerance(candidate.AsSpan(0, FramePacket.HeaderBytes), out _, out _, out _, out _, out _, out _, out _))
                        {
                            int score = PacketQualityScorer.Score(candidate) + (Math.Abs(delta) < 8 ? 64 : 0) + (startOffset == 0 ? 128 : 0);
                            if (score > bestScore)
                            {
                                bestScore = score;
                                hasValidHeaderCandidate = true;
                                candidate.AsSpan().CopyTo(bestCandidate);
                            }
                        }
                    }
                }
            }

            if (hasValidHeaderCandidate)
            {
                bestCandidate.AsSpan().CopyTo(packet);
                return;
            }

            int fallbackLength = Math.Min(totalSamples, packet.Length);
            for (int i = 0; i < fallbackLength; i++)
            {
                packet[i] = (byte)Math.Clamp(signalVariants[3][i], 0, 255);
            }
        }
    }
}
