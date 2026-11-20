using RadSharp;
using RadSharp.Drawing;

namespace SampleData;

/// <summary>Something a control needs from another assembly of its project (not a constant: it must be loaded).</summary>
public static class Swatches
{
    public static string Fill => "#3366CC";
}

/// <summary>A library's own control.</summary>
public sealed class Swatch : Control
{
    public Swatch() => SetDefaultSize(24, 24);

    protected override void Paint(Canvas canvas) => canvas.FillRect(new Rect(0, 0, Width, Height), Color.FromHex(Swatches.Fill));
}
