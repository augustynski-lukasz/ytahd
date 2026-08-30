using System;

namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Abstract modulator contract. Implementations operate on spans for high-performance in-memory work.
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
        int GetPayloadBytesPerFrame(int width, int height, int headerBytes, int borderWidth = 0, int macroblockSize = 0);

        /// <summary>
        /// Render an RGBA frame for the selected modulation layout using the given payload bytes.
        /// </summary>
        byte[] CreateFrame(int width, int height, int borderWidth, ReadOnlySpan<byte> payload);

        /// <summary>
        /// Encode input bytes into a pixel buffer. Pixel buffer must be large enough for a single macroblock frame (width*height*bytesPerPixel).
        /// </summary>
        void Encode(ReadOnlySpan<byte> input, Span<byte> pixelBuffer);

        /// <summary>
        /// Decode pixel buffer back into output bytes.
        /// </summary>
        void Decode(ReadOnlySpan<byte> pixelBuffer, Span<byte> output);
    }
}
