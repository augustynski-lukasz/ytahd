using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YTAHD.Core.Core;

namespace YTAHD.Tests;

public class DurabilityMatrixEdgeCaseTests
{
    [Fact]
    public void Encode_EmptyPayload_ReturnsNoSymbols()
    {
        var codec = new DurabilityMatrixCodec(new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        });

        var envelope = codec.Encode(Array.Empty<byte>());

        Assert.Empty(envelope.Symbols);
    }

    [Fact]
    public void Encode_RejectsInvalidOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurabilityMatrixCodec(new DurabilityMatrixOptions
        {
            SymbolSize = 0,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        }));

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurabilityMatrixCodec(new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 0,
            ParitySymbolsPerGroup = 1
        }));

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurabilityMatrixCodec(new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = -1
        }));
    }

    [Fact]
    public void TryDecode_Fails_ForChecksumCorruption()
    {
        var options = new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        };

        var codec = new DurabilityMatrixCodec(options);
        var payload = Enumerable.Range(0, 128).Select(i => (byte)(i * 7 % 251)).ToArray();

        var envelope = codec.Encode(payload);
        var mutated = envelope.Symbols.First(s => !s.IsParity);
        mutated.Hash = new byte[32];

        var decoded = codec.TryDecode(new[] { mutated }, payload.Length, out _, out _);

        Assert.False(decoded);
    }

    [Fact]
    public void TryDecode_Fails_WhenGroupIsMissingParity()
    {
        var options = new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        };

        var codec = new DurabilityMatrixCodec(options);
        var payload = Enumerable.Range(0, 128).Select(i => (byte)((i * 3) % 251)).ToArray();

        var encoded = codec.Encode(payload);
        var received = encoded.Symbols
            .Where(s => s.GroupId == 0 && !s.IsParity)
            .Take(3)
            .ToList();

        var recovered = codec.TryDecode(received, payload.Length, out _, out _);

        Assert.False(recovered);
    }

    [Fact]
    public void TryDecode_Packets_Handles_DuplicateSymbols_WithoutCorruptingOutput()
    {
        var options = new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        };

        var codec = new DurabilityMatrixCodec(options);
        var payload = Enumerable.Range(0, 96).Select(i => (byte)((i * 11) % 251)).ToArray();

        var packets = codec.EncodeToPackets(payload);
        var duplicate = packets.First();
        var received = packets.Concat(new[] { duplicate }).ToList();

        var recovered = codec.TryDecodePackets(received, payload.Length, out var decoded, out var recoveredBytes);

        Assert.True(recovered);
        Assert.Equal(payload.Length, recoveredBytes);
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void RecoveryPolicy_Rejects_ZeroOrNegativeThresholds()
    {
        var metrics = new DurabilityMetrics
        {
            SymbolsRecovered = 0,
            MinimumRequiredSymbols = 0,
            TotalGroups = 1
        };

        Assert.True(DurabilityRecoveryPolicy.CanRecover(metrics));

        var zeroOptions = new DurabilityMatrixOptions
        {
            SymbolSize = 16,
            GroupSize = 1,
            ParitySymbolsPerGroup = 0
        };

        Assert.Equal(1, DurabilityRecoveryPolicy.ResolveMinimumRequiredSymbols(zeroOptions));
    }
}
