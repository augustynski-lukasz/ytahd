using System;

namespace YTAHD.Core.Modulation
{
    /// <summary>
    /// Phase 4 helper: deterministic tile texture and the byte ↔ (dx, dy) motion-offset
    /// alphabet. Both encoder and decoder regenerate the same reference texture from a fixed
    /// seed, so no side-channel is needed to communicate what the "home" tile looks like.
    /// Per axis, all 16 alphabet offsets are non-zero even pixel counts, so the combined
    /// offset (dx, dy) can never be (0, 0). That combination is reserved exclusively for the
    /// canonical/no-data frame marker.
    /// </summary>
    public static class MotionTileBasis
    {
        public const int TextureSize = 8;
        public const int OffsetStepPx = 2;
        public const int OffsetLevelsPerAxis = 16; // 4 bits
        public const int MaxAbsOffsetPx = 16;       // guard margin required on each side
        public const int CellSize = TextureSize + 2 * MaxAbsOffsetPx;

        // Per-axis code → signed pixel offset. 16 distinct non-zero even values, so an axis
        // offset is never 0 and the combined (dx, dy) can never collide with the canonical marker.
        private static readonly int[] OffsetTable = BuildOffsetTable(MotionTileProfile.Default);

        /// <summary>Deterministic reference tile texture (band-limited, centered on 128).</summary>
        public static readonly byte[,] Texture = BuildTexture(MotionTileProfile.Default);

        private static int[] BuildOffsetTable(MotionTileProfile profile)
        {
            var table = new int[profile.OffsetLevelsPerAxis];
            for (int code = 0; code < profile.OffsetLevelsPerAxis; code++)
            {
                int half = profile.OffsetLevelsPerAxis / 2;
                table[code] = code < half
                    ? -profile.OffsetStepPx * (half - code) // code 0..7 → -16..-2 (default profile)
                    : profile.OffsetStepPx * (code - half + 1); // code 8..15 → 2..16 (default profile)
            }

            return table;
        }

        public static int GetOffsetForAxisCode(int code)
        {
            if (code < 0 || code >= OffsetLevelsPerAxis) throw new ArgumentOutOfRangeException(nameof(code));
            return OffsetTable[code];
        }

        public static bool TryGetAxisCodeForOffset(int offsetPx, out int code)
            => TryGetAxisCodeForOffset(MotionTileProfile.Default, offsetPx, out code);

        public static bool TryGetAxisCodeForOffset(MotionTileProfile profile, int offsetPx, out int code)
        {
            int[] table = BuildOffsetTable(profile);
            for (int c = 0; c < profile.OffsetLevelsPerAxis; c++)
            {
                if (table[c] == offsetPx)
                {
                    code = c;
                    return true;
                }
            }

            code = -1;
            return false;
        }

        /// <summary>Returns every valid signed pixel offset an axis can take (never 0).</summary>
        public static int[] GetAxisOffsets() => (int[])OffsetTable.Clone();

        /// <summary>Returns every valid signed pixel offset for the given profile (never 0).</summary>
        public static int[] GetAxisOffsets(MotionTileProfile profile) => BuildOffsetTable(profile);

        /// <summary>Returns the deterministic reference texture for the given profile.</summary>
        public static byte[,] GetTexture(MotionTileProfile profile) => BuildTexture(profile);

        /// <summary>True only for the reserved canonical/no-data marker, never for a valid alphabet symbol.</summary>
        public static bool IsCanonicalOffset(int dx, int dy) => dx == 0 && dy == 0;

        /// <summary>Encode one payload byte as a (dx, dy) pixel displacement pair.</summary>
        public static (int Dx, int Dy) EncodeOffset(byte value)
            => EncodeOffset(MotionTileProfile.Default, value);

        public static (int Dx, int Dy) EncodeOffset(MotionTileProfile profile, byte value)
        {
            int[] table = BuildOffsetTable(profile);
            int dxCode = (value >> 4) & 0xF;
            int dyCode = value & 0xF;
            return (table[dxCode], table[dyCode]);
        }

        /// <summary>Decode a (dx, dy) pixel displacement pair back into its payload byte, if valid.</summary>
        public static bool TryDecodeOffset(int dx, int dy, out byte value)
            => TryDecodeOffset(MotionTileProfile.Default, dx, dy, out value);

        public static bool TryDecodeOffset(MotionTileProfile profile, int dx, int dy, out byte value)
        {
            if (TryGetAxisCodeForOffset(profile, dx, out int dxCode) && TryGetAxisCodeForOffset(profile, dy, out int dyCode))
            {
                value = (byte)((dxCode << 4) | dyCode);
                return true;
            }

            value = 0;
            return false;
        }

        // Deterministic band-limited texture: seeded pseudo-random noise smoothed with a
        // repeated box blur, then normalised around neutral luma (128) with a moderate
        // amplitude — smooth enough for a lossy codec to preserve, structured enough for
        // shift discrimination during SAD matching.
        private static byte[,] BuildTexture(MotionTileProfile profile)
        {
            const uint Seed = 0x59544148; // 'YTAH' protocol constant
            int size = profile.TextureSize;
            var raw = new double[size, size];
            uint state = Seed;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    state = NextState(state);
                    raw[y, x] = (state / (double)uint.MaxValue) * 2.0 - 1.0; // [-1, 1]
                }
            }

            double[,] smoothed = BoxBlur(BoxBlur(raw));

            double min = double.MaxValue, max = double.MinValue;
            foreach (double v in smoothed)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }

            double range = Math.Max(1e-9, max - min);
            const double amplitude = 48.0;
            var texture = new byte[size, size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double normalized = (smoothed[y, x] - min) / range * 2.0 - 1.0; // [-1, 1]
                    double pixel = 128.0 + normalized * amplitude;
                    texture[y, x] = (byte)Math.Clamp((int)Math.Round(pixel), 0, 255);
                }
            }

            return texture;
        }

        private static uint NextState(uint state)
        {
            // xorshift32 — deterministic, dependency-free PRNG.
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        private static double[,] BoxBlur(double[,] source)
        {
            int size = source.GetLength(0);
            var result = new double[size, size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double sum = 0;
                    int count = 0;
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int sx = x + ox, sy = y + oy;
                            if (sx < 0 || sx >= size || sy < 0 || sy >= size) continue;
                            sum += source[sy, sx];
                            count++;
                        }
                    }

                    result[y, x] = sum / count;
                }
            }

            return result;
        }
    }
}
