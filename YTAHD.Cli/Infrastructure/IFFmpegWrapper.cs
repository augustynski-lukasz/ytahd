using System.IO;
using System.Threading.Tasks;

namespace YTAHD.Cli.Infrastructure
{
    public interface IFFmpegProcess : System.IDisposable
    {
        Stream StandardInput { get; }
        Task WaitForExitAsync();
    }

    public interface IFFmpegWrapper
    {
        Task<bool> IsAvailableAsync();
        Task<IFFmpegProcess> StartAsync(string outputPath);
    }
}
