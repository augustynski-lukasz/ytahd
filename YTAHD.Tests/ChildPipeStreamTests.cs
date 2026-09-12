using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Infrastructure;

namespace YTAHD.Tests
{
    /// <summary>
    /// Regression coverage for the child-pipe pump.
    /// </summary>
    /// <remarks>
    /// A child's standard streams are synchronous pipes, so awaiting them directly parks a thread-pool
    /// thread for the whole lifetime of the child; enough concurrent ffmpeg children then starve the pool
    /// the pipeline itself runs on, which shows up as a test run that reports every test passed but never
    /// exits. See docs/decisions/CR-20260912-04-child-pipe-thread-pool-starvation.md.
    /// </remarks>
    public class ChildPipeStreamTests
    {
        [Fact]
        public async Task Pipe_Source_Is_Read_On_A_Dedicated_Thread_Not_A_Thread_Pool_Thread()
        {
            using var source = new BlockingSourceStream();
            using var stream = new ChildPipeStream(source);

            source.Write(new byte[] { 1, 2, 3 });

            var buffer = new byte[3];
            int read = await stream.ReadAsync(buffer, 0, buffer.Length);

            Assert.Equal(3, read);
            Assert.True(source.AnyReadObserved, "The pump never touched the source.");
            Assert.False(
                source.AnyReadObservedOnThreadPoolThread,
                "The child pipe was read on a thread-pool thread; that is exactly the starvation this type exists to prevent.");
        }

        [Fact]
        public async Task Bytes_Arrive_In_Order_And_Eof_Is_Reported_Repeatedly()
        {
            var first = CreatePattern(100 * 1024, 7);
            var second = new byte[] { 0xAB };
            var third = CreatePattern(200 * 1024, 11);

            using var source = new BlockingSourceStream();
            using var stream = new ChildPipeStream(source);

            source.Write(first);
            source.Write(second);
            source.Write(third);
            source.Complete();

            using var sink = new MemoryStream();
            await stream.CopyToAsync(sink);

            var expected = new byte[first.Length + second.Length + third.Length];
            Buffer.BlockCopy(first, 0, expected, 0, first.Length);
            Buffer.BlockCopy(second, 0, expected, first.Length, second.Length);
            Buffer.BlockCopy(third, 0, expected, first.Length + second.Length, third.Length);

            Assert.Equal(expected.Length, sink.Length);
            Assert.True(expected.AsSpan().SequenceEqual(sink.ToArray()));

            // EOF is sticky: a consumer that loops until a zero read must terminate.
            var probe = new byte[16];
            Assert.Equal(0, await stream.ReadAsync(probe, 0, probe.Length));
            Assert.Equal(0, await stream.ReadAsync(probe, 0, probe.Length));
        }

        [Fact]
        public async Task Async_Read_Waits_For_Late_Data_Instead_Of_Spinning_Or_Failing()
        {
            using var source = new BlockingSourceStream();
            using var stream = new ChildPipeStream(source);

            var buffer = new byte[8];
            var readTask = stream.ReadAsync(buffer, 0, buffer.Length);

            // Nothing has been produced yet, so the read must still be outstanding rather than
            // reporting a premature end of stream.
            Assert.False(readTask.IsCompleted);

            source.Write(new byte[] { 9, 8, 7, 6, 5 });
            source.Complete();

            Assert.Equal(5, await readTask);
            Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, buffer[..5]);
        }

        [Fact]
        public async Task Source_Failure_Is_Surfaced_Instead_Of_Being_Reported_As_A_Short_Stream()
        {
            using var source = new BlockingSourceStream();
            using var stream = new ChildPipeStream(source);

            source.Write(new byte[] { 1, 2, 3 });
            source.Fail(new IOException("pipe broke"));

            var buffer = new byte[3];
            Assert.Equal(3, await stream.ReadAsync(buffer, 0, buffer.Length));

            // A dead child must not look like a video that simply ended early.
            await Assert.ThrowsAsync<IOException>(() => stream.ReadAsync(buffer, 0, buffer.Length));
        }

        [Fact]
        public async Task Dispose_Ends_The_Stream_Cleanly_So_Teardown_Races_Do_Not_Throw()
        {
            using var source = new BlockingSourceStream();
            var stream = new ChildPipeStream(source);

            var pending = stream.ReadAsync(new byte[4], 0, 4);
            Assert.False(pending.IsCompleted);

            stream.Dispose();

            Assert.Equal(0, await pending);
            Assert.Equal(0, await stream.ReadAsync(new byte[4], 0, 4));
        }

        [Fact]
        public async Task Large_Payload_Survives_Backpressure_From_Bounded_Queueing()
        {
            const int total = 4 * 1024 * 1024;
            var payload = CreatePattern(total, 23);

            using var source = new BlockingSourceStream();
            using var stream = new ChildPipeStream(source);

            // Feed from a separate task so the pump hits a full queue and has to wait for the consumer.
            var producer = Task.Run(() =>
            {
                int offset = 0;
                var random = new Random(4242);
                while (offset < payload.Length)
                {
                    int size = Math.Min(random.Next(1, 128 * 1024), payload.Length - offset);
                    source.Write(payload.AsSpan(offset, size).ToArray());
                    offset += size;
                }

                source.Complete();
            });

            using var sink = new MemoryStream();
            await stream.CopyToAsync(sink);
            await producer;

            Assert.Equal(total, sink.Length);
            Assert.True(payload.AsSpan().SequenceEqual(sink.ToArray()));
        }

        [Fact]
        public async Task Synchronous_Read_Returns_Queued_Data()
        {
            using var source = new BlockingSourceStream();
            using var stream = new ChildPipeStream(source);

            source.Write(new byte[] { 4, 5, 6 });
            source.Complete();

            var buffer = new byte[8];
            int read = await Task.Run(() => stream.Read(buffer, 0, buffer.Length));

            Assert.Equal(3, read);
            Assert.Equal(new byte[] { 4, 5, 6 }, buffer[..3]);
        }

        private static byte[] CreatePattern(int length, int seed)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)((i * seed + seed) % 251);
            }

            return data;
        }

        /// <summary>
        /// Stands in for a child pipe: synchronous, unbounded, blocking, and with exactly one reader.
        /// </summary>
        private sealed class BlockingSourceStream : Stream
        {
            private readonly BlockingCollection<byte[]> _chunks = new();
            private byte[]? _pending;
            private int _pendingOffset;
            private Exception? _failure;

            public bool AnyReadObserved { get; private set; }

            public bool AnyReadObservedOnThreadPoolThread { get; private set; }

            public void Write(byte[] data) => _chunks.Add(data);

            public void Complete() => _chunks.CompleteAdding();

            public void Fail(Exception failure)
            {
                _failure = failure;
                _chunks.CompleteAdding();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                AnyReadObserved = true;
                if (Thread.CurrentThread.IsThreadPoolThread)
                {
                    AnyReadObservedOnThreadPoolThread = true;
                }

                if (_pending == null || _pendingOffset >= _pending.Length)
                {
                    _pending = null;
                    if (!_chunks.TryTake(out var next, Timeout.Infinite))
                    {
                        if (_failure != null)
                        {
                            throw _failure;
                        }

                        return 0;
                    }

                    _pending = next;
                    _pendingOffset = 0;
                }

                int take = Math.Min(_pending.Length - _pendingOffset, count);
                Buffer.BlockCopy(_pending, _pendingOffset, buffer, offset, take);
                _pendingOffset += take;
                return take;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
