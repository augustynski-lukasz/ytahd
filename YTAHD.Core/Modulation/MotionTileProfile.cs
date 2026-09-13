namespace YTAHD.Core.Modulation;

/// <summary>
/// Phase 4 tile geometry profile (F-20260913-01): texture size, offset step, and offset
/// alphabet size. The guard margin and cell size are derived. The default profile is the
/// shipped 8×8 / 2 px-step / 16-level protocol baseline.
/// </summary>
public readonly record struct MotionTileProfile(int TextureSize, int OffsetStepPx, int OffsetLevelsPerAxis)
{
    /// <summary>Shipped protocol baseline: 8×8 texture, 2 px steps, 16 levels per axis (4 bits).</summary>
    public static readonly MotionTileProfile Default = new(8, 2, 16);

    /// <summary>Small-tile candidate: 4×4 texture. Higher capacity, smaller discrimination surface.</summary>
    public static readonly MotionTileProfile Small = new(4, 2, 16);

    /// <summary>Large-tile candidate: 16×16 texture. Lower capacity, larger discrimination surface.</summary>
    public static readonly MotionTileProfile Large = new(16, 2, 16);

    /// <summary>Guard margin in pixels required on each side of a cell (half the offset span).</summary>
    public int MaxAbsOffsetPx => OffsetStepPx * (OffsetLevelsPerAxis / 2);

    /// <summary>Full cell size: texture plus guard margin on both sides.</summary>
    public int CellSize => TextureSize + 2 * MaxAbsOffsetPx;
}
