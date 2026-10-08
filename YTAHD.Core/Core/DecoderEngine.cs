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
        /// <summary>Decode-side hardware acceleration mode; <see cref="HardwareAcceleration.None"/> emits no flag (CR-20260913-04).</summary>
        private readonly HardwareAcceleration _hardwareAcceleration;
        /// <summary>Resolved worker count from <see cref="ParallelismPolicy"/>; <c>1</c> selects the serial path.</summary>
        private readonly int _maxDegreeOfParallelism;
        /// <summary>RM-10: stream the output file incrementally instead of assembling it in memory.</summary>
        private readonly bool _streamingOutput;

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
            _hardwareAcceleration = HardwareAcceleration.None;
            _maxDegreeOfParallelism = ParallelismPolicy.Resolve(0);
        }

        public DecoderEngine(IModulator modulator, YTAHD.Core.Infrastructure.IFFmpegWrapper ffmpeg, VideoCodecOptions options)
            : this(modulator, ffmpeg, options.MacroblockSize, options.Width, options.Height, options.Fps)
        {
            _useDurabilityMatrix = options.UseDurabilityMatrix;
            _durabilityMatrixOptions = options.DurabilityMatrixOptions ?? new DurabilityMatrixOptions();
            _useAudioClock = options.UseAudioClock;
            _progress = options is DecodeOptions decodeOptions ? decodeOptions.Progress : null;
            _hardwareAcceleration = options.HardwareAcceleration;
            _maxDegreeOfParallelism = ParallelismPolicy.Resolve(options.MaxDegreeOfParallelism);
            _streamingOutput = options is DecodeOptions decodeOptions2 && decodeOptions2.StreamingOutput;
        }

        private static IModulator NormalizeModulator(IModulator modulator, int macroblockSize)
        {
            // Shared helper (TD-20260913-02, F12): identical logic lived in both engines.
            return ModulatorNormalizer.Normalize(modulator, macroblockSize);
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

        /// <summary>
        /// Builds the ffmpeg decode arguments. <paramref name="hardwareAcceleration"/> of
        /// <see cref="HardwareAcceleration.None"/> emits no flag, so the default arguments stay
        /// byte-identical to the pre-CR-20260913-04 behavior. <c>-hwaccel</c> must precede
        /// <c>-i</c> to affect input decoding. When a mode is active,
        /// <c>-hwaccel_output_format nv12</c> is required: without it ffmpeg defaults to keeping
        /// frames in GPU surface memory (e.g. qsv surfaces) and the rgb24 swscale conversion
        /// fails with "Impossible to convert between the formats" (exit -40), yielding zero
        /// decodable frames.
        /// <c>-nostdin</c> (CR-20260913-08) closes the child's stdin: ffmpeg monitors stdin for
        /// interactive commands, and under a test host or service host the inherited stdin is a
        /// pipe that never delivers data and never closes, which can freeze the child before it
        /// writes a single frame.
        /// </summary>
        internal static string BuildDecodeArguments(string inputVideo, int width, int height, int fps, HardwareAcceleration acceleration)
        {
            var hwaccelFlag = FFmpegEncoderArguments.HwaccelValue(acceleration);
            var hwaccelArgs = hwaccelFlag is null
                ? string.Empty
                : $"-hwaccel {hwaccelFlag} -hwaccel_output_format nv12 ";
            return $"-nostdin -hide_banner -loglevel error {hwaccelArgs}-i \"{inputVideo}\" -f rawvideo -pix_fmt rgb24 -s {width}x{height} -r {fps} -";
        }

        private string ResolveFfprobeExecutablePath()
        {
            // Single source of truth (TD-20260913-02, F8): FFmpegProbe.ResolveFfprobePath
            // already handles the explicit-path and PATH cases, including the
            // CR-20260911-01 directory-override handling this private copy could miss.
            return FFmpegProbe.ResolveFfprobePath(_ffmpeg.ExecutablePath) ?? "ffprobe";
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
                RedirectStandardError = true,
                // CR-20260913-08: ffprobe has no -nostdin option; the inherited never-closing
                // stdin under a host must be redirected (and closed right after start).
                RedirectStandardInput = true
            };

            try
            {
                using var child = ChildProcessScope.Start(psi, "Failed to start ffprobe.");
                var proc = child.Process;
                proc.StandardInput.Close();

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
            var args = BuildDecodeArguments(inputVideo, width, height, fps, _hardwareAcceleration);
            var psi = new ProcessStartInfo(ffmpegPath, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // CR-20260913-08: the decode child reads nothing from stdin; an explicitly
                // redirected (and closed) pipe removes the inherited never-closing stdin.
                RedirectStandardInput = true
            };

            DebugTrace.Log("DecoderEngine", $"FFmpeg decode command: {ffmpegPath} {args}");
            using var child = ChildProcessScope.Start(psi, "Failed to start ffmpeg for decode.");
            var process = child.Process;
            // Close the redirected stdin immediately: the decode reads nothing from stdin
            // (CR-20260913-08).
            process.StandardInput.Close();

            // stderr must be consumed while stdout is being read; otherwise a chatty decode can fill
            // the stderr pipe buffer and block ffmpeg before it finishes writing rgb frames to stdout.
            var stderrDrain = ChildProcessPipes.DrainAsync(process.StandardError);

            // stdout is wrapped rather than read directly: a pipe read cannot complete on an IO completion
            // port, so awaiting the raw pipe parks a thread-pool thread for the whole decode and enough
            // concurrent decodes then starve the pool the pipeline itself runs on.
            // RM-10 (F-20260914-02): the file output streams incrementally when the durability
            // matrix is on, so a large payload never materializes in memory.
            using (var rgbStream = new ChildPipeStream(process.StandardOutput.BaseStream))
            {
                if (_useDurabilityMatrix && _streamingOutput)
                {
                    await using (var outputSink = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        await DecodeToSinkAsync(rgbStream, width, height, _macroblockSize, outputSink, audioDatagramCount, totalVideoFrames, cancellationToken);
                    }
                }
                else
                {
                    await DecodeFromRgbStreamAsync(rgbStream, width, height, _macroblockSize, outputFile, audioDatagramCount, totalVideoFrames, cancellationToken);
                }
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

        /// <summary>
        /// Streaming decode into a caller-supplied sink (RM-10, F-20260914-02): durability-path
        /// bytes are written as their groups finalize, so peak memory is bounded by the group
        /// size rather than the payload size. Metrics report the same shape as a batch decode.
        /// </summary>
        public async Task<long> DecodeToSinkAsync(Stream rgbStream, int width, int height, int macroblockSize, Stream outputSink, int? audioDatagramCount = null, int totalVideoFrames = 0, CancellationToken cancellationToken = default)
        {
            var orchestrator = new DecodeStreamOrchestrator(_modulator, width, height, macroblockSize, _useDurabilityMatrix, _durabilityMatrixOptions, _maxDegreeOfParallelism);
            var flushedBytes = await orchestrator.ProcessToSinkAsync(rgbStream, outputSink, 0, audioDatagramCount, totalVideoFrames, _progress, cancellationToken);
            LastDecodeMetrics = orchestrator.LastDecodeMetrics;
            return flushedBytes;
        }

        /// <summary>
        /// Machine-readable stream triage (RM-13, CR-20261007-02): decode the video's frame
        /// packets WITHOUT assembling a payload and report what the stream reveals — recovered
        /// manifest, packet statistics, geometry, and loss indicators. Reuses the same ffmpeg
        /// pipe + frame-bit-decoder path as a real decode so the report reflects what a decode
        /// would see, at a fraction of the cost (no payload buffer, no aggregation).
        /// </summary>
        public async Task<InspectReport> InspectAsync(string inputVideo, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(inputVideo))
                throw new FileNotFoundException("Input video not found", inputVideo);

            var report = new InspectReport { InputVideo = inputVideo, ModulatorId = _modulator.GetType().Name };
            var (width, height, fps) = await GetVideoMetadataAsync(inputVideo);
            report.Width = width;
            report.Height = height;
            report.Fps = fps;
            report.TotalVideoFrames = await FFmpegProbe.GetVideoFrameCountAsync(inputVideo, _ffmpeg.ExecutablePath);

            var ffmpegPath = _ffmpeg.ExecutablePath;
            if (string.IsNullOrWhiteSpace(ffmpegPath) || ffmpegPath.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                ffmpegPath = "ffmpeg";
            }

            var args = BuildDecodeArguments(inputVideo, width, height, fps, _hardwareAcceleration);
            var psi = new ProcessStartInfo(ffmpegPath, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };

            using var child = ChildProcessScope.Start(psi, "Failed to start ffmpeg for inspect.");
            var process = child.Process;
            process.StandardInput.Close();
            var stderrDrain = ChildProcessPipes.DrainAsync(process.StandardError);

            using (var rgbStream = new ChildPipeStream(process.StandardOutput.BaseStream))
            {
                await InspectFromRgbStreamAsync(rgbStream, width, height, report, cancellationToken);
            }

            await process.WaitForExitAsync();
            await stderrDrain;
            return report;
        }

        private async Task InspectFromRgbStreamAsync(Stream rgbStream, int width, int height, InspectReport report, CancellationToken cancellationToken)
        {
            var geometry = new ModulatorGeometry(width, height, _macroblockSize, HeaderBytes, BitsPerFrame: 0);
            int borderWidth = _modulator.GetBorderWidth(geometry);
            geometry = geometry with { BorderWidth = borderWidth };
            int payloadBytesPerFrame = _modulator.GetPayloadBytesPerFrame(geometry);
            if (payloadBytesPerFrame <= 0)
                throw new InvalidOperationException("Frame capacity too small for metadata header and payload.");

            int rowBytes = width * 3;
            int frameBytes = rowBytes * height;
            int blocksX = width / _macroblockSize;
            int blocksY = height / _macroblockSize;
            int bitsPerFrame = blocksX * blocksY;

            var frameBitDecoder = FrameBitDecoderFactory.CreateForModulator(_modulator);
            byte[] frameBuf = new byte[frameBytes];
            var dataIndices = new SortedSet<int>();
            var declaredTotals = new SortedSet<int>();
            var payloadLengths = new SortedSet<int>();
            var manifests = new List<StreamManifest>();
            var manifestChunks = new Dictionary<bool, Dictionary<int, byte[]>>();
            bool seenSymbolFrame = false;

            while (true)
            {
                int read = 0;
                while (read < frameBytes)
                {
                    int r = await rgbStream.ReadAsync(frameBuf, read, frameBytes - read, cancellationToken);
                    if (r == 0) break;
                    read += r;
                }

                if (read < frameBytes) break;
                report.FramesSeen++;

                if (frameBitDecoder.IsCanonicalFrameMemory(frameBuf, width, height, borderWidth))
                {
                    report.CanonicalFrameCount++;
                    continue;
                }

                if (!DecoderEngine.TryReadDecodedPacket(frameBuf, width, height, _macroblockSize, rowBytes, frameBytes, bitsPerFrame, _modulator, out var packet))
                {
                    report.InvalidPacketCount++;
                    continue;
                }

                if (!FramePacketCodec.TryDecode(packet, out var frameType, out var frameIndex, out var totalDataFrames, out var groupStart, out var groupCount, out var payloadLength, out var payload))
                {
                    report.InvalidPacketCount++;
                    continue;
                }

                report.PacketCount++;
                report.PayloadBytesPerFrame = payloadBytesPerFrame;
                declaredTotals.Add(totalDataFrames);

                switch (frameType)
                {
                    case FrameTypeData:
                        report.DataPacketCount++;
                        dataIndices.Add(frameIndex);
                        break;
                    case FrameTypeParity:
                        report.ParityPacketCount++;
                        break;
                    case FramePacket.FrameTypeManifest:
                        report.ManifestPacketCount++;
                        var copyKey = !seenSymbolFrame;
                        if (!manifestChunks.TryGetValue(copyKey, out var chunkList))
                        {
                            chunkList = new Dictionary<int, byte[]>();
                            manifestChunks[copyKey] = chunkList;
                        }
                        chunkList.TryAdd(frameIndex, payload);
                        break;
                }

                seenSymbolFrame = true;
            }

            if (declaredTotals.Count > 0)
            {
                report.DeclaredTotalDataFrames = declaredTotals.Min;
            }

            report.DistinctDataFrameIndices = dataIndices.Count;
            if (dataIndices.Count > 0)
            {
                var missing = new List<int>();
                for (int i = 0; i <= System.Linq.Enumerable.Max(dataIndices); i++)
                {
                    if (!dataIndices.Contains(i)) missing.Add(i);
                }
                report.MissingDataFrameIndices = missing;
            }

            // Reconcile manifest copies exactly like the durability decode (CR-20260912-05):
            // reassemble each copy's chunks, then require agreement.
            var reassembled = new List<StreamManifest>();
            foreach (var chunkList in manifestChunks.Values)
            {
                if (chunkList.Count == 0) continue;
                var ordered = chunkList.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray();
                var body = ordered.Length == 1 ? ordered[0] : ordered.SelectMany(c => c).ToArray();
                if (StreamManifestCodec.TryDeserialize(body, out var manifest) && manifest is not null)
                {
                    reassembled.Add(manifest);
                }
            }

            if (reassembled.Count > 0)
            {
                report.Manifest = reassembled[0];
                if (reassembled.Count > 1)
                {
                    var first = reassembled[0];
                    report.ManifestCopiesAgree = reassembled.All(m =>
                        m.TotalPayloadBytes == first.TotalPayloadBytes
                        && m.PayloadSha256.AsSpan().SequenceEqual(first.PayloadSha256)
                        && m.DataFrameCount == first.DataFrameCount
                        && m.ModulatorId == first.ModulatorId);
                }
            }

            report.LooksLikeYtahdStream = report.PacketCount > 0;
        }
    }
}
