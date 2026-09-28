using RadSharp.Drawing;

namespace RadSharp.Designer;

/// <summary>
/// The component palette's "toolbox icons": a small rounded swatch with a control's initials, tinted by its
/// <see cref="PaletteCategories"/> colour. Drawn entirely with <see cref="Canvas"/> primitives (rounded rect,
/// border, text) rather than image assets — real per-control artwork is a separate, larger piece of work
/// this doesn't attempt, but a colour-coded initial is already what tells two toolbox entries apart at a
/// glance, the same idea <c>DesignSurface.PaintComponentIcon</c> uses for non-visual components on the form.
/// </summary>
static class PaletteIcons
{
    /// <summary>An icon painter for <paramref name="cls"/>, to set on the palette tree's leaf <c>TreeNode.Icon</c>.</summary>
    public static Action<Canvas, Rect> For(ComponentClass cls)
    {
        var color = PaletteCategories.ColorOf(PaletteCategories.Of(cls.Name));
        var initials = Initials(cls.Name);
        return (canvas, rect) =>
        {
            int size = Math.Min(rect.Width, rect.Height) - canvas.Px(4);
            var box = new Rect(rect.X + (rect.Width - size) / 2, rect.Y + (rect.Height - size) / 2, size, size);
            canvas.FillRoundRect(box, canvas.Px(3), new Color(color.R, color.G, color.B, 40));
            canvas.DrawRoundBorder(box, canvas.Px(3), color, Math.Max(1, canvas.Px(1)));
            var font = canvas.DefaultBoldFont.WithSize(canvas.DefaultBoldFont.Size * 0.68f);
            canvas.DrawText(initials, font, color, box, TextAlign.Center, VerticalAlign.Center);
        };
    }

    /// <summary>Up to two initials from a class name's capital letters (e.g. "ScrollBar" → "SB"), matching
    /// the abbreviation <c>DesignSurface.PaintComponentIcon</c> already uses for non-visual components.</summary>
    static string Initials(string name)
    {
        var caps = new string([.. name.Where(char.IsUpper).Take(2)]);
        return caps.Length > 0 ? caps : name[..1].ToUpperInvariant();
    }
}
