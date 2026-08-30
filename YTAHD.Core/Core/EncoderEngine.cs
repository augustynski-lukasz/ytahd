using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SkiaSharp;
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

        public EncoderEngine(IModulator modulator, YTAHD.Core.Infrastructure.IFFmpegWrapper ffmpeg, int macroblockSize = 16, int width = 3840, int height = 2160, int fps = 60)
        {
            _modulator = modulator ?? throw new ArgumentNullException(nameof(modulator));
            _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
            _macroblockSize = macroblockSize;
            _width = width;
            _height = height;
            _fps = fps;
        }

        public static byte[] CreateDataFramePacket(int frameIndex, int totalDataFrames, int groupStart, int groupCount, int payloadLength, ReadOnlySpan<byte> payload, int payloadCapacity = 0)
        {
            int capacity = payloadCapacity > 0 ? payloadCapacity : payload.Length;
            byte[] framePacket = new byte[HeaderBytes + capacity];
            WriteFrameHeader(framePacket, FrameTypeData, frameIndex, totalDataFrames, groupStart, groupCount, payloadLength);

            var hash = SHA256.HashData(payload.Slice(0, Math.Min(payloadLength, payload.Length)));
            Buffer.BlockCopy(hash, 0, framePacket, 19, hash.Length);
            Buffer.BlockCopy(payload.ToArray(), 0, framePacket, HeaderBytes, Math.Min(capacity, payload.Length));

            return framePacket;
        }

        public static byte[] CreateParityFramePacket(int groupStart, int groupCount, int totalDataFrames, ReadOnlySpan<byte> parityPayload)
        {
            byte[] parityPacket = new byte[HeaderBytes + parityPayload.Length];
            WriteFrameHeader(parityPacket, FrameTypeParity, 0, totalDataFrames, groupStart, groupCount, parityPayload.Length);

            var parityHash = SHA256.HashData(parityPayload);
            Buffer.BlockCopy(parityHash, 0, parityPacket, 19, parityHash.Length);
            parityPayload.CopyTo(parityPacket.AsSpan(HeaderBytes, parityPacket.Length - HeaderBytes));

            return parityPacket;
        }

        private static void WriteFrameHeader(byte[] framePacket, byte frameType, int frameIndex, int totalDataFrames, int groupStart, int groupCount, int payloadLength)
        {
            framePacket[0] = (byte)((FrameMagic >> 8) & 0xFF);
            framePacket[1] = (byte)(FrameMagic & 0xFF);
            framePacket[2] = FrameVersion;
            framePacket[3] = frameType;
            framePacket[4] = (byte)((frameIndex >> 24) & 0xFF);
            framePacket[5] = (byte)((frameIndex >> 16) & 0xFF);
            framePacket[6] = (byte)((frameIndex >> 8) & 0xFF);
            framePacket[7] = (byte)(frameIndex & 0xFF);
            framePacket[8] = (byte)((totalDataFrames >> 24) & 0xFF);
            framePacket[9] = (byte)((totalDataFrames >> 16) & 0xFF);
            framePacket[10] = (byte)((totalDataFrames >> 8) & 0xFF);
            framePacket[11] = (byte)(totalDataFrames & 0xFF);
            framePacket[12] = (byte)((groupStart >> 24) & 0xFF);
            framePacket[13] = (byte)((groupStart >> 16) & 0xFF);
            framePacket[14] = (byte)((groupStart >> 8) & 0xFF);
            framePacket[15] = (byte)(groupStart & 0xFF);
            framePacket[16] = (byte)groupCount;
            framePacket[17] = (byte)((payloadLength >> 8) & 0xFF);
            framePacket[18] = (byte)(payloadLength & 0xFF);
        }

        public static byte[] ConvertRgbaToRgb(ReadOnlySpan<byte> rgbaFrame, int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));

            int pixelCount = width * height;
            var rgbFrame = new byte[pixelCount * 3];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcIndex = (y * width + x) * 4;
                    int dstIndex = (y * width + x) * 3;
                    rgbFrame[dstIndex] = rgbaFrame[srcIndex];
                    rgbFrame[dstIndex + 1] = rgbaFrame[srcIndex + 1];
                    rgbFrame[dstIndex + 2] = rgbaFrame[srcIndex + 2];
                }
            }

            return rgbFrame;
        }

        public async Task VerifyAsync()
        {
            if (!await _ffmpeg.IsAvailableAsync())
                throw new InvalidOperationException("ffmpeg not found in PATH or not runnable");
        }

        public async Task EncodeAsync(string inputFile, string outputVideo)
        {
            if (!File.Exists(inputFile))
                throw new FileNotFoundException("Input file not found", inputFile);

            var data = await File.ReadAllBytesAsync(inputFile);

            int borderWidth = _modulator is BinaryGridModulator ? 0 : 32;
            int payloadBytesPerFrame = _modulator.GetPayloadBytesPerFrame(_width, _height, HeaderBytes, borderWidth, _macroblockSize);
            if (payloadBytesPerFrame <= 0)
                throw new InvalidOperationException("Frame capacity too small for metadata header and payload.");

            int blocksX = _width / _macroblockSize;
            int blocksY = _height / _macroblockSize;
            int totalDataFrames = (data.Length + payloadBytesPerFrame - 1) / payloadBytesPerFrame;

            using var ff = await _ffmpeg.StartAsync(outputVideo);
            var stdin = ff.StandardInput;

            // paints for black/white
            using var paintWhite = new SKPaint { Color = SKColors.White, IsAntialias = false };
            using var paintBlack = new SKPaint { Color = SKColors.Black, IsAntialias = false };

            int dataOffset = 0;

            async Task WriteFramePacketAsync(byte[] framePacket)
            {
                byte[] rgbaFrame = _modulator.CreateFrame(_width, _height, borderWidth, framePacket.AsSpan(0, Math.Min(framePacket.Length, _width * _height * 4)));
                byte[] rgbFrame = ConvertRgbaToRgb(rgbaFrame, _width, _height);

                for (int rep = 0; rep < 3; rep++)
                {
                    await stdin.WriteAsync(rgbFrame, 0, rgbFrame.Length);
                    await stdin.FlushAsync();
                }
            }

            try
            {
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
                        dataOffset += payloadLen;
                    }

                    var parityPacket = CreateParityFramePacket(groupStart, groupCount, totalDataFrames, parityPayload);
                    await WriteFramePacketAsync(parityPacket);
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

            await ff.WaitForExitAsync();
        }
    }
}
