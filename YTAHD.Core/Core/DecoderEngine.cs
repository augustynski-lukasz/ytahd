using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using YTAHD.Core.Modulation;
using YTAHD.Core.Infrastructure;

namespace YTAHD.Core.Core
{
    public class DecoderEngine
    {
        private const int HeaderBytes = FramePacket.HeaderBytes;
        private const byte FrameTypeData = FramePacket.FrameTypeData;
        private const byte FrameTypeParity = FramePacket.FrameTypeParity;

        private readonly IModulator _modulator;
        private readonly YTAHD.Core.Infrastructure.IFFmpegWrapper _ffmpeg;
        private readonly int _macroblockSize;
        private readonly int _width;
        private readonly int _height;
        private readonly int _fps;

        public DecoderEngine(IModulator modulator, YTAHD.Core.Infrastructure.IFFmpegWrapper ffmpeg, int macroblockSize = 16, int width = 3840, int height = 2160, int fps = 60)
        {
            _modulator = modulator ?? throw new ArgumentNullException(nameof(modulator));
            _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
            _macroblockSize = macroblockSize;
            _width = width;
            _height = height;
            _fps = fps;
        }

        public static int GetPayloadBytesPerFrame(int width, int height, int macroblockSize, int headerBytes)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (macroblockSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(macroblockSize));
            if (headerBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(headerBytes));

            int blocksX = width / macroblockSize;
            int blocksY = height / macroblockSize;
            int bitsPerFrame = blocksX * blocksY;
            int payloadBitsPerFrame = bitsPerFrame - (headerBytes * 8);
            return payloadBitsPerFrame >= 8 ? payloadBitsPerFrame / 8 : 0;
        }

        public static bool TryParseFramePacket(byte[] packet, out byte frameType, out int frameIndex, out int totalDataFrames, out int groupStart, out int groupCount, out int payloadLength, out byte[] payload)
        {
            return FramePacket.TryParse(packet, out frameType, out frameIndex, out totalDataFrames, out groupStart, out groupCount, out payloadLength, out payload);
        }

        private static int GetDuplicateFrameCount(int lastRunLength, int repeatedFrameCount)
        {
            if (repeatedFrameCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(repeatedFrameCount));

            return Math.Max(1, (lastRunLength + (repeatedFrameCount / 2)) / repeatedFrameCount);
        }

        private static void FlushDuplicateRun(
            byte[] lastFrame,
            ref bool hasLastFrame,
            ref int lastRunLength,
            int repeatedFrameCount,
            Func<byte[], bool> decodePayloadFrame)
        {
            if (!hasLastFrame || lastRunLength <= 0)
            {
                return;
            }

            int payloadCopies = GetDuplicateFrameCount(lastRunLength, repeatedFrameCount);
            for (int i = 0; i < payloadCopies; i++)
            {
                decodePayloadFrame(lastFrame);
            }
        }

        private static bool TryReadDecodedPacket(
            ReadOnlySpan<byte> frame,
            int width,
            int height,
            int macroblockSize,
            int rowBytes,
            int frameBytes,
            int bitsPerFrame,
            out byte[] packet)
        {
            int framePacketBytes = bitsPerFrame / 8;
            packet = new byte[framePacketBytes];

            var strategy = FrameBitDecoderFactory.CreateForModulator(new BinaryGridModulator());
            strategy.Decode(frame, width, height, macroblockSize, rowBytes, frameBytes, packet);

            if (packet.Length < HeaderBytes)
            {
                return false;
            }

            return TryParseFramePacket(packet, out _, out _, out _, out _, out _, out _, out _);
        }

        public async Task VerifyAsync()
        {
            if (!await _ffmpeg.IsAvailableAsync())
                throw new InvalidOperationException("ffmpeg not found in PATH or not runnable");
        }

        private string ResolveFfprobeExecutablePath()
        {
            if (string.IsNullOrWhiteSpace(_ffmpeg.ExecutablePath) || _ffmpeg.ExecutablePath.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                return "ffprobe";
            }

            var directory = Path.GetDirectoryName(_ffmpeg.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var ffprobePath = Path.Combine(directory, "ffprobe.exe");
                if (File.Exists(ffprobePath))
                {
                    return ffprobePath;
                }

                var ffprobeAltPath = Path.Combine(directory, "ffprobe");
                if (File.Exists(ffprobeAltPath))
                {
                    return ffprobeAltPath;
                }
            }

            return "ffprobe";
        }

        private async Task<(int Width, int Height, int Fps)> GetVideoMetadataAsync(string inputVideo)
        {
            var ffprobePath = ResolveFfprobeExecutablePath();
            var args = $"-v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate -of csv=p=0 \"{inputVideo}\"";
            var psi = new ProcessStartInfo(ffprobePath, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return (_width, _height, _fps);
                }

                var output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    return (_width, _height, _fps);
                }

                var parts = output.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 3)
                {
                    return (_width, _height, _fps);
                }

                int width = int.TryParse(parts[0], out var parsedWidth) ? parsedWidth : _width;
                int height = int.TryParse(parts[1], out var parsedHeight) ? parsedHeight : _height;
                string fpsText = parts[2];
                double fpsValue = _fps;
                if (fpsText.Contains('/'))
                {
                    var numeratorDenominator = fpsText.Split('/');
                    if (numeratorDenominator.Length == 2 && double.TryParse(numeratorDenominator[0], out var numerator) && double.TryParse(numeratorDenominator[1], out var denominator) && denominator > 0)
                    {
                        fpsValue = numerator / denominator;
                    }
                }
                else if (double.TryParse(fpsText, out var parsedFps))
                {
                    fpsValue = parsedFps;
                }

                return (width, height, (int)Math.Round(fpsValue));
            }
            catch
            {
                return (_width, _height, _fps);
            }
        }

        public async Task DecodeAsync(string inputVideo, string outputFile)
        {
            if (!File.Exists(inputVideo))
                throw new FileNotFoundException("Input video not found", inputVideo);

            var (width, height, fps) = await GetVideoMetadataAsync(inputVideo);
            var ffmpegPath = _ffmpeg.ExecutablePath;
            if (string.IsNullOrWhiteSpace(ffmpegPath) || ffmpegPath.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                ffmpegPath = "ffmpeg";
            }

            var args = $"-hide_banner -loglevel error -i \"{inputVideo}\" -f rawvideo -pix_fmt rgb24 -s {width}x{height} -r {fps} -";
            var psi = new ProcessStartInfo(ffmpegPath, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg for decode.");
            await DecodeFromRgbStreamAsync(process.StandardOutput.BaseStream, width, height, _macroblockSize, outputFile);
            await process.WaitForExitAsync();
        }

        /// <summary>
        /// Decode a raw RGB24 stream produced by the encoder into the original payload bytes.
        /// This helper is intended for tests that use a fake ffmpeg process which exposes raw RGB24 frames.
        /// </summary>
        public async Task DecodeFromRgbStreamAsync(Stream rgbStream, int width, int height, int macroblockSize, int expectedOutputBytes, string outputFile)
        {
            if (rgbStream == null) throw new ArgumentNullException(nameof(rgbStream));
            if (!rgbStream.CanRead) throw new ArgumentException("Stream is not readable", nameof(rgbStream));

            int payloadBytesPerFrame = GetPayloadBytesPerFrame(width, height, macroblockSize, HeaderBytes);
            if (payloadBytesPerFrame <= 0)
                throw new InvalidOperationException("Frame capacity too small for metadata header and payload.");

            int rowBytes = width * 3;
            int frameBytes = rowBytes * height;
            int blocksX = width / macroblockSize;
            int blocksY = height / macroblockSize;
            int bitsPerFrame = blocksX * blocksY;

            var accumulator = new DecodedFrameAccumulator();
            const int repeatedFrameCount = 3;
            byte[] frameBuf = new byte[frameBytes];
            byte[] lastFrame = Array.Empty<byte>();
            byte[] lastLogicalSignature = Array.Empty<byte>();
            bool hasLastFrame = false;
            int lastRunLength = 0;

            byte[] CreateLogicalSignature(ReadOnlySpan<byte> frame)
            {
                var packet = new byte[bitsPerFrame / 8];
                var strategy = FrameBitDecoderFactory.CreateForModulator(new BinaryGridModulator());
                strategy.Decode(frame, width, height, macroblockSize, rowBytes, frameBytes, packet);
                return packet;
            }

            bool DecodePayloadFrame(ReadOnlySpan<byte> frame)
            {
                return accumulator.TryAddDecodedFrame(frame, width, height, macroblockSize, rowBytes, frameBytes, payloadBytesPerFrame, bitsPerFrame);
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

                if (!TryReadDecodedPacket(frameBuf, width, height, macroblockSize, rowBytes, frameBytes, bitsPerFrame, out _))
                {
                    throw new InvalidDataException("Decoded payload is incomplete. Invalid packet hash detected in the stream.");
                }

                var currentLogicalSignature = CreateLogicalSignature(frameBuf);

                if (!hasLastFrame)
                {
                    lastFrame = new byte[frameBytes];
                    Buffer.BlockCopy(frameBuf, 0, lastFrame, 0, frameBytes);
                    lastLogicalSignature = currentLogicalSignature;
                    hasLastFrame = true;
                    lastRunLength = 1;
                    continue;
                }

                if (currentLogicalSignature.AsSpan().SequenceEqual(lastLogicalSignature.AsSpan()))
                {
                    lastRunLength++;
                    continue;
                }

                FlushDuplicateRun(lastFrame, ref hasLastFrame, ref lastRunLength, repeatedFrameCount, frame => DecodePayloadFrame(frame));
                if (accumulator.SawInvalidPacket)
                {
                    throw new InvalidDataException("Decoded payload is incomplete. Invalid packet hash detected in the stream.");
                }

                Buffer.BlockCopy(frameBuf, 0, lastFrame, 0, frameBytes);
                lastLogicalSignature = currentLogicalSignature;
                lastRunLength = 1;

                if (expectedOutputBytes > 0)
                {
                    int accumulated = 0;
                    foreach (var kv in accumulator.OrderedPayload)
                    {
                        accumulated += kv.Value.Length;
                        if (accumulated >= expectedOutputBytes)
                        {
                            break;
                        }
                    }

                    if (accumulated >= expectedOutputBytes)
                    {
                        break;
                    }
                }
            }

            FlushDuplicateRun(lastFrame, ref hasLastFrame, ref lastRunLength, repeatedFrameCount, frame => DecodePayloadFrame(frame));

            if (accumulator.SawInvalidPacket)
            {
                throw new InvalidDataException("Decoded payload is incomplete. Invalid packet hash detected in the stream.");
            }

            if (accumulator.TotalDataFrames < 0)
            {
                throw new InvalidDataException("Decoded payload is incomplete. No valid frames were decoded.");
            }

            accumulator.RecoverMissingPayloadFrames(accumulator.TotalDataFrames, payloadBytesPerFrame, expectedOutputBytes);
            var outBuf = accumulator.AssembleOutput(expectedOutputBytes);
            await File.WriteAllBytesAsync(outputFile, outBuf);
        }

        public async Task DecodeFromRgbStreamAsync(Stream rgbStream, int width, int height, int macroblockSize, string outputFile)
        {
            if (rgbStream == null) throw new ArgumentNullException(nameof(rgbStream));
            if (!rgbStream.CanRead) throw new ArgumentException("Stream is not readable", nameof(rgbStream));

            int payloadBytesPerFrame = GetPayloadBytesPerFrame(width, height, macroblockSize, HeaderBytes);
            if (payloadBytesPerFrame <= 0)
                throw new InvalidOperationException("Frame capacity too small for metadata header and payload.");

            int rowBytes = width * 3;
            int frameBytes = rowBytes * height;
            int blocksX = width / macroblockSize;
            int blocksY = height / macroblockSize;
            int bitsPerFrame = blocksX * blocksY;

            var accumulator = new DecodedFrameAccumulator();
            const int repeatedFrameCount = 3;
            byte[] frameBuf = new byte[frameBytes];
            byte[] lastFrame = Array.Empty<byte>();
            byte[] lastLogicalSignature = Array.Empty<byte>();
            bool hasLastFrame = false;
            int lastRunLength = 0;

            byte[] CreateLogicalSignature(ReadOnlySpan<byte> frame)
            {
                var packet = new byte[bitsPerFrame / 8];
                var strategy = FrameBitDecoderFactory.CreateForModulator(new BinaryGridModulator());
                strategy.Decode(frame, width, height, macroblockSize, rowBytes, frameBytes, packet);
                return packet;
            }

            bool DecodePayloadFrame(ReadOnlySpan<byte> frame)
            {
                return accumulator.TryAddDecodedFrame(frame, width, height, macroblockSize, rowBytes, frameBytes, payloadBytesPerFrame, bitsPerFrame);
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

                if (!TryReadDecodedPacket(frameBuf, width, height, macroblockSize, rowBytes, frameBytes, bitsPerFrame, out _))
                {
                    throw new InvalidDataException("Decoded payload is incomplete. Invalid packet hash detected in the stream.");
                }

                var currentLogicalSignature = CreateLogicalSignature(frameBuf);

                if (!hasLastFrame)
                {
                    lastFrame = new byte[frameBytes];
                    Buffer.BlockCopy(frameBuf, 0, lastFrame, 0, frameBytes);
                    lastLogicalSignature = currentLogicalSignature;
                    hasLastFrame = true;
                    lastRunLength = 1;
                    continue;
                }

                if (currentLogicalSignature.AsSpan().SequenceEqual(lastLogicalSignature.AsSpan()))
                {
                    lastRunLength++;
                    continue;
                }

                FlushDuplicateRun(lastFrame, ref hasLastFrame, ref lastRunLength, repeatedFrameCount, frame => DecodePayloadFrame(frame));
                if (accumulator.SawInvalidPacket)
                {
                    throw new InvalidDataException("Decoded payload is incomplete. Invalid packet hash detected in the stream.");
                }

                Buffer.BlockCopy(frameBuf, 0, lastFrame, 0, frameBytes);
                lastLogicalSignature = currentLogicalSignature;
                lastRunLength = 1;
            }

            FlushDuplicateRun(lastFrame, ref hasLastFrame, ref lastRunLength, repeatedFrameCount, frame => DecodePayloadFrame(frame));

            if (accumulator.SawInvalidPacket)
            {
                throw new InvalidDataException("Decoded payload is incomplete. Invalid packet hash detected in the stream.");
            }

            if (accumulator.TotalDataFrames < 0)
            {
                throw new InvalidDataException("Decoded payload is incomplete. No valid frames were decoded.");
            }

            int expectedLength = 0;
            foreach (var payload in accumulator.OrderedPayload.Values)
            {
                expectedLength += payload.Length;
            }

            accumulator.RecoverMissingPayloadFrames(accumulator.TotalDataFrames, payloadBytesPerFrame, expectedLength);
            var outBuf = accumulator.AssembleOutput(expectedLength);
            await File.WriteAllBytesAsync(outputFile, outBuf);
        }
    }
}
