using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace YTAHD.Core.Infrastructure
{
    /// <summary>
    /// Exposes a child process pipe as a stream whose asynchronous reads never occupy a thread-pool thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Process.StandardOutput</c> and <c>Process.StandardError</c> are synchronous <see cref="FileStream"/>s
    /// over anonymous pipes. Windows cannot complete a pipe read on an IO completion port, so .NET emulates
    /// <c>ReadAsync</c> with <c>AsyncOverSyncWithIoCancellation</c>, which parks a <em>thread-pool</em> thread in
    /// a blocking <c>ReadFile</c> for as long as the child keeps the pipe open. Every in-flight ffmpeg child
    /// therefore permanently consumes pool threads, and once several round trips run concurrently the caller's
    /// own work is starved by the very pool it runs on.
    /// </para>
    /// <para>
    /// This stream moves the blocking read onto one dedicated thread and serves asynchronous reads from a
    /// bounded queue, so a decode of any duration costs zero thread-pool threads.
    /// </para>
    /// </remarks>
    internal sealed class ChildPipeStream : Stream
    {
        /// <summary>Size of one queued chunk. Large enough that the hand-off is cheap, small enough to bound latency.</summary>
        private const int ChunkBytes = 64 * 1024;

        /// <summary>How many chunks may be buffered ahead of the consumer (≈ 512 KiB of slack).</summary>
        private const int QueuedChunks = 8;

        private readonly Stream _source;
        private readonly Channel<byte[]> _chunks;

        // Deliberately not disposed: a live pump thread may still be observing this token, and disposing a
        // CancellationTokenSource does not interrupt the read it is registered against anyway.
        private readonly CancellationTokenSource _pumpCancellation = new();

        private readonly Task _pump;

        private byte[]? _chunk;
        private int _chunkOffset;
        private bool _disposed;

        internal ChildPipeStream(Stream source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));

            _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueuedChunks)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            // LongRunning asks the scheduler for a dedicated thread instead of a queued pool work item;
            // that dedicated thread is the entire point of this type.
            _pump = Task.Factory.StartNew(Pump, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException("A child pipe has no known length.");

        public override long Position
        {
            get => throw new NotSupportedException("A child pipe is not seekable.");
            set => throw new NotSupportedException("A child pipe is not seekable.");
        }

        public override void Flush()
        {
            // Read-only stream: nothing to flush.
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException("A child pipe is not seekable.");

        public override void SetLength(long value)
            => throw new NotSupportedException("A child pipe is not seekable.");

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException("A child pipe is read-only.");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            while (true)
            {
                if (_chunk != null && _chunkOffset < _chunk.Length)
                {
                    int take = Math.Min(_chunk.Length - _chunkOffset, buffer.Length);
                    _chunk.AsMemory(_chunkOffset, take).CopyTo(buffer);
                    _chunkOffset += take;
                    return take;
                }

                _chunk = null;
                _chunkOffset = 0;

                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // The pump finished. Surface a failure as an error rather than a silent truncation,
                    // so a decode never mistakes a dead ffmpeg for a short video.
                    ThrowPumpFailure();
                    return 0;
                }

                if (_chunks.Reader.TryRead(out var next))
                {
                    _chunk = next;
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    // Releases a pump parked on a full queue. A pump parked inside the pipe read itself is
                    // released when the owning process is killed and the pipe closes - see ChildProcessScope.
                    try
                    {
                        _pumpCancellation.Cancel();
                    }
                    catch (ObjectDisposedException) { }

                    _chunks.Writer.TryComplete();
                }
            }

            base.Dispose(disposing);
        }

        private void ThrowPumpFailure()
        {
            var completion = _chunks.Reader.Completion;
            if (completion.IsFaulted && completion.Exception is { } aggregate)
            {
                throw aggregate.InnerExceptions.Count == 1 ? aggregate.InnerExceptions[0] : aggregate;
            }
        }

        private void Pump()
        {
            Exception? failure = null;
            try
            {
                var buffer = new byte[ChunkBytes];
                while (!_pumpCancellation.IsCancellationRequested)
                {
                    int read = _source.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    var chunk = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, read);

                    // Blocking on this dedicated thread is exactly what the type exists for, and the token
                    // guarantees a disposed reader cannot leave the pump parked on a full queue forever.
                    _chunks.Writer.WriteAsync(chunk, _pumpCancellation.Token).AsTask().GetAwaiter().GetResult();
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (IOException ex) { failure = ex; }
            catch (NotSupportedException ex) { failure = ex; }
            finally
            {
                // Teardown (cancel/dispose) reads as a clean end of stream; a genuine pipe failure is
                // reported to the consumer so it cannot mistake a broken child for a short video.
                _chunks.Writer.TryComplete(failure);
            }
        }
    }
}
