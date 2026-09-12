using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using YTAHD.Core.Application;
using YTAHD.Core.Audio;
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
        private readonly bool _useDurabilityMatrix;
        private readonly DurabilityMatrixOptions? _durabilityMatrixOptions;
        private readonly bool _useAudioClock;
        private readonly IProgress<DecodeProgress>? _progress;
        /// <summary>Resolved worker count from <see cref="ParallelismPolicy"/>; <c>1</c> selects the serial path.</summary>
        private readonly int _maxDegreeOfParallelism;

        public DecodeMetrics LastDecodeMetrics { get; private set; } = new();

        public DecoderEngine(IModulator modulator, YTAHD.Core.Infrastructure.IFFmpegWrapper ffmpeg, int macroblockSize = 16, int width = 3840, int height = 2160, int fps = 60)
        {
            _modulator = NormalizeModulator(modulator, macroblockSize);
            _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
            _macroblockSize = macroblockSize;
            _width = width;
            _height = height;
            _fps = fps;
            _useDurabilityMatrix = false;
            _durabilityMatrixOptions = null;
            _useAudioClock = false;
            _progress = null;
            _maxDegreeOfParallelism = ParallelismPolicy.Resolve(0);
        }

        public DecoderEngine(IModulator modulator, YTAHD.Core.Infrastructure.IFFmpegWrapper ffmpeg, VideoCodecOptions options)
            : this(modulator, ffmpeg, options.MacroblockSize, options.Width, options.Height, options.Fps)
        {
            _useDurabilityMatrix = options.UseDurabilityMatrix;
            _durabilityMatrixOptions = options.DurabilityMatrixOptions ?? new DurabilityMatrixOptions();
            _useAudioClock = options.UseAudioClock;
            _progress = options is DecodeOptions decodeOptions ? decodeOptions.Progress : null;
            _maxDegreeOfParallelism = ParallelismPolicy.Resolve(options.MaxDegreeOfParallelism);
        }

        private static IModulator NormalizeModulator(IModulator modulator, int macroblockSize)
        {
            if (modulator is BinaryGridModulator binary && (binary.MacroblockWidth != macroblockSize || binary.MacroblockHeight != macroblockSize))
            {
                return new BinaryGridModulator(macroblockSize, macroblockSize);
            }

            return modulator ?? throw new ArgumentNullException(nameof(modulator));
        }

        public static int GetPayloadBytesPerFrame(int width, int height, int macroblockSize, int headerBytes)
        {
            return FrameProtocolHelpers.GetPayloadBytesPerFrame(width, height, macroblockSize, headerBytes);
        }

        public static bool TryParseFramePacket(byte[] packet, out byte frameType, out int frameIndex, out int totalDataFrames, out int groupStart, out int groupCount, out int payloadLength, out byte[] payload)
        {
            return FrameProtocolHelpers.TryParseFramePacket(packet, out frameType, out frameIndex, out totalDataFrames, out groupStart, out groupCount, out payloadLength, out payload);
        }

        public static int GetPacketQualityScore(ReadOnlySpan<byte> packet)
        {
            return FrameProtocolHelpers.GetPacketQualityScore(packet);
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

            hasLastFrame = false;
            lastRunLength = 0;
        }

        internal static bool TryReadDecodedPacket(
            ReadOnlyMemory<byte> frame,
            int width,
            int height,
            int macroblockSize,
            int rowBytes,
            int frameBytes,
            int bitsPerFrame,
            IModulator modulator,
            out byte[] packet)
        {
            var geometry = new ModulatorGeometry(width, height, macroblockSize, HeaderBytes, BitsPerFrame: bitsPerFrame);
            int borderWidth = modulator.GetBorderWidth(geometry);
            geometry = geometry with { BorderWidth = borderWidth };
            int payloadBytesPerFrame = modulator.GetPayloadBytesPerFrame(geometry);
            int framePacketBytes = modulator.GetPacketBufferLength(geometry, payloadBytesPerFrame);
            packet = new byte[framePacketBytes];

            var strategy = FrameBitDecoderFactory.CreateForModulator(modulator ?? new BinaryGridModulator(macroblockSize, macroblockSize));
            strategy.DecodeMemory(frame, width, height, macroblockSize, rowBytes, frameBytes, packet, borderWidth);

            if (packet.Length < HeaderBytes)
            {
                return false;
            }

            return FramePacketCodec.TryDecodeWithTolerance(packet, out _, out _, out _, out _, out _, out _, out _) && PacketQualityScorer.IsFramePacketValid(packet);
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
                using var child = ChildProcessScope.Start(psi, "Failed to start ffprobe.");
                var proc = child.Process;

                var stderrDrain = ChildProcessPipes.DrainAsync(proc.StandardError);
                var output = await ChildProcessPipes.ReadToEndAsync(proc.StandardOutput);
                await proc.WaitForExitAsync();
                await stderrDrain;
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

        public async Task DecodeAsync(string inputVideo, string outputFile, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(inputVideo))
                throw new FileNotFoundException("Input video not found", inputVideo);

            DebugTrace.Log("DecoderEngine", $"Starting decode for '{inputVideo}' => '{outputFile}' width={_width} height={_height} fps={_fps} modulator={_modulator.GetType().Name} useDurability={_useDurabilityMatrix}");

            var (width, height, fps) = await GetVideoMetadataAsync(inputVideo);

            int? audioDatagramCount = null;
            if (_useAudioClock)
            {
                var pcm = await _ffmpeg.TryExtractAudioPcmAsync(inputVideo);
                if (pcm != null && pcm.Length > 0)
                {
                    var samples = GoertzelDetector.ToInt16Samples(pcm);
                    audioDatagramCount = GoertzelDetector.DetectDatagramBoundaries(samples, FskGenerator.SampleRate, fps).Length;
                    DebugTrace.Log("DecoderEngine", $"Audio clock detected {audioDatagramCount} datagram boundaries.");
                }
                else
                {
                    DebugTrace.Log("DecoderEngine", "Audio clock enabled but no audio track was found or extractable.");
                }
            }

            var ffmpegPath = _ffmpeg.ExecutablePath;
            if (string.IsNullOrWhiteSpace(ffmpegPath) || ffmpegPath.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                ffmpegPath = "ffmpeg";
            }

            int totalVideoFrames = await FFmpegProbe.GetVideoFrameCountAsync(inputVideo, _ffmpeg.ExecutablePath);
            var args = $"-hide_banner -loglevel error -i \"{inputVideo}\" -f rawvideo -pix_fmt rgb24 -s {width}x{height} -r {fps} -";
            var psi = new ProcessStartInfo(ffmpegPath, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            DebugTrace.Log("DecoderEngine", $"FFmpeg decode command: {ffmpegPath} {args}");
            using var child = ChildProcessScope.Start(psi, "Failed to start ffmpeg for decode.");
            var process = child.Process;

            // stderr must be consumed while stdout is being read; otherwise a chatty decode can fill
            // the stderr pipe buffer and block ffmpeg before it finishes writing rgb frames to stdout.
            var stderrDrain = ChildProcessPipes.DrainAsync(process.StandardError);

            // stdout is wrapped rather than read directly: a pipe read cannot complete on an IO completion
            // port, so awaiting the raw pipe parks a thread-pool thread for the whole decode and enough
            // concurrent decodes then starve the pool the pipeline itself runs on.
            using (var rgbStream = new ChildPipeStream(process.StandardOutput.BaseStream))
            {
                await DecodeFromRgbStreamAsync(rgbStream, width, height, _macroblockSize, outputFile, audioDatagramCount, totalVideoFrames, cancellationToken);
            }

            await process.WaitForExitAsync();
            await stderrDrain;
            DebugTrace.Log("DecoderEngine", $"FFmpeg decode exited with code {process.ExitCode}.");
        }

        /// <summary>
        /// Decode a raw RGB24 stream produced by the encoder into the original payload bytes.
        /// This helper is intended for tests that use a fake ffmpeg process which exposes raw RGB24 frames.
        /// </summary>
        public async Task DecodeFromRgbStreamAsync(Stream rgbStream, int width, int height, int macroblockSize, int expectedOutputBytes, string outputFile, int? audioDatagramCount = null, CancellationToken cancellationToken = default)
        {
            var orchestrator = new DecodeStreamOrchestrator(_modulator, width, height, macroblockSize, _useDurabilityMatrix, _durabilityMatrixOptions, _maxDegreeOfParallelism);
            var output = await orchestrator.ProcessAsync(rgbStream, expectedOutputBytes, audioDatagramCount, progress: _progress, cancellationToken: cancellationToken);
            LastDecodeMetrics = orchestrator.LastDecodeMetrics;
            await File.WriteAllBytesAsync(outputFile, output);
        }

        public async Task DecodeFromRgbStreamAsync(Stream rgbStream, int width, int height, int macroblockSize, string outputFile, int? audioDatagramCount = null, int totalVideoFrames = 0, CancellationToken cancellationToken = default)
        {
            var orchestrator = new DecodeStreamOrchestrator(_modulator, width, height, macroblockSize, _useDurabilityMatrix, _durabilityMatrixOptions, _maxDegreeOfParallelism);
            var output = await orchestrator.ProcessAsync(rgbStream, 0, audioDatagramCount, totalVideoFrames, _progress, cancellationToken);
            LastDecodeMetrics = orchestrator.LastDecodeMetrics;
            await File.WriteAllBytesAsync(outputFile, output);
        }
    }
}
