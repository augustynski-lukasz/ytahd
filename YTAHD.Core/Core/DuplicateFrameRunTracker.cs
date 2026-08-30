using System;

namespace YTAHD.Core.Core
{
    public sealed class DuplicateFrameRun
    {
        public DuplicateFrameRun(byte[] bestFrame, int runLength, int bestQuality = 0)
        {
            BestFrame = bestFrame ?? throw new ArgumentNullException(nameof(bestFrame));
            RunLength = runLength;
            BestQuality = bestQuality;
        }

        public byte[] BestFrame { get; }
        public int RunLength { get; }
        public int BestQuality { get; }
    }

    public sealed class DuplicateFrameRunTracker
    {
        private byte[] _bestFrame = Array.Empty<byte>();
        private byte[] _logicalSignature = Array.Empty<byte>();

        public bool HasCurrentRun { get; private set; }
        public int RunLength { get; private set; }
        public int BestQuality { get; private set; } = int.MinValue;
        public byte[] BestFrame => _bestFrame;

        public DuplicateFrameRun? Update(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> logicalSignature, int quality)
        {
            if (!HasCurrentRun)
            {
                SetCurrentRun(frame, logicalSignature, quality);
                return null;
            }

            if (logicalSignature.SequenceEqual(_logicalSignature))
            {
                RunLength++;
                if (quality > BestQuality)
                {
                    BestQuality = quality;
                    _bestFrame = frame.ToArray();
                }

                return null;
            }

            var completedRun = new DuplicateFrameRun(_bestFrame.ToArray(), RunLength, BestQuality);
            SetCurrentRun(frame, logicalSignature, quality);
            return completedRun;
        }

        public void Flush(DuplicateFrameRun run, int repeatedFrameCount, Func<byte[], bool> decodePayloadFrame)
        {
            if (run == null)
            {
                throw new ArgumentNullException(nameof(run));
            }

            int payloadCopies = GetDuplicateFrameCount(run.RunLength, repeatedFrameCount);
            for (int i = 0; i < payloadCopies; i++)
            {
                decodePayloadFrame(run.BestFrame);
            }
        }

        public void FlushCurrentRun(int repeatedFrameCount, Func<byte[], bool> decodePayloadFrame)
        {
            if (!HasCurrentRun || RunLength <= 0 || _bestFrame.Length == 0)
            {
                return;
            }

            Flush(new DuplicateFrameRun(_bestFrame.ToArray(), RunLength, BestQuality), repeatedFrameCount, decodePayloadFrame);
            Reset();
        }

        public void Reset()
        {
            HasCurrentRun = false;
            RunLength = 0;
            BestQuality = int.MinValue;
            _bestFrame = Array.Empty<byte>();
            _logicalSignature = Array.Empty<byte>();
        }

        private void SetCurrentRun(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> logicalSignature, int quality)
        {
            HasCurrentRun = true;
            RunLength = 1;
            BestQuality = quality;
            _bestFrame = frame.ToArray();
            _logicalSignature = logicalSignature.ToArray();
        }

        private static int GetDuplicateFrameCount(int runLength, int repeatedFrameCount)
        {
            if (repeatedFrameCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(repeatedFrameCount));
            }

            return Math.Max(1, (runLength + (repeatedFrameCount / 2)) / repeatedFrameCount);
        }
    }
}
