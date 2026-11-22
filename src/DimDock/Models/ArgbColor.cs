namespace DimDock.Models;

public readonly record struct ArgbColor(byte A, byte R, byte G, byte B)
{
    public static readonly ArgbColor Transparent = new(0, 0, 0, 0);

    public static ArgbColor FromRgb(byte r, byte g, byte b) => new(0xFF, r, g, b);

    public static ArgbColor FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);

    public ArgbColor WithAlpha(byte a) => new(a, R, G, B);
}
