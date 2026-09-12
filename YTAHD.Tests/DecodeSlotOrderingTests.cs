using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    /// <summary>
    /// Regression coverage for the parallel decode orchestrator's result-slot accounting.
    /// </summary>
    /// <remarks>
    /// The slot semaphore used to be acquired by each worker after its decode, but released by the
    /// aggregator only on <em>in-order</em> consumption. A slow head frame could then see every slot
    /// taken by later frames the aggregator was still holding out of order, and the run froze with
    /// every test already reported as passed. The fix reserves slots in the reader, in sequence
    /// order, before dispatching work. See docs/decisions/CR-20260912-04-child-pipe-thread-pool-starvation.md.
    /// </remarks>
    public class DecodeSlotOrderingTests
    {
        private const int Width = 128;
        private const int Height = 64;
        private const int Macroblock = 1;

        // Large enough that the encode produces well over the eight result slots the orchestrator
        // creates for four workers, so out-of-order slot exhaustion is actually reachable.
        private const int PayloadBytes = 8 * 1024;

        [Fact]
        public async Task Parallel_Decode_Completes_When_The_Head_Frame_Decodes_Slowest()
        {
            var raw = await EncodeAsync(new Random(77), PayloadBytes);

            // The head frame's decode is gated until three later frames have finished, which is the
            // precondition that used to exhaust the result slots out of order and deadlock.
            var gating = new HeadGatedModulator();
            var orchestrator = new DecodeStreamOrchestrator(gating, Width, Height, Macroblock, false, null, 4);
            var output = await orchestrator.ProcessAsync(new MemoryStream(raw, writable: false), PayloadBytes);

            Assert.Equal(PayloadBytes, output.Length);
            Assert.True(gating.HeadWasGated, "The test did not exercise the slow-head precondition.");
        }

        [Fact]
        public async Task Parallel_Decode_Matches_Serial_Output_With_Slow_Head_Frame()
        {
            var raw = await EncodeAsync(new Random(78), PayloadBytes);

            var serial = new DecodeStreamOrchestrator(new BinaryGridModulator(), Width, Height, Macroblock);
            var serialOutput = await serial.ProcessAsync(new MemoryStream(raw, writable: false), PayloadBytes);

            var gating = new HeadGatedModulator();
            var parallel = new DecodeStreamOrchestrator(gating, Width, Height, Macroblock, false, null, 4);
            var parallelOutput = await parallel.ProcessAsync(new MemoryStream(raw, writable: false), PayloadBytes);

            Assert.Equal(serialOutput, parallelOutput);
            Assert.True(gating.HeadWasGated, "The test did not exercise the slow-head precondition.");
        }

        private static async Task<byte[]> EncodeAsync(Random random, int payloadBytes)
        {
            byte[] data = new byte[payloadBytes];
            random.NextBytes(data);
            var tmpIn = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(tmpIn, data);

                var modulator = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(modulator, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");
                return fake.Process!.Buffer.ToArray();
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }

        /// <summary>
        /// Decorator whose first per-frame decode call blocks until three later frames have completed,
        /// deterministically producing the out-of-order slot exhaustion that used to deadlock the
        /// parallel decode pipeline.
        /// </summary>
        /// <remarks>
        /// The decode path reaches the modulator through <see cref="IModulator.GetBorderWidth"/>
        /// (called once per frame inside <c>DecoderEngine.TryReadDecodedPacket</c>, on the worker
        /// thread), which is therefore the hook that can make one frame's decode slower than the rest.
        /// <see cref="FrameBitDecoderFactory.CreateForModulator"/> unwraps the decorator so the worker
        /// still gets the real <c>BinaryGridFrameBitDecoder</c>.
        /// </remarks>
        private sealed class HeadGatedModulator : IModulatorDecorator
        {
            // The orchestrator's channel capacity for four workers is max(2, 4 * 2) = 8 result slots.
            // The upfront geometry call plus eight per-frame decodes exhaust them, so the gate waits
            // for nine completions before releasing the head frame's worker.
            private const int GateThreshold = 9;

            private readonly BinaryGridModulator _inner = new();
            private int _borderCalls;
            private int _completedCalls;

            public IModulator Inner => _inner;

            public bool HeadWasGated { get; private set; }

            public int MacroblockWidth => _inner.MacroblockWidth;

            public int MacroblockHeight => _inner.MacroblockHeight;

            public int GetPayloadBytesPerFrame(ModulatorGeometry geometry) => _inner.GetPayloadBytesPerFrame(geometry);

            public int GetPacketBufferLength(ModulatorGeometry geometry, int payloadBytesPerFrame) => _inner.GetPacketBufferLength(geometry, payloadBytesPerFrame);

            public int GetBorderWidth(ModulatorGeometry geometry)
            {
                int call = Interlocked.Increment(ref _borderCalls);

                // Call 1 is the orchestrator's upfront geometry probe on the caller thread; call 2 is
                // the first worker's per-frame decode. Gating the first per-frame decode holds the
                // head item's worker while the other workers decode later frames - the precondition
                // under which the old worker-side slot acquisition deadlocked.
                if (call == 2)
                {
                    HeadWasGated = true;

                    // Hold until every result slot is spoken for: with four workers the orchestrator
                    // creates max(2, 4 * 2) = 8 slots, so eight per-frame decodes (calls 3..10) plus
                    // the upfront call exhaust them. A no-progress escape keeps the gate honest when
                    // fewer frames are available than the threshold needs.
                    var stopwatch = Stopwatch.StartNew();
                    int lastSeen = -1;
                    long lastChangeMilliseconds = 0;
                    SpinWait.SpinUntil(
                        () =>
                        {
                            int current = Volatile.Read(ref _completedCalls);
                            if (current >= GateThreshold) return true;
                            if (current != lastSeen)
                            {
                                lastSeen = current;
                                lastChangeMilliseconds = stopwatch.ElapsedMilliseconds;
                            }

                            return stopwatch.ElapsedMilliseconds - lastChangeMilliseconds > 100;
                        },
                        TimeSpan.FromSeconds(10));
                }

                Interlocked.Increment(ref _completedCalls);
                return _inner.GetBorderWidth(geometry);
            }

            public byte[] CreateFrame(ModulatorGeometry geometry, ReadOnlySpan<byte> payload) => _inner.CreateFrame(geometry, payload);

            public void Encode(ReadOnlySpan<byte> input, Span<byte> pixelBuffer) => _inner.Encode(input, pixelBuffer);

            public void Decode(ReadOnlySpan<byte> pixelBuffer, Span<byte> output) => _inner.Decode(pixelBuffer, output);
        }
    }
}
