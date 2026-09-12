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
/// Legacy-compatibility and real-codec corruption coverage (CR-20260912-05 stages 4): legacy
/// durability streams without a manifest report frame-only integrity, and deliberate frame
/// corruption fails loudly instead of producing a truncated payload.
/// </summary>
public class IntegrityLegacyAndCorruptionTests
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

    [Fact]
    public void Legacy_Durability_Stream_Without_Manifest_Reports_FrameOnly()
    {
        var codec = new DurabilityTransportCodec(MatrixOptions());
        var payload = Enumerable.Range(0, 256).Select(i => (byte)(i * 17 % 251)).ToArray();
        var packets = codec.EncodeToFramePackets(payload);

        Assert.DoesNotContain(packets, p => p[3] == FramePacket.FrameTypeManifest);

        var decoded = codec.TryDecodeFramePackets(packets, payload.Length, out var restored, out var recoveredBytes, out var manifest);

        Assert.True(decoded);
        Assert.Equal(payload, restored);
        Assert.Null(manifest);
    }

    [Fact]
    public void Corrupted_Data_Frame_Payload_Is_Rejected_Strictly()
    {
        var codec = new DurabilityTransportCodec(MatrixOptions());
        var payload = Enumerable.Range(0, 256).Select(i => (byte)(i * 19 % 251)).ToArray();
        var packets = codec.EncodeToFramePackets(payload, CreateManifest(payload));

        // Corrupt one data frame's payload bytes after the header hash: strict per-frame hashing
        // must reject that frame; with one loss per group the parity symbol recovers it.
        var dataFrame = packets.First(p => p[3] == FramePacket.FrameTypeData);
        dataFrame[FramePacket.HeaderBytes + 5] ^= 0xFF;

        var decoded = codec.TryDecodeFramePackets(packets, payload.Length, out var restored, out var recoveredBytes, out var manifest);

        Assert.True(decoded);
        Assert.Equal(payload, restored);
        Assert.NotNull(manifest);
    }

    [Fact]
    public void Corrupted_Frame_And_Its_Parity_Fail_Loudly()
    {
        var codec = new DurabilityTransportCodec(MatrixOptions());
        var payload = Enumerable.Range(0, 256).Select(i => (byte)(i * 19 % 251)).ToArray();
        var packets = codec.EncodeToFramePackets(payload, CreateManifest(payload));

        // Corrupt a data frame AND its group's parity frame: two losses in one group exceed the
        // XOR budget, so recovery must fail rather than return damaged bytes.
        var dataFrame = packets.First(p => p[3] == FramePacket.FrameTypeData);
        dataFrame[FramePacket.HeaderBytes + 5] ^= 0xFF;

        var parityIndex = FindParityIndexForFirstGroup(packets);
        Assert.True(parityIndex >= 0, "the stream must contain a parity frame for the first group");
        packets[parityIndex][FramePacket.HeaderBytes + 2] ^= 0xFF;

        var decoded = codec.TryDecodeFramePackets(packets, payload.Length, out _, out _, out var manifest);

        Assert.False(decoded);
        Assert.NotNull(manifest);
    }

    [Fact]
    public async Task RealFfmpeg_Corrupted_Video_Still_Decodes_Or_Fails_Loudly_Never_Silently_Truncates()
    {
        var ffmpegPath = TestFfmpeg.GetAvailableFfmpegPath();
        Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be available for the corruption round-trip test.");

        var inputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        var outputVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        var outputFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.out");

        try
        {
            var payload = new byte[512];
            new Random(911).NextBytes(payload);
            await File.WriteAllBytesAsync(inputFile, payload);

            var service = new YtahdCodecService(new BinaryGridModulator(), new DefaultFFmpegWrapperFactory(ffmpegPath));
            await service.EncodeAsync(new EncodeOptions
            {
                InputFile = inputFile,
                OutputVideo = outputVideo,
                Width = Width,
                Height = Height,
                MacroblockSize = MacroblockSize,
                Fps = Fps,
                VerifyFfmpeg = true,
                UseDurabilityMatrix = true,
                DurabilityMatrixOptions = MatrixOptions()
            });

            // Real lossy decode without deliberate corruption: per-frame hashes must accept every
            // healthy frame, the matrix must rebuild the payload, and integrity must be Passed.
            await service.DecodeAsync(new DecodeOptions
            {
                InputVideo = outputVideo,
                OutputFile = outputFile,
                Width = Width,
                Height = Height,
                MacroblockSize = MacroblockSize,
                Fps = Fps,
                VerifyFfmpeg = true,
                UseDurabilityMatrix = true,
                DurabilityMatrixOptions = MatrixOptions()
            });

            var decoded = await File.ReadAllBytesAsync(outputFile);
            Assert.Equal(payload, decoded);
            Assert.Equal(IntegrityStatus.Passed, service.LastDecodeMetrics.IntegrityStatus);
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
            if (File.Exists(outputVideo)) File.Delete(outputVideo);
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }

    private static StreamManifest CreateManifest(byte[] payload) => new()
    {
        TotalPayloadBytes = payload.LongLength,
        PayloadSha256 = SHA256.HashData(payload),
        ModulatorId = "phase1",
        Width = Width,
        Height = Height,
        MacroblockSize = MacroblockSize,
        Fps = Fps,
        DataFrameCount = 16,
        ParityGroupSize = 4,
        ParitySymbolsPerGroup = 1,
        UseDurabilityMatrix = true
    };

    private static int FindParityIndexForFirstGroup(System.Collections.Generic.IReadOnlyList<byte[]> packets)
    {
        for (int i = 0; i < packets.Count; i++)
        {
            var p = packets[i];
            if (p[3] == FramePacket.FrameTypeParity && p[12] == 0 && p[13] == 0 && p[14] == 0 && p[15] == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
