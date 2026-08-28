using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using YTAHD.Cli.Modulation;
using YTAHD.Cli.Infrastructure;

namespace YTAHD.Cli.Core
{
    public class DecoderEngine
    {
        private const int FrameMagic = 0x5954; // 'YT'
        private const byte FrameVersion = 1;
        private const int HeaderBytes = 2 + 1 + 4 + 2 + 32;

        private readonly IModulator _modulator;
        private readonly YTAHD.Cli.Infrastructure.IFFmpegWrapper _ffmpeg;

        public DecoderEngine(IModulator modulator, YTAHD.Cli.Infrastructure.IFFmpegWrapper ffmpeg)
        {
            _modulator = modulator ?? throw new ArgumentNullException(nameof(modulator));
            _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
        }

        public async Task VerifyAsync()
        {
            if (!await _ffmpeg.IsAvailableAsync())
                throw new InvalidOperationException("ffmpeg not found in PATH or not runnable");
        }

        public async Task DecodeAsync(string inputVideo, string outputFile)
        {
            if (!File.Exists(inputVideo))
                throw new FileNotFoundException("Input video not found", inputVideo);

            // Placeholder: spawn ffmpeg to extract raw frames and decode using _modulator
            await Task.Run(() => File.WriteAllText(outputFile, "YTAHD-DECODE-PLACEHOLDER"));
        }

        /// <summary>
        /// Decode a raw RGB24 stream produced by the encoder into the original payload bytes.
        /// This helper is intended for tests that use a fake ffmpeg process which exposes raw RGB24 frames.
        /// </summary>
        public async Task DecodeFromRgbStreamAsync(Stream rgbStream, int width, int height, int macroblockSize, int expectedOutputBytes, string outputFile)
        {
            if (rgbStream == null) throw new ArgumentNullException(nameof(rgbStream));
            if (!rgbStream.CanRead) throw new ArgumentException("Stream is not readable", nameof(rgbStream));

            int blocksX = width / macroblockSize;
            int blocksY = height / macroblockSize;
            int bitsPerFrame = blocksX * blocksY;
            int headerBits = HeaderBytes * 8;
            int payloadBitsPerFrame = bitsPerFrame - headerBits;
            if (payloadBitsPerFrame < 8)
                throw new InvalidOperationException("Frame capacity too small for metadata header and payload.");
            int payloadBytesPerFrame = payloadBitsPerFrame / 8;

            int rowBytes = width * 3;
            int frameBytes = rowBytes * height;

            var orderedPayload = new SortedDictionary<int, byte[]>();

            const int repeatedFrameCount = 3;
            byte[] frameBuf = new byte[frameBytes];
            byte[] lastFrame = Array.Empty<byte>();
            bool hasLastFrame = false;
            int lastRunLength = 0;

            void DecodePayloadFrame(ReadOnlySpan<byte> frame)
            {
                int framePacketBytes = bitsPerFrame / 8;
                var packet = new byte[framePacketBytes];

                for (int by = 0; by < blocksY; by++)
                {
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        int frameBitIndex = by * blocksX + bx;
                        if (frameBitIndex >= framePacketBytes * 8)
                        {
                            continue;
                        }

                        int sampleX = bx * macroblockSize + macroblockSize / 2;
                        int sampleY = by * macroblockSize + macroblockSize / 2;
                        int idx = sampleY * rowBytes + sampleX * 3; // R channel
                        int bitValue = 0;
                        if (idx >= 0 && idx + 2 < frameBytes)
                        {
                            byte r = frame[idx];
                            bitValue = r > 128 ? 1 : 0;
                        }

                        if (bitValue == 1)
                        {
                            int byteIdx = frameBitIndex / 8;
                            int bitInByte = 7 - (frameBitIndex % 8);
                            packet[byteIdx] |= (byte)(1 << bitInByte);
                        }
                    }
                }

                if (packet.Length < HeaderBytes)
                {
                    return;
                }

                int magic = (packet[0] << 8) | packet[1];
                byte version = packet[2];
                if (magic != FrameMagic || version != FrameVersion)
                {
                    return;
                }

                int frameIndex = (packet[3] << 24) | (packet[4] << 16) | (packet[5] << 8) | packet[6];
                int payloadLength = (packet[7] << 8) | packet[8];
                if (frameIndex < 0 || payloadLength < 0)
                {
                    return;
                }

                int clampedLen = Math.Min(payloadLength, payloadBytesPerFrame);
                int availablePayload = Math.Max(0, packet.Length - HeaderBytes);
                clampedLen = Math.Min(clampedLen, availablePayload);

                var payload = new byte[clampedLen];
                if (clampedLen > 0)
                {
                    Buffer.BlockCopy(packet, HeaderBytes, payload, 0, clampedLen);
                }

                orderedPayload[frameIndex] = payload;
            }

            void FlushRun()
            {
                if (!hasLastFrame || lastRunLength <= 0)
                {
                    return;
                }

                // Expand a run of duplicated frames back into payload-frame count.
                // This preserves legitimate adjacent identical payload frames (e.g. 6 repeated frames => 2 payload frames).
                int payloadCopies = Math.Max(1, (lastRunLength + (repeatedFrameCount / 2)) / repeatedFrameCount);
                for (int i = 0; i < payloadCopies; i++)
                {
                    DecodePayloadFrame(lastFrame);
                }
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

                if (read < frameBytes) break; // end

                if (!hasLastFrame)
                {
                    lastFrame = new byte[frameBytes];
                    Buffer.BlockCopy(frameBuf, 0, lastFrame, 0, frameBytes);
                    hasLastFrame = true;
                    lastRunLength = 1;
                    continue;
                }

                if (frameBuf.AsSpan(0, frameBytes).SequenceEqual(lastFrame.AsSpan(0, frameBytes)))
                {
                    lastRunLength++;
                    continue;
                }

                FlushRun();
                Buffer.BlockCopy(frameBuf, 0, lastFrame, 0, frameBytes);
                lastRunLength = 1;

                int accumulated = 0;
                foreach (var kv in orderedPayload)
                {
                    accumulated += kv.Value.Length;
                    if (accumulated >= expectedOutputBytes)
                    {
                        break;
                    }
                }
                if (accumulated >= expectedOutputBytes) break;
            }

            FlushRun();

            var outBuf = new byte[expectedOutputBytes];
            int written = 0;
            foreach (var kv in orderedPayload)
            {
                if (written >= expectedOutputBytes)
                {
                    break;
                }

                var payload = kv.Value;
                int toCopy = Math.Min(payload.Length, expectedOutputBytes - written);
                if (toCopy > 0)
                {
                    Buffer.BlockCopy(payload, 0, outBuf, written, toCopy);
                    written += toCopy;
                }
            }

            await File.WriteAllBytesAsync(outputFile, outBuf);
        }
    }
}
