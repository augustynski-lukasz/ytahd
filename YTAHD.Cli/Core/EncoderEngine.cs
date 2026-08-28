using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SkiaSharp;
using YTAHD.Cli.Modulation;
using YTAHD.Cli.Infrastructure;

namespace YTAHD.Cli.Core
{
    public class EncoderEngine
    {
        private const int FrameMagic = 0x5954; // 'YT'
        private const byte FrameVersion = 1;
        private const int HeaderBytes = 2 + 1 + 4 + 2 + 32; // magic + version + frameIndex + payloadLen + sha256

        private readonly IModulator _modulator;
        private readonly YTAHD.Cli.Infrastructure.IFFmpegWrapper _ffmpeg;
        private readonly int _macroblockSize;
        private readonly int _width;
        private readonly int _height;
        private readonly int _fps;

        public EncoderEngine(IModulator modulator, YTAHD.Cli.Infrastructure.IFFmpegWrapper ffmpeg, int macroblockSize = 16, int width = 3840, int height = 2160, int fps = 60)
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
            int framesNeeded = (data.Length + payloadBytesPerFrame - 1) / payloadBytesPerFrame;

            using var ff = await _ffmpeg.StartAsync(outputVideo);
            var stdin = ff.StandardInput;

            // paints for black/white
            using var paintWhite = new SKPaint { Color = SKColors.White, IsAntialias = false };
            using var paintBlack = new SKPaint { Color = SKColors.Black, IsAntialias = false };

            int dataOffset = 0;

            for (int frameIdx = 0; frameIdx < framesNeeded; frameIdx++)
            {
                int payloadLen = Math.Min(payloadBytesPerFrame, data.Length - dataOffset);
                var payload = new ReadOnlySpan<byte>(data, dataOffset, payloadLen);

                var framePacket = new byte[HeaderBytes + payloadBytesPerFrame];
                framePacket[0] = (byte)((FrameMagic >> 8) & 0xFF);
                framePacket[1] = (byte)(FrameMagic & 0xFF);
                framePacket[2] = FrameVersion;

                framePacket[3] = (byte)((frameIdx >> 24) & 0xFF);
                framePacket[4] = (byte)((frameIdx >> 16) & 0xFF);
                framePacket[5] = (byte)((frameIdx >> 8) & 0xFF);
                framePacket[6] = (byte)(frameIdx & 0xFF);

                framePacket[7] = (byte)((payloadLen >> 8) & 0xFF);
                framePacket[8] = (byte)(payloadLen & 0xFF);

                var hash = SHA256.HashData(payload);
                Buffer.BlockCopy(hash, 0, framePacket, 9, hash.Length);
                if (payloadLen > 0)
                {
                    Buffer.BlockCopy(data, dataOffset, framePacket, HeaderBytes, payloadLen);
                }

                dataOffset += payloadLen;

                // create surface
                var info = new SKImageInfo(_width, _height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                using var surface = SKSurface.Create(info);
                var canvas = surface.Canvas;

                // optional: draw calibration border (simple checker border)
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
                            int bitInByte = 7 - (frameBitIndex % 8); // MSB first
                            bit = ((framePacket[byteIdx] >> bitInByte) & 1) != 0;
                        }

                        int x = bx * _macroblockSize;
                        int y = by * _macroblockSize;
                        var rect = new SKRectI(x, y, x + _macroblockSize, y + _macroblockSize);
                        canvas.DrawRect(rect, bit ? paintWhite : paintBlack);
                    }
                }

                // repeat each datagram frame 3x to stabilize inter-frame compression
                using var image = surface.Snapshot();
                using var pixmap = image.PeekPixels();

                // copy RGBA -> RGB24 without unsafe code
                int rowBytes = _width * 3;
                int frameBytes = rowBytes * _height;
                int srcTotal = pixmap.RowBytes * _height; // RGBA source bytes including possible padding
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
                            int srcIdx = srcRowStart + col * 4; // RGBA
                            int dstIdx = dstRowStart + col * 3; // RGB
                            dstSpan[dstIdx] = srcSpan[srcIdx];
                            dstSpan[dstIdx + 1] = srcSpan[srcIdx + 1];
                            dstSpan[dstIdx + 2] = srcSpan[srcIdx + 2];
                        }
                    }

                    // write the same RGB frame 3 times
                    for (int rep = 0; rep < 3; rep++)
                    {
                        await stdin.WriteAsync(dstBuf, 0, frameBytes);
                        await stdin.FlushAsync();
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(srcBuf);
                    ArrayPool<byte>.Shared.Return(dstBuf);
                }
            }

            await ff.WaitForExitAsync();
        }
    }
}
