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

            int blocksX = Math.Max(1, (width - (borderWidth * 2)) / blockSize);
            int blocksY = Math.Max(1, (height - (borderWidth * 2)) / blockSize);

            var candidateStreams = new List<List<byte>>
            {
                new List<byte>(),
                new List<byte>(),
                new List<byte>(),
                new List<byte>(),
                new List<byte>(),
                new List<byte>(),
                new List<byte>(),
                new List<byte>(),
                new List<byte>()
            };

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
                            int luminance = (int)Math.Round(0.299d * r + 0.587d * g + 0.114d * b);
                            int average = (r + g + b) / 3;
                            int diffRG = (int)r - g;
                            int diffGB = (int)g - b;
                            int diffBR = (int)b - r;

                            candidateStreams[0].Add(r);
                            candidateStreams[1].Add(g);
                            candidateStreams[2].Add(b);
                            candidateStreams[3].Add((byte)Math.Clamp(average, 0, 255));
                            candidateStreams[4].Add((byte)Math.Clamp(luminance, 0, 255));
                            candidateStreams[5].Add((byte)Math.Clamp(diffRG + 128, 0, 255));
                            candidateStreams[6].Add((byte)Math.Clamp(diffGB + 128, 0, 255));
                            candidateStreams[7].Add((byte)Math.Clamp(diffBR + 128, 0, 255));
                            candidateStreams[8].Add((byte)Math.Clamp((r + g + b) / 3 + (luminance - average), 0, 255));
                        }
                    }
                }
            }

            static bool HasPacketHeader(ReadOnlySpan<byte> sample)
            {
                if (sample.Length < FramePacket.HeaderBytes)
                {
                    return false;
                }

                bool magicClose = Math.Abs(sample[0] - 0x59) <= 16 && Math.Abs(sample[1] - 0x54) <= 16;
                bool versionClose = Math.Abs(sample[2] - FramePacket.FrameVersion) <= 4;
                bool typeValid = sample[3] == FramePacket.FrameTypeData || sample[3] == FramePacket.FrameTypeParity;
                return magicClose && versionClose && typeValid;
            }

            byte[] bestCandidate = new byte[packet.Length];
            int bestScore = int.MinValue;
            bool hasValidHeaderCandidate = false;

            foreach (var rawSamples in candidateStreams)
            {
                if (rawSamples.Count == 0)
                {
                    continue;
                }

                int maxCandidateBytes = Math.Min(rawSamples.Count, packet.Length);
                int maxStartOffset = Math.Max(0, rawSamples.Count - FramePacket.HeaderBytes);
                for (int startOffset = 0; startOffset <= maxStartOffset; startOffset++)
                {
                    for (int delta = -128; delta <= 128; delta += 2)
                    {
                        int sampleLength = Math.Min(maxCandidateBytes, rawSamples.Count - startOffset);
                        if (sampleLength < FramePacket.HeaderBytes)
                        {
                            continue;
                        }

                        var headerCandidate = new byte[FramePacket.HeaderBytes];
                        for (int i = 0; i < headerCandidate.Length; i++)
                        {
                            int adjusted = rawSamples[startOffset + i] - delta;
                            headerCandidate[i] = (byte)Math.Clamp(adjusted, 0, 255);
                        }

                        if (!HasPacketHeader(headerCandidate))
                        {
                            continue;
                        }

                        int payloadLength = ((headerCandidate[17] << 8) | headerCandidate[18]);
                        int declaredLength = FramePacket.HeaderBytes + payloadLength;
                        int candidateLength = Math.Min(maxCandidateBytes, Math.Max(declaredLength, FramePacket.HeaderBytes));
                        if (startOffset + candidateLength > rawSamples.Count)
                        {
                            candidateLength = rawSamples.Count - startOffset;
                        }

                        if (candidateLength < FramePacket.HeaderBytes)
                        {
                            continue;
                        }

                        var candidate = new byte[candidateLength];
                        for (int i = 0; i < candidate.Length; i++)
                        {
                            int adjusted = rawSamples[startOffset + i] - delta;
                            candidate[i] = (byte)Math.Clamp(adjusted, 0, 255);
                        }

                        bool isAlignedStart = startOffset == 0;
                        if (FramePacketCodec.TryDecode(candidate, out _, out _, out _, out _, out _, out _, out _))
                        {
                            if (isAlignedStart || candidate[0] == 0x59 && candidate[1] == 0x54)
                            {
                                candidate.AsSpan().CopyTo(packet);
                                return;
                            }
                        }

                        bool hasHeader = HasPacketHeader(candidate);
                        int score = PacketQualityScorer.Score(candidate) + (Math.Abs(delta) < 8 ? 128 : 0) + (isAlignedStart ? 256 : 0) + (startOffset <= 4 ? 32 : 0);
                        if (hasHeader && score > bestScore)
                        {
                            bestScore = score;
                            hasValidHeaderCandidate = true;
                            candidate.AsSpan().CopyTo(bestCandidate);
                        }
                    }
                }
            }

            if (hasValidHeaderCandidate)
            {
                bestCandidate.AsSpan().CopyTo(packet);
                return;
            }

            var fallback = new byte[packet.Length];
            int fallbackLength = Math.Min(candidateStreams[0].Count, packet.Length);
            for (int i = 0; i < fallbackLength; i++)
            {
                fallback[i] = candidateStreams[0][i];
            }
            fallback.AsSpan().CopyTo(packet);
        }
    }
}
