using System;
using System.IO;
using System.Threading.Tasks;
using YTAHD.Cli.Modulation;
using YTAHD.Cli.Infrastructure;

namespace YTAHD.Cli.Core
{
    public class DecoderEngine
    {
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

            int rowBytes = width * 3;
            int frameBytes = rowBytes * height;

            var bits = new System.Collections.Generic.List<int>(expectedOutputBytes * 8);

            byte[] frameBuf = new byte[frameBytes];
            int frameOrdinal = 0;

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

                // Encoder repeats each payload frame exactly 3 times. Keep the first and skip the next two.
                if ((frameOrdinal % 3) != 0)
                {
                    frameOrdinal++;
                    continue;
                }

                // sample macroblocks
                for (int by = 0; by < blocksY; by++)
                {
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        int sampleX = bx * macroblockSize + macroblockSize / 2;
                        int sampleY = by * macroblockSize + macroblockSize / 2;
                        int idx = sampleY * rowBytes + sampleX * 3; // R channel
                        if (idx < 0 || idx + 2 >= frameBytes) { bits.Add(0); continue; }
                        byte r = frameBuf[idx];
                        bits.Add(r > 128 ? 1 : 0);
                    }
                }

                frameOrdinal++;

                if (bits.Count >= expectedOutputBytes * 8) break;
            }

            // assemble bytes MSB-first
            var outBuf = new byte[expectedOutputBytes];
            for (int i = 0; i < expectedOutputBytes; i++)
            {
                byte b = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    int bitIdx = i * 8 + bit;
                    int v = (bitIdx < bits.Count) ? bits[bitIdx] : 0;
                    b = (byte)((b << 1) | (v & 1));
                }
                outBuf[i] = b;
            }

            await File.WriteAllBytesAsync(outputFile, outBuf);
        }
    }
}
