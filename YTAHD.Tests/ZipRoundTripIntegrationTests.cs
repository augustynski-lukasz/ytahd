using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Cli.Core;
using YTAHD.Cli.Modulation;

namespace YTAHD.Tests
{
    public class ZipRoundTripIntegrationTests
    {
        [Fact]
        public async Task TextFile_Zip_EncodeDecode_Unzip_MatchesOriginalText()
        {
            var root = Path.Combine(Path.GetTempPath(), "ytahd-zip-it-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var sourceTextPath = Path.Combine(root, "source.txt");
            var originalZipPath = Path.Combine(root, "source.zip");
            var decodedZipPath = Path.Combine(root, "decoded.zip");
            var extractOriginalDir = Path.Combine(root, "original-unzipped");
            var extractDecodedDir = Path.Combine(root, "decoded-unzipped");

            try
            {
                var originalText = BuildSampleText();
                await File.WriteAllTextAsync(sourceTextPath, originalText, Encoding.UTF8);

                using (var zipFs = File.Create(originalZipPath))
                using (var archive = new ZipArchive(zipFs, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry("payload.txt", CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await using var writer = new StreamWriter(entryStream, Encoding.UTF8);
                    await writer.WriteAsync(originalText);
                }

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(128, 64, 30);
                var encoder = new EncoderEngine(mod, fake, 1, 128, 64, 30);

                await encoder.VerifyAsync();
                await encoder.EncodeAsync(originalZipPath, Path.Combine(root, "out.mp4"));

                var rawFrames = fake.Process?.Buffer;
                Assert.NotNull(rawFrames);
                rawFrames.Position = 0;

                var expectedZipBytes = checked((int)new FileInfo(originalZipPath).Length);
                var decoder = new DecoderEngine(mod, fake);
                await decoder.DecodeFromRgbStreamAsync(rawFrames, 128, 64, 1, expectedZipBytes, decodedZipPath);

                ZipFile.ExtractToDirectory(originalZipPath, extractOriginalDir);
                ZipFile.ExtractToDirectory(decodedZipPath, extractDecodedDir);

                var originalExtractedText = await File.ReadAllTextAsync(Path.Combine(extractOriginalDir, "payload.txt"), Encoding.UTF8);
                var decodedExtractedText = await File.ReadAllTextAsync(Path.Combine(extractDecodedDir, "payload.txt"), Encoding.UTF8);

                Assert.Equal(originalExtractedText, decodedExtractedText);
                Assert.Equal(originalText, decodedExtractedText);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static string BuildSampleText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("YTAHD zip round-trip payload");
            for (int i = 0; i < 200; i++)
            {
                sb.AppendLine($"Line {i:D4}: {Guid.NewGuid():N}");
            }
            return sb.ToString();
        }
    }
}
