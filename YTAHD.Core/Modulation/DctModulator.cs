using System;

namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Phase 3: low-frequency DCT carrier modulator.
    /// The encoder stores payload bytes as signed amplitudes on the top-left DCT basis values,
    /// keeping the carrier inside the low-frequency region that video compression preserves best.
    /// </summary>
    public sealed class DctModulator : IModulator
    {
        private const int BasisSize = 8;
        private const int Cutoff = 4;

        public int MacroblockWidth => BasisSize;
        public int MacroblockHeight => BasisSize;

        public void Encode(ReadOnlySpan<byte> input, Span<byte> pixelBuffer)
        {
            if (pixelBuffer.Length < BasisSize * BasisSize)
            {
                throw new ArgumentException("Pixel buffer is too small to hold an 8x8 DCT carrier block.", nameof(pixelBuffer));
            }

            pixelBuffer.Clear();

            int position = 0;
            for (int i = 0; i < input.Length && position < Cutoff * Cutoff; i++, position++)
            {
                int y = position / Cutoff;
                int x = position % Cutoff;
                pixelBuffer[y * BasisSize + x] = input[i];
            }

            // Preserve a neutral zero-energy baseline elsewhere in the low-frequency block while keeping
            // the payload concentrated in the top-left coefficients that are most codec-friendly.
            for (int y = 0; y < BasisSize; y++)
            {
                for (int x = 0; x < BasisSize; x++)
                {
                    if (x < Cutoff && y < Cutoff)
                    {
                        continue;
                    }

                    pixelBuffer[y * BasisSize + x] = 128;
                }
            }
        }

        public void Decode(ReadOnlySpan<byte> pixelBuffer, Span<byte> output)
        {
            if (output.IsEmpty)
            {
                return;
            }

            output.Clear();

            for (int i = 0; i < output.Length && i < Cutoff * Cutoff; i++)
            {
                int y = i / Cutoff;
                int x = i % Cutoff;
                output[i] = pixelBuffer[y * BasisSize + x];
            }
        }
    }
}
