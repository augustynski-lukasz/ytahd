using System;

namespace YTAHD.Tests
{
    internal static class DecoderTestHelpers
    {
        public static byte[] RemoveLogicalFrameCopies(byte[] raw, int frameBytes, int repeatsPerLogicalFrame, params int[] logicalFrameIndexes)
        {
            if (logicalFrameIndexes == null || logicalFrameIndexes.Length == 0)
            {
                return raw;
            }

            int logicalSize = frameBytes * repeatsPerLogicalFrame;
            Array.Sort(logicalFrameIndexes);

            var output = new byte[raw.Length - (logicalSize * logicalFrameIndexes.Length)];
            int srcPos = 0;
            int dstPos = 0;

            foreach (int logicalFrameIndex in logicalFrameIndexes)
            {
                int start = logicalFrameIndex * logicalSize;
                if (start < srcPos || start + logicalSize > raw.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(logicalFrameIndexes));
                }

                int copyLen = start - srcPos;
                if (copyLen > 0)
                {
                    Buffer.BlockCopy(raw, srcPos, output, dstPos, copyLen);
                    dstPos += copyLen;
                }

                srcPos = start + logicalSize;
            }

            if (srcPos < raw.Length)
            {
                Buffer.BlockCopy(raw, srcPos, output, dstPos, raw.Length - srcPos);
            }

            return output;
        }

        public static byte[] RemovePhysicalFrames(byte[] raw, int frameBytes, params int[] physicalFrameIndexes)
        {
            if (physicalFrameIndexes == null || physicalFrameIndexes.Length == 0)
            {
                return raw;
            }

            Array.Sort(physicalFrameIndexes);
            var output = new byte[raw.Length - (frameBytes * physicalFrameIndexes.Length)];
            int srcPos = 0;
            int dstPos = 0;

            foreach (int physicalFrameIndex in physicalFrameIndexes)
            {
                int start = physicalFrameIndex * frameBytes;
                if (start < srcPos || start + frameBytes > raw.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(physicalFrameIndexes));
                }

                int copyLen = start - srcPos;
                if (copyLen > 0)
                {
                    Buffer.BlockCopy(raw, srcPos, output, dstPos, copyLen);
                    dstPos += copyLen;
                }

                srcPos = start + frameBytes;
            }

            if (srcPos < raw.Length)
            {
                Buffer.BlockCopy(raw, srcPos, output, dstPos, raw.Length - srcPos);
            }

            return output;
        }
    }
}
