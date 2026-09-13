using System;
using System.IO;
using System.Threading.Tasks;
using YTAHD.Core.Application;
using YTAHD.Core.Infrastructure;

namespace YTAHD.Tests
{
    // Test fake implementations shared across tests
    internal class FakeFFmpegWrapper : IFFmpegWrapper
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _fps;
        public long WrittenBytes => _process?.WrittenBytes ?? 0;
        public FakeFFmpegProcess? Process => _process;
        private FakeFFmpegProcess? _process;
        public string ExecutablePath => "ffmpeg";
        public string? LastAudioPcmFilePath { get; private set; }
        public byte[]? AudioPcmToReturn { get; set; }

        public FakeFFmpegWrapper(int width, int height, int fps)
        {
            _width = width; _height = height; _fps = fps;
        }

        public Task<bool> IsAvailableAsync() => Task.FromResult(true);

        public Task<IFFmpegProcess> StartAsync(string outputPath, string? audioPcmFilePath = null)
        {
            LastAudioPcmFilePath = audioPcmFilePath;
            _process = new FakeFFmpegProcess();
            return Task.FromResult<IFFmpegProcess>(_process);
        }

        public Task<byte[]?> TryExtractAudioPcmAsync(string inputVideo) => Task.FromResult(AudioPcmToReturn);
    }

    internal sealed class FakeFFmpegWrapperFactory : IFFmpegWrapperFactory
    {
        private readonly IFFmpegWrapper _encodeWrapper;
        private readonly IFFmpegWrapper _decodeWrapper;

        public FakeFFmpegWrapperFactory(IFFmpegWrapper wrapper)
        {
            _encodeWrapper = wrapper;
            _decodeWrapper = wrapper;
        }

        public FakeFFmpegWrapperFactory(IFFmpegWrapper encodeWrapper, IFFmpegWrapper decodeWrapper)
        {
            _encodeWrapper = encodeWrapper;
            _decodeWrapper = decodeWrapper;
        }

        public IFFmpegWrapper CreateForEncode(int width, int height, int fps, string? ffmpegPath = null, VideoEncoder? videoEncoder = null)
        {
            _ = width;
            _ = height;
            _ = fps;
            _ = ffmpegPath;
            _ = videoEncoder;
            return _encodeWrapper;
        }

        public IFFmpegWrapper CreateForDecode(string? ffmpegPath = null)
        {
            _ = ffmpegPath;
            return _decodeWrapper;
        }
    }

    internal sealed class NonClosingStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly Action _onFlush;

        public NonClosingStream(MemoryStream inner, Action onFlush)
        {
            _inner = inner;
            _onFlush = onFlush;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush()
        {
            _onFlush();
            _inner.Flush();
        }

        public override Task FlushAsync(System.Threading.CancellationToken cancellationToken)
        {
            _onFlush();
            return _inner.FlushAsync(cancellationToken);
        }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) => _inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override void Close()
        {
            // Keep the underlying buffer available for assertions after encoding finishes.
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Keep the underlying buffer available for assertions after encoding finishes.
            }
        }
    }

    internal class FakeFFmpegProcess : IFFmpegProcess
    {
        private readonly MemoryStream _ms = new MemoryStream();
        private readonly Stream _stdin;

        public FakeFFmpegProcess()
        {
            _stdin = new NonClosingStream(_ms, () => FlushCount++);
        }

        public Stream StandardInput => _stdin;
        public MemoryStream Buffer => _ms;
        public long WrittenBytes => _ms.Length;
        public int FlushCount { get; private set; }
        public Task WaitForExitAsync() => Task.CompletedTask;
        public void Dispose()
        {
            // Intentionally do not dispose the memory stream so tests can inspect it after encoding completes.
        }
    }
}
