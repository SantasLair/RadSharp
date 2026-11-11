using RadSharp.Drawing;

namespace RadSharp;

/// <summary>Internal tooltip popup: renders hint text (one or more lines) in the popup layer, sized to fit it.</summary>
internal sealed class ToolTipPopup(string text) : Control
{
    const int PadPx = 8;

    static string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n');

    /// <summary>A tooltip for <paramref name="text"/> whose size fits the text at <paramref name="form"/>'s theme and scale.</summary>
    public static ToolTipPopup Create(Form form, string text)
    {
        var theme = form.Theme;
        var font = new RenderFont(FontFace.Resolve(theme.DefaultFont), (float)(theme.DefaultFont.Size * form.Scale));
        int pad = LayoutMath.Round(PadPx * form.Scale);
        var lines = Lines(text);
        int width = (int)Math.Ceiling(lines.Max(font.MeasureWidth)) + 2 * pad;
        int height = lines.Length * (int)Math.Ceiling(font.LineHeight) + 2 * pad;
        var popup = new ToolTipPopup(text);
        popup.SetBounds(0, 0, width, height);
        return popup;
    }

    protected override void Paint(Canvas canvas)
    {
        if (string.IsNullOrEmpty(text)) return;

        var theme = canvas.Theme;
        var font = canvas.Resolve(EffectiveFont);
        int padding = canvas.Px(PadPx);
        int lineHeight = (int)Math.Ceiling(font.LineHeight);

        var bounds = new Rect(0, 0, Width, Height);
        canvas.FillRect(bounds, theme.PopupBackground);
        canvas.DrawBorder(bounds, theme.PopupBorder, canvas.Px(1));

        int y = padding;
        foreach (var line in Lines(text))
        {
            canvas.DrawText(line, font, theme.Text, new Rect(padding, y, Width - 2 * padding, lineHeight), TextAlign.Left, VerticalAlign.Top);
            y += lineHeight;
        }
    }
}
