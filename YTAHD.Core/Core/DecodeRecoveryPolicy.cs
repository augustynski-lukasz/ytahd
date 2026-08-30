using System;

namespace YTAHD.Core.Core
{
    public static class DecodeRecoveryPolicy
    {
        public static bool ShouldStopDecoding(DecodedFrameAccumulator accumulator, int expectedOutputBytes)
        {
            if (accumulator == null)
                throw new ArgumentNullException(nameof(accumulator));

            if (expectedOutputBytes <= 0)
            {
                return false;
            }

            int total = 0;
            foreach (var payload in accumulator.OrderedPayload.Values)
            {
                total += payload.Length;
                if (total >= expectedOutputBytes)
                {
                    return true;
                }
            }

            return false;
        }

        public static int ResolveExpectedOutputBytes(DecodedFrameAccumulator accumulator, int expectedOutputBytes)
        {
            if (accumulator == null)
                throw new ArgumentNullException(nameof(accumulator));

            if (expectedOutputBytes > 0)
            {
                return expectedOutputBytes;
            }

            int total = 0;
            foreach (var payload in accumulator.OrderedPayload.Values)
            {
                total += payload.Length;
            }

            return total;
        }
    }
}
