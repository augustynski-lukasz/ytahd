namespace YTAHD.Core.Modulation;

public readonly record struct ModulatorGeometry(
    int Width,
    int Height,
    int MacroblockSize,
    int HeaderBytes,
    int BorderWidth = 0,
    int BitsPerFrame = 0)
{
    public static ModulatorGeometry Create(int width, int height, int macroblockSize, int headerBytes, int borderWidth = 0, int bitsPerFrame = 0)
        => new(width, height, macroblockSize, headerBytes, borderWidth, bitsPerFrame);
}
