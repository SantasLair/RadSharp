using RadSharp.Drawing;

namespace RadSharp;

/// <summary>
/// Internal tooltip popup control: renders a Hint string near the hovered control.
/// </summary>
internal sealed class ToolTipPopup : Control
{
    private readonly string _text;
    private readonly Theme _theme;

    public ToolTipPopup(string text, Theme theme)
    {
        _text = text;
        _theme = theme;
        Visible = false;
    }

    protected override void Paint(Canvas canvas)
    {
        if (string.IsNullOrEmpty(_text)) return;

        var font = new Font(_theme.FontFace, (float)(_theme.FontSize * Scale));
        var padding = 8;
        var lines = _text.Split('\n');

        var textWidth = (int)Math.Ceiling(lines.Max(l => font.MeasureWidth(l))) + 2 * padding;
        var textHeight = (int)Math.Ceiling(font.LineHeight * lines.Length) + 2 * padding;

        // Draw background
        var bgRect = new Rect(0, 0, Width, Height);
        canvas.FillRect(bgRect, _theme.PopupBackground);
        canvas.DrawRect(bgRect, _theme.PopupBorder);

        // Draw text
        var textRect = new Rect(padding, padding, Width - 2 * padding, Height - 2 * padding);
        int y = padding;
        foreach (var line in lines)
        {
            canvas.DrawText(line, font, _theme.Foreground, new Rect(padding, y, Width - 2 * padding, (int)Math.Ceiling(font.LineHeight)), TextAlign.Left, VerticalAlign.Top);
            y += (int)Math.Ceiling(font.LineHeight);
        }
    }
}
