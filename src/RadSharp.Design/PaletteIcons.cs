using RadSharp.Drawing;

namespace RadSharp.Design;

/// <summary>
/// Toolbox icons: a small vector picture per component class, in the flat outline-and-accent style of the Visual
/// Studio and Fluent icon sets. Each is drawn on a 16 × 16 grid with <see cref="Canvas"/> paths, so it stays sharp at
/// any scale with no image assets, and its colours follow the theme (dark outlines on light themes, light outlines on
/// dark ones). A class with no picture of its own, such as a new control, falls back to a colour-coded badge with its
/// initials. The same pictures label non-visual components on the design surface.
/// </summary>
public static class PaletteIcons
{
    /// <summary>An icon painter for <paramref name="cls"/>, to set on the palette tree's leaf <c>TreeNode.Icon</c>.</summary>
    public static Action<Canvas, Rect> For(ComponentClass cls) => (canvas, rect) =>
    {
        int size = Math.Min(rect.Width, rect.Height) - canvas.Px(4);
        Paint(canvas, cls.Name, new Rect(rect.X + (rect.Width - size) / 2, rect.Y + (rect.Height - size) / 2, size, size));
    };

    /// <summary>Whether <paramref name="className"/> has its own picture (otherwise <see cref="Paint"/> draws initials).</summary>
    public static bool HasArt(string className) => Art.ContainsKey(className);

    /// <summary>Draws the icon for <paramref name="className"/> filling the square <paramref name="box"/>.</summary>
    public static void Paint(Canvas canvas, string className, Rect box)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        if (Art.TryGetValue(className, out var draw))
        {
            canvas.Save();
            canvas.ClipRect(box);
            draw(new Ink(canvas, box));
            canvas.Restore();
            return;
        }
        PaintInitials(canvas, className, box);
    }

    static void PaintInitials(Canvas canvas, string className, Rect box)
    {
        var color = PaletteCategories.ColorOf(PaletteCategories.Of(className));
        canvas.FillRoundRect(box, canvas.Px(3), new Color(color.R, color.G, color.B, 40));
        canvas.DrawRoundBorder(box, canvas.Px(3), color, Math.Max(1, canvas.Px(1)));
        var font = canvas.DefaultBoldFont.WithSize(canvas.DefaultBoldFont.Size * 0.68f * box.Height / Math.Max(1, canvas.Px(16)));
        canvas.DrawText(Initials(className), font, color, box, TextAlign.Center, VerticalAlign.Center);
    }

    /// <summary>Up to two initials from a class name's capital letters (e.g. "ScrollBar" → "SB").</summary>
    public static string Initials(string name)
    {
        var caps = new string([.. name.Where(char.IsUpper).Take(2)]);
        return caps.Length > 0 ? caps : name[..1].ToUpperInvariant();
    }

    // ---- The pictures ----
    // Grid units: 0..16. A 1-unit outline sits on a half unit (1.5, 14.5) so it covers whole pixels at 100 %.

    static readonly Dictionary<string, Action<Ink>> Art = new(StringComparer.Ordinal)
    {
        // ---- Standard ----
        ["Button"] = k =>
        {
            k.Box(1, 4, 14, 8, k.P.Accent, null, 2);
            k.Line(5, 8, 11, 8, k.P.OnAccent, 1.5f, LineCap.Round);
        },
        ["CheckBox"] = k =>
        {
            k.Box(2, 2, 12, 12, k.P.Accent, null, 2);
            k.Polyline(k.P.OnAccent, 1.7f, 4.8f, 8.2f, 7, 10.4f, 11.3f, 5.8f);
        },
        ["RadioButton"] = k =>
        {
            k.Circle(8, 8, 6, k.P.Fill, k.P.Outline);
            k.Circle(8, 8, 3, k.P.Accent, null);
        },
        ["Edit"] = k =>
        {
            k.Box(1.5f, 4.5f, 13, 7, k.P.Fill, k.P.Outline, 1);
            k.Line(3.5f, 8, 8, 8, k.P.Faint, 1.5f);
            k.Line(10.5f, 6, 10.5f, 10, k.P.Accent, 1);
        },
        ["Memo"] = k =>
        {
            k.Box(1.5f, 1.5f, 13, 13, k.P.Fill, k.P.Outline, 1);
            k.Line(3.5f, 4.5f, 12.5f, 4.5f, k.P.Faint);
            k.Line(3.5f, 7, 11, 7, k.P.Faint);
            k.Line(3.5f, 9.5f, 12.5f, 9.5f, k.P.Faint);
            k.Line(3.5f, 12, 7.5f, 12, k.P.Faint);
            k.Line(9, 10.5f, 9, 13.5f, k.P.Accent);
        },
        ["Label"] = k =>
        {
            k.Polyline(k.P.Outline, 1.6f, 2.5f, 13.5f, 8, 2.5f, 13.5f, 13.5f);
            k.Line(4.8f, 9.5f, 11.2f, 9.5f, k.P.Accent, 1.6f);
        },
        ["GroupBox"] = k =>
        {
            k.Polyline(k.P.Outline, 1, LineCap.Butt, 9.5f, 4.5f, 14.5f, 4.5f, 14.5f, 14.5f, 1.5f, 14.5f, 1.5f, 4.5f, 3, 4.5f);
            k.Line(4, 4.5f, 8.5f, 4.5f, k.P.Accent, 2, LineCap.Round);
            k.Line(4, 8.5f, 11, 8.5f, k.P.Faint);
            k.Line(4, 11.5f, 9, 11.5f, k.P.Faint);
        },
        ["Panel"] = k =>
        {
            k.Box(1.5f, 2.5f, 13, 11, k.P.Fill2, k.P.Outline, 1);
            k.Polyline(k.P.Highlight, 1, LineCap.Butt, 2.5f, 12, 2.5f, 3.5f, 13.5f, 3.5f);
            k.Polyline(k.P.Faint, 1, LineCap.Butt, 13.5f, 4, 13.5f, 12.5f, 3, 12.5f);
        },
        ["MainMenu"] = k =>
        {
            k.Box(1.5f, 1.5f, 13, 3, k.P.Fill2, k.P.Outline);
            k.Line(3, 3, 4.5f, 3, k.P.Outline);
            k.Line(6.5f, 3, 9, 3, k.P.Accent, 1.5f);
            k.Box(5.5f, 4.5f, 9, 10, k.P.Fill, k.P.Outline);
            k.Box(6, 6, 8, 2.5f, k.P.Accent, null);
            k.Line(7.5f, 10.5f, 12.5f, 10.5f, k.P.Faint);
            k.Line(7.5f, 12.5f, 11, 12.5f, k.P.Faint);
        },
        ["PopupMenu"] = k =>
        {
            k.Box(5.5f, 0.5f, 10, 12, k.P.Fill, k.P.Outline, 1);
            k.Line(7.5f, 3, 13.5f, 3, k.P.Faint);
            k.Box(6, 4.5f, 9, 3, k.P.Accent, null);
            k.Line(7.5f, 6, 12, 6, k.P.OnAccent);
            k.Line(7, 9, 14, 9, k.P.Fill2);
            k.Line(7.5f, 10.5f, 12, 10.5f, k.P.Faint);
            // The right-click pointer.
            k.Poly(k.P.Fill, k.P.Outline, 1.5f, 6.5f, 1.5f, 14.5f, 3.5f, 12.6f, 4.9f, 15.5f, 6.3f, 14.9f, 4.9f, 12, 7.6f, 12);
        },

        // ---- Additional ----
        ["ComboBox"] = k =>
        {
            k.Box(1.5f, 4.5f, 13, 7, k.P.Fill, k.P.Outline, 1);
            k.Line(3.5f, 8, 7.5f, 8, k.P.Faint, 1.5f);
            k.Line(10.5f, 5, 10.5f, 11, k.P.Fill2);
            k.Polyline(k.P.Accent, 1.4f, 11.3f, 7.2f, 12.5f, 8.6f, 13.7f, 7.2f);
        },
        ["ColorBox"] = k =>
        {
            k.Box(1.5f, 4.5f, 13, 7, k.P.Fill, k.P.Outline, 1);
            k.Box(3, 6, 2.5f, 4, k.P.Red, null);
            k.Box(5.5f, 6, 2.5f, 4, k.P.Green, null);
            k.Box(8, 6, 2.5f, 4, k.P.Accent, null);
            k.Polyline(k.P.Outline, 1.3f, 11.4f, 7.3f, 12.5f, 8.5f, 13.6f, 7.3f);
        },
        ["ListBox"] = k =>
        {
            k.Box(1.5f, 1.5f, 13, 13, k.P.Fill, k.P.Outline, 1);
            k.Line(3.5f, 4.5f, 11.5f, 4.5f, k.P.Faint);
            k.Box(2, 6, 12, 3, k.P.Accent, null);
            k.Line(3.5f, 7.5f, 10.5f, 7.5f, k.P.OnAccent);
            k.Line(3.5f, 10.5f, 12, 10.5f, k.P.Faint);
            k.Line(3.5f, 12.5f, 9, 12.5f, k.P.Faint);
        },
        ["ListView"] = k =>
        {
            k.Box(1.5f, 1.5f, 13, 13, k.P.Fill, k.P.Outline, 1);
            k.Box(3, 3, 3, 3, k.P.Accent, null, 0.5f);
            k.Line(7.5f, 4.5f, 12.5f, 4.5f, k.P.Faint);
            k.Box(3, 6.5f, 3, 3, k.P.Green, null, 0.5f);
            k.Line(7.5f, 8, 11.5f, 8, k.P.Faint);
            k.Box(3, 10, 3, 3, k.P.Orange, null, 0.5f);
            k.Line(7.5f, 11.5f, 12.5f, 11.5f, k.P.Faint);
        },
        ["TreeView"] = k =>
        {
            k.Line(3.5f, 5, 3.5f, 12.5f, k.P.Faint, 1, LineCap.Butt, [1, 1]);
            k.Line(4, 8.5f, 6, 8.5f, k.P.Faint, 1, LineCap.Butt, [1, 1]);
            k.Line(4, 12.5f, 6, 12.5f, k.P.Faint, 1, LineCap.Butt, [1, 1]);
            k.Box(2, 2, 3, 3, k.P.Accent, null, 0.5f);
            k.Box(6, 7, 3, 3, k.P.Green, null, 0.5f);
            k.Box(6, 11, 3, 3, k.P.Orange, null, 0.5f);
            k.Line(6.5f, 3.5f, 13, 3.5f, k.P.Outline);
            k.Line(10.5f, 8.5f, 14.5f, 8.5f, k.P.Faint);
            k.Line(10.5f, 12.5f, 14, 12.5f, k.P.Faint);
        },
        ["PageControl"] = k =>
        {
            k.Box(8, 3.5f, 5, 2.5f, k.P.Fill2, k.P.Outline);
            k.Box(1.5f, 5.5f, 13, 9, k.P.Fill, k.P.Outline);
            k.Box(1.5f, 2.5f, 6, 3.5f, k.P.Fill, k.P.Outline);
            k.Line(2, 5.5f, 7, 5.5f, k.P.Fill, 1.2f);
            k.Line(1.5f, 2.5f, 7.5f, 2.5f, k.P.Accent, 1.5f);
            k.Line(3.5f, 8.5f, 11, 8.5f, k.P.Faint);
            k.Line(3.5f, 11, 9, 11, k.P.Faint);
        },
        ["TabSheet"] = k =>
        {
            k.Box(1.5f, 5.5f, 13, 9, k.P.Fill, k.P.Outline);
            k.Box(1, 2, 7, 4, k.P.Accent, null, 0.5f);
            k.Line(3.5f, 8.5f, 11, 8.5f, k.P.Faint);
            k.Line(3.5f, 11, 9, 11, k.P.Faint);
        },
        ["PaintBox"] = k =>
        {
            k.Box(1.5f, 1.5f, 13, 13, k.P.Fill, null);
            k.StrokeBox(1.5f, 1.5f, 13, 13, k.P.Outline, 1, [2, 1.5f]);
            k.Curve(k.P.Accent, 1.7f, 3.8f, 11.5f, 6, 4, 9, 14, 12, 4.5f);
            k.Circle(5, 5, 1.6f, k.P.Orange, null);
        },
        ["Image"] = k =>
        {
            k.Box(1.5f, 2.5f, 13, 11, k.P.Fill, k.P.Outline, 1);
            k.Circle(5.5f, 6, 1.6f, k.P.Orange, null);
            k.Poly(k.P.Accent, null, 2, 13, 6, 8.5f, 8.5f, 11, 10.5f, 9, 14, 13);
        },
        ["ProgressBar"] = k =>
        {
            k.Box(1.5f, 4.5f, 13, 7, k.P.Fill, k.P.Outline, 1);
            k.Box(3, 6, 7, 4, k.P.Accent, null, 0.5f);
        },
        ["TrackBar"] = k =>
        {
            k.Line(2, 7, 14, 7, k.P.Fill2, 2, LineCap.Round);
            k.Line(2, 7, 7.5f, 7, k.P.Accent, 2, LineCap.Round);
            foreach (var x in new[] { 2.5f, 5.5f, 8.5f, 11.5f, 14.5f })
                k.Line(x, 11.5f, x, 13.5f, k.P.Faint);
            k.Circle(7.5f, 7, 3, k.P.Fill, k.P.Outline);
            k.Circle(7.5f, 7, 1.4f, k.P.Accent, null);
        },
        ["ScrollBar"] = k =>
        {
            k.Box(1.5f, 4.5f, 13, 7, k.P.Fill2, k.P.Outline, 1);
            k.Poly(k.P.Outline, null, 3, 8, 5, 6.2f, 5, 9.8f);
            k.Poly(k.P.Outline, null, 13, 8, 11, 6.2f, 11, 9.8f);
            k.Box(6, 6.5f, 4, 3, k.P.Accent, null, 1);
        },
        ["ScrollBox"] = k =>
        {
            k.Box(1.5f, 1.5f, 13, 13, k.P.Fill, k.P.Outline, 1);
            k.Box(11, 2, 3, 12, k.P.Fill2, null);
            k.Box(11.75f, 4, 1.5f, 4.5f, k.P.Accent, null, 0.75f);
            k.Box(3.5f, 3.5f, 5, 3.5f, k.P.Fill, k.P.Faint);
            k.Box(3.5f, 9, 5, 3.5f, k.P.Fill, k.P.Faint);
        },
        ["ScrollView"] = k =>
        {
            k.Box(1.5f, 1.5f, 13, 13, k.P.Fill, k.P.Outline, 1);
            k.Box(11, 2, 3, 9, k.P.Fill2, null);
            k.Box(2, 11, 9, 3, k.P.Fill2, null);
            k.Box(11.75f, 3.5f, 1.5f, 4, k.P.Accent, null, 0.75f);
            k.Box(3.5f, 11.75f, 4, 1.5f, k.P.Accent, null, 0.75f);
            k.Line(3.5f, 4.5f, 9, 4.5f, k.P.Faint);
            k.Line(3.5f, 7, 8, 7, k.P.Faint);
            k.Line(3.5f, 9.5f, 9, 9.5f, k.P.Faint);
        },
        ["SpinEdit"] = k =>
        {
            k.Box(1.5f, 4.5f, 13, 7, k.P.Fill, k.P.Outline, 1);
            k.Line(3.5f, 8, 7.5f, 8, k.P.Faint, 1.5f);
            k.Line(10.5f, 5, 10.5f, 11, k.P.Fill2);
            k.Polyline(k.P.Accent, 1.2f, 11.4f, 7.3f, 12.5f, 6.2f, 13.6f, 7.3f);
            k.Polyline(k.P.Accent, 1.2f, 11.4f, 8.7f, 12.5f, 9.8f, 13.6f, 8.7f);
        },
        ["Splitter"] = k =>
        {
            k.Box(1.5f, 2.5f, 5, 11, k.P.Fill, k.P.Outline);
            k.Box(9.5f, 2.5f, 5, 11, k.P.Fill, k.P.Outline);
            k.Box(7, 2, 2, 12, k.P.Accent, null);
            k.Poly(k.P.Outline, null, 3, 8, 5, 6.3f, 5, 9.7f);
            k.Poly(k.P.Outline, null, 13, 8, 11, 6.3f, 11, 9.7f);
        },
        ["StatusBar"] = k =>
        {
            k.Box(1.5f, 1.5f, 13, 13, k.P.Fill, k.P.Outline, 1);
            k.Line(3.5f, 4.5f, 11, 4.5f, k.P.Faint);
            k.Line(3.5f, 7, 9, 7, k.P.Faint);
            k.Box(2, 10.5f, 12, 3.5f, k.P.Accent, null);
            k.Line(3.5f, 12.25f, 6.5f, 12.25f, k.P.OnAccent);
            k.Line(8.5f, 11, 8.5f, 13.5f, k.P.OnAccent);
            k.Line(11.5f, 11, 11.5f, 13.5f, k.P.OnAccent);
        },
        ["ToolBar"] = k =>
        {
            k.Box(1.5f, 4.5f, 13, 7, k.P.Fill2, k.P.Outline, 1);
            k.Line(3.5f, 6.5f, 3.5f, 10, k.P.Faint, 1, LineCap.Butt, [1, 1]);
            k.Box(5, 6, 4, 4, k.P.Accent, null, 1);
            k.Box(10, 6, 3.5f, 4, k.P.Green, null, 1);
        },
        ["ToolButton"] = k =>
        {
            k.Box(2.5f, 2.5f, 11, 11, k.P.Fill, k.P.Outline, 2);
            k.Poly(k.P.Accent, null, 6.5f, 5.3f, 11, 8, 6.5f, 10.7f);
        },
        ["ToolSeparator"] = k =>
        {
            k.Box(1.5f, 5.5f, 4, 5, k.P.Fill2, k.P.Faint, 1);
            k.Box(10.5f, 5.5f, 4, 5, k.P.Fill2, k.P.Faint, 1);
            k.Line(8, 2.5f, 8, 13.5f, k.P.Accent, 1.5f, LineCap.Round);
        },
        ["PropertyGrid"] = k =>
        {
            k.Box(1.5f, 1.5f, 13, 13, k.P.Fill, k.P.Outline);
            k.Box(2, 2, 12, 3, k.P.Fill2, null);
            k.Line(3.5f, 3.5f, 8, 3.5f, k.P.Outline);
            k.Line(7.5f, 5, 7.5f, 14, k.P.Fill2);
            k.Line(3, 6.5f, 6, 6.5f, k.P.Faint);
            k.Line(9, 6.5f, 12.5f, 6.5f, k.P.Outline);
            k.Box(2, 8, 12, 3, k.P.Accent, null);
            k.Line(3, 9.5f, 6, 9.5f, k.P.OnAccent);
            k.Line(9, 9.5f, 12, 9.5f, k.P.OnAccent);
            k.Line(3, 12.5f, 6, 12.5f, k.P.Faint);
            k.Line(9, 12.5f, 13, 12.5f, k.P.Outline);
        },

        // ---- Dialogs ----
        ["OpenDialog"] = k =>
        {
            k.Poly(k.P.FolderBack, k.P.FolderEdge, 1.5f, 13.5f, 1.5f, 3.5f, 6, 3.5f, 7.5f, 5, 13.5f, 5, 13.5f, 7.5f, 1.5f, 13.5f);
            k.Poly(k.P.Folder, k.P.FolderEdge, 4, 7.5f, 15.2f, 7.5f, 12.8f, 13.5f, 1.5f, 13.5f);
        },
        ["SaveDialog"] = k =>
        {
            k.Poly(k.P.Accent, null, 2, 2, 11.8f, 2, 14, 4.2f, 14, 14, 2, 14);
            k.Box(5, 2, 5.5f, 4, k.P.Fill2, null);
            k.Box(8.5f, 2.75f, 1, 2.5f, k.P.Accent, null);
            k.Box(4, 8.5f, 8, 5.5f, k.P.Fill, null);
            k.Line(5.5f, 10.5f, 10.5f, 10.5f, k.P.Faint);
            k.Line(5.5f, 12.5f, 9, 12.5f, k.P.Faint);
        },
        ["FolderDialog"] = k =>
        {
            k.Poly(k.P.FolderBack, k.P.FolderEdge, 1.5f, 13.5f, 1.5f, 3.5f, 6, 3.5f, 7.5f, 5, 14.5f, 5, 14.5f, 13.5f);
            k.Box(1.5f, 6.5f, 13, 7, k.P.Folder, k.P.FolderEdge);
            k.Polyline(k.P.Accent, 1.5f, 5.5f, 10, 7.3f, 11.7f, 10.8f, 8.3f);
        },

        // ---- System ----
        ["Timer"] = k =>
        {
            k.Line(6.5f, 1.75f, 9.5f, 1.75f, k.P.Outline, 1.5f, LineCap.Round);
            k.Line(8, 2, 8, 3.5f, k.P.Outline, 1.5f);
            k.Line(12.3f, 4.3f, 13.3f, 3.3f, k.P.Outline, 1.5f, LineCap.Round);
            k.Circle(8, 9, 6, k.P.Fill, k.P.Outline);
            k.Polyline(k.P.Accent, 1.4f, 8, 5.6f, 8, 9, 10.6f, 10.4f);
        },
        ["ImageList"] = k =>
        {
            k.Box(4.5f, 1.5f, 10, 8, k.P.Fill2, k.P.Outline);
            k.Box(1.5f, 5.5f, 10, 9, k.P.Fill, k.P.Outline);
            k.Circle(4.5f, 8.5f, 1.3f, k.P.Orange, null);
            k.Poly(k.P.Accent, null, 2, 14, 5, 10.5f, 7, 12.5f, 8.5f, 11, 11, 14);
        },
    };

    // ---- Colours ----

    /// <summary>The icon colours for one theme brightness (VS's light and dark icon palettes, slightly softened).</summary>
    sealed record Palette(Color Outline, Color Fill, Color Fill2, Color Faint, Color Highlight, Color Accent, Color OnAccent,
        Color Green, Color Orange, Color Red, Color Folder, Color FolderBack, Color FolderEdge)
    {
        public static readonly Palette Light = new(
            Outline: Color.FromHex("#424242"), Fill: Color.FromHex("#FFFFFF"), Fill2: Color.FromHex("#D9DCE1"),
            Faint: Color.FromHex("#8C8C8C"), Highlight: Color.FromHex("#FFFFFF"),
            Accent: Color.FromHex("#1C73CF"), OnAccent: Color.FromHex("#FFFFFF"),
            Green: Color.FromHex("#2E8B3E"), Orange: Color.FromHex("#E3920E"), Red: Color.FromHex("#D13438"),
            Folder: Color.FromHex("#F4C35A"), FolderBack: Color.FromHex("#DDA83A"), FolderEdge: Color.FromHex("#B07F1C"));

        public static readonly Palette Dark = new(
            Outline: Color.FromHex("#C8C8C8"), Fill: Color.FromHex("#323236"), Fill2: Color.FromHex("#55575D"),
            Faint: Color.FromHex("#9A9A9A"), Highlight: Color.FromHex("#7A7D84"),
            Accent: Color.FromHex("#4AA0F2"), OnAccent: Color.FromHex("#FFFFFF"),
            Green: Color.FromHex("#5DBE6B"), Orange: Color.FromHex("#F2A93B"), Red: Color.FromHex("#F06A6E"),
            Folder: Color.FromHex("#E9BC55"), FolderBack: Color.FromHex("#C99A35"), FolderEdge: Color.FromHex("#8F6A1E"));

        /// <summary>The dark palette when the theme's input background is dark.</summary>
        public static Palette For(Theme theme)
        {
            var c = theme.InputBackground;
            return (c.R * 299 + c.G * 587 + c.B * 114) / 1000 < 128 ? Dark : Light;
        }
    }

    // ---- Drawing in grid units ----

    /// <summary>Draws in 16 × 16 grid units mapped onto a square box.</summary>
    sealed class Ink(Canvas canvas, Rect box)
    {
        readonly float _x = box.X, _y = box.Y, _u = box.Width / 16f;
        public readonly Palette P = Palette.For(canvas.Theme);

        PointF Pt(float x, float y) => new(_x + x * _u, _y + y * _u);
        float W(float w) => Math.Max(1f, w * _u);

        static GraphicsPath RoundRect(PointF a, PointF b, float r)
        {
            var p = new GraphicsPath();
            if (r <= 0)
                return p.MoveTo(a).LineTo(new(b.X, a.Y)).LineTo(b).LineTo(new(a.X, b.Y)).Close();
            r = Math.Min(r, Math.Min(b.X - a.X, b.Y - a.Y) / 2);
            const float K = 0.4477f; // 1 - 0.5523: cubic control points for a quarter circle
            p.MoveTo(new(a.X + r, a.Y)).LineTo(new(b.X - r, a.Y));
            p.CubicTo(new(b.X - r * K, a.Y), new(b.X, a.Y + r * K), new(b.X, a.Y + r));
            p.LineTo(new(b.X, b.Y - r));
            p.CubicTo(new(b.X, b.Y - r * K), new(b.X - r * K, b.Y), new(b.X - r, b.Y));
            p.LineTo(new(a.X + r, b.Y));
            p.CubicTo(new(a.X + r * K, b.Y), new(a.X, b.Y - r * K), new(a.X, b.Y - r));
            p.LineTo(new(a.X, a.Y + r));
            p.CubicTo(new(a.X, a.Y + r * K), new(a.X + r * K, a.Y), new(a.X + r, a.Y));
            return p.Close();
        }

        /// <summary>A rectangle in grid units, filled and/or outlined.</summary>
        public void Box(float x, float y, float w, float h, Color? fill, Color? stroke, float radius = 0)
        {
            var path = RoundRect(Pt(x, y), Pt(x + w, y + h), radius * _u);
            if (fill is { } f) canvas.Fill(path, f);
            if (stroke is { } s) canvas.Stroke(path, s, W(1), LineCap.Butt, LineJoin.Miter);
        }

        public void StrokeBox(float x, float y, float w, float h, Color color, float width, float[] dash) =>
            canvas.Stroke(RoundRect(Pt(x, y), Pt(x + w, y + h), 0), color, W(width), LineCap.Butt, LineJoin.Miter,
                [.. dash.Select(d => d * _u)]);

        public void Line(float x1, float y1, float x2, float y2, Color color, float width = 1, LineCap cap = LineCap.Butt, float[]? dash = null) =>
            canvas.Stroke(new GraphicsPath().MoveTo(Pt(x1, y1)).LineTo(Pt(x2, y2)), color, W(width), cap, LineJoin.Miter,
                dash == null ? null : [.. dash.Select(d => d * _u)]);

        /// <summary>An open stroke through x,y pairs with round caps and joins.</summary>
        public void Polyline(Color color, float width, params float[] xy) => Polyline(color, width, LineCap.Round, xy);

        public void Polyline(Color color, float width, LineCap cap, params float[] xy)
        {
            var p = new GraphicsPath().MoveTo(Pt(xy[0], xy[1]));
            for (int i = 2; i + 1 < xy.Length; i += 2) p.LineTo(Pt(xy[i], xy[i + 1]));
            canvas.Stroke(p, color, W(width), cap, cap == LineCap.Round ? LineJoin.Round : LineJoin.Miter);
        }

        /// <summary>A closed shape through x,y pairs.</summary>
        public void Poly(Color? fill, Color? stroke, params float[] xy)
        {
            var p = new GraphicsPath().MoveTo(Pt(xy[0], xy[1]));
            for (int i = 2; i + 1 < xy.Length; i += 2) p.LineTo(Pt(xy[i], xy[i + 1]));
            p.Close();
            if (fill is { } f) canvas.Fill(p, f);
            if (stroke is { } s) canvas.Stroke(p, s, W(1), LineCap.Butt, LineJoin.Miter);
        }

        /// <summary>A circle; an outline is drawn just inside the radius.</summary>
        public void Circle(float cx, float cy, float r, Color? fill, Color? stroke)
        {
            float rr = stroke != null ? r - 0.5f : r;
            const float K = 0.5523f;
            var c = Pt(cx, cy);
            float d = rr * _u, k = d * K;
            var p = new GraphicsPath().MoveTo(new(c.X + d, c.Y))
                .CubicTo(new(c.X + d, c.Y + k), new(c.X + k, c.Y + d), new(c.X, c.Y + d))
                .CubicTo(new(c.X - k, c.Y + d), new(c.X - d, c.Y + k), new(c.X - d, c.Y))
                .CubicTo(new(c.X - d, c.Y - k), new(c.X - k, c.Y - d), new(c.X, c.Y - d))
                .CubicTo(new(c.X + k, c.Y - d), new(c.X + d, c.Y - k), new(c.X + d, c.Y))
                .Close();
            if (fill is { } f) canvas.Fill(p, f);
            if (stroke is { } s) canvas.Stroke(p, s, W(1));
        }

        /// <summary>A cubic curve from (x0,y0) through two control points to (x3,y3), round-capped.</summary>
        public void Curve(Color color, float width, float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3) =>
            canvas.Stroke(new GraphicsPath().MoveTo(Pt(x0, y0)).CubicTo(Pt(x1, y1), Pt(x2, y2), Pt(x3, y3)),
                color, W(width), LineCap.Round, LineJoin.Round);
    }
}
