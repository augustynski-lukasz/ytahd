using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;
using YTAHD.Core.Core;

namespace YTAHD.Tests
{
    public class DecodedFrameAccumulatorTests
    {
        private const int Width = 128;
        private const int Height = 64;
        private const int Macroblock = 1;
        private const int BitsPerFrame = Width * Height;
        private const int FrameBytes = Width * Height * 3;
        private const int PayloadBytesPerFrame = 256;

        [Fact]
        public void TryAddDecodedFrame_Rejects_Invalid_Packets()
        {
            var accumulator = new DecodedFrameAccumulator();
            var invalidFrame = new byte[FrameBytes];

            Assert.False(accumulator.TryAddDecodedFrame(invalidFrame, Width, Height, Macroblock, Width * 3, FrameBytes, PayloadBytesPerFrame, BitsPerFrame));
            Assert.True(accumulator.SawInvalidPacket);
        }

        [Fact]
        public void RecoverMissingPayloadFrames_Rebuilds_Missing_Data_Using_Parity()
        {
            var accumulator = new DecodedFrameAccumulator();
            var existingPayload = new byte[PayloadBytesPerFrame];
            var missingPayload = new byte[PayloadBytesPerFrame];
            for (int i = 0; i < PayloadBytesPerFrame; i++)
            {
                existingPayload[i] = (byte)(i + 5);
                missingPayload[i] = (byte)(i + 10);
            }

            var parityPayload = new byte[PayloadBytesPerFrame];
            for (int i = 0; i < PayloadBytesPerFrame; i++)
            {
                parityPayload[i] = (byte)(missingPayload[i] ^ existingPayload[i]);
            }

            SetOrderedPayload(accumulator, 1, existingPayload);
            RemoveOrderedPayload(accumulator, 0);
            SetParityPayload(accumulator, 0, parityPayload);
            SetGroupCount(accumulator, 0, 2);
            SetTotalDataFrames(accumulator, 2);

            accumulator.RecoverMissingPayloadFrames(2, PayloadBytesPerFrame, PayloadBytesPerFrame * 2);

            Assert.Equal(missingPayload, accumulator.OrderedPayload[0]);
            Assert.Equal(existingPayload, accumulator.OrderedPayload[1]);
        }

        [Fact]
        public void AssembleOutput_Concatenates_Ordered_Payloads_In_Frame_Order()
        {
            var accumulator = new DecodedFrameAccumulator();
            SetOrderedPayload(accumulator, 0, new byte[] { 1, 2, 3 });
            SetOrderedPayload(accumulator, 1, new byte[] { 4, 5 });
            SetTotalDataFrames(accumulator, 2);

            var output = accumulator.AssembleOutput(5);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, output);
        }

        private static void SetOrderedPayload(DecodedFrameAccumulator accumulator, int frameIndex, byte[] payload)
        {
            var ordered = GetField<SortedDictionary<int, byte[]>>(accumulator, "_orderedPayload");
            ordered[frameIndex] = payload;
        }

        private static void RemoveOrderedPayload(DecodedFrameAccumulator accumulator, int frameIndex)
        {
            var ordered = GetField<SortedDictionary<int, byte[]>>(accumulator, "_orderedPayload");
            ordered.Remove(frameIndex);
        }

        private static void SetParityPayload(DecodedFrameAccumulator accumulator, int groupStart, byte[] payload)
        {
            var parity = GetField<Dictionary<int, byte[]>>(accumulator, "_parityPayloadByGroup");
            parity[groupStart] = payload;
        }

        private static void SetGroupCount(DecodedFrameAccumulator accumulator, int groupStart, int groupCount)
        {
            var groups = GetField<Dictionary<int, int>>(accumulator, "_groupCountByGroup");
            groups[groupStart] = groupCount;
        }

        private static void SetTotalDataFrames(DecodedFrameAccumulator accumulator, int totalDataFrames)
        {
            var property = typeof(DecodedFrameAccumulator).GetProperty("TotalDataFrames", BindingFlags.Instance | BindingFlags.Public);
            var backingField = typeof(DecodedFrameAccumulator).GetField("<TotalDataFrames>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            backingField!.SetValue(accumulator, totalDataFrames);
            Assert.Equal(totalDataFrames, property!.GetValue(accumulator));
        }

        private static T GetField<T>(DecodedFrameAccumulator accumulator, string fieldName)
        {
            var field = typeof(DecodedFrameAccumulator).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return (T)field!.GetValue(accumulator)!;
        }
    }
}
