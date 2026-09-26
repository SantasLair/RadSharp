using SkiaSharp;

namespace RadSharp.Drawing;

public enum TextAlign { Left, Center, Right }
public enum VerticalAlign { Top, Center, Bottom }

/// <summary>
/// What controls paint with. Coordinates are whole physical pixels relative to the control being
/// painted. Rectangles and borders are filled without anti-aliasing so edges stay crisp; curves and
/// text are anti-aliased by Skia's CPU rasterizer, which is the same on every OS.
/// </summary>
public sealed class Canvas
{
    readonly SKCanvas _sk;
    readonly SKPaint _fill = new() { Style = SKPaintStyle.Fill, IsAntialias = false };
    readonly SKPaint _smooth = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    readonly SKPaint _stroke = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    Font? _defaultFont, _defaultBoldFont;

    public Canvas(SKCanvas canvas, Theme theme, double scale)
    {
        _sk = canvas;
        Theme = theme;
        Scale = scale;
    }

    public Theme Theme { get; }
    /// <summary>The UI scale factor (1.0 = 100 %).</summary>
    public double Scale { get; }

    /// <summary>Scales a length given at 100 % to whole physical pixels.</summary>
    public int Px(int length) => LayoutMath.Round(length * Scale);

    /// <summary>The theme's text font at the current scale.</summary>
    public Font DefaultFont => _defaultFont ??= new Font(Theme.FontFace, (float)(Theme.FontSize * Scale));
    public Font DefaultBoldFont => _defaultBoldFont ??= new Font(Theme.BoldFontFace, (float)(Theme.FontSize * Scale));

    // ---- State ----

    public void Save() => _sk.Save();
    public void Restore() => _sk.Restore();
    public void Translate(int dx, int dy) => _sk.Translate(dx, dy);
    public void ClipRect(Rect r) => _sk.ClipRect(ToSK(r));
    public bool IsClippedOut(Rect r) => _sk.QuickReject(ToSK(r));

    // ---- Shapes ----

    public void FillRect(Rect r, Color color)
    {
        if (r.Width <= 0 || r.Height <= 0 || color.A == 0) return;
        _fill.Color = color.ToSK();
        _sk.DrawRect(ToSK(r), _fill);
    }

    /// <summary>A border drawn inside <paramref name="r"/>, <paramref name="thickness"/> pixels wide.</summary>
    public void DrawBorder(Rect r, Color color, int thickness = 1)
    {
        if (thickness <= 0) return;
        int t = Math.Min(thickness, Math.Min(r.Width, r.Height));
        FillRect(new Rect(r.X, r.Y, r.Width, t), color);
        FillRect(new Rect(r.X, r.Y + r.Height - t, r.Width, t), color);
        FillRect(new Rect(r.X, r.Y + t, t, r.Height - 2 * t), color);
        FillRect(new Rect(r.X + r.Width - t, r.Y + t, t, r.Height - 2 * t), color);
    }

    public void FillRoundRect(Rect r, float radius, Color color)
    {
        if (r.Width <= 0 || r.Height <= 0 || color.A == 0) return;
        _smooth.Color = color.ToSK();
        _sk.DrawRoundRect(ToSK(r), radius, radius, _smooth);
    }

    /// <summary>A rounded border whose outer edge is <paramref name="r"/>; strokes sit on half-pixel centres.</summary>
    public void DrawRoundBorder(Rect r, float radius, Color color, int thickness = 1)
    {
        if (r.Width <= 0 || r.Height <= 0 || thickness <= 0) return;
        float h = thickness / 2f;
        _stroke.Color = color.ToSK();
        _stroke.StrokeWidth = thickness;
        _sk.DrawRoundRect(new SKRect(r.X + h, r.Y + h, r.X + r.Width - h, r.Y + r.Height - h),
            Math.Max(0, radius - h), Math.Max(0, radius - h), _stroke);
    }

    public void FillEllipse(Rect r, Color color)
    {
        if (r.Width <= 0 || r.Height <= 0 || color.A == 0) return;
        _smooth.Color = color.ToSK();
        _sk.DrawOval(ToSK(r), _smooth);
    }

    /// <summary>An ellipse outline whose outer edge is <paramref name="r"/>.</summary>
    public void DrawEllipseBorder(Rect r, Color color, int thickness = 1)
    {
        if (r.Width <= 0 || r.Height <= 0 || thickness <= 0) return;
        float h = thickness / 2f;
        _stroke.Color = color.ToSK();
        _stroke.StrokeWidth = thickness;
        _sk.DrawOval(new SKRect(r.X + h, r.Y + h, r.X + r.Width - h, r.Y + r.Height - h), _stroke);
    }

    /// <summary>A check mark (✓) fitted inside <paramref name="r"/>.</summary>
    public void DrawCheckMark(Rect r, Color color, float thickness)
    {
        using var path = new SKPathBuilder();
        path.MoveTo(r.X + r.Width * 0.22f, r.Y + r.Height * 0.52f);
        path.LineTo(r.X + r.Width * 0.42f, r.Y + r.Height * 0.72f);
        path.LineTo(r.X + r.Width * 0.78f, r.Y + r.Height * 0.30f);
        using var p = path.Detach();
        _stroke.Color = color.ToSK();
        _stroke.StrokeWidth = thickness;
        _stroke.StrokeCap = SKStrokeCap.Round;
        _stroke.StrokeJoin = SKStrokeJoin.Round;
        _sk.DrawPath(p, _stroke);
        _stroke.StrokeCap = SKStrokeCap.Butt;
        _stroke.StrokeJoin = SKStrokeJoin.Miter;
    }

    /// <summary>The keyboard focus ring: a rounded outline just inside <paramref name="r"/>.</summary>
    public void DrawFocusRing(Rect r, float radius) =>
        DrawRoundBorder(r, radius, Theme.FocusRing, Math.Max(1, Px(2)));

    /// <summary>Returns <paramref name="r"/> grown by <paramref name="d"/> pixels on every side (negative shrinks).</summary>
    public static Rect Inflate(Rect r, int d) =>
        new(r.X - d, r.Y - d, Math.Max(0, r.Width + 2 * d), Math.Max(0, r.Height + 2 * d));

    /// <summary>Draws a picture scaled into <paramref name="dest"/> (smooth, mipmapped sampling).</summary>
    public void DrawPicture(Picture picture, Rect dest)
    {
        if (dest.Width <= 0 || dest.Height <= 0) return;
        _sk.DrawImage(picture.Image, ToSK(dest), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    /// <summary>
    /// Paints another form (at its own scale) at (<paramref name="x"/>, <paramref name="y"/>), magnified by
    /// <paramref name="zoom"/>: how the designer shows the form being designed.
    /// </summary>
    public void DrawForm(Form form, int x, int y, double zoom)
    {
        _sk.Save();
        _sk.Translate(x, y);
        _sk.Scale((float)zoom);
        FormRenderer.Render(form, _sk);
        _sk.Restore();
    }

    // ---- Text ----

    /// <summary>Draws one line of text with its baseline at <paramref name="baseline"/> (rounded to whole pixels).</summary>
    public void DrawText(string text, Font font, Color color, float x, float baseline)
    {
        if (text.Length == 0 || color.A == 0) return;
        DrawText(TextLayout.Get(text, font), color, x, baseline);
    }

    /// <summary>Draws a laid-out line with its pen origin at (<paramref name="x"/>, <paramref name="baseline"/>), both rounded.</summary>
    public void DrawText(TextLayout layout, Color color, float x, float baseline)
    {
        if (layout.Text.Length == 0 || color.A == 0) return;
        _smooth.Color = color.ToSK();
        layout.Draw(_sk, MathF.Round(x, MidpointRounding.AwayFromZero), MathF.Round(baseline, MidpointRounding.AwayFromZero), _smooth);
    }

    /// <summary>Draws one line of text aligned inside a rectangle.</summary>
    public void DrawText(string text, Font font, Color color, Rect r, TextAlign align, VerticalAlign valign)
    {
        if (text.Length == 0) return;
        float width = font.MeasureWidth(text);
        float x = align switch
        {
            TextAlign.Center => r.X + (r.Width - width) / 2,
            TextAlign.Right => r.X + r.Width - width,
            _ => r.X,
        };
        float textHeight = font.Ascent + font.Descent;
        float top = valign switch
        {
            VerticalAlign.Center => r.Y + (r.Height - textHeight) / 2,
            VerticalAlign.Bottom => r.Y + r.Height - textHeight,
            _ => r.Y,
        };
        DrawText(text, font, color, x, top + font.Ascent);
    }

    static SKRect ToSK(Rect r) => new(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
}
