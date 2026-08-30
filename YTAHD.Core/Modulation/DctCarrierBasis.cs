using System;

namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Phase 3 helper: generate a low-frequency DCT carrier basis, keeping the signal inside the
    /// top-left coefficients to match the VP9-style smoothness expectations described in the roadmap.
    /// </summary>
    public static class DctCarrierBasis
    {
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
                    if (x >= cutoff || y >= cutoff)
                    {
                        continue;
                    }

                    double waveX = Math.Cos(((2 * x + 1) * Math.PI) / (2.0d * size));
                    double waveY = Math.Cos(((2 * y + 1) * Math.PI) / (2.0d * size));
                    double value = waveX * waveY / (baseScale * baseScale);
                    basis[y, x] = value;
                }
            }

            basis[0, 0] = 1.0d;

            return basis;
        }
    }
}
