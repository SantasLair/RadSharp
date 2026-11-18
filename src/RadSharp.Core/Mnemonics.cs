using RadSharp.Drawing;

namespace RadSharp;

/// <summary>
/// Mnemonics (access keys) in control captions: <c>&amp;Save</c> underlines S and Alt+S activates the
/// control (spec, Appendix C). <c>&amp;&amp;</c> is a literal <c>&amp;</c>. Menus use the same caption syntax
/// (<see cref="MenuItem.StripAccessKey"/>).
/// </summary>
public static class Mnemonics
{
    /// <summary>
    /// The one switch for mnemonics on controls: when false, Alt+letter does nothing and no letter is
    /// underlined, but <c>&amp;</c> markers are still removed from captions. Default: on, except on macOS, where
    /// Option+letter types characters. (Menu bars keep their own access keys on every platform.)
    /// </summary>
    public static bool Enabled { get; set; } = !OperatingSystem.IsMacOS();

    /// <summary>The caption without its <c>&amp;</c> markers; <paramref name="index"/> is the mnemonic's position in the result, or -1.</summary>
    public static string Strip(string caption, out int index) => MenuItem.StripAccessKey(caption, out index);

    /// <summary>The mnemonic letter or digit of a caption, upper-cased; null if it has none.</summary>
    public static char? KeyOf(string? caption)
    {
        if (string.IsNullOrEmpty(caption) || caption.IndexOf('&') < 0) return null;
        var text = Strip(caption, out int i);
        return i >= 0 && i < text.Length ? char.ToUpperInvariant(text[i]) : null;
    }

    /// <summary>A caption's text without markers (cheap when there are none).</summary>
    internal static string Display(string caption) => caption.IndexOf('&') < 0 ? caption : Strip(caption, out _);

    /// <summary>
    /// Draws one line of caption text like <see cref="Canvas.DrawText(string, Font, Color, Rect, TextAlign, VerticalAlign, bool)"/>
    /// (identical pixels when the caption has no <c>&amp;</c>), and underlines the mnemonic letter when
    /// <paramref name="underline"/> is true.
    /// </summary>
    internal static void Draw(Canvas canvas, string caption, Font font, Color color, Rect r, TextAlign align, VerticalAlign valign,
        bool underline, bool ellipsis = false, bool parse = true)
    {
        int index = -1;
        var text = caption;
        if (parse && caption.IndexOf('&') >= 0) text = Strip(caption, out index);
        canvas.DrawText(text, font, color, r, align, valign, ellipsis);
        if (!underline || index < 0 || index >= text.Length || color.A == 0) return;

        var render = canvas.Resolve(font);
        if (ellipsis)
        {
            var cut = TextLayout.Ellipsize(text, render, r.Width);
            if (cut != text) { if (index >= cut.Length - 1) return; text = cut; }
        }
        float width = render.MeasureWidth(text);
        float x = align switch
        {
            TextAlign.Center => r.X + (r.Width - width) / 2,
            TextAlign.Right => r.X + r.Width - width,
            _ => r.X,
        };
        float textHeight = render.Ascent + render.Descent;
        float top = valign switch
        {
            VerticalAlign.Center => r.Y + (r.Height - textHeight) / 2,
            VerticalAlign.Bottom => r.Y + r.Height - textHeight,
            _ => r.Y,
        };
        float baseline = MathF.Round(top + render.Ascent, MidpointRounding.AwayFromZero);
        float ux = MathF.Round(x, MidpointRounding.AwayFromZero) + render.MeasureWidth(text[..index]);
        int uw = Math.Max(1, (int)MathF.Round(render.MeasureWidth(text[index].ToString())));
        canvas.FillRect(new Rect((int)MathF.Round(ux), (int)baseline + canvas.Px(1), uw, canvas.Px(1)), color);
    }
}
