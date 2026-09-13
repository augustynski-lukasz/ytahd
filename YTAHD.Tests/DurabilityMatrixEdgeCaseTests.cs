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

    // ── CR-20260913-02: hole-tolerant multi-erasure repair ──────────────────────

    [Fact]
    public void TryDecodeWithHoles_RecoversIntactGroups_AroundAWholeMissingGroup()
    {
        var options = new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        };

        var codec = new DurabilityMatrixCodec(options);
        // 3 full groups of 4 symbols = 384 bytes.
        var payload = Enumerable.Range(0, 384).Select(i => (byte)((i * 13) % 251)).ToArray();

        var encoded = codec.Encode(payload);

        // Drop group 1 entirely (data AND parity): 5 symbols gone.
        var received = encoded.Symbols.Where(s => s.GroupId != 1).ToList();

        var ok = codec.TryDecodeWithHoles(received, payload.Length, out var decoded, out _, out var missingGroups);

        Assert.True(ok, "Intact groups must reconstruct even when one whole group is missing.");
        Assert.Single(missingGroups);
        Assert.Equal(1, missingGroups[0]);
        Assert.Equal(payload.Length, decoded.Length);
        // Hole content is zero-filled; everything outside the hole matches the original.
        // Each group spans 4 symbols x 32 bytes = 128 bytes, so the hole is bytes 128..255.
        for (int i = 0; i < payload.Length; i++)
        {
            byte expected = (i / 128) == 1 ? (byte)0 : payload[i];
            Assert.Equal(expected, decoded[i]);
        }
    }

    [Fact]
    public void TryDecodeWithHoles_Reports_MultipleMissingGroups_AndPadsTail()
    {
        var options = new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        };

        var codec = new DurabilityMatrixCodec(options);
        // 4 full groups of 4 symbols = 512 bytes.
        var payload = Enumerable.Range(0, 512).Select(i => (byte)((i * 5) % 251)).ToArray();

        var encoded = codec.Encode(payload);

        // Drop groups 0 and 2 entirely.
        var received = encoded.Symbols.Where(s => s.GroupId != 0 && s.GroupId != 2).ToList();

        var ok = codec.TryDecodeWithHoles(received, payload.Length, out _, out _, out var missingGroups);

        Assert.True(ok);
        Assert.Equal(new[] { 0, 2 }, missingGroups);
    }

    [Fact]
    public void TryDecodeWithHoles_AllSymbolsLost_ReportsEverythingMissing()
    {
        var options = new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        };

        var codec = new DurabilityMatrixCodec(options);
        // 128 bytes = 4 symbols x 32 = exactly one full group.
        var payload = new byte[128];

        var ok = codec.TryDecodeWithHoles(Array.Empty<DurabilitySymbol>(), payload.Length, out var decoded, out _, out var missingGroups);

        Assert.True(ok, "An all-holes decode must still report the loss map rather than throw.");
        Assert.Single(missingGroups);
        Assert.Equal(0, missingGroups[0]);
        Assert.All(decoded, b => Assert.Equal(0, b));
    }
}
