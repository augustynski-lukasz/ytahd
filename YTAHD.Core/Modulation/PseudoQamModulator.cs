using System;

namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Phase 2: 12-bit RGB PAM modulation placeholder.
    /// </summary>
    public sealed class PseudoQamModulator : IModulator
    {
        public int MacroblockWidth => 16; // example
        public int MacroblockHeight => 16;

        public void Encode(ReadOnlySpan<byte> input, Span<byte> pixelBuffer)
        {
            // Placeholder algorithm: spread bytes into pixelBuffer
            for (int i = 0; i < pixelBuffer.Length; i++)
                pixelBuffer[i] = (byte)(i < input.Length ? input[i] : 0);
        }

        public void Decode(ReadOnlySpan<byte> pixelBuffer, Span<byte> output)
        {
            for (int i = 0; i < output.Length && i < pixelBuffer.Length; i++)
                output[i] = pixelBuffer[i];
        }
    }
}
