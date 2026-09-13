using System;
using Xunit;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests;

/// <summary>
/// CR-20260913-07: frame-bit decoder resolution is owned by the modulator via the
/// <see cref="IFrameBitDecoderProvider"/> capability interface. A modulator without the
/// capability (and without a legacy ladder entry) must fail at registration time — when the
/// factory is asked for a decoder — not mid-decode after FFmpeg has already run.
/// </summary>
public class FrameBitDecoderFactoryTests
{
    [Theory]
    [InlineData(typeof(BinaryGridFrameBitDecoder))]
    [InlineData(typeof(PseudoQamFrameBitDecoder))]
    [InlineData(typeof(DctFrameBitDecoder))]
    [InlineData(typeof(MotionFrameBitDecoder))]
    public void Factory_Resolves_Decoder_For_Every_Shipped_Modulator(Type decoderType)
    {
        var modulator = decoderType.Name switch
        {
            nameof(BinaryGridFrameBitDecoder) => (IModulator)new BinaryGridModulator(),
            nameof(PseudoQamFrameBitDecoder) => new PseudoQamModulator(),
            nameof(DctFrameBitDecoder) => new DctModulator(),
            nameof(MotionFrameBitDecoder) => new MotionVectorModulator(),
            _ => throw new ArgumentException(decoderType.Name)
        };

        var decoder = FrameBitDecoderFactory.CreateForModulator(modulator);

        Assert.IsType(decoderType, decoder);
    }

    [Fact]
    public void Factory_Resolves_Decoder_Through_Decorator_Wrapper()
    {
        var decoder = FrameBitDecoderFactory.CreateForModulator(new PassthroughDecorator(new BinaryGridModulator()));

        Assert.IsType<BinaryGridFrameBitDecoder>(decoder);
    }

    [Fact]
    public void Factory_Fails_At_Registration_For_Modulator_Without_Decoder()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => FrameBitDecoderFactory.CreateForModulator(new DecoderlessModulator()));

        Assert.Contains("DecoderlessModulator", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_Inherits_InnerDegreeOfParallelism_From_Modulator()
    {
        var modulator = new DctModulator { InnerDegreeOfParallelism = 3 };

        var decoder = Assert.IsType<DctFrameBitDecoder>(FrameBitDecoderFactory.CreateForModulator(modulator));

        Assert.Equal(3, decoder.InnerDegreeOfParallelism);
    }

    private sealed class PassthroughDecorator : IModulatorDecorator
    {
        private readonly IModulator _inner;

        public PassthroughDecorator(IModulator inner) => _inner = inner;

        public IModulator Inner => _inner;

        public int MacroblockWidth => _inner.MacroblockWidth;
        public int MacroblockHeight => _inner.MacroblockHeight;
        public int GetPayloadBytesPerFrame(ModulatorGeometry geometry) => _inner.GetPayloadBytesPerFrame(geometry);
        public int GetPacketBufferLength(ModulatorGeometry geometry, int payloadBytesPerFrame) => _inner.GetPacketBufferLength(geometry, payloadBytesPerFrame);
        public int GetBorderWidth(ModulatorGeometry geometry) => _inner.GetBorderWidth(geometry);
        public byte[] CreateFrame(ModulatorGeometry geometry, ReadOnlySpan<byte> payload) => _inner.CreateFrame(geometry, payload);
        public void Encode(ReadOnlySpan<byte> input, Span<byte> pixelBuffer) => _inner.Encode(input, pixelBuffer);
        public void Decode(ReadOnlySpan<byte> pixelBuffer, Span<byte> output) => _inner.Decode(pixelBuffer, output);
    }

    /// <summary>
    /// Encode-only modulator stub with no decoder capability and no legacy ladder entry:
    /// resolution must fail loudly here, not at first frame.
    /// </summary>
    private sealed class DecoderlessModulator : IModulator
    {
        public int MacroblockWidth => 1;
        public int MacroblockHeight => 1;
        public int GetPayloadBytesPerFrame(ModulatorGeometry geometry) => 1;
        public int GetPacketBufferLength(ModulatorGeometry geometry, int payloadBytesPerFrame) => geometry.HeaderBytes + payloadBytesPerFrame;
        public int GetBorderWidth(ModulatorGeometry geometry) => 0;
        public byte[] CreateFrame(ModulatorGeometry geometry, ReadOnlySpan<byte> payload) => new byte[geometry.Width * geometry.Height * 4];
        public void Encode(ReadOnlySpan<byte> input, Span<byte> pixelBuffer) { }
        public void Decode(ReadOnlySpan<byte> pixelBuffer, Span<byte> output) { }
    }
}
