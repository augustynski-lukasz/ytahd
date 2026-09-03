using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SkiaSharp;
using YTAHD.Core.Application;
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
        // magic + version + frameType + frameIndex + totalDataFrames + groupStart + groupCount + payloadLen + sha256
        private const int HeaderBytes = 2 + 1 + 1 + 4 + 4 + 4 + 1 + 2 + 32;

        private readonly IModulator _modulator;
        private readonly YTAHD.Core.Infrastructure.IFFmpegWrapper _ffmpeg;
        private readonly int _macroblockSize;
        private readonly int _width;
        private readonly int _height;
        private readonly int _fps;
        private readonly bool _useDurabilityMatrix;
        private readonly DurabilityMatrixOptions? _durabilityMatrixOptions;

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
        }

        public EncoderEngine(IModulator modulator, YTAHD.Core.Infrastructure.IFFmpegWrapper ffmpeg, VideoCodecOptions options)
            : this(modulator, ffmpeg, options.MacroblockSize, options.Width, options.Height, options.Fps)
        {
            _useDurabilityMatrix = options.UseDurabilityMatrix;
            _durabilityMatrixOptions = options.DurabilityMatrixOptions ?? new DurabilityMatrixOptions();
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

        public async Task EncodeAsync(string inputFile, string outputVideo)
        {
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
                var durabilityCodec = new DurabilityTransportCodec(_durabilityMatrixOptions ?? new DurabilityMatrixOptions());
                framePackets = durabilityCodec.EncodeToFramePackets(data).ToArray();
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

            using var ff = await _ffmpeg.StartAsync(outputVideo);
            var stdin = ff.StandardInput;
            DebugTrace.Log("EncoderEngine", $"FFmpeg process started; totalDataFrames={totalDataFrames} totalFramesWritten target={totalFramesWritten}");

            async Task WriteFramePacketAsync(byte[] framePacket)
            {
                var frameGeometry = new ModulatorGeometry(_width, _height, _macroblockSize, HeaderBytes, borderWidth);
                byte[] rgbaFrame = _modulator.CreateFrame(frameGeometry, framePacket.AsSpan(0, Math.Min(framePacket.Length, _width * _height * 4)));
                byte[] rgbFrame = ConvertRgbaToRgb(rgbaFrame, _width, _height);
                DebugTrace.Log("EncoderEngine", $"Writing packet length={framePacket.Length} rgba={rgbaFrame.Length} rgb={rgbFrame.Length} modulator={_modulator.GetType().Name}");

                var emission = _modulator as IFrameEmissionStrategy;
                int repeatCount = emission?.RepeatCount ?? 3;
                for (int rep = 0; rep < repeatCount; rep++)
                {
                    await stdin.WriteAsync(rgbFrame, 0, rgbFrame.Length);
                    await stdin.FlushAsync();
                }

                if (emission?.UsesCanonicalSeparator == true)
                {
                    byte[] canonicalRgba = _modulator.CreateFrame(frameGeometry, ReadOnlySpan<byte>.Empty);
                    byte[] canonicalRgb = ConvertRgbaToRgb(canonicalRgba, _width, _height);
                    DebugTrace.Log("EncoderEngine", $"Writing canonical separator frame modulator={_modulator.GetType().Name}");
                    await stdin.WriteAsync(canonicalRgb, 0, canonicalRgb.Length);
                    await stdin.FlushAsync();
                }
            }

            try
            {
                if (_useDurabilityMatrix)
                {
                    foreach (var framePacket in framePackets)
                    {
                        await WriteFramePacketAsync(framePacket);
                        totalFramesWritten++;
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
                            await WriteFramePacketAsync(framePacket);
                            totalFramesWritten++;
                            dataOffset += payloadLen;
                        }

                        var parityPacket = CreateParityFramePacket(groupStart, groupCount, totalDataFrames, parityPayload);
                        await WriteFramePacketAsync(parityPacket);
                        totalFramesWritten++;
                    }
                }
            }
            finally
            {
                try
                {
                    await stdin.FlushAsync();
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
            int actualFramesInVideo = await GetActualVideoFrameCountAsync(outputVideo);
            DebugTrace.Log("EncoderEngine", $"ffmpeg exited. actualFramesInVideo={actualFramesInVideo} totalFramesWritten={totalFramesWritten}");
            LastEncodeMetrics = new EncodeMetrics
            {
                InputPayloadBytes = data.Length,
                PayloadBytesPerFrame = payloadBytesPerFrame,
                TotalDataFrames = totalDataFrames,
                TotalFramesWritten = totalFramesWritten,
                TotalFramesInVideo = actualFramesInVideo > 0 ? actualFramesInVideo : totalFramesWritten
            };
        }
    }
}
