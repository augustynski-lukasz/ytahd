using System.IO;
using System.Threading.Tasks;
using YTAHD.Core.Audio;

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

        /// <summary>
        /// Starts an ffmpeg process that reads raw RGB24 video frames from stdin. When
        /// <paramref name="audioPcmFilePath"/> is provided (16-bit PCM mono, see
        /// <see cref="FskGenerator.SampleRate"/>), it is muxed in as an AAC audio track
        /// instead of the default video-only (<c>-an</c>) output.
        /// </summary>
        Task<IFFmpegProcess> StartAsync(string outputPath, string? audioPcmFilePath = null);

        /// <summary>
        /// Extracts the audio track of <paramref name="inputVideo"/> as raw 16-bit PCM mono
        /// samples at <see cref="FskGenerator.SampleRate"/>. Returns null if the video
        /// has no audio track or extraction fails; this feature is optional end-to-end.
        /// </summary>
        Task<byte[]?> TryExtractAudioPcmAsync(string inputVideo);
    }
}
