using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YTAHD.Core.Core;

namespace YTAHD.Tests;

public class DurabilityMatrixTests
{
    [Fact]
    public void Encode_ThenDecode_RestoresPayload_WhenSingleSourceSymbolIsMissing()
    {
        var options = new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 8,
            ParitySymbolsPerGroup = 1
        };

        var codec = new DurabilityMatrixCodec(options);
        var payload = Enumerable.Range(0, 4096)
            .Select(i => (byte)(i % 251))
            .ToArray();

        var encoded = codec.Encode(payload);
        var groupId = 2;
        var groupSymbols = encoded.Symbols.Where(s => s.GroupId == groupId).ToList();
        var sourceSymbol = groupSymbols.First(s => !s.IsParity);

        var received = encoded.Symbols.Where(s => s.GroupId != groupId || s.SymbolId != sourceSymbol.SymbolId).ToList();

        var recovered = codec.TryDecode(received, payload.Length, out var decoded, out var recoveredBytes);

        Assert.True(recovered);
        Assert.Equal(payload.Length, recoveredBytes);
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void TryDecode_ReturnsFalse_WhenTooManySymbolsAreMissing()
    {
        var options = new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 8,
            ParitySymbolsPerGroup = 1
        };

        var codec = new DurabilityMatrixCodec(options);
        var payload = Enumerable.Range(0, 512).Select(i => (byte)(i * 7 % 251)).ToArray();

        var encoded = codec.Encode(payload);
        var groupId = 0;
        var received = encoded.Symbols.Where(s => s.GroupId != groupId || s.SymbolId > 5).ToList();

        var recovered = codec.TryDecode(received, payload.Length, out _, out _);

        Assert.False(recovered);
    }

    [Fact]
    public void Encode_ThenDecode_RestoresPayload_AcrossMultipleGroups()
    {
        var options = new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        };

        var codec = new DurabilityMatrixCodec(options);
        var payload = Enumerable.Range(0, 256)
            .Select(i => (byte)(i * 11 % 251))
            .ToArray();

        var encoded = codec.Encode(payload);
        var received = encoded.Symbols
            .Where(s => !(s.GroupId == 0 && s.SymbolId == 1))
            .Where(s => !(s.GroupId == 1 && s.SymbolId == 3))
            .ToList();

        var recovered = codec.TryDecode(received, payload.Length, out var decoded, out var recoveredBytes);

        Assert.True(recovered);
        Assert.Equal(payload.Length, recoveredBytes);
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void RecoveryThreshold_IsConservative_AndDerivedFromGroupSize()
    {
        var options = new DurabilityMatrixOptions
        {
            SymbolSize = 16,
            GroupSize = 8,
            ParitySymbolsPerGroup = 1
        };

        Assert.Equal(7, options.ComputeRecoveryThreshold());
        Assert.Equal(7, DurabilityRecoveryPolicy.ResolveMinimumRequiredSymbols(options));
    }

    [Fact]
    public void RecoveryPolicy_AllowsRecovery_WhenThresholdIsMet()
    {
        var metrics = new DurabilityMetrics
        {
            SymbolsRecovered = 7,
            MinimumRequiredSymbols = 7,
            TotalGroups = 2
        };

        Assert.True(DurabilityRecoveryPolicy.CanRecover(metrics));
    }

    [Fact]
    public void RecoveryPolicy_PreventsRecovery_WhenThresholdIsNotMet()
    {
        var metrics = new DurabilityMetrics
        {
            SymbolsRecovered = 5,
            MinimumRequiredSymbols = 7,
            TotalGroups = 2
        };

        Assert.False(DurabilityRecoveryPolicy.CanRecover(metrics));
    }

    [Fact]
    public void EncodeToPackets_ThenDecodePackets_RestoresPayload_WhenSourcePacketsAreMissing()
    {
        var options = new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        };

        var codec = new DurabilityMatrixCodec(options);
        var payload = Enumerable.Range(0, 256)
            .Select(i => (byte)((i * 17) % 251))
            .ToArray();

        var packets = codec.EncodeToPackets(payload);
        var received = packets
            .Where(p => !(p.GroupId == 0 && p.SymbolId == 1))
            .Where(p => !(p.GroupId == 1 && p.SymbolId == 3))
            .ToList();

        var decoded = codec.TryDecodePackets(received, payload.Length, out var recoveredPayload, out var recoveredBytes);

        Assert.True(decoded);
        Assert.Equal(payload.Length, recoveredBytes);
        Assert.Equal(payload, recoveredPayload);
    }

    [Fact]
    public void DurabilitySymbol_TracksMetadata_AndChecksum()
    {
        var options = new DurabilityMatrixOptions
        {
            SymbolSize = 32,
            GroupSize = 4,
            ParitySymbolsPerGroup = 1
        };

        var codec = new DurabilityMatrixCodec(options);
        var payload = Enumerable.Range(0, 128).Select(i => (byte)((i * 13) % 251)).ToArray();

        var envelope = codec.Encode(payload);
        var symbol = envelope.Symbols.First(s => !s.IsParity);

        Assert.IsAssignableFrom<IDataDurabilityCodec>(codec);
        Assert.Equal(0, symbol.GroupId);
        Assert.Equal(0, symbol.SymbolId);
        Assert.Equal(symbol.Data.Length, symbol.SourceLength);
        Assert.NotEmpty(symbol.Hash);
        Assert.True(symbol.HasValidHash());
    }
}
