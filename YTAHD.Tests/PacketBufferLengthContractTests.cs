using System.Collections.Generic;
using Xunit;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests;

/// <summary>
/// CR-20260913-06: the packet buffer-length contract. For every registered modulator,
/// <c>GetPacketBufferLength</c> must equal <c>HeaderBytes + GetPayloadBytesPerFrame</c> —
/// the byte-count invariant the method name promises. Callers allocate the per-frame packet
/// buffer from this value, so a modulator returning a bit count (the historical Phase 2
/// behavior) over-allocates ~8× and breaks the contract.
/// </summary>
public class PacketBufferLengthContractTests
{
    public static IEnumerable<object[]> RegisteredModulators()
    {
        yield return new object[] { new BinaryGridModulator() };
        yield return new object[] { new PseudoQamModulator() };
        yield return new object[] { new DctModulator() };
        yield return new object[] { new MotionVectorModulator() };
    }

    [Theory]
    [MemberData(nameof(RegisteredModulators))]
    public void GetPacketBufferLength_Equals_HeaderBytes_Plus_PayloadBytes(IModulator modulator)
    {
        const int width = 640;
        const int height = 480;
        const int macroblockSize = 16;

        var geometry = new ModulatorGeometry(width, height, macroblockSize, FramePacket.HeaderBytes, BitsPerFrame: 0);
        int borderWidth = modulator.GetBorderWidth(geometry);
        geometry = geometry with { BorderWidth = borderWidth };
        int bitsPerFrame = (width / macroblockSize) * (height / macroblockSize);
        var packetGeometry = geometry with { BitsPerFrame = bitsPerFrame };

        int payloadBytes = modulator.GetPayloadBytesPerFrame(packetGeometry);
        int bufferLength = modulator.GetPacketBufferLength(packetGeometry, payloadBytes);

        Assert.True(payloadBytes > 0, $"{modulator.GetType().Name} has no payload capacity at {width}×{height}.");
        Assert.Equal(FramePacket.HeaderBytes + payloadBytes, bufferLength);
    }

    [Theory]
    [MemberData(nameof(RegisteredModulators))]
    public void GetPacketBufferLength_Is_Not_A_Bit_Count(IModulator modulator)
    {
        // The historical Phase 2 defect: Math.Max(BitsPerFrame, HeaderBytes) returned a bit
        // count when BitsPerFrame dominated. Pin that the returned value stays within the
        // byte range of the frame's pixel count (the loosest sane bound that holds for every
        // modulator regardless of its own block size), not the bit range.
        const int width = 640;
        const int height = 480;
        const int macroblockSize = 16;

        var geometry = new ModulatorGeometry(width, height, macroblockSize, FramePacket.HeaderBytes, BitsPerFrame: 0);
        int borderWidth = modulator.GetBorderWidth(geometry);
        geometry = geometry with { BorderWidth = borderWidth };
        int bitsPerFrame = (width / macroblockSize) * (height / macroblockSize);
        var packetGeometry = geometry with { BitsPerFrame = bitsPerFrame };

        int payloadBytes = modulator.GetPayloadBytesPerFrame(packetGeometry);
        int bufferLength = modulator.GetPacketBufferLength(packetGeometry, payloadBytes);

        // A byte count can never exceed the frame's total pixel count; a bit count
        // (BitsPerFrame-dominated) would be ~8× the pixel count for a 3-byte-per-pixel frame.
        Assert.True(bufferLength <= width * height, $"{modulator.GetType().Name}: buffer length {bufferLength} exceeds the frame's pixel count {width * height} — a bit count leaked into a byte contract.");
    }
}
