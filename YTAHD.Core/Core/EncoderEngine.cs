using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using YTAHD.Core.Application;
using YTAHD.Core.Audio;
using YTAHD.Core.Modulation;
using YTAHD.Core.Infrastructure;

namespace YTAHD.Core.Core
{
    public class EncoderEngine
    {
        private const int FrameMagic = 0x5954; // 'YT'
        private const byte FrameVersion = 1;
        private const byte FrameTypeData = 0;
        private const byte FrameTypeParity = 1;
        private const int DataFramesPerParityGroup = 4;
        private const int HeaderBytes = FramePacket.HeaderBytes;

        private readonly IModulator _modulator;
        private readonly YTAHD.Core.Infrastructure.IFFmpegWrapper _ffmpeg;
        private readonly int _macroblockSize;
        private readonly int _width;
        private readonly int _height;
        private readonly int _fps;
        private readonly bool _useDurabilityMatrix;
        private readonly DurabilityMatrixOptions? _durabilityMatrixOptions;
        private readonly bool _useAudioClock;
        /// <summary>Resolved worker count from <see cref="ParallelismPolicy"/>; <c>1</c> selects the serial path.</summary>
        private readonly int _maxDegreeOfParallelism;
        /// <summary>The degree of parallelism as configured, before resolution.</summary>
        private readonly int _requestedDegreeOfParallelism;

        public EncodeMetrics LastEncodeMetrics { get; private set; } = new();

        public EncoderEngine(IModulator modulator, YTAHD.Core.Infrastructure.IFFmpegWrapper ffmpeg, int macroblockSize = 16, int width = 3840, int height = 2160, int fps = 60)
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
            _maxDegreeOfParallelism = ParallelismPolicy.Resolve(0);
            _requestedDegreeOfParallelism = 0;
        }

        public EncoderEngine(IModulator modulator, YTAHD.Core.Infrastructure.IFFmpegWrapper ffmpeg, VideoCodecOptions options)
            : this(modulator, ffmpeg, options.MacroblockSize, options.Width, options.Height, options.Fps)
        {
            _useDurabilityMatrix = options.UseDurabilityMatrix;
            _durabilityMatrixOptions = options.DurabilityMatrixOptions ?? new DurabilityMatrixOptions();
            _useAudioClock = options.UseAudioClock;
            _maxDegreeOfParallelism = ParallelismPolicy.Resolve(options.MaxDegreeOfParallelism);
            _requestedDegreeOfParallelism = options.MaxDegreeOfParallelism;
        }

        private static IModulator NormalizeModulator(IModulator modulator, int macroblockSize)
        {
            if (modulator is BinaryGridModulator binary && (binary.MacroblockWidth != macroblockSize || binary.MacroblockHeight != macroblockSize))
            {
                return new BinaryGridModulator(macroblockSize, macroblockSize);
            }

            return modulator ?? throw new ArgumentNullException(nameof(modulator));
        }

        public static byte[] CreateDataFramePacket(int frameIndex, int totalDataFrames, int groupStart, int groupCount, int payloadLength, ReadOnlySpan<byte> payload, int payloadCapacity = 0)
        {
            return FrameProtocolHelpers.CreateDataFramePacket(frameIndex, totalDataFrames, groupStart, groupCount, payloadLength, payload, payloadCapacity);
        }

        public static byte[] CreateParityFramePacket(int groupStart, int groupCount, int totalDataFrames, ReadOnlySpan<byte> parityPayload)
        {
            return FrameProtocolHelpers.CreateParityFramePacket(groupStart, groupCount, totalDataFrames, parityPayload);
        }

        public static byte[] ConvertRgbaToRgb(ReadOnlySpan<byte> rgbaFrame, int width, int height)
        {
            return FrameProtocolHelpers.ConvertRgbaToRgb(rgbaFrame, width, height);
        }

        public async Task VerifyAsync()
        {
            if (!await _ffmpeg.IsAvailableAsync())
                throw new InvalidOperationException("ffmpeg not found in PATH or not runnable");
        }

        private async Task<int> GetActualVideoFrameCountAsync(string videoPath)
        {
            return await FFmpegProbe.GetVideoFrameCountAsync(videoPath, _ffmpeg.ExecutablePath);
        }

        public async Task EncodeAsync(string inputFile, string outputVideo, CancellationToken cancellationToken = default)
        {
            var totalStopwatch = Stopwatch.StartNew();
            var timings = new EncodeTimings();

            if (!File.Exists(inputFile))
                throw new FileNotFoundException("Input file not found", inputFile);

            DebugTrace.Log("EncoderEngine", $"Starting encode for '{inputFile}' => '{outputVideo}' width={_width} height={_height} fps={_fps} modulator={_modulator.GetType().Name} useDurability={_useDurabilityMatrix}");

            var data = await File.ReadAllBytesAsync(inputFile);

            var geometry = new ModulatorGeometry(_width, _height, _macroblockSize, HeaderBytes, BitsPerFrame: 0);
            int borderWidth = _modulator.GetBorderWidth(geometry);
            geometry = geometry with { BorderWidth = borderWidth };
            int payloadBytesPerFrame = _modulator.GetPayloadBytesPerFrame(geometry);
            DebugTrace.Log("EncoderEngine", $"Frame geometry: payloadBytesPerFrame={payloadBytesPerFrame} borderWidth={borderWidth} headerBytes={HeaderBytes}");
            if (payloadBytesPerFrame <= 0)
                throw new InvalidOperationException("Frame capacity too small for metadata header and payload.");

            int totalDataFrames;
            var framePackets = Array.Empty<byte[]>();
            if (_useDurabilityMatrix)
            {
                var packetStopwatch = Stopwatch.StartNew();
                var matrixOptions = _durabilityMatrixOptions ?? new DurabilityMatrixOptions();
                var durabilityCodec = new DurabilityTransportCodec(matrixOptions);

                // Stream manifest (CR-20260912-05 stage 2/3): proves whole-payload integrity at
                // decode time and gives the matrix an authoritative expected length. The data
                // frame count is known before emission: the matrix splits the payload into
                // fixed-size symbols, one data frame per symbol.
                var dataFrameCount = (int)Math.Ceiling(data.Length / (double)matrixOptions.SymbolSize);
                var manifest = new StreamManifest
                {
                    TotalPayloadBytes = data.LongLength,
                    PayloadSha256 = SHA256.HashData(data),
                    ModulatorId = _modulator.GetType().Name,
                    Width = _width,
                    Height = _height,
                    MacroblockSize = _macroblockSize,
                    Fps = _fps,
                    DataFrameCount = dataFrameCount,
                    ParityGroupSize = matrixOptions.GroupSize,
                    ParitySymbolsPerGroup = matrixOptions.ParitySymbolsPerGroup,
                    UseDurabilityMatrix = true
                };

                framePackets = durabilityCodec.EncodeToFramePackets(data, manifest).ToArray();
                packetStopwatch.Stop();
                timings.PacketBuildMilliseconds += packetStopwatch.Elapsed.TotalMilliseconds;
                totalDataFrames = framePackets.Count(p => p[3] == FramePacket.FrameTypeData);
            }
            else
            {
                totalDataFrames = (data.Length + payloadBytesPerFrame - 1) / payloadBytesPerFrame;
            }

            int totalFramesWritten = 0;

            LastEncodeMetrics = new EncodeMetrics
            {
                InputPayloadBytes = data.Length,
                PayloadBytesPerFrame = payloadBytesPerFrame,
                TotalDataFrames = totalDataFrames,
                TotalFramesWritten = 0,
                TotalFramesInVideo = 0
            };

            // Logical frame count: data frames plus (unless the durability matrix supplies its own
            // redundancy) one XOR parity frame per group. Shared by the audio clock track and the
            // worker-budget split below.
            int parityFrameCount = _useDurabilityMatrix ? 0 : (totalDataFrames + DataFramesPerParityGroup - 1) / DataFramesPerParityGroup;
            int totalLogicalFrames = totalDataFrames + parityFrameCount;

            string? audioPcmFilePath = null;
            if (_useAudioClock)
            {
                var emissionForAudio = _modulator as IFrameEmissionStrategy;
                int physicalFramesPerLogicalFrame = (emissionForAudio?.RepeatCount ?? 3) + (emissionForAudio?.UsesCanonicalSeparator == true ? 1 : 0);

                audioPcmFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_ytahd_audio.pcm");
                await using (var pcmStream = new FileStream(audioPcmFilePath, FileMode.Create, FileAccess.Write))
                {
                    await WriteAudioClockTrackAsync(pcmStream, totalLogicalFrames, physicalFramesPerLogicalFrame, _fps);
                }
                DebugTrace.Log("EncoderEngine", $"Audio clock track written: '{audioPcmFilePath}' totalLogicalFrames={totalLogicalFrames} physicalFramesPerLogicalFrame={physicalFramesPerLogicalFrame}");
            }

            using var ff = await _ffmpeg.StartAsync(outputVideo, audioPcmFilePath);
            var stdin = ff.StandardInput;
            DebugTrace.Log("EncoderEngine", $"FFmpeg process started; totalDataFrames={totalDataFrames} totalFramesWritten target={totalFramesWritten}");

            int workerCount = _maxDegreeOfParallelism;
            using var pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken pipelineToken = pipelineCancellation.Token;

            // The resolved worker count is the whole stage's CPU budget, so it is split rather
            // than granted twice: with several frame workers the modulator renders block rows
            // serially, and with a single (or unused) frame worker the inner loop keeps the
            // machine's processors. Short payloads clamp the split so a one-frame encode still
            // gets a parallel inner loop.
            int effectiveFrameWorkers = Math.Min(workerCount, Math.Max(1, totalLogicalFrames));
            ApplyInnerDegreeOfParallelism(effectiveFrameWorkers);

            var framePlan = new EncodeFramePlan(data, framePackets, totalDataFrames, payloadBytesPerFrame, borderWidth);

            try
            {
                if (workerCount <= 1)
                {
                    // Serial fallback: one logical frame at a time, in stream order, with no worker
                    // tasks and no channels. Deterministic for debugging and cheap on low-core machines.
                    totalFramesWritten = await WriteFramesSeriallyAsync(stdin, framePlan, timings, pipelineToken);
                }
                else
                {
                    // Bounded ordered pipeline: one packet producer, N render workers, one writer.
                    var packetChannel = Channel.CreateBounded<(int Index, byte[] Packet)>(new BoundedChannelOptions(workerCount * 2)
                    {
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleWriter = true
                    });
                    var renderedChannel = Channel.CreateBounded<(int Index, byte[] Rgb, byte[]? CanonicalRgb, double RenderMilliseconds, double ConversionMilliseconds)>(new BoundedChannelOptions(workerCount * 2)
                    {
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleReader = true
                    });

                    async Task WriteRenderedFramesAsync()
                    {
                        var pending = new SortedDictionary<int, (byte[] Rgb, byte[]? CanonicalRgb, double RenderMilliseconds, double ConversionMilliseconds)>();
                        int nextIndex = 0;
                        try
                        {
                            await foreach (var rendered in renderedChannel.Reader.ReadAllAsync(pipelineToken))
                            {
                                pending[rendered.Index] = (rendered.Rgb, rendered.CanonicalRgb, rendered.RenderMilliseconds, rendered.ConversionMilliseconds);
                                while (pending.Remove(nextIndex, out var frame))
                                {
                                    timings.FrameRenderMilliseconds += frame.RenderMilliseconds;
                                    timings.RgbConversionMilliseconds += frame.ConversionMilliseconds;
                                    await WriteRenderedFrameAsync(stdin, frame, nextIndex, timings, pipelineToken);
                                    nextIndex++;
                                }
                            }
                        }
                        catch
                        {
                            pipelineCancellation.Cancel();
                            throw;
                        }
                    }

                    var workers = new List<Task>();
                    for (int worker = 0; worker < workerCount; worker++)
                    {
                        workers.Add(Task.Run(async () =>
                        {
                            try
                            {
                                await foreach (var work in packetChannel.Reader.ReadAllAsync(pipelineToken))
                                {
                                    var rendered = RenderFramePacket(work.Packet, framePlan.BorderWidth);
                                    await renderedChannel.Writer.WriteAsync((work.Index, rendered.Rgb, rendered.CanonicalRgb, rendered.RenderMilliseconds, rendered.ConversionMilliseconds), pipelineToken);
                                }
                            }
                            catch
                            {
                                pipelineCancellation.Cancel();
                                throw;
                            }
                        }));
                    }
                    var writerTask = WriteRenderedFramesAsync();

                    async Task ProducePacketAsync(byte[] packet)
                    {
                        await packetChannel.Writer.WriteAsync((totalFramesWritten, packet), pipelineToken);
                        totalFramesWritten++;
                    }

                    async Task ProducePacketsAsync()
                    {
                        try
                        {
                            foreach (var framePacket in EnumerateFramePackets(framePlan, timings))
                            {
                                await ProducePacketAsync(framePacket);
                            }
                        }
                        catch
                        {
                            pipelineCancellation.Cancel();
                            throw;
                        }
                        finally
                        {
                            packetChannel.Writer.TryComplete();
                        }
                    }

                    var producerTask = ProducePacketsAsync();
                    var workerTask = Task.WhenAll(workers);
                    var workerCompletionTask = workerTask.ContinueWith(
                        _ => renderedChannel.Writer.TryComplete(),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    await Task.WhenAll(producerTask, workerTask, workerCompletionTask, writerTask);
                }
            }
            finally
            {
                pipelineCancellation.Cancel();
                try
                {
                    var flushStopwatch = Stopwatch.StartNew();
                    await stdin.FlushAsync();
                    flushStopwatch.Stop();
                    timings.FfmpegWriteMilliseconds += flushStopwatch.Elapsed.TotalMilliseconds;
                }
                catch { }

                try
                {
                    stdin.Dispose();
                }
                catch { }
            }

            DebugTrace.Log("EncoderEngine", $"Encoder loop finished. Waiting for ffmpeg exit; outputVideo='{outputVideo}'");
            await ff.WaitForExitAsync();

            if (audioPcmFilePath != null)
            {
                try { File.Delete(audioPcmFilePath); } catch { }
            }

            int actualFramesInVideo = await GetActualVideoFrameCountAsync(outputVideo);
            totalStopwatch.Stop();
            DebugTrace.Log("EncoderEngine", $"ffmpeg exited. actualFramesInVideo={actualFramesInVideo} totalFramesWritten={totalFramesWritten}");
            LastEncodeMetrics = new EncodeMetrics
            {
                InputPayloadBytes = data.Length,
                PayloadBytesPerFrame = payloadBytesPerFrame,
                TotalDataFrames = totalDataFrames,
                TotalFramesWritten = totalFramesWritten,
                TotalFramesInVideo = actualFramesInVideo > 0 ? actualFramesInVideo : totalFramesWritten,
                TotalElapsedMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds,
                PacketBuildMilliseconds = timings.PacketBuildMilliseconds,
                FrameRenderMilliseconds = timings.FrameRenderMilliseconds,
                RgbConversionMilliseconds = timings.RgbConversionMilliseconds,
                FfmpegWriteMilliseconds = timings.FfmpegWriteMilliseconds
            };
        }

        /// <summary>
        /// Renders one logical frame packet into RGB (plus the canonical separator frame when the
        /// modulator emits one) and reports the render and RGB-conversion timings.
        /// </summary>
        private (byte[] Rgb, byte[]? CanonicalRgb, double RenderMilliseconds, double ConversionMilliseconds) RenderFramePacket(byte[] framePacket, int borderWidth)
        {
            var frameGeometry = new ModulatorGeometry(_width, _height, _macroblockSize, HeaderBytes, borderWidth);
            var renderStopwatch = Stopwatch.StartNew();
            byte[] rgbaFrame = _modulator.CreateFrame(frameGeometry, framePacket.AsSpan(0, Math.Min(framePacket.Length, _width * _height * 4)));
            renderStopwatch.Stop();
            double renderMilliseconds = renderStopwatch.Elapsed.TotalMilliseconds;

            var conversionStopwatch = Stopwatch.StartNew();
            byte[] rgbFrame = ConvertRgbaToRgb(rgbaFrame, _width, _height);
            conversionStopwatch.Stop();
            double conversionMilliseconds = conversionStopwatch.Elapsed.TotalMilliseconds;

            byte[]? canonicalRgb = null;
            var emission = _modulator as IFrameEmissionStrategy;
            if (emission?.UsesCanonicalSeparator == true)
            {
                renderStopwatch.Restart();
                byte[] canonicalRgba = _modulator.CreateFrame(frameGeometry, ReadOnlySpan<byte>.Empty);
                renderStopwatch.Stop();
                renderMilliseconds += renderStopwatch.Elapsed.TotalMilliseconds;

                conversionStopwatch.Restart();
                canonicalRgb = ConvertRgbaToRgb(canonicalRgba, _width, _height);
                conversionStopwatch.Stop();
                conversionMilliseconds += conversionStopwatch.Elapsed.TotalMilliseconds;
            }

            return (rgbFrame, canonicalRgb, renderMilliseconds, conversionMilliseconds);
        }

        /// <summary>
        /// Writes one rendered logical frame to FFmpeg: the physical emission repeats first, then
        /// the canonical separator frame when present. Shared by the serial and parallel paths so
        /// both produce identical stream ordering.
        /// </summary>
        private async Task WriteRenderedFrameAsync(
            Stream stdin,
            (byte[] Rgb, byte[]? CanonicalRgb, double RenderMilliseconds, double ConversionMilliseconds) frame,
            int frameIndex,
            EncodeTimings timings,
            CancellationToken cancellationToken)
        {
            DebugTrace.Log("EncoderEngine", $"Writing logical frame #{frameIndex} modulator={_modulator.GetType().Name}");
            int repeatCount = (_modulator as IFrameEmissionStrategy)?.RepeatCount ?? 3;
            for (int rep = 0; rep < repeatCount; rep++)
            {
                var writeStopwatch = Stopwatch.StartNew();
                await stdin.WriteAsync(frame.Rgb, 0, frame.Rgb.Length, cancellationToken);
                writeStopwatch.Stop();
                timings.FfmpegWriteMilliseconds += writeStopwatch.Elapsed.TotalMilliseconds;
            }

            if (frame.CanonicalRgb != null)
            {
                var writeStopwatch = Stopwatch.StartNew();
                await stdin.WriteAsync(frame.CanonicalRgb, 0, frame.CanonicalRgb.Length, cancellationToken);
                writeStopwatch.Stop();
                timings.FfmpegWriteMilliseconds += writeStopwatch.Elapsed.TotalMilliseconds;
            }
        }

        /// <summary>
        /// Serial encode path: render and write one logical frame at a time in stream order. Used
        /// whenever the resolved degree of parallelism is one (see <see cref="ParallelismPolicy"/>)
        /// so debugging is deterministic and single-core machines avoid thread-pool overhead.
        /// </summary>
        /// <summary>
        /// Splits the resolved worker budget between the frame-level pipeline and the modulator's
        /// inner loop (see <see cref="ParallelismPolicy.ResolveInnerDegree(int, int)"/>). No-op for
        /// modulators that do not expose inner-loop parallelism.
        /// </summary>
        private void ApplyInnerDegreeOfParallelism(int frameWorkers)
        {
            if (_modulator is IParallelismConfigurable configurable)
            {
                configurable.InnerDegreeOfParallelism = ParallelismPolicy.ResolveInnerDegree(_requestedDegreeOfParallelism, frameWorkers);
            }
        }

        private async Task<int> WriteFramesSeriallyAsync(Stream stdin, EncodeFramePlan plan, EncodeTimings timings, CancellationToken cancellationToken)
        {
            int framesWritten = 0;
            foreach (var framePacket in EnumerateFramePackets(plan, timings))
            {
                var rendered = RenderFramePacket(framePacket, plan.BorderWidth);
                timings.FrameRenderMilliseconds += rendered.RenderMilliseconds;
                timings.RgbConversionMilliseconds += rendered.ConversionMilliseconds;
                await WriteRenderedFrameAsync(stdin, rendered, framesWritten, timings, cancellationToken);
                framesWritten++;
            }

            return framesWritten;
        }

        /// <summary>
        /// Produces the ordered logical frame packets for a payload: either the durability matrix
        /// packets, or data packets interleaved with one parity packet per group. Shared by the
        /// serial and parallel paths so packet content and order cannot drift apart.
        /// </summary>
        private IEnumerable<byte[]> EnumerateFramePackets(EncodeFramePlan plan, EncodeTimings timings)
        {
            if (_useDurabilityMatrix)
            {
                foreach (var framePacket in plan.FramePackets)
                {
                    yield return framePacket;
                }

                yield break;
            }

            int dataOffset = 0;
            for (int groupStart = 0; groupStart < plan.TotalDataFrames; groupStart += DataFramesPerParityGroup)
            {
                int groupCount = Math.Min(DataFramesPerParityGroup, plan.TotalDataFrames - groupStart);
                var parityPayload = new byte[plan.PayloadBytesPerFrame];

                for (int idxInGroup = 0; idxInGroup < groupCount; idxInGroup++)
                {
                    var packetStopwatch = Stopwatch.StartNew();
                    int frameIdx = groupStart + idxInGroup;
                    int payloadLen = Math.Min(plan.PayloadBytesPerFrame, plan.Data.Length - dataOffset);
                    var payload = new byte[plan.PayloadBytesPerFrame];
                    if (payloadLen > 0)
                    {
                        Buffer.BlockCopy(plan.Data, dataOffset, payload, 0, payloadLen);
                    }

                    for (int i = 0; i < plan.PayloadBytesPerFrame; i++)
                    {
                        parityPayload[i] ^= payload[i];
                    }

                    var framePacket = CreateDataFramePacket(frameIdx, plan.TotalDataFrames, groupStart, groupCount, payloadLen, payload, plan.PayloadBytesPerFrame);
                    packetStopwatch.Stop();
                    timings.PacketBuildMilliseconds += packetStopwatch.Elapsed.TotalMilliseconds;
                    dataOffset += payloadLen;
                    yield return framePacket;
                }

                var parityStopwatch = Stopwatch.StartNew();
                var parityPacket = CreateParityFramePacket(groupStart, groupCount, plan.TotalDataFrames, parityPayload);
                parityStopwatch.Stop();
                timings.PacketBuildMilliseconds += parityStopwatch.Elapsed.TotalMilliseconds;
                yield return parityPacket;
            }
        }

        /// <summary>Mutable per-stage timing accumulator shared by the serial and parallel encode paths.</summary>
        private sealed class EncodeTimings
        {
            public double PacketBuildMilliseconds { get; set; }
            public double FrameRenderMilliseconds { get; set; }
            public double RgbConversionMilliseconds { get; set; }
            public double FfmpegWriteMilliseconds { get; set; }
        }

        /// <summary>Packet-level inputs shared by the serial and parallel encode paths.</summary>
        private sealed record EncodeFramePlan(byte[] Data, byte[][] FramePackets, int TotalDataFrames, int PayloadBytesPerFrame, int BorderWidth);

        /// <summary>
        /// Writes the FSK datagram-clock audio track: a pulse at the start of each logical
        /// frame followed by a hold tone for the rest of its physical-frame span. If a
        /// modulator's physical-frame span is shorter than the standard pulse duration (e.g.
        /// Phase 4's displaced+canonical pair), the pulse is capped to fit — the whole span
        /// is pulse tone and there is no hold gap for that logical frame.
        /// </summary>
        public static async Task WriteAudioClockTrackAsync(Stream outStream, int totalLogicalFrames, int physicalFramesPerLogicalFrame, int fps)
        {
            if (totalLogicalFrames <= 0 || physicalFramesPerLogicalFrame <= 0) return;

            var fsk = new FskGenerator();
            int pulseFrames = Math.Min(FskGenerator.PulseDurationVideoFrames, physicalFramesPerLogicalFrame);
            int holdFrames = physicalFramesPerLogicalFrame - pulseFrames;

            for (int i = 0; i < totalLogicalFrames; i++)
            {
                await fsk.WritePulseAsync(outStream, fps, pulseFrames);
                if (holdFrames > 0)
                {
                    await fsk.WriteHoldToneAsync(outStream, holdFrames, fps);
                }
            }
        }
    }
}
