using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Application;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests;

/// <summary>
/// Fast discriminator for the phase4 + durability-matrix failure: runs the same round trip the
/// QSV validation uses, but through the fake FFmpeg wrapper so the codec layer is removed from
/// the equation. If this fails, the defect is in the pipeline (most plausibly how the manifest
/// frames interact with phase4's canonical/displaced emission), not in any encoder.
/// </summary>
public class Phase4DurabilityDiscriminatorTests
{
    private const int Width = 640;
    private const int Height = 480;
    private const int MacroblockSize = 16;
    private const int Fps = 30;

    private static DurabilityMatrixOptions MatrixOptions() => new()
    {
        SymbolSize = 32,
        GroupSize = 4,
        ParitySymbolsPerGroup = 1
    };

    [Theory]
    [InlineData("phase1", 64)]
    [InlineData("phase2", 64)]
    [InlineData("phase3", 64)]
    [InlineData("phase4", 64)]
    public async Task Durability_RoundTrip_Through_Fake_Wrapper(string modeName, int payloadSize)
    {
        var payload = new byte[payloadSize];
        new Random(5500 + modeName.Length).NextBytes(payload);
        var inputFile = Path.GetTempFileName();

        try
        {
            await File.WriteAllBytesAsync(inputFile, payload);

            var modulator = modeName switch
            {
                "phase1" => (IModulator)new BinaryGridModulator(),
                "phase2" => new PseudoQamModulator(),
                "phase3" => new DctModulator(),
                "phase4" => new MotionVectorModulator(),
                _ => throw new ArgumentException(modeName)
            };

            var fake = new FakeFFmpegWrapper(Width, Height, Fps);
            var encoder = new EncoderEngine(modulator, fake, new VideoCodecOptions
            {
                Width = Width,
                Height = Height,
                MacroblockSize = MacroblockSize,
                Fps = Fps,
                UseDurabilityMatrix = true,
                DurabilityMatrixOptions = MatrixOptions()
            });
            await encoder.EncodeAsync(inputFile, "out.mp4");

            var buffer = fake.Process?.Buffer;
            Assert.NotNull(buffer);
            buffer!.Position = 0;

            var decoder = new DecoderEngine(modulator, fake, new VideoCodecOptions
            {
                Width = Width,
                Height = Height,
                MacroblockSize = MacroblockSize,
                Fps = Fps,
                UseDurabilityMatrix = true,
                DurabilityMatrixOptions = MatrixOptions()
            });
            var outputFile = Path.GetTempFileName();
            try
            {
                await decoder.DecodeFromRgbStreamAsync(buffer, Width, Height, MacroblockSize, payloadSize, outputFile);
                var decoded = await File.ReadAllBytesAsync(outputFile);
                Assert.True(payload.AsSpan().SequenceEqual(decoded), $"{modeName} + durability matrix did not round-trip through the fake wrapper.");

                // Integrity discriminator (CR-20260912-06 stage 4): the manifest machinery must
                // survive every modulator even before a real codec is involved. If this fails
                // the defect is in the pipeline, not in any encoder.
                Assert.Equal(IntegrityStatus.Passed, decoder.LastDecodeMetrics.IntegrityStatus);
                Assert.NotNull(decoder.LastDecodeMetrics.Manifest);
                Assert.True(
                    decoder.LastDecodeMetrics.Manifest!.PayloadSha256.AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(payload)),
                    $"{modeName}: recovered manifest SHA does not match the encoded payload.");
            }
            finally
            {
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
        }
    }

    /// <summary>
    /// Focused variant of the phase4 case with a canary dump of which frames made it through
    /// decode: prints per-frame canonical/valid flags so a manifest-frame loss can be seen
    /// directly instead of only via the final IntegrityStatus.
    /// </summary>
    [Fact]
    public async Task Phase4_Manifest_Frame_Survives_Fake_Wrapper_Decode()
    {
        var payload = new byte[64];
        new Random(9911).NextBytes(payload);
        var inputFile = Path.GetTempFileName();

        try
        {
            await File.WriteAllBytesAsync(inputFile, payload);

            var fake = new FakeFFmpegWrapper(Width, Height, Fps);
            var encoder = new EncoderEngine(new MotionVectorModulator(), fake, new VideoCodecOptions
            {
                Width = Width,
                Height = Height,
                MacroblockSize = MacroblockSize,
                Fps = Fps,
                UseDurabilityMatrix = true,
                DurabilityMatrixOptions = MatrixOptions()
            });
            await encoder.EncodeAsync(inputFile, "out.mp4");

            var buffer = fake.Process?.Buffer;
            Assert.NotNull(buffer);
            buffer!.Position = 0;

            var decoder = new DecoderEngine(new MotionVectorModulator(), fake, new VideoCodecOptions
            {
                Width = Width,
                Height = Height,
                MacroblockSize = MacroblockSize,
                Fps = Fps,
                UseDurabilityMatrix = true,
                DurabilityMatrixOptions = MatrixOptions()
            });
            var outputFile = Path.GetTempFileName();
            try
            {
                await decoder.DecodeFromRgbStreamAsync(buffer, Width, Height, MacroblockSize, payload.Length, outputFile);
                var decoded = await File.ReadAllBytesAsync(outputFile);
                Assert.True(payload.AsSpan().SequenceEqual(decoded), "phase4 payload did not round-trip through the fake wrapper.");

                // Manual frame walk: report the decode verdict of every frame in the buffer so a
                // manifest-frame loss is visible directly.
                buffer.Position = 0;
                var modulator = new MotionVectorModulator();
                var frameBytes = Width * Height * 3;
                var walk = new System.Text.StringBuilder();
                int totalBufferFrames = (int)(buffer.Length / frameBytes);
                for (int f = 0; f < totalBufferFrames; f++)
                {
                    var frameMem = new byte[frameBytes];
                    buffer.ReadExactly(frameMem);
                    var verdict = DecoderEngine.TryReadDecodedPacket(frameMem, Width, Height, MacroblockSize, frameBytes / Height * 0 + (Width * 3), frameBytes, 0, modulator, out var pkt);
                    bool canonical = MotionFrameBitDecoder.IsCanonicalFrame(frameMem, Width, Height, 32);
                    string type = "none";
                    int frameIndex = -1, declaredLen = -1;
                    if (verdict && YTAHD.Core.Core.FramePacketCodec.TryDecode(pkt, out var ft, out var fi, out var tdf, out var gs, out var gc, out var pl, out var _))
                    {
                        type = ft switch { 0 => "data", 1 => "parity", 2 => "manifest", _ => ft.ToString() };
                        frameIndex = fi;
                        declaredLen = pl;
                    }
                    else if (!verdict)
                    {
                        // Break down why the packet is invalid: header parse vs field sanity vs hash.
                        bool headerOk = YTAHD.Core.Core.FramePacketCodec.TryDecodeWithTolerance(pkt, out var ft2, out _, out var tdf2, out _, out _, out var pl2, out var body2);
                        string reason;
                        if (!headerOk)
                        {
                            reason = $"tolerance-decode-failed(magic={pkt[0]:X2}{pkt[1]:X2} ver={pkt[2]} type={pkt[3]} totalFrames={tdf2} declaredLen={pl2} pktLen={pkt.Length})";
                        }
                        else
                        {
                            var expected = pkt.AsSpan(0, Math.Min(pkt.Length, 160));
                            var hex = System.Convert.ToHexString(expected.Slice(0, 48));
                            reason = $"tolerance-ok-but-rejected(type={ft2} totalFrames={tdf2} declaredLen={pl2} bodyLen={body2.Length} hex0-48={hex})";
                        }
                        walk.Append($"f{f}:INVALID[{reason}] ");
                        continue;
                    }
                    walk.Append($"f{f}:{(canonical ? "C" : "-")}{(verdict ? "V" : "i")}({type}#{frameIndex},len={declaredLen}) ");
                }

                var metrics = decoder.LastDecodeMetrics;
                Assert.True(
                    metrics.IntegrityStatus == IntegrityStatus.Passed,
                    $"phase4 integrity={metrics.IntegrityStatus}: manifest frames were lost in decode. " +
                    $"framesSeen={metrics.TotalFramesSeen} canonical={metrics.CanonicalFrameCount} " +
                    $"invalidPackets={metrics.InvalidPacketCount} manifest={(metrics.Manifest is null ? "null" : "recovered")} " +
                    $"bufferFrames={totalBufferFrames} WALK[{walk}]");
            }
            finally
            {
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
        }
    }
}
