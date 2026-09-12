using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace YTAHD.Core.Infrastructure
{
    /// <summary>
    /// Helpers for consuming the redirected pipes of child ffmpeg/ffprobe processes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every redirected child pipe must be drained concurrently with whatever work is waiting on
    /// that child. Windows anonymous pipes have a fixed buffer, so once it fills the child blocks
    /// on its next write while the parent blocks waiting for the child to finish - a deadlock that
    /// depends on buffer occupancy rather than on correctness of the command line, and therefore
    /// typically only appears when several encodes/decodes are in flight at once.
    /// </para>
    /// <para>
    /// Consuming a pipe is a <em>blocking</em> operation: a pipe read cannot complete on an IO completion
    /// port, so <c>ReadAsync</c> on a child's standard stream is emulated by parking a thread-pool thread
    /// for as long as the child keeps the pipe open. These helpers therefore run the blocking loop on a
    /// dedicated thread, so a long-lived ffmpeg child costs zero thread-pool threads.
    /// </para>
    /// </remarks>
    internal static class ChildProcessPipes
    {
        /// <summary>
        /// True when this process owns an interactive stderr, in which case child stderr is echoed
        /// through. Under a test host or any other redirected parent the child output is discarded
        /// instead of being written to <see cref="Console"/>, whose writer is globally locked and is
        /// not a safe thing to block a pipeline on.
        /// </summary>
        private static readonly bool EchoChildStderr = !Console.IsErrorRedirected;

        /// <summary>
        /// Reads <paramref name="reader"/> to end so the owning process can never block on a full
        /// pipe buffer.
        /// </summary>
        internal static Task DrainAsync(TextReader? reader)
        {
            if (reader == null) return Task.CompletedTask;

            return RunOnDedicatedThread(() => DrainCore(reader));
        }

        /// <summary>
        /// Reads <paramref name="reader"/> to end and returns everything it produced.
        /// Used where the probe output is the actual result, not just diagnostics.
        /// </summary>
        internal static Task<string> ReadToEndAsync(TextReader? reader)
        {
            if (reader == null) return Task.FromResult(string.Empty);

            return RunOnDedicatedThreadReturning(() => ReadToEndCore(reader));
        }

        /// <summary>
        /// Runs <paramref name="work"/> on a dedicated thread. <see cref="TaskCreationOptions.LongRunning"/>
        /// is what guarantees a dedicated thread rather than a queued thread-pool work item; the thread is a
        /// background thread, so it can never keep the process alive.
        /// </summary>
        private static Task RunOnDedicatedThread(Action work)
            => Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        private static Task<T> RunOnDedicatedThreadReturning<T>(Func<T> work)
            => Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        private static void DrainCore(TextReader reader)
        {
            try
            {
                var buffer = new char[4096];
                while (true)
                {
                    // Synchronous on purpose: this thread is already dedicated, so the async machinery
                    // would only add overhead without freeing anything the pool needs.
                    int read = reader.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;

                    if (!EchoChildStderr) continue;

                    try
                    {
                        Console.Error.Write(buffer, 0, read);
                    }
                    catch
                    {
                        // Diagnostics must never take down the pipeline.
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private static string ReadToEndCore(TextReader reader)
        {
            try
            {
                return reader.ReadToEnd();
            }
            catch (IOException) { return string.Empty; }
            catch (ObjectDisposedException) { return string.Empty; }
            catch (InvalidOperationException) { return string.Empty; }
        }
    }
}
