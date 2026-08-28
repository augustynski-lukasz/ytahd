using System.IO;
using System.Threading.Tasks;
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

        public FakeFFmpegWrapper(int width, int height, int fps)
        {
            _width = width; _height = height; _fps = fps;
        }

        public Task<bool> IsAvailableAsync() => Task.FromResult(true);

        public Task<IFFmpegProcess> StartAsync(string outputPath)
        {
            _process = new FakeFFmpegProcess();
            return Task.FromResult<IFFmpegProcess>(_process);
        }
    }

    internal class FakeFFmpegProcess : IFFmpegProcess
    {
        private readonly MemoryStream _ms = new MemoryStream();
        public Stream StandardInput => _ms;
        public MemoryStream Buffer => _ms;
        public long WrittenBytes => _ms.Length;
        public Task WaitForExitAsync() => Task.CompletedTask;
        public void Dispose()
        {
            // Intentionally do not dispose the memory stream so tests can inspect it after encoding completes.
        }
    }
}
