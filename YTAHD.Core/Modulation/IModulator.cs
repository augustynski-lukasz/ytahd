using System;

namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Visual mapping contract for a modulation algorithm.
    /// Implementations describe how payload bits are laid out in pixels and how a frame is rendered.
    /// Packet framing, validation, duplicate-run selection, and recovery remain the responsibility of
    /// the decoder pipeline and the packet codec layer rather than the modulator itself.
    /// Note: Span<T> cannot cross async/await boundaries, so long-running operations should use synchronous span-based APIs
    /// and expose async wrappers if needed.
    /// </summary>
    public interface IModulator
    {
        int MacroblockWidth { get; }
        int MacroblockHeight { get; }

        /// <summary>
        /// Returns how many payload bytes can fit in a single frame for the selected modulation geometry,
        /// excluding the fixed frame header size.
        /// </summary>
        int GetPayloadBytesPerFrame(ModulatorGeometry geometry);

        /// <summary>
        /// Returns the buffer size required to hold the decoded packet payload and metadata for a frame.
        /// Implementations can account for their own encoding-specific geometry and packet framing.
        /// </summary>
        int GetPacketBufferLength(ModulatorGeometry geometry, int payloadBytesPerFrame);

        /// <summary>
        /// Returns the fixed border width that the modulator reserves when painting or parsing frames.
        /// </summary>
        int GetBorderWidth(ModulatorGeometry geometry);

        /// <summary>
        /// Render an RGBA frame for the selected modulation layout using the given payload bytes.
        /// </summary>
        byte[] CreateFrame(ModulatorGeometry geometry, ReadOnlySpan<byte> payload);

        /// <summary>
        /// Encode input bytes into a pixel buffer. Pixel buffer must be large enough for a single macroblock frame (width*height*bytesPerPixel).
        /// </summary>
        void Encode(ReadOnlySpan<byte> input, Span<byte> pixelBuffer);

        /// <summary>
        /// Decode pixel buffer back into output bytes.
        /// </summary>
        void Decode(ReadOnlySpan<byte> pixelBuffer, Span<byte> output);
    }

    /// <summary>
    /// A modulator that forwards to another modulator. The frame-bit-decoder factory resolves the
    /// decoder for the effective (inner) modulator, so decorating wrappers keep working with the
    /// pipeline instead of failing decoder resolution.
    /// </summary>
    public interface IModulatorDecorator : IModulator
    {
        /// <summary>Gets the modulator this wrapper forwards to.</summary>
        IModulator Inner { get; }
    }

    /// <summary>
    /// Optional capability interface (CR-20260913-07): a modulator that can supply its own
    /// frame-bit decoder implements this, so decoder resolution is owned by the modulator
    /// instead of a type ladder in <c>FrameBitDecoderFactory</c>. Adding a new modulator then
    /// requires no factory edit, and a forgotten implementation fails at registration time
    /// (factory throws immediately) rather than mid-decode after FFmpeg has already run.
    /// </summary>
    public interface IFrameBitDecoderProvider
    {
        /// <summary>Creates the frame-bit decoder matching this modulator's modulation scheme.</summary>
        Core.IFrameBitDecoder CreateFrameBitDecoder();
    }
}
