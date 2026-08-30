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

            int blocksX = _width / _macroblockSize;
            int blocksY = _height / _macroblockSize;
            int bitsPerFrame = blocksX * blocksY; // Phase 1: 1 bit per macroblock

            int headerBits = HeaderBytes * 8;
            int payloadBitsPerFrame = bitsPerFrame - headerBits;
            if (payloadBitsPerFrame < 8)
                throw new InvalidOperationException("Frame capacity too small for metadata header and payload.");

            int payloadBytesPerFrame = payloadBitsPerFrame / 8;
            int totalDataFrames = (data.Length + payloadBytesPerFrame - 1) / payloadBytesPerFrame;

            using var ff = await _ffmpeg.StartAsync(outputVideo);
            var stdin = ff.StandardInput;

            // paints for black/white
            using var paintWhite = new SKPaint { Color = SKColors.White, IsAntialias = false };
            using var paintBlack = new SKPaint { Color = SKColors.Black, IsAntialias = false };

            int dataOffset = 0;

            async Task WriteFramePacketAsync(byte[] framePacket)
            {
                byte[] rgbFrame = null;

                if (_modulator is PseudoQamModulator)
                {
                    var rgbaFrame = PseudoQamModulator.CreatePhase2Frame(_width, _height, borderWidth: 32, framePacket.AsSpan(0, Math.Min(framePacket.Length, _width * _height * 4)));
                    rgbFrame = new byte[_width * _height * 3];
                    for (int y = 0; y < _height; y++)
                    {
                        for (int x = 0; x < _width; x++)
                        {
                            int srcIndex = (y * _width + x) * 4;
                            int dstIndex = (y * _width + x) * 3;
                            rgbFrame[dstIndex] = rgbaFrame[srcIndex];
                            rgbFrame[dstIndex + 1] = rgbaFrame[srcIndex + 1];
                            rgbFrame[dstIndex + 2] = rgbaFrame[srcIndex + 2];
                        }
                    }
                }
                else if (_modulator is DctModulator)
                {
                    var rgbaFrame = DctModulator.CreatePhase3Frame(_width, _height, borderWidth: 32, framePacket.AsSpan(0, Math.Min(framePacket.Length, _width * _height * 4)));
                    rgbFrame = new byte[_width * _height * 3];
                    for (int y = 0; y < _height; y++)
                    {
                        for (int x = 0; x < _width; x++)
                        {
                            int srcIndex = (y * _width + x) * 4;
                            int dstIndex = (y * _width + x) * 3;
                            rgbFrame[dstIndex] = rgbaFrame[srcIndex];
                            rgbFrame[dstIndex + 1] = rgbaFrame[srcIndex + 1];
                            rgbFrame[dstIndex + 2] = rgbaFrame[srcIndex + 2];
                        }
                    }
                }
                else
                {
                    var info = new SKImageInfo(_width, _height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    using var surface = SKSurface.Create(info);
                    var canvas = surface.Canvas;

                    canvas.Clear(SKColors.Black);

                    for (int by = 0; by < blocksY; by++)
                    {
                        for (int bx = 0; bx < blocksX; bx++)
                        {
                            int frameBitIndex = by * blocksX + bx;
                            bool bit = false;
                            if (frameBitIndex < framePacket.Length * 8)
                            {
                                int byteIdx = frameBitIndex / 8;
                                int bitInByte = 7 - (frameBitIndex % 8);
                                bit = ((framePacket[byteIdx] >> bitInByte) & 1) != 0;
                            }

                            int x = bx * _macroblockSize;
                            int y = by * _macroblockSize;
                            var rect = new SKRectI(x, y, x + _macroblockSize, y + _macroblockSize);
                            canvas.DrawRect(rect, bit ? paintWhite : paintBlack);
                        }
                    }

                    using var image = surface.Snapshot();
                    using var pixmap = image.PeekPixels();

                    int rowBytes = _width * 3;
                    int frameBytes = rowBytes * _height;
                    int srcTotal = pixmap.RowBytes * _height;
                    var srcBuf = ArrayPool<byte>.Shared.Rent(srcTotal);
                    var dstBuf = ArrayPool<byte>.Shared.Rent(frameBytes);
                    try
                    {
                        var srcPtr = pixmap.GetPixels();
                        System.Runtime.InteropServices.Marshal.Copy(srcPtr, srcBuf, 0, srcTotal);

                        Span<byte> srcSpan = srcBuf.AsSpan(0, srcTotal);
                        Span<byte> dstSpan = dstBuf.AsSpan(0, frameBytes);

                        for (int row = 0; row < _height; row++)
                        {
                            int srcRowStart = row * pixmap.RowBytes;
                            int dstRowStart = row * rowBytes;
                            for (int col = 0; col < _width; col++)
                            {
                                int srcIdx = srcRowStart + col * 4;
                                int dstIdx = dstRowStart + col * 3;
                                dstSpan[dstIdx] = srcSpan[srcIdx];
                                dstSpan[dstIdx + 1] = srcSpan[srcIdx + 1];
                                dstSpan[dstIdx + 2] = srcSpan[srcIdx + 2];
                            }
                        }

                        rgbFrame = dstBuf.ToArray();
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(srcBuf);
                        ArrayPool<byte>.Shared.Return(dstBuf);
                    }
                }

                if (rgbFrame != null)
                {
                    for (int rep = 0; rep < 3; rep++)
                    {
                        await stdin.WriteAsync(rgbFrame, 0, rgbFrame.Length);
                        await stdin.FlushAsync();
                    }
                }
            }

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

                    var framePacket = new byte[HeaderBytes + payloadBytesPerFrame];
                    framePacket[0] = (byte)((FrameMagic >> 8) & 0xFF);
                    framePacket[1] = (byte)(FrameMagic & 0xFF);
                    framePacket[2] = FrameVersion;
                    framePacket[3] = FrameTypeData;
                    framePacket[4] = (byte)((frameIdx >> 24) & 0xFF);
                    framePacket[5] = (byte)((frameIdx >> 16) & 0xFF);
                    framePacket[6] = (byte)((frameIdx >> 8) & 0xFF);
                    framePacket[7] = (byte)(frameIdx & 0xFF);
                    framePacket[8] = (byte)((totalDataFrames >> 24) & 0xFF);
                    framePacket[9] = (byte)((totalDataFrames >> 16) & 0xFF);
                    framePacket[10] = (byte)((totalDataFrames >> 8) & 0xFF);
                    framePacket[11] = (byte)(totalDataFrames & 0xFF);
                    framePacket[12] = (byte)((groupStart >> 24) & 0xFF);
                    framePacket[13] = (byte)((groupStart >> 16) & 0xFF);
                    framePacket[14] = (byte)((groupStart >> 8) & 0xFF);
                    framePacket[15] = (byte)(groupStart & 0xFF);
                    framePacket[16] = (byte)groupCount;
                    framePacket[17] = (byte)((payloadLen >> 8) & 0xFF);
                    framePacket[18] = (byte)(payloadLen & 0xFF);

                    var hash = SHA256.HashData(payload.AsSpan(0, payloadLen));
                    Buffer.BlockCopy(hash, 0, framePacket, 19, hash.Length);
                    Buffer.BlockCopy(payload, 0, framePacket, HeaderBytes, payloadBytesPerFrame);

                    await WriteFramePacketAsync(framePacket);
                    dataOffset += payloadLen;
                }

                var parityPacket = new byte[HeaderBytes + payloadBytesPerFrame];
                parityPacket[0] = (byte)((FrameMagic >> 8) & 0xFF);
                parityPacket[1] = (byte)(FrameMagic & 0xFF);
                parityPacket[2] = FrameVersion;
                parityPacket[3] = FrameTypeParity;
                parityPacket[4] = 0;
                parityPacket[5] = 0;
                parityPacket[6] = 0;
                parityPacket[7] = 0;
                parityPacket[8] = (byte)((totalDataFrames >> 24) & 0xFF);
                parityPacket[9] = (byte)((totalDataFrames >> 16) & 0xFF);
                parityPacket[10] = (byte)((totalDataFrames >> 8) & 0xFF);
                parityPacket[11] = (byte)(totalDataFrames & 0xFF);
                parityPacket[12] = (byte)((groupStart >> 24) & 0xFF);
                parityPacket[13] = (byte)((groupStart >> 16) & 0xFF);
                parityPacket[14] = (byte)((groupStart >> 8) & 0xFF);
                parityPacket[15] = (byte)(groupStart & 0xFF);
                parityPacket[16] = (byte)groupCount;
                parityPacket[17] = (byte)((payloadBytesPerFrame >> 8) & 0xFF);
                parityPacket[18] = (byte)(payloadBytesPerFrame & 0xFF);

                var parityHash = SHA256.HashData(parityPayload);
                Buffer.BlockCopy(parityHash, 0, parityPacket, 19, parityHash.Length);
                Buffer.BlockCopy(parityPayload, 0, parityPacket, HeaderBytes, payloadBytesPerFrame);

                await WriteFramePacketAsync(parityPacket);
            }

            await ff.WaitForExitAsync();
        }
    }
}
