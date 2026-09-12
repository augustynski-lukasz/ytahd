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
        private readonly int _maxDegreeOfParallelism;

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
            _maxDegreeOfParallelism = 0;
        }

        public EncoderEngine(IModulator modulator, YTAHD.Core.Infrastructure.IFFmpegWrapper ffmpeg, VideoCodecOptions options)
            : this(modulator, ffmpeg, options.MacroblockSize, options.Width, options.Height, options.Fps)
        {
            _useDurabilityMatrix = options.UseDurabilityMatrix;
            _durabilityMatrixOptions = options.DurabilityMatrixOptions ?? new DurabilityMatrixOptions();
            _useAudioClock = options.UseAudioClock;
            if (options.MaxDegreeOfParallelism < 0)
                throw new ArgumentOutOfRangeException(nameof(options.MaxDegreeOfParallelism), "Maximum degree of parallelism cannot be negative.");
            _maxDegreeOfParallelism = options.MaxDegreeOfParallelism;
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
            double packetBuildMilliseconds = 0d;
            double frameRenderMilliseconds = 0d;
            double rgbConversionMilliseconds = 0d;
            double ffmpegWriteMilliseconds = 0d;

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
                var durabilityCodec = new DurabilityTransportCodec(_durabilityMatrixOptions ?? new DurabilityMatrixOptions());
                framePackets = durabilityCodec.EncodeToFramePackets(data).ToArray();
                packetStopwatch.Stop();
                packetBuildMilliseconds += packetStopwatch.Elapsed.TotalMilliseconds;
                totalDataFrames = framePackets.Length;
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

            string? audioPcmFilePath = null;
            if (_useAudioClock)
            {
                int parityFrameCount = _useDurabilityMatrix ? 0 : (totalDataFrames + DataFramesPerParityGroup - 1) / DataFramesPerParityGroup;
                int totalLogicalFrames = totalDataFrames + parityFrameCount;
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

            int workerCount = _maxDegreeOfParallelism > 0 ? _maxDegreeOfParallelism : 1;
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
            using var pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken pipelineToken = pipelineCancellation.Token;

            async Task<(byte[] Rgb, byte[]? CanonicalRgb, double RenderMilliseconds, double ConversionMilliseconds)> RenderFramePacketAsync(byte[] framePacket)
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
                            frameRenderMilliseconds += frame.RenderMilliseconds;
                            rgbConversionMilliseconds += frame.ConversionMilliseconds;
                            DebugTrace.Log("EncoderEngine", $"Writing logical frame #{nextIndex} modulator={_modulator.GetType().Name}");
                            var emission = _modulator as IFrameEmissionStrategy;
                            int repeatCount = emission?.RepeatCount ?? 3;
                            for (int rep = 0; rep < repeatCount; rep++)
                            {
                                var writeStopwatch = Stopwatch.StartNew();
                                await stdin.WriteAsync(frame.Rgb, 0, frame.Rgb.Length, pipelineToken);
                                writeStopwatch.Stop();
                                ffmpegWriteMilliseconds += writeStopwatch.Elapsed.TotalMilliseconds;
                            }

                            if (frame.CanonicalRgb != null)
                            {
                                var writeStopwatch = Stopwatch.StartNew();
                                await stdin.WriteAsync(frame.CanonicalRgb, 0, frame.CanonicalRgb.Length, pipelineToken);
                                writeStopwatch.Stop();
                                ffmpegWriteMilliseconds += writeStopwatch.Elapsed.TotalMilliseconds;
                            }

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

            try
            {
                var workers = new List<Task>();
                for (int worker = 0; worker < workerCount; worker++)
                {
                    workers.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (var work in packetChannel.Reader.ReadAllAsync(pipelineToken))
                            {
                                var rendered = await RenderFramePacketAsync(work.Packet);
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
                        if (_useDurabilityMatrix)
                        {
                            foreach (var framePacket in framePackets)
                            {
                                await ProducePacketAsync(framePacket);
                            }
                        }
                        else
                        {
                            int dataOffset = 0;
                            for (int groupStart = 0; groupStart < totalDataFrames; groupStart += DataFramesPerParityGroup)
                            {
                                int groupCount = Math.Min(DataFramesPerParityGroup, totalDataFrames - groupStart);
                                var parityPayload = new byte[payloadBytesPerFrame];

                                for (int idxInGroup = 0; idxInGroup < groupCount; idxInGroup++)
                                {
                                    var packetStopwatch = Stopwatch.StartNew();
                                    int frameIdx = groupStart + idxInGroup;
                                    int payloadLen = Math.Min(payloadBytesPerFrame, data.Length - dataOffset);
                                    var payload = new byte[payloadBytesPerFrame];
                                    if (payloadLen > 0)
                                    {
                                        Buffer.BlockCopy(data, dataOffset, payload, 0, payloadLen);
                                    }

                                    for (int i = 0; i < payloadBytesPerFrame; i++)
                                    {
                                        parityPayload[i] ^= payload[i];
                                    }

                                    var framePacket = CreateDataFramePacket(frameIdx, totalDataFrames, groupStart, groupCount, payloadLen, payload, payloadBytesPerFrame);
                                    packetStopwatch.Stop();
                                    packetBuildMilliseconds += packetStopwatch.Elapsed.TotalMilliseconds;
                                    await ProducePacketAsync(framePacket);
                                    dataOffset += payloadLen;
                                }

                                var parityStopwatch = Stopwatch.StartNew();
                                var parityPacket = CreateParityFramePacket(groupStart, groupCount, totalDataFrames, parityPayload);
                                parityStopwatch.Stop();
                                packetBuildMilliseconds += parityStopwatch.Elapsed.TotalMilliseconds;
                                await ProducePacketAsync(parityPacket);
                            }
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
            finally
            {
                pipelineCancellation.Cancel();
                packetChannel.Writer.TryComplete();
                renderedChannel.Writer.TryComplete();
                try
                {
                    var flushStopwatch = Stopwatch.StartNew();
                    await stdin.FlushAsync();
                    flushStopwatch.Stop();
                    ffmpegWriteMilliseconds += flushStopwatch.Elapsed.TotalMilliseconds;
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
                PacketBuildMilliseconds = packetBuildMilliseconds,
                FrameRenderMilliseconds = frameRenderMilliseconds,
                RgbConversionMilliseconds = rgbConversionMilliseconds,
                FfmpegWriteMilliseconds = ffmpegWriteMilliseconds
            };
        }

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
