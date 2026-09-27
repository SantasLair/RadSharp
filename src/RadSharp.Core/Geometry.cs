namespace RadSharp;

/// <summary>A point in fractional pixels (for drawing, gradients, paths).</summary>
public readonly record struct PointF(float X, float Y)
{
    public override string ToString() => $"{X}, {Y}";
}

/// <summary>A rectangle in whole pixels. <c>X</c>/<c>Y</c> are relative to the parent unless stated otherwise.</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public override string ToString() => $"{X}, {Y}, {Width}, {Height}";
}

/// <summary>Margin or padding: left, top, right, bottom.</summary>
public readonly record struct Thickness(int L, int T, int R, int B)
{
    public static readonly Thickness Zero = default;
}

public enum Align
{
    None,
    Top,
    Bottom,
    Left,
    Right,
    Client,
}

[Flags]
public enum Anchors
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
    All = Left | Top | Right | Bottom,
}

internal static class LayoutMath
{
    /// <summary>Rounds half away from zero (spec, Appendix B), not banker's rounding.</summary>
    public static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
}
