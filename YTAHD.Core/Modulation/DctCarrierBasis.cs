using System;

namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Phase 3 helper: low-frequency DCT carrier basis and shared DCT math tables.
    /// </summary>
    public static class DctCarrierBasis
    {
        public const int BlockSize = 8;
        public const int Cutoff = 4;

        // cos_table[n, k] = cos((2n+1) * k * π / 16)  (for 8-point DCT)
        public static readonly double[,] CosTable = BuildCosTable();

        // 8 lowest-frequency AC positions in the top-left 4×4 of the 8×8 DCT matrix,
        // ordered by increasing spatial frequency (u+v ascending).
        // Each position encodes 1 bit. Together they give 1 byte per 8×8 block.
        public static readonly (int U, int V)[] CarrierPositions =
        {
            (0, 1), (1, 0),              // sum = 1
            (1, 1), (0, 2), (2, 0),      // sum = 2
            (0, 3), (3, 0), (1, 2)       // sum = 3
        };

        // DC coefficient that produces mean pixel = 128 under the orthonormal IDCT.
        // f_dc(x,y) = C(0)*C(0)/4 * DcCoeff = 1/8 * DcCoeff  → DcCoeff = 1024.
        public const double DcCoeff = 1024.0;

        // Carrier AC coefficient amplitude.  bit=1 → +Amplitude, bit=0 → -Amplitude.
        // 128 gives pixel-domain waves ≈ ±18–32 luma units above the DC mean (128),
        // which is well above H.264 CRF-23 quantization noise for smooth blocks.
        public const double CarrierAmplitude = 128.0;

        // Orthonormal DCT normalisation factor: C(0) = 1/√2, C(k>0) = 1.
        public static double C(int k) => k == 0 ? 0.7071067811865475 : 1.0;

        public static bool[,] CreateLowFrequencyMask(int size, int cutoff)
        {
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
            if (cutoff < 0 || cutoff > size) throw new ArgumentOutOfRangeException(nameof(cutoff));

            var mask = new bool[size, size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    mask[y, x] = x < cutoff && y < cutoff;
                }
            }

            return mask;
        }

        public static double[,] GenerateBasis(int size, int cutoff)
        {
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
            if (cutoff < 0 || cutoff > size) throw new ArgumentOutOfRangeException(nameof(cutoff));

            var basis = new double[size, size];
            double baseScale = Math.Cos(Math.PI / (2.0d * size));
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (x >= cutoff || y >= cutoff) continue;
                    double waveX = Math.Cos(((2 * x + 1) * Math.PI) / (2.0d * size));
                    double waveY = Math.Cos(((2 * y + 1) * Math.PI) / (2.0d * size));
                    basis[y, x] = waveX * waveY / (baseScale * baseScale);
                }
            }

            basis[0, 0] = 1.0d;
            return basis;
        }

        private static double[,] BuildCosTable()
        {
            var t = new double[8, 8];
            for (int n = 0; n < 8; n++)
                for (int k = 0; k < 8; k++)
                    t[n, k] = Math.Cos((2 * n + 1) * k * Math.PI / 16.0);
            return t;
        }
    }
}
