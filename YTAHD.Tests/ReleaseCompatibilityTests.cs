using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Xunit;
using YTAHD.Core.Core;

namespace YTAHD.Tests;

/// <summary>
/// RM-12 cross-release compatibility: fixtures under <c>ReleaseFixtures/&lt;tag&gt;/</c>
/// were produced by THAT tag's own encoder (see <c>scripts/generate-release-fixtures.ps1</c>),
/// committed as artifacts. These tests decode them with the CURRENT code without running any
/// encoder, proving the decoder still reads streams produced by prior releases —
/// compatibility against history, not just the current pair.
///
/// Wire-format eras covered:
/// - v0.1.1: v1 packets (51-byte header, 16-bit payload length), durability matrix, no manifest
/// - v0.1.3: v2 packets (53-byte header, 32-bit payload length), no manifest
/// - v0.2.0: v2 packets + StreamManifest (start/end copies) — the release the RM-12
///   acceptance criterion names ("at least the previous tagged release")
///
/// When a NEW tag is released, add its fixture set to the generator script and a decode
/// test here in the same change. When a wire-format change lands intentionally, the OLD
/// fixtures stay: if a decode test breaks, the change broke backward compatibility and
/// needs an ADR + RM-19 spec note saying so (or a protocol-version gate).
/// </summary>
public class ReleaseCompatibilityTests
{
    private static string FixtureRoot
    {
        get
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            return Path.Combine(repoRoot, "YTAHD.Tests", "ReleaseFixtures");
        }
    }

    private static byte[] ReadPayloadHex(string tag, string name) =>
        Convert.FromHexString(File.ReadAllText(Path.Combine(FixtureRoot, tag, name + ".payload.hex")).Trim());

    private static List<byte[]> ReadPackets(string tag, string name) =>
        GoldenVectorFixtureGenerator.FromHexLines(File.ReadAllText(Path.Combine(FixtureRoot, tag, name + ".packets.hex")));

    private static DurabilityMatrixOptions FixtureMatrix() =>
        new() { SymbolSize = 32, GroupSize = 4, ParitySymbolsPerGroup = 1 };

    private static void AssertDecodesByteExact(string tag, string name)
    {
        var expectedPayload = ReadPayloadHex(tag, name);
        var packets = ReadPackets(tag, name);

        // Every packet must still parse strictly with the current codec (magic, version,
        // per-frame SHA-256) — v1 legacy packets via the hash-gated wrapped-length path.
        Assert.All(packets, p =>
            Assert.True(FramePacketCodec.TryDecode(p, out _, out _, out _, out _, out _, out _, out _),
                $"{tag}/{name}: a packet produced by the tagged release failed strict decode with the current codec."));

        // Full durability decode: the manifest-aware hole-tolerant path used by the real
        // pipeline (pre-manifest releases simply have no manifest frames to reconcile).
        var codec = new DurabilityTransportCodec(FixtureMatrix());
        Assert.True(
            codec.TryDecodeFramePacketsWithHoles(packets, expectedPayload.Length, out var payload, out var recoveredBytes, out var manifest, out var missingGroups),
            $"{tag}/{name}: the current decoder failed to decode the tagged release's packets.");

        Assert.Empty(missingGroups);
        Assert.Equal(expectedPayload.Length, recoveredBytes);
        Assert.True(expectedPayload.AsSpan().SequenceEqual(payload),
            $"{tag}/{name}: decoded bytes differ from the tagged release's payload — backward compatibility broke.");

        // Payload hash must agree with the fixture sidecar contract.
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(expectedPayload)).ToLowerInvariant(),
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
    }

    [Fact]
    public void V0_1_1_V1_Packets_Decode_ByteExact_With_Current_Codec()
    {
        AssertDecodesByteExact("v0.1.1", "v1-256b");
    }

    [Fact]
    public void V0_1_1_V1_PartialFinalGroup_Decodes_ByteExact_With_Current_Codec()
    {
        // 200 bytes = 7 symbols of 32: the final group holds 3 of 4 slots — pins that the
        // current partial-group recovery (CR-20260913-02) still accepts v1's groupCount
        // declaration rather than counting never-existing symbols as losses.
        AssertDecodesByteExact("v0.1.1", "v1-200b-partial-group");
    }

    [Fact]
    public void V0_1_3_V2_NoManifest_Packets_Decode_ByteExact_With_Current_Codec()
    {
        AssertDecodesByteExact("v0.1.3", "v2-nomanifest-256b");
    }

    [Fact]
    public void V0_2_0_V2_Manifest_Packets_Decode_ByteExact_With_Current_Codec()
    {
        AssertDecodesByteExact("v0.2.0", "v2-manifest-256b");
    }

    [Fact]
    public void V0_2_0_V2_ChunkedManifest_Packets_Decode_ByteExact_With_Current_Codec()
    {
        AssertDecodesByteExact("v0.2.0", "v2-manifest-256b-chunked");
    }

    [Fact]
    public void V0_2_0_Manifest_Reconciles_With_Current_Schema()
    {
        // The manifest wire format is a contract too: v0.2.0's serialized manifest must
        // deserialize into the current StreamManifest with identical fields.
        var expectedPayload = ReadPayloadHex("v0.2.0", "v2-manifest-256b");
        var packets = ReadPackets("v0.2.0", "v2-manifest-256b");

        var codec = new DurabilityTransportCodec(FixtureMatrix());
        Assert.True(
            codec.TryDecodeFramePacketsWithHoles(packets, expectedPayload.Length, out _, out _, out var manifest, out _),
            "v0.2.0 stream failed to decode.");

        Assert.NotNull(manifest);
        Assert.Equal(expectedPayload.LongLength, manifest!.TotalPayloadBytes);
        Assert.Equal("phase1", manifest.ModulatorId);
        Assert.Equal(8, manifest.DataFrameCount);
        Assert.Equal(4, manifest.ParityGroupSize);
        Assert.Equal(1, manifest.ParitySymbolsPerGroup);
        Assert.True(manifest.UseDurabilityMatrix);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(expectedPayload)).ToLowerInvariant(),
            Convert.ToHexString(manifest.PayloadSha256).ToLowerInvariant());
    }

    [Fact]
    public void Legacy_V1_Packets_Survive_Tail_Truncation_As_Holes()
    {
        // The v1 era predates manifest-clamped finalize holes, but the CURRENT decoder's
        // hole-tolerant contract must still hold for v1 streams: dropping the final group's
        // packets yields a zero-filled hole + reported group, not a decode failure.
        var expectedPayload = ReadPayloadHex("v0.1.1", "v1-256b");
        var packets = ReadPackets("v0.1.1", "v1-256b"); // 8 data (2 groups of 4) + 2 parity

        var truncated = packets.Take(8).ToList(); // drop group 1's data + parity

        var codec = new DurabilityTransportCodec(FixtureMatrix());
        Assert.True(
            codec.TryDecodeFramePacketsWithHoles(truncated, expectedPayload.Length, out var payload, out _, out var manifest, out var missingGroups),
            "Truncated v1 stream should produce holes, not a decode failure.");

        Assert.Equal(new[] { 1 }, missingGroups);
        Assert.Null(manifest); // v1 era carried no manifest frames
        Assert.Equal(expectedPayload.Length, payload.Length);
        Assert.True(payload.Skip(128).All(b => b == 0), "Hole region must be zero-filled.");
        Assert.True(expectedPayload.AsSpan(0, 128).SequenceEqual(payload.AsSpan(0, 128)),
            "Surviving group must decode byte-exactly.");
    }
}
