using System;
using System.Threading;
using Xunit;
using YTAHD.Core.Application;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    public class ParallelismPolicyTests
    {
        // ── frame-level worker resolution ────────────────────────────────────────────

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 1)]
        [InlineData(4, 3)]
        [InlineData(5, 4)]
        [InlineData(8, 4)]
        [InlineData(64, 4)]
        public void Auto_Leaves_A_Processor_For_The_Codec_And_Is_Capped(int logicalProcessors, int expected)
        {
            // auto = min(AutoWorkerCap, cores - 1), never below one.
            Assert.Equal(expected, ParallelismPolicy.Resolve(ParallelismPolicy.Auto, logicalProcessors));
        }

        [Fact]
        public void Auto_Respects_AutoWorkerCap_On_Large_Machines()
        {
            Assert.Equal(ParallelismPolicy.AutoWorkerCap, ParallelismPolicy.Resolve(ParallelismPolicy.Auto, 128));
        }

        [Theory]
        [InlineData(2, 2)]
        [InlineData(8, 8)]
        [InlineData(64, 64)]
        public void Explicit_Worker_Count_Is_Used_As_Requested(int requested, int expected)
        {
            // An explicit request is not capped by core count: the caller may know better.
            Assert.Equal(expected, ParallelismPolicy.Resolve(requested, 4));
        }

        [Theory]
        [InlineData(65)]
        [InlineData(4096)]
        public void Explicit_Worker_Count_Is_Clamped_To_MaxWorkerLimit(int requested)
        {
            Assert.Equal(ParallelismPolicy.MaxWorkerLimit, ParallelismPolicy.Resolve(requested, 8));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void Zero_And_One_Select_The_Serial_Frame_Pipeline(int requested)
        {
            // Zero is the unconfigured VideoCodecOptions default; one is the explicit serial request.
            Assert.Equal(1, ParallelismPolicy.Resolve(requested, 16));
        }

        [Theory]
        [InlineData(-2)]
        [InlineData(-100)]
        public void Negative_Request_That_Is_Not_Auto_Throws(int requested)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ParallelismPolicy.Resolve(requested, 8));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Non_Positive_Logical_Processor_Count_Throws(int logicalProcessors)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ParallelismPolicy.Resolve(ParallelismPolicy.Auto, logicalProcessors));
        }

        [Fact]
        public void Parameterless_Overload_Resolves_Against_Environment()
        {
            Assert.True(ParallelismPolicy.Resolve(ParallelismPolicy.Auto) >= 1);
            Assert.Equal(1, ParallelismPolicy.Resolve(0));
            Assert.Equal(1, ParallelismPolicy.Resolve(1));
        }

        // ── inner-loop worker resolution ─────────────────────────────────────────────

        [Fact]
        public void Unconfigured_Default_Keeps_Every_Processor_For_The_Inner_Loop()
        {
            // The historical behaviour: no frame-level parallelism, inner loops use the machine.
            Assert.Equal(8, ParallelismPolicy.ResolveInnerDegree(0, frameWorkers: 1, logicalProcessorCount: 8));
        }

        [Theory]
        [InlineData(1, 8)]
        [InlineData(4, 8)]
        public void Explicit_Serial_Request_Forces_The_Inner_Loop_Serial(int frameWorkers, int logicalProcessors)
        {
            // Deterministic debugging: one worker everywhere, regardless of the frame worker count.
            Assert.Equal(1, ParallelismPolicy.ResolveInnerDegree(1, frameWorkers, logicalProcessors));
        }

        [Theory]
        // requested, frameWorkers, logicalProcessors, expected
        [InlineData(ParallelismPolicy.Auto, 4, 8, 2)]
        [InlineData(ParallelismPolicy.Auto, 8, 8, 1)]
        [InlineData(ParallelismPolicy.Auto, 32, 8, 1)]
        [InlineData(ParallelismPolicy.Auto, 1, 2, 2)]
        [InlineData(4, 1, 8, 8)]
        [InlineData(4, 4, 8, 2)]
        [InlineData(64, 64, 8, 1)]
        public void Inner_Degree_Splits_The_Budget_With_Frame_Workers(int requested, int frameWorkers, int logicalProcessors, int expected)
        {
            Assert.Equal(expected, ParallelismPolicy.ResolveInnerDegree(requested, frameWorkers, logicalProcessors));
        }

        [Fact]
        public void Inner_Degree_Is_Never_Below_One()
        {
            Assert.Equal(1, ParallelismPolicy.ResolveInnerDegree(ParallelismPolicy.Auto, 64, 2));
        }

        [Fact]
        public void Inner_Degree_Invalid_Frame_Worker_Count_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ParallelismPolicy.ResolveInnerDegree(ParallelismPolicy.Auto, frameWorkers: 0, logicalProcessorCount: 8));
        }

        [Fact]
        public void Inner_Degree_Invalid_Logical_Processor_Count_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ParallelismPolicy.ResolveInnerDegree(ParallelismPolicy.Auto, frameWorkers: 2, logicalProcessorCount: 0));
        }

        [Theory]
        [InlineData(-2)]
        [InlineData(-100)]
        public void Inner_Degree_Negative_Request_That_Is_Not_Auto_Throws(int requested)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ParallelismPolicy.ResolveInnerDegree(requested, frameWorkers: 2, logicalProcessorCount: 8));
        }

        [Fact]
        public void Inner_Degree_Parameterless_Overload_Uses_Environment()
        {
            Assert.Equal(1, ParallelismPolicy.ResolveInnerDegree(1, frameWorkers: 1));
            Assert.Equal(1, ParallelismPolicy.ResolveInnerDegree(ParallelismPolicy.Auto, frameWorkers: 64));
        }

        // ── options integration ──────────────────────────────────────────────────────

        [Fact]
        public void CodecOptions_Default_Stays_Serial_Framed_With_Parallel_Inner_Loops()
        {
            var options = new VideoCodecOptions();

            Assert.Equal(0, options.MaxDegreeOfParallelism);
            Assert.Equal(1, ParallelismPolicy.Resolve(options.MaxDegreeOfParallelism, 8));
            Assert.Equal(8, ParallelismPolicy.ResolveInnerDegree(options.MaxDegreeOfParallelism, frameWorkers: 1, logicalProcessorCount: 8));
        }

        // ── inner loop helper ────────────────────────────────────────────────────────

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(64)]
        public void ForEachRow_Visits_Every_Row_Exactly_Once(int degreeOfParallelism)
        {
            const int rowCount = 10;
            var visits = new int[rowCount];

            InnerLoopParallelism.ForEachRow(rowCount, degreeOfParallelism, row => Interlocked.Increment(ref visits[row]));

            for (int row = 0; row < rowCount; row++)
            {
                Assert.Equal(1, visits[row]);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void ForEachRow_Ignores_Empty_Row_Ranges(int rowCount)
        {
            int calls = 0;
            InnerLoopParallelism.ForEachRow(rowCount, degreeOfParallelism: 4, _ => calls++);

            Assert.Equal(0, calls);
        }

        [Fact]
        public void ForEachRow_Rejects_Null_Body()
        {
            Assert.Throws<ArgumentNullException>(() => InnerLoopParallelism.ForEachRow(4, 2, null!));
        }

        // ── modulator / decoder wiring ───────────────────────────────────────────────

        [Fact]
        public void DctModulator_Renders_Identical_Frames_Serially_And_In_Parallel()
        {
            var payload = new byte[64];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i * 7 + 3);
            }

            byte[] parallel = DctModulator.CreatePhase3Frame(64, 64, 0, payload, Environment.ProcessorCount);
            byte[] serial = DctModulator.CreatePhase3Frame(64, 64, 0, payload, 1);

            Assert.Equal(parallel, serial);
        }

        [Fact]
        public void DctModulator_Instance_Render_Honours_The_Configured_Inner_Degree()
        {
            var payload = new byte[64];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i * 11 + 5);
            }

            var geometry = new ModulatorGeometry(64, 64, 8, 0);
            var parallel = new DctModulator();
            var serial = new DctModulator { InnerDegreeOfParallelism = 1 };

            Assert.Equal(parallel.CreateFrame(geometry, payload), serial.CreateFrame(geometry, payload));
        }

        [Fact]
        public void FrameBitDecoderFactory_Inherits_The_Modulator_Inner_Degree()
        {
            var modulator = new DctModulator { InnerDegreeOfParallelism = 1 };

            var decoder = FrameBitDecoderFactory.CreateForModulator(modulator);

            var configurable = Assert.IsAssignableFrom<IParallelismConfigurable>(decoder);
            Assert.Equal(1, configurable.InnerDegreeOfParallelism);
        }

        [Fact]
        public void FrameBitDecoderFactory_Still_Creates_Decoders_Without_Inner_Parallelism()
        {
            var decoder = FrameBitDecoderFactory.CreateForModulator(new BinaryGridModulator(16, 16));

            Assert.NotNull(decoder);
            Assert.IsNotAssignableFrom<IParallelismConfigurable>(decoder);
        }
    }
}
