using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Runs an independent per-row body either serially or over a bounded number of thread-pool
    /// workers. Used by modulators and frame bit decoders so their inner loops honour the
    /// configured degree of parallelism (see <see cref="IParallelismConfigurable"/>).
    /// </summary>
    public static class InnerLoopParallelism
    {
        /// <summary>
        /// Invokes <paramref name="body"/> once per row index in <c>[0, rowCount)</c>, serially
        /// when <paramref name="degreeOfParallelism"/> is one or less.
        /// </summary>
        /// <remarks>
        /// Rows are partitioned into contiguous chunks that are dispatched as ordinary thread-pool
        /// work items and then awaited from the calling thread. This deliberately avoids
        /// <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/>: a blocking parallel
        /// loop nested inside the async encode/decode pipeline parks pool threads without leaving
        /// detectable work on the pool queue, so when several pipelines run concurrently the pool
        /// never grows and the process stalls at zero CPU instead of merely running slower.
        /// Queued work items keep that starvation signal visible to the pool.
        /// </remarks>
        public static void ForEachRow(int rowCount, int degreeOfParallelism, Action<int> body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            if (rowCount <= 0) return;

            int workers = degreeOfParallelism > rowCount ? rowCount : degreeOfParallelism;
            if (workers <= 1)
            {
                for (int row = 0; row < rowCount; row++)
                {
                    body(row);
                }

                return;
            }

            int chunkSize = (rowCount + workers - 1) / workers;
            var pending = new List<Task>(workers);

            int chunkStart = 0;
            for (int worker = 0; worker < workers; worker++)
            {
                // Copy into a per-iteration local so the closure does not capture a mutated value.
                int start = chunkStart;
                int end = start + chunkSize;
                if (end > rowCount) end = rowCount;
                if (start >= end) break;

                chunkStart = end;
                pending.Add(Task.Run(() =>
                {
                    for (int row = start; row < end; row++)
                    {
                        body(row);
                    }
                }));
            }

            if (pending.Count == 0) return;

            // Exceptions surface as an AggregateException, matching the previous Parallel.For behaviour.
            Task.WaitAll(pending.ToArray());
        }
    }
}
