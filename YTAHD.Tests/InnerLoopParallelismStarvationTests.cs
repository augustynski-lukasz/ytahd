using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    /// <summary>
    /// Regression coverage for the encode/decode pipeline stall described in
    /// <c>docs/decisions/CR-20260912-03-inner-loop-thread-pool-starvation.md</c>.
    /// </summary>
    /// <remarks>
    /// The modulators and frame bit decoders call <see cref="InnerLoopParallelism.ForEachRow"/>
    /// from inside thread-pool workers owned by the parallel pipeline, so the helper must stay
    /// correct when many of those workers run at once.
    /// <para>
    /// This test asserts completion within a bounded budget rather than hanging, but it is a
    /// guard, not a reproduction: the original stall needed the real pipeline (an async worker
    /// pool parked inside the row loop) and did not reproduce in this synthetic shape. The
    /// authoritative regression evidence for the fix is the full-suite <c>--blame-hang</c>
    /// differential recorded in the ADR.
    /// </para>
    /// </remarks>
    public class InnerLoopParallelismStarvationTests
    {
        private const int ConcurrentPipelines = 24;
        private const int RowCount = 16;
        private const int Degree = 4;
        private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(60);

        [Fact]
        public Task ForEachRow_Completes_When_Many_Pipelines_Run_Concurrently() =>
            AssertAllPipelinesComplete(
                visits => InnerLoopParallelism.ForEachRow(RowCount, Degree, row => Interlocked.Increment(ref visits[row])));

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(5)]
        [InlineData(17)]
        public void ForEachRow_Visits_Every_Row_With_A_Degree_That_Does_Not_Divide_The_Row_Count(int degree)
        {
            const int rowCount = 17;
            var visits = new int[rowCount];

            InnerLoopParallelism.ForEachRow(rowCount, degree, row => Interlocked.Increment(ref visits[row]));

            Assert.All(visits, visit => Assert.Equal(1, visit));
        }

        private static async Task AssertAllPipelinesComplete(Action<int[]> rowBody)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new CountdownEvent(ConcurrentPipelines);
            var pipelines = new Task[ConcurrentPipelines];

            for (int i = 0; i < pipelines.Length; i++)
            {
                pipelines[i] = Task.Run(async () =>
                {
                    ready.Signal();
                    await release.Task.ConfigureAwait(false);

                    var visits = new int[RowCount];
                    rowBody(visits);

                    Assert.All(visits, visit => Assert.Equal(1, visit));
                });
            }

            // Release every pipeline together so their nested row loops contend for the same pool.
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "Pipelines did not reach the release point in time.");
            release.SetResult();

            var all = Task.WhenAll(pipelines);
            var finished = await Task.WhenAny(all, Task.Delay(CompletionBudget)).ConfigureAwait(false);
            Assert.True(
                ReferenceEquals(finished, all),
                $"Inner loop did not finish within {CompletionBudget.TotalSeconds}s under {ConcurrentPipelines} concurrent pipelines (thread-pool starvation).");

            await all.ConfigureAwait(false);
        }
    }
}
