using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Application;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests;

/// <summary>
/// F-20260913-01: the deferred Phase 4 tile-size sweep (4×4 / 8×8 / 16×16). Measures
/// real-codec (libx264 CRF 23) round-trip durability per profile plus synthetic
/// degradation robustness and decode cost, so a tile-size default change can be made
/// on evidence rather than intuition.
/// </summary>
public class Phase4TileSizeSweepTests
{
    private const int Width = 640;
    private const int Height = 480;
    private const int Fps = 30;

    public static TheoryData<MotionTileProfile> Profiles() => new()
    {
        MotionTileProfile.Small,   // 4×4 texture → 36 px cells
        MotionTileProfile.Default, // 8×8 texture → 40 px cells (shipped baseline)
        MotionTileProfile.Large    // 16×16 texture → 48 px cells
    };

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public Phase4TileSizeSweepTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Profile_DerivedGeometry_Is_Consistent()
    {
        Assert.Equal(36, MotionTileProfile.Small.CellSize);
        Assert.Equal(40, MotionTileProfile.Default.CellSize);
        Assert.Equal(48, MotionTileProfile.Large.CellSize);
        Assert.All(new[] { MotionTileProfile.Small, MotionTileProfile.Default, MotionTileProfile.Large }, p =>
        {
            Assert.Equal(p.OffsetStepPx * (p.OffsetLevelsPerAxis / 2), p.MaxAbsOffsetPx);
        });
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public async Task RealLibx264_RoundTrips_Payload_For_Profile(MotionTileProfile profile)
    {
        var ffmpegPath = TestFfmpeg.GetAvailableFfmpegPath();
        Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be available for the real-codec sweep.");

        var modulator = new MotionVectorModulator(profile);
        int payloadBytesPerFrame = modulator.GetPayloadBytesPerFrame(
            ModulatorGeometryFactory(modulator));
        Assert.True(payloadBytesPerFrame > 0, $"Profile {profile} has no payload capacity at {Width}×{Height}.");

        // One full frame of payload: the hardest single-frame case for the profile.
        var payload = new byte[payloadBytesPerFrame];
        new Random(9100 + profile.TextureSize).NextBytes(payload);

        var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

        try
        {
            await File.WriteAllBytesAsync(inputFile, payload);
            var service = new YtahdCodecService(modulator, new DefaultFFmpegWrapperFactory(ffmpegPath));

            await service.EncodeAsync(new EncodeOptions
            {
                InputFile = inputFile,
                OutputVideo = outputVideo,
                Width = Width,
                Height = Height,
                MacroblockSize = 16,
                Fps = Fps,
                VerifyFfmpeg = true,
                UseDurabilityMatrix = true,
                DurabilityMatrixOptions = new DurabilityMatrixOptions { SymbolSize = 32, GroupSize = 4, ParitySymbolsPerGroup = 1 }
            });

            await service.DecodeAsync(new DecodeOptions
            {
                InputVideo = outputVideo,
                OutputFile = outputFile,
                Width = Width,
                Height = Height,
                MacroblockSize = 16,
                Fps = Fps,
                VerifyFfmpeg = true,
                UseDurabilityMatrix = true,
                DurabilityMatrixOptions = new DurabilityMatrixOptions { SymbolSize = 32, GroupSize = 4, ParitySymbolsPerGroup = 1 }
            });

            var decoded = await File.ReadAllBytesAsync(outputFile);
            Assert.True(payload.AsSpan().SequenceEqual(decoded),
                $"Profile {profile.TextureSize}×{profile.TextureSize} failed the real libx264 round trip.");

            // Integrity machinery (Workstream E) must agree: manifest recovered with matching SHA.
            Assert.Equal(IntegrityStatus.Passed, service.LastDecodeMetrics.IntegrityStatus);
            Assert.NotNull(service.LastDecodeMetrics.Manifest);
            Assert.True(
                service.LastDecodeMetrics.Manifest!.PayloadSha256.AsSpan().SequenceEqual(SHA256.HashData(payload)),
                $"Profile {profile.TextureSize}: manifest SHA mismatch.");
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
            if (File.Exists(outputVideo)) File.Delete(outputVideo);
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Synthetic_Noise_Recovers_Payload_Above_Threshold(MotionTileProfile profile)
    {
        var modulator = new MotionVectorModulator(profile);
        int totalCells = PayloadCells(modulator);
        var payload = new byte[totalCells];
        new Random(9200 + profile.TextureSize).NextBytes(payload);

        byte[] frame = modulator.CreatePhase4Frame(Width, Height, 32, payload);
        ApplyGaussianNoise(frame, sigma: 8, seed: 9300 + profile.TextureSize);

        var decoder = new MotionFrameBitDecoder(profile);
        var packet = new byte[totalCells];
        decoder.Decode(frame, Width, Height, 16, Width * 3, frame.Length, packet, 32);

        int matches = payload.Zip(packet, (e, a) => e == a ? 1 : 0).Sum();
        Assert.True(matches >= totalCells * 0.9,
            $"Profile {profile.TextureSize}×{profile.TextureSize}: only {matches}/{totalCells} cells matched under sigma=8 noise.");
    }

    [Fact]
    public void Decode_Cost_Is_Bounded_And_Reports_Per_Profile()
    {
        // Informational: keep decode cost visible in test output for the sweep report.
        foreach (var profile in new[] { MotionTileProfile.Small, MotionTileProfile.Default, MotionTileProfile.Large })
        {
            var modulator = new MotionVectorModulator(profile);
            int totalCells = PayloadCells(modulator);
            var payload = new byte[totalCells];
            new Random(9400 + profile.TextureSize).NextBytes(payload);
            byte[] frame = modulator.CreatePhase4Frame(Width, Height, 32, payload);

            var decoder = new MotionFrameBitDecoder(profile);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var packet = new byte[totalCells];
            decoder.Decode(frame, Width, Height, 16, Width * 3, frame.Length, packet, 32);
            sw.Stop();

            Assert.Equal(payload, packet);
            Assert.True(sw.ElapsedMilliseconds < 5000, $"Profile {profile.TextureSize}: decode took {sw.ElapsedMilliseconds} ms.");
            _output.WriteLine($"profile {profile.TextureSize}x{profile.TextureSize}: cells={totalCells} decodeMs={sw.ElapsedMilliseconds}");
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static ModulatorGeometry ModulatorGeometryFactory(MotionVectorModulator modulator)
        => new(Width, Height, modulator.MacroblockWidth, 0, 32);

    private static int PayloadCells(MotionVectorModulator modulator)
        => modulator.GetPayloadBytesPerFrame(ModulatorGeometryFactory(modulator));

    private static void ApplyGaussianNoise(byte[] rgbaFrame, double sigma, int seed)
    {
        var rnd = new Random(seed);
        for (int i = 0; i < rgbaFrame.Length; i += 4)
        {
            rgbaFrame[i] = Clamp(rgbaFrame[i] + NextGaussian(rnd, sigma));
            rgbaFrame[i + 1] = Clamp(rgbaFrame[i + 1] + NextGaussian(rnd, sigma));
            rgbaFrame[i + 2] = Clamp(rgbaFrame[i + 2] + NextGaussian(rnd, sigma));
        }
    }

    private static double NextGaussian(Random rnd, double sigma)
    {
        double u1 = 1.0 - rnd.NextDouble();
        double u2 = 1.0 - rnd.NextDouble();
        return sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}
