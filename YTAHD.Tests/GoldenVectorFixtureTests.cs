using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using YTAHD.Core.Core;

namespace YTAHD.Tests;

/// <summary>
/// RM-11 golden-vector fixture generator. Runs ONLY when the environment variable
/// <c>YTAHD_REGENERATE_GOLDEN=1</c> is set; otherwise it is a no-op (skipped assertion-free).
///
/// The committed fixtures under <c>YTAHD.Tests/GoldenVectors/</c> are the wire-format
/// contract: deterministic payloads (no RNG), encoded once with the current codec, stored
/// as one hex packet per line. <see cref="GoldenVectorFixtureTests"/> decodes them WITHOUT
/// running the encoder, so any wire-format regression is caught byte-exactly.
///
/// To regenerate after an intentional protocol change:
/// <code>
/// $env:YTAHD_REGENERATE_GOLDEN='1'
/// dotnet test --filter "FullyQualifiedName~GoldenVectorFixtureGenerator"
/// </code>
/// Then review the diff — fixture changes must always ride with a spec update (RM-19)
/// and an ADR.
/// </summary>
public class GoldenVectorFixtureGenerator
{
    public const string RegenerateEnvVar = "YTAHD_REGENERATE_GOLDEN";

    private static string FixtureDir
    {
        get
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            return Path.Combine(repoRoot, "YTAHD.Tests", "GoldenVectors");
        }
    }

    /// <summary>Deterministic 256-byte payload: byte[i] = (i * 11 + 3) % 251.</summary>
    internal static byte[] DeterministicPayload(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)((i * 11 + 3) % 251)).ToArray();

    internal static StreamManifest CreateManifest(int payloadLength, int dataFrameCount, string modulatorId) =>
        new()
        {
            TotalPayloadBytes = payloadLength,
            PayloadSha256 = SHA256.HashData(DeterministicPayload(payloadLength)),
            ModulatorId = modulatorId,
            Width = 640,
            Height = 480,
            MacroblockSize = 16,
            Fps = 30,
            DataFrameCount = dataFrameCount,
            ParityGroupSize = 4,
            ParitySymbolsPerGroup = 1,
            UseDurabilityMatrix = true
        };

    internal static string ToHexLines(IEnumerable<byte[]> packets) =>
        string.Join(Environment.NewLine, packets.Select(p => Convert.ToHexString(p))) + Environment.NewLine;

    internal static List<byte[]> FromHexLines(string content) =>
        content.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
            .Select(l => Convert.FromHexString(l)).ToList();

    [Fact]
    public void Regenerate_When_Requested()
    {
        if (Environment.GetEnvironmentVariable(RegenerateEnvVar) != "1")
        {
            // No-op by default: fixtures are committed artifacts, not test outputs.
            return;
        }

        Directory.CreateDirectory(FixtureDir);
        var matrixOptions = new DurabilityMatrixOptions { SymbolSize = 32, GroupSize = 4, ParitySymbolsPerGroup = 1 };

        // Fixture 1: v2 packets, 256-byte payload, phase1, single-chunk manifest.
        {
            const int payloadLength = 256;
            var payload = DeterministicPayload(payloadLength);
            var dataFrameCount = (int)Math.Ceiling(payloadLength / (double)matrixOptions.SymbolSize); // 8
            var manifest = CreateManifest(payloadLength, dataFrameCount, "phase1");
            var packets = new DurabilityTransportCodec(matrixOptions)
                .EncodeToFramePackets(payload, manifest, maxPayloadBytesPerFrame: 0);
            WriteFixture("golden-v2-phase1-256b", payload, packets, matrixOptions, manifest, maxPayloadBytesPerFrame: 0);
        }

        // Fixture 2: v2 packets, 256-byte payload, phase1, manifest chunked at 64-byte frames
        // (pins the manifest chunking wire format: chunkIndex/chunkCount in header fields).
        {
            const int payloadLength = 256;
            var payload = DeterministicPayload(payloadLength);
            var dataFrameCount = (int)Math.Ceiling(payloadLength / (double)matrixOptions.SymbolSize); // 8
            var manifest = CreateManifest(payloadLength, dataFrameCount, "phase1");
            var packets = new DurabilityTransportCodec(matrixOptions)
                .EncodeToFramePackets(payload, manifest, maxPayloadBytesPerFrame: 64);
            WriteFixture("golden-v2-phase1-256b-chunked-manifest", payload, packets, matrixOptions, manifest, maxPayloadBytesPerFrame: 64);
        }

        // Fixture 3: legacy v1 packet (hand-built, hash-gated wrapped-length recovery path).
        {
            var payload = DeterministicPayload(300); // above the v1 16-bit length field wrap? 300 < 65536; use 70000 to exceed it
            payload = DeterministicPayload(70000);
            var packet = new byte[FramePacket.LegacyHeaderBytes + payload.Length];
            packet[0] = (byte)((FramePacket.FrameMagic >> 8) & 0xFF);
            packet[1] = (byte)(FramePacket.FrameMagic & 0xFF);
            packet[2] = FramePacket.LegacyFrameVersion;
            packet[3] = FramePacket.FrameTypeData;
            packet[11] = 1; // totalDataFrames = 1
            packet[16] = 1; // groupCount = 1
            int truncatedPayloadLength = payload.Length & 0xFFFF;
            packet[17] = (byte)((truncatedPayloadLength >> 8) & 0xFF);
            packet[18] = (byte)(truncatedPayloadLength & 0xFF);
            var hash = SHA256.HashData(payload);
            Buffer.BlockCopy(hash, 0, packet, 19, hash.Length);
            Buffer.BlockCopy(payload, 0, packet, FramePacket.LegacyHeaderBytes, payload.Length);
            File.WriteAllText(Path.Combine(FixtureDir, "golden-v1-data-70000b.packets.hex"), ToHexLines(new[] { packet }));
            File.WriteAllText(Path.Combine(FixtureDir, "golden-v1-data-70000b.payload.hex"), Convert.ToHexString(payload) + Environment.NewLine);
        }
    }

    private static void WriteFixture(string baseName, byte[] payload, List<byte[]> packets, DurabilityMatrixOptions options, StreamManifest manifest, int maxPayloadBytesPerFrame)
    {
        File.WriteAllText(Path.Combine(FixtureDir, baseName + ".packets.hex"), ToHexLines(packets));
        File.WriteAllText(Path.Combine(FixtureDir, baseName + ".payload.hex"), Convert.ToHexString(payload) + Environment.NewLine);

        var manifestPackets = packets.Where(p => p[3] == FramePacket.FrameTypeManifest).ToList();
        var sidecar = new
        {
            description = "YTAHD golden vector (RM-11). packets.hex = one hex packet per line; payload.hex = the expected decoded payload.",
            protocolVersion = FramePacket.FrameVersion,
            payloadBytes = payload.Length,
            payloadSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            packetCount = packets.Count,
            manifestFrameCount = manifestPackets.Count,
            symbolSize = options.SymbolSize,
            groupSize = options.GroupSize,
            paritySymbolsPerGroup = options.ParitySymbolsPerGroup,
            maxPayloadBytesPerFrame = maxPayloadBytesPerFrame,
            manifest = new
            {
                manifest.Version,
                manifest.TotalPayloadBytes,
                payloadSha256 = Convert.ToHexString(manifest.PayloadSha256).ToLowerInvariant(),
                manifest.ModulatorId,
                manifest.Width,
                manifest.Height,
                manifest.MacroblockSize,
                manifest.Fps,
                manifest.DataFrameCount,
                manifest.ParityGroupSize,
                manifest.ParitySymbolsPerGroup,
                manifest.UseDurabilityMatrix
            },
            // Pin the v2 header layout of the first data packet (RM-19 spec anchor).
            firstDataFrameHeader = DescribeHeader(packets.First(p => p[3] == FramePacket.FrameTypeData))
        };
        File.WriteAllText(Path.Combine(FixtureDir, baseName + ".json"), System.Text.Json.JsonSerializer.Serialize(sidecar, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static object DescribeHeader(byte[] packet)
    {
        return new
        {
            magic = ((packet[0] << 8) | packet[1]).ToString("X4"),
            version = packet[2],
            frameType = packet[3],
            frameIndex = (packet[4] << 24) | (packet[5] << 16) | (packet[6] << 8) | packet[7],
            totalDataFrames = (packet[8] << 24) | (packet[9] << 16) | (packet[10] << 8) | packet[11],
            groupStart = (packet[12] << 24) | (packet[13] << 16) | (packet[14] << 8) | packet[15],
            groupCount = packet[16],
            payloadLength = (packet[17] << 24) | (packet[18] << 16) | (packet[19] << 8) | packet[20],
            headerBytes = FramePacket.HeaderBytes
        };
    }
}

/// <summary>
/// RM-11 acceptance: the committed golden fixtures decode byte-exactly WITHOUT running the
/// encoder. Any diff against these files means the wire format changed — such a change must
/// update the ARCHITECTURE.md spec (RM-19) and ship an ADR in the same commit.
/// </summary>
public class GoldenVectorFixtureTests
{
    private static string FixtureDir
    {
        get
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            return Path.Combine(repoRoot, "YTAHD.Tests", "GoldenVectors");
        }
    }

    private static byte[] ReadPayloadHex(string baseName) =>
        Convert.FromHexString(File.ReadAllText(Path.Combine(FixtureDir, baseName + ".payload.hex")).Trim());

    private static List<byte[]> ReadPackets(string baseName) =>
        GoldenVectorFixtureGenerator.FromHexLines(File.ReadAllText(Path.Combine(FixtureDir, baseName + ".packets.hex")));

    [Fact]
    public void V2_Phase1_256b_Fixtures_Decode_ByteExact_Without_Encoder()
    {
        const string baseName = "golden-v2-phase1-256b";
        var expectedPayload = ReadPayloadHex(baseName);
        var packets = ReadPackets(baseName);

        // Every packet must parse strictly (magic, version, per-frame SHA-256).
        Assert.All(packets, p =>
            Assert.True(FramePacketCodec.TryDecode(p, out _, out _, out _, out _, out _, out _, out _),
                "A committed golden packet failed strict decode — wire format regressed."));

        // Data/parity/manifest counts per the current schedule: 8 data + 2 parity + 2 manifest copies.
        Assert.Equal(8, packets.Count(p => p[3] == FramePacket.FrameTypeData));
        Assert.Equal(2, packets.Count(p => p[3] == FramePacket.FrameTypeParity));
        Assert.Equal(2, packets.Count(p => p[3] == FramePacket.FrameTypeManifest));

        // Full durability decode (hole-tolerant path used by the real pipeline).
        var codec = new DurabilityTransportCodec(new DurabilityMatrixOptions { SymbolSize = 32, GroupSize = 4, ParitySymbolsPerGroup = 1 });
        Assert.True(codec.TryDecodeFramePacketsWithHoles(packets, expectedPayload.Length, out var payload, out var recoveredBytes, out var manifest, out var missingGroups));
        Assert.Empty(missingGroups);
        Assert.Equal(expectedPayload.Length, recoveredBytes);
        Assert.True(expectedPayload.AsSpan().SequenceEqual(payload), "Golden fixture decoded to different bytes — wire format regressed.");

        // Manifest fields pinned by the sidecar contract.
        Assert.NotNull(manifest);
        Assert.Equal(256, manifest!.TotalPayloadBytes);
        Assert.Equal("phase1", manifest.ModulatorId);
        Assert.Equal(8, manifest.DataFrameCount);
        Assert.Equal(4, manifest.ParityGroupSize);
        Assert.Equal(1, manifest.ParitySymbolsPerGroup);
        Assert.True(manifest.UseDurabilityMatrix);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expectedPayload)).ToLowerInvariant(), Convert.ToHexString(manifest.PayloadSha256).ToLowerInvariant());
    }

    [Fact]
    public void V2_ChunkedManifest_Fixtures_Decode_ByteExact_Without_Encoder()
    {
        const string baseName = "golden-v2-phase1-256b-chunked-manifest";
        var expectedPayload = ReadPayloadHex(baseName);
        var packets = ReadPackets(baseName);

        // Manifest body (~80 bytes) exceeds the 64-byte frame capacity: 2 chunks per copy.
        var manifestPackets = packets.Where(p => p[3] == FramePacket.FrameTypeManifest).ToList();
        Assert.Equal(4, manifestPackets.Count); // 2 chunks x 2 copies (start + end)

        var codec = new DurabilityTransportCodec(new DurabilityMatrixOptions { SymbolSize = 32, GroupSize = 4, ParitySymbolsPerGroup = 1 });
        Assert.True(codec.TryDecodeFramePacketsWithHoles(packets, expectedPayload.Length, out var payload, out var recoveredBytes, out var manifest, out var missingGroups));
        Assert.Empty(missingGroups);
        Assert.Equal(expectedPayload.Length, recoveredBytes);
        Assert.True(expectedPayload.AsSpan().SequenceEqual(payload), "Chunked-manifest golden fixture decoded to different bytes — wire format regressed.");
        Assert.NotNull(manifest);
        Assert.Equal(256, manifest!.TotalPayloadBytes);
    }

    [Fact]
    public void V1_Legacy_OversizedLength_Fixture_Decodes_ByteExact_Without_Encoder()
    {
        var packets = ReadPackets("golden-v1-data-70000b");
        var expectedPayload = ReadPayloadHex("golden-v1-data-70000b");
        var packet = Assert.Single(packets);

        Assert.Equal(FramePacket.LegacyFrameVersion, packet[2]);
        Assert.True(FramePacketCodec.TryDecode(packet, out _, out _, out _, out _, out _, out var payloadLength, out var payload));
        Assert.Equal(expectedPayload.Length, payloadLength); // hash-gated wrapped-length recovery
        Assert.True(expectedPayload.AsSpan().SequenceEqual(payload), "Legacy v1 golden fixture decoded to different bytes — wire format regressed.");
    }
}
