using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Cli.Audio;

namespace YTAHD.Tests
{
    public class FskGeneratorTests
    {
        [Fact]
        public async Task Generates_Silence_Length()
        {
            using var ms = new MemoryStream();
            var duration = TimeSpan.FromSeconds(0.1);
            await FskGenerator.GenerateSilenceAsync(ms, duration);
            int expectedSamples = (int)(FskGenerator.SampleRate * duration.TotalSeconds);
            Assert.Equal(expectedSamples * 2, (int)ms.Length);
        }
    }
}
