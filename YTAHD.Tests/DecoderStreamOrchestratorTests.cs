using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    public class DecoderStreamOrchestratorTests
    {
        private const int Width = 128;
        private const int Height = 64;
        private const int Macroblock = 1;

        [Fact]
        public async Task DecodeStreamOrchestrator_Reproduces_Output_From_Rgb_Stream()
        {
            var tmpIn = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[16];
                new Random(2).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var buffer = fake.Process?.Buffer;
                Assert.NotNull(buffer);
                buffer.Position = 0;

                var orchestrator = new DecodeStreamOrchestrator(Width, Height, Macroblock);
                var output = await orchestrator.ProcessAsync(buffer, data.Length);

                Assert.Equal(data, output);
                Assert.True(orchestrator.LastDecodeMetrics.TotalElapsedMilliseconds > 0);
                Assert.True(orchestrator.LastDecodeMetrics.FrameReadMilliseconds >= 0);
                Assert.True(orchestrator.LastDecodeMetrics.PacketDecodeMilliseconds >= 0);
                Assert.True(orchestrator.LastDecodeMetrics.AggregationMilliseconds >= 0);
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }

        [Fact]
        public async Task DecodeStreamOrchestrator_Reports_Completion_Percentage()
        {
            var tmpIn = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[16];
                new Random(3).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var buffer = fake.Process?.Buffer;
                Assert.NotNull(buffer);
                buffer.Position = 0;

                var frameBytes = Width * Height * 3;
                var totalFrames = (int)(buffer.Length / frameBytes);
                var progressReports = new List<DecodeProgress>();
                var progress = new CaptureProgress(progressReports);

                var orchestrator = new DecodeStreamOrchestrator(Width, Height, Macroblock);
                var output = await orchestrator.ProcessAsync(buffer, 0, totalVideoFrames: totalFrames, progress: progress);

                Assert.Equal(data, output[..data.Length]);
                Assert.NotEmpty(progressReports);
                Assert.Equal(totalFrames, progressReports[^1].FramesSeen);
                Assert.Equal(100d, progressReports[^1].Percentage);
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }

        [Fact]
        public async Task DecodeStreamOrchestrator_Parallel_Matches_Serial_Output_And_Ordered_Progress()
        {
            var tmpIn = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[1600];
                new Random(31).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");
                var raw = fake.Process!.Buffer.ToArray();
                int totalFrames = raw.Length / (Width * Height * 3);

                var serial = new DecodeStreamOrchestrator(mod, Width, Height, Macroblock);
                var serialOutput = await serial.ProcessAsync(new MemoryStream(raw, writable: false), 0);
                var reports = new List<DecodeProgress>();
                var parallel = new DecodeStreamOrchestrator(mod, Width, Height, Macroblock, false, null, 3);
                var parallelOutput = await parallel.ProcessAsync(new MemoryStream(raw, writable: false), 0, totalVideoFrames: totalFrames, progress: new CaptureProgress(reports));

                Assert.Equal(serialOutput, parallelOutput);
                Assert.Equal(serial.LastDecodeMetrics.InvalidPacketCount, parallel.LastDecodeMetrics.InvalidPacketCount);
                Assert.Equal(totalFrames, reports[^1].FramesSeen);
                Assert.True(reports.Zip(reports.Skip(1), (left, right) => right.FramesSeen >= left.FramesSeen).All(value => value));
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }

        [Fact]
        public async Task DecodeStreamOrchestrator_Parallel_Propagates_Cancellation()
        {
            using var cancellation = new CancellationTokenSource();
            var orchestrator = new DecodeStreamOrchestrator(new BinaryGridModulator(), Width, Height, Macroblock, false, null, 2);
            var processing = orchestrator.ProcessAsync(new BlockingReadStream(), 0, cancellationToken: cancellation.Token);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        }

        [Fact]
        public async Task EncoderDecoder_RoundTrip_FakeFFmpeg()
        {
            var tmpIn = Path.GetTempFileName();
            var tmpOut = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[16];
                new Random(2).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(Width, Height, 30);
                var encoder = new EncoderEngine(mod, fake, Macroblock, Width, Height, 30);
                await encoder.VerifyAsync();
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var buf = fake.Process?.Buffer;
                Assert.NotNull(buf);
                buf.Position = 0;

                var decoder = new DecoderEngine(mod, fake);
                await decoder.DecodeFromRgbStreamAsync(buf, Width, Height, Macroblock, data.Length, tmpOut);

                var outData = await File.ReadAllBytesAsync(tmpOut);
                Assert.Equal(data, outData);
            }
            finally
            {
                File.Delete(tmpIn);
                File.Delete(tmpOut);
            }
        }

        private sealed class CaptureProgress : IProgress<DecodeProgress>
        {
            private readonly List<DecodeProgress> _reports;

            public CaptureProgress(List<DecodeProgress> reports)
            {
                _reports = reports;
            }

            public void Report(DecodeProgress value)
            {
                _reports.Add(value);
            }
        }

        private sealed class BlockingReadStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => 0;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
        }
    }
}
