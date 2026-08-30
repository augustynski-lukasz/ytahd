using System.IO;
using System.Threading.Tasks;

namespace YTAHD.Core.Infrastructure
{
    public interface IFFmpegProcess : System.IDisposable
    {
        Stream StandardInput { get; }
        Task WaitForExitAsync();
    }

    public interface IFFmpegWrapper
    {
        string ExecutablePath { get; }
        Task<bool> IsAvailableAsync();
        Task<IFFmpegProcess> StartAsync(string outputPath);
    }
}
