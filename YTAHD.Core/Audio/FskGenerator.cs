using System;
using System.IO;
using System.Threading.Tasks;

namespace YTAHD.Core.Audio
{
    /// <summary>
    /// Generates a simple PCM 16-bit 44.1kHz FSK-like stream to use as clock/sync.
    /// This is a placeholder demonstrating a memory-forward API.
    /// </summary>
    public static class FskGenerator
    {
        public const int SampleRate = 44100;

        public static async Task GenerateSilenceAsync(Stream outStream, TimeSpan duration)
        {
            int samples = (int)(SampleRate * duration.TotalSeconds);
            int bytesToWrite = samples * 2; // 16-bit PCM
            byte[] buffer = new byte[4096];
            int written = 0;
            while (written < bytesToWrite)
            {
                int toWrite = Math.Min(buffer.Length, bytesToWrite - written);
                await outStream.WriteAsync(buffer, 0, toWrite);
                written += toWrite;
            }
        }
    }
}
