using RadSharp.Drawing;

namespace RadSharp;

/// <summary>Internal tooltip popup: renders a control's <see cref="Control.Hint"/> in the popup layer.</summary>
internal sealed class ToolTipPopup(string text) : Control
{
    protected override void Paint(Canvas canvas)
    {
        if (string.IsNullOrEmpty(text)) return;

        var theme = canvas.Theme;
        var font = canvas.DefaultFont;
        int padding = canvas.Px(8);
        int lineHeight = (int)Math.Ceiling(font.LineHeight);

        var bounds = new Rect(0, 0, Width, Height);
        canvas.FillRect(bounds, theme.PopupBackground);
        canvas.DrawBorder(bounds, theme.PopupBorder, canvas.Px(1));

        int y = padding;
        foreach (var line in text.Split('\n'))
        {
            canvas.DrawText(line, font, theme.Text, new Rect(padding, y, Width - 2 * padding, lineHeight), TextAlign.Left, VerticalAlign.Top);
            y += lineHeight;
        }
    }
}
