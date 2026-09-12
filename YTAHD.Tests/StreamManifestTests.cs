using System;
using System.Linq;
using Xunit;
using YTAHD.Core.Core;

namespace YTAHD.Tests;

/// <summary>
/// Regression coverage for the stream manifest (CR-20260912-05 stage 2): serialisation
/// round-trip, redundant emission, reconciliation of identical copies, and loud failure on
/// conflicting manifests.
/// </summary>
public class StreamManifestTests
{
    private static StreamManifest CreateManifest(int dataFrameCount = 8)
    {
        return new StreamManifest
        {
            TotalPayloadBytes = 256,
            PayloadSha256 = System.Security.Cryptography.SHA256.HashData(Enumerable.Range(0, 256).Select(i => (byte)(i * 7 % 251)).ToArray()),
            ModulatorId = "phase1",
            Width = 640,
            Height = 480,
            MacroblockSize = 16,
            Fps = 30,
            DataFrameCount = dataFrameCount,
            ParityGroupSize = 4,
            ParitySymbolsPerGroup = 1,
            UseDurabilityMatrix = true
        };
    }

    [Fact]
    public void Serialize_ThenDeserialize_RoundTrips_All_Fields()
    {
        var manifest = CreateManifest();

        var payload = StreamManifestCodec.Serialize(manifest);
        Assert.True(StreamManifestCodec.TryDeserialize(payload, out var decoded));
        Assert.NotNull(decoded);

        Assert.Equal(manifest.Version, decoded!.Version);
        Assert.Equal(manifest.TotalPayloadBytes, decoded.TotalPayloadBytes);
        Assert.Equal(manifest.PayloadSha256, decoded.PayloadSha256);
        Assert.Equal(manifest.ModulatorId, decoded.ModulatorId);
        Assert.Equal(manifest.Width, decoded.Width);
        Assert.Equal(manifest.Height, decoded.Height);
        Assert.Equal(manifest.MacroblockSize, decoded.MacroblockSize);
        Assert.Equal(manifest.Fps, decoded.Fps);
        Assert.Equal(manifest.DataFrameCount, decoded.DataFrameCount);
        Assert.Equal(manifest.ParityGroupSize, decoded.ParityGroupSize);
        Assert.Equal(manifest.ParitySymbolsPerGroup, decoded.ParitySymbolsPerGroup);
        Assert.Equal(manifest.UseDurabilityMatrix, decoded.UseDurabilityMatrix);
    }

    [Fact]
    public void TryDeserialize_Rejects_Truncated_And_UnknownVersion_Payloads()
    {
        Assert.False(StreamManifestCodec.TryDeserialize(new byte[10], out _));

        var payload = StreamManifestCodec.Serialize(CreateManifest());
        payload[0] = 0xFF;
        Assert.False(StreamManifestCodec.TryDeserialize(payload, out _));
    }

    [Fact]
    public void EncodeToFramePackets_Emits_Manifest_At_Start_And_End()
    {
        var codec = new DurabilityTransportCodec(new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        });

        var payload = Enumerable.Range(0, 256).Select(i => (byte)(i * 11 % 251)).ToArray();
        var packets = codec.EncodeToFramePackets(payload, CreateManifest());

        var manifestPackets = packets.Where(p => p[3] == FramePacket.FrameTypeManifest).ToList();
        Assert.Equal(2, manifestPackets.Count);
        Assert.Equal(FramePacket.FrameTypeManifest, packets[0][3]);
        Assert.Equal(FramePacket.FrameTypeManifest, packets[^1][3]);
    }

    [Fact]
    public void Manifest_Frames_RoundTrip_Through_TryDecode()
    {
        var manifest = CreateManifest();
        var packet = FramePacketCodec.CreateManifestFramePacket(8, StreamManifestCodec.Serialize(manifest));

        Assert.True(FramePacketCodec.TryDecode(packet, out var frameType, out _, out _, out _, out _, out var payloadLength, out var payload));
        Assert.Equal(FramePacket.FrameTypeManifest, frameType);
        Assert.True(StreamManifestCodec.TryDeserialize(payload, out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(manifest.TotalPayloadBytes, decoded!.TotalPayloadBytes);
        Assert.Equal(payloadLength, payload.Length);
    }

    [Fact]
    public void TryDecodeFramePackets_Reconciles_Identical_Manifest_Copies()
    {
        var codec = new DurabilityTransportCodec(new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        });

        var payload = Enumerable.Range(0, 256).Select(i => (byte)(i * 11 % 251)).ToArray();
        var packets = codec.EncodeToFramePackets(payload, CreateManifest());

        var decoded = codec.TryDecodeFramePackets(packets, payload.Length, out var restored, out var recoveredBytes, out var manifest);

        Assert.True(decoded);
        Assert.Equal(payload, restored);
        Assert.NotNull(manifest);
        Assert.Equal(payload.Length, manifest!.TotalPayloadBytes);
        Assert.Equal(payload.Length, recoveredBytes);
    }

    [Fact]
    public void TryDecodeFramePackets_Fails_Loudly_On_Conflicting_Manifests()
    {
        var codec = new DurabilityTransportCodec(new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        });

        var payload = Enumerable.Range(0, 256).Select(i => (byte)(i * 11 % 251)).ToArray();
        var packets = codec.EncodeToFramePackets(payload, CreateManifest());

        // Replace the trailing manifest copy with a conflicting one: the two copies no longer
        // agree, so the decode must fail rather than silently pick one.
        var baseManifest = CreateManifest();
        var conflicting = new StreamManifest
        {
            Version = baseManifest.Version,
            TotalPayloadBytes = payload.Length + 1,
            PayloadSha256 = baseManifest.PayloadSha256,
            ModulatorId = baseManifest.ModulatorId,
            Width = baseManifest.Width,
            Height = baseManifest.Height,
            MacroblockSize = baseManifest.MacroblockSize,
            Fps = baseManifest.Fps,
            DataFrameCount = baseManifest.DataFrameCount,
            ParityGroupSize = baseManifest.ParityGroupSize,
            ParitySymbolsPerGroup = baseManifest.ParitySymbolsPerGroup,
            UseDurabilityMatrix = baseManifest.UseDurabilityMatrix
        };
        packets[^1] = FramePacketCodec.CreateManifestFramePacket(8, StreamManifestCodec.Serialize(conflicting));

        var decoded = codec.TryDecodeFramePackets(packets, payload.Length, out _, out _, out var manifest);

        Assert.False(decoded);
        Assert.Null(manifest);
    }

    [Fact]
    public void TryDecodeFramePackets_Without_Manifest_Still_Decodes()
    {
        var codec = new DurabilityTransportCodec(new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        });

        var payload = Enumerable.Range(0, 256).Select(i => (byte)(i * 11 % 251)).ToArray();
        var packets = codec.EncodeToFramePackets(payload);

        var decoded = codec.TryDecodeFramePackets(packets, payload.Length, out var restored, out var recoveredBytes, out var manifest);

        Assert.True(decoded);
        Assert.Equal(payload, restored);
        Assert.Null(manifest);
    }
}
