using System;
using System.Linq;
using Xunit;
using YTAHD.Core.Core;

namespace YTAHD.Tests;

public class DurabilityTransportCodecTests
{
    [Fact]
    public void EncodeToFramePackets_ThenDecodeFramePackets_RestoresPayload()
    {
        var options = new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        };

        var codec = new DurabilityTransportCodec(options);
        var payload = Enumerable.Range(0, 256).Select(i => (byte)((i * 19) % 251)).ToArray();

        var packets = codec.EncodeToFramePackets(payload);
        var received = packets
            .Where(p => !(p[12] == 0 && p[16] == 1 && p[3] == FramePacket.FrameTypeData))
            .ToList();

        var decoded = codec.TryDecodeFramePackets(received, payload.Length, out var restored, out var recoveredBytes);

        Assert.True(decoded);
        Assert.Equal(payload.Length, recoveredBytes);
        Assert.Equal(payload, restored);
    }

    [Fact]
    public void TryDecodeFramePackets_Ignores_InvalidPackets_And_Uses_ValidSubset()
    {
        var options = new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        };

        var codec = new DurabilityTransportCodec(options);
        var payload = Enumerable.Range(0, 128).Select(i => (byte)((i * 7) % 251)).ToArray();

        var packets = codec.EncodeToFramePackets(payload);
        var invalid = new byte[16];
        var received = packets.Concat(new[] { invalid }).ToList();

        var decoded = codec.TryDecodeFramePackets(received, payload.Length, out var restored, out var recoveredBytes);

        Assert.True(decoded);
        Assert.Equal(payload.Length, recoveredBytes);
        Assert.Equal(payload, restored);
    }
}
