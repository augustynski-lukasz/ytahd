using System;

namespace YTAHD.Core.Core
{
    public static class FrameLayoutCalculator
    {
        public static int CalculatePayloadBytesPerFrame(int width, int height, int blockWidth, int blockHeight, int headerBytes, int borderWidth = 0)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            if (blockWidth <= 0 || blockHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(blockWidth));
            }

            if (headerBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(headerBytes));
            }

            if (borderWidth < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(borderWidth));
            }

            int usableWidth = Math.Max(0, width - (borderWidth * 2));
            int usableHeight = Math.Max(0, height - (borderWidth * 2));
            int blocksX = Math.Max(1, usableWidth / blockWidth);
            int blocksY = Math.Max(1, usableHeight / blockHeight);
            int bitsPerFrame = blocksX * blocksY;
            int payloadBitsPerFrame = bitsPerFrame - (headerBytes * 8);
            return payloadBitsPerFrame >= 8 ? payloadBitsPerFrame / 8 : 0;
        }
    }
}
