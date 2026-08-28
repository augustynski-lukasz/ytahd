using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;
using SkiaSharp;
using YTAHD.Cli.Modulation;
using YTAHD.Cli.Infrastructure;

namespace YTAHD.Cli.Core
{
    public class EncoderEngine
    {
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

            int totalBits = data.Length * 8;
            int framesNeeded = (totalBits + bitsPerFrame - 1) / bitsPerFrame;

            using var ff = await _ffmpeg.StartAsync(outputVideo);
            var stdin = ff.StandardInput;

            // paints for black/white
            using var paintWhite = new SKPaint { Color = SKColors.White, IsAntialias = false };
            using var paintBlack = new SKPaint { Color = SKColors.Black, IsAntialias = false };

            int bitIndex = 0;

            for (int frameIdx = 0; frameIdx < framesNeeded; frameIdx++)
            {
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
                        bool bit = false;
                        if (bitIndex < totalBits)
                        {
                            int byteIdx = bitIndex / 8;
                            int bitInByte = 7 - (bitIndex % 8); // MSB first
                            bit = ((data[byteIdx] >> bitInByte) & 1) != 0;
                        }

                        int x = bx * _macroblockSize;
                        int y = by * _macroblockSize;
                        var rect = new SKRectI(x, y, x + _macroblockSize, y + _macroblockSize);
                        canvas.DrawRect(rect, bit ? paintWhite : paintBlack);
                        bitIndex++;
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
