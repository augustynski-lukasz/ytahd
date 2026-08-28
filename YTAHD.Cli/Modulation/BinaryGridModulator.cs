using System;

namespace YTAHD.Cli.Modulation
{
    /// <summary>
    /// Phase 1: Monochrome 16x16 macroblock modulator placeholder.
    /// </summary>
    public sealed class BinaryGridModulator : IModulator
    {
        public int MacroblockWidth => 16;
        public int MacroblockHeight => 16;

        public void Encode(ReadOnlySpan<byte> input, Span<byte> pixelBuffer)
        {
            // Very small placeholder: fill pixelBuffer depending on input first byte
            byte fill = input.Length > 0 ? input[0] : (byte)0;
            for (int i = 0; i < pixelBuffer.Length; i++)
                pixelBuffer[i] = fill;
        }

        public void Decode(ReadOnlySpan<byte> pixelBuffer, Span<byte> output)
        {
            // Placeholder: copy first pixel value to output[0]
            if (output.Length > 0 && pixelBuffer.Length > 0)
                output[0] = pixelBuffer[0];
        }
    }
}
