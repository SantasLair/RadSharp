namespace RadSharp;

/// <summary>
/// A move of a control's stored lengths from one UI scale to another (<see cref="Control.ScaleLengths"/>,
/// <see cref="Form.ChangeScale"/>), handed to <see cref="Control.RescaleOwnLengths"/>. <see cref="Apply"/> rescales a
/// length without drift: it remembers the 100 % value a length came from, so 100 % → 150 % → 100 % gives back exactly
/// the original lengths instead of accumulating rounding errors.
/// </summary>
public readonly struct ScaleChange
{
    public ScaleChange(double from, double to)
    {
        if (!(from > 0) || !double.IsFinite(from)) throw new ArgumentOutOfRangeException(nameof(from), "Scale must be positive.");
        if (!(to > 0) || !double.IsFinite(to)) throw new ArgumentOutOfRangeException(nameof(to), "Scale must be positive.");
        From = from;
        To = to;
    }

    /// <summary>The scale the lengths are in now.</summary>
    public double From { get; }

    /// <summary>The scale the lengths are moved to.</summary>
    public double To { get; }

    /// <summary><see cref="To"/> / <see cref="From"/>: what a length is multiplied by.</summary>
    public double Ratio => To / From;

    /// <summary>
    /// Returns <paramref name="pixels"/> (a length at <see cref="From"/>) at <see cref="To"/>, rounded half away from zero.
    /// <paramref name="design"/> is the length at 100 % this call last saw (start a field at <see cref="double.NaN"/>):
    /// while it still gives <paramref name="pixels"/> at <see cref="From"/>, it is used instead of
    /// <c>pixels / From</c>, so a length nobody changed comes back exactly; a length changed since (from code) is
    /// converted from its pixels. The call stores the design value it used back into <paramref name="design"/>.
    /// </summary>
    public int Apply(int pixels, ref double design)
    {
        design = DesignOf(pixels, design, From);
        return LayoutMath.Round(design * To);
    }

    /// <summary>The 100 % value of <paramref name="pixels"/> at <paramref name="scale"/>: <paramref name="design"/> while it still maps to it.</summary>
    internal static double DesignOf(int pixels, double design, double scale) =>
        !double.IsNaN(design) && LayoutMath.Round(design * scale) == pixels ? design : pixels / scale;
}
