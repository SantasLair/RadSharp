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
    RenderFont? _defaultFont, _defaultBoldFont;

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
    public RenderFont DefaultFont => _defaultFont ??= new RenderFont(Theme.FontFace, (float)(Theme.FontSize * Scale));
    public RenderFont DefaultBoldFont => _defaultBoldFont ??= new RenderFont(Theme.BoldFontFace, (float)(Theme.FontSize * Scale));

    /// <summary>Resolves a published <see cref="Font"/> descriptor to a drawable <see cref="RenderFont"/> at the current scale.</summary>
    public RenderFont Resolve(Font font) => new(FontFace.Resolve(font), (float)(font.Size * Scale));

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

    /// <summary>Draws a region of a picture scaled into a destination rect (smooth, mipmapped sampling).</summary>
    public void DrawPicture(Picture picture, Rect src, Rect dest)
    {
        if (dest.Width <= 0 || dest.Height <= 0) return;
        _sk.DrawImage(picture.Image, ToSK(src), ToSK(dest), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    // ---- Lines, curves and paths ----
    // Everything here is anti-aliased and uses its coordinates exactly as given (no snapping). A 1 px line
    // centred on a whole-pixel coordinate straddles two pixel rows and looks soft; centre it on a half
    // pixel (x + 0.5) to keep it crisp, as DrawRoundBorder does for its strokes.

    /// <summary>Draws a line from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public void DrawLine(PointF a, PointF b, Color color, float thickness = 1, LineCap cap = LineCap.Butt)
    {
        if (thickness <= 0 || color.A == 0) return;
        using var path = ToSKPath([a, b], close: false);
        StrokePath(path, color, thickness, cap, LineJoin.Miter, null, 255, BlendMode.SrcOver);
    }

    /// <summary>Draws connected line segments through <paramref name="points"/>. A <paramref name="dash"/> pattern alternates on and off lengths (an even count).</summary>
    public void DrawPolyline(IReadOnlyList<PointF> points, Color color, float thickness = 1, LineCap cap = LineCap.Butt, LineJoin join = LineJoin.Miter, float[]? dash = null)
    {
        if (points.Count < 2 || thickness <= 0 || color.A == 0) return;
        using var path = ToSKPath(points, close: false);
        StrokePath(path, color, thickness, cap, join, dash, 255, BlendMode.SrcOver);
    }

    /// <summary>Draws an elliptical arc inside <paramref name="bounds"/>. Degrees; 0° is 3 o'clock and positive sweeps run clockwise (y down).</summary>
    public void DrawArc(Rect bounds, float startDeg, float sweepDeg, Color color, float thickness = 1, LineCap cap = LineCap.Butt)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || thickness <= 0 || color.A == 0) return;
        using var builder = new SKPathBuilder();
        builder.AddArc(ToSK(bounds), startDeg, sweepDeg);
        using var path = builder.Detach();
        StrokePath(path, color, thickness, cap, LineJoin.Miter, null, 255, BlendMode.SrcOver);
    }

    /// <summary>Fills the polygon through <paramref name="points"/> (closed automatically; may be concave).</summary>
    public void FillPolygon(IReadOnlyList<PointF> points, Color color)
    {
        if (points.Count < 3 || color.A == 0) return;
        using var path = ToSKPath(points, close: true);
        FillSKPath(path, color, 255, BlendMode.SrcOver);
    }

    public void Fill(GraphicsPath path, Color color, byte alpha = 255, BlendMode blend = BlendMode.SrcOver)
    {
        if (color.A == 0 || alpha == 0) return;
        using var p = path.Snapshot();
        FillSKPath(p, color, alpha, blend);
    }

    public void Stroke(GraphicsPath path, Color color, float thickness = 1, LineCap cap = LineCap.Butt, LineJoin join = LineJoin.Miter, float[]? dash = null, byte alpha = 255, BlendMode blend = BlendMode.SrcOver)
    {
        if (thickness <= 0 || color.A == 0 || alpha == 0) return;
        using var p = path.Snapshot();
        StrokePath(p, color, thickness, cap, join, dash, alpha, blend);
    }

    static SKPath ToSKPath(IReadOnlyList<PointF> points, bool close)
    {
        using var builder = new SKPathBuilder();
        builder.MoveTo(points[0].X, points[0].Y);
        for (int i = 1; i < points.Count; i++) builder.LineTo(points[i].X, points[i].Y);
        if (close) builder.Close();
        return builder.Detach();
    }

    static SKColor WithAlpha(Color color, byte alpha) =>
        alpha == 255 ? color.ToSK() : new SKColor(color.R, color.G, color.B, (byte)(color.A * alpha / 255));

    void FillSKPath(SKPath path, Color color, byte alpha, BlendMode blend)
    {
        _smooth.Color = WithAlpha(color, alpha);
        _smooth.BlendMode = blend.ToSK();
        _sk.DrawPath(path, _smooth);
        _smooth.BlendMode = SKBlendMode.SrcOver;
    }

    void StrokePath(SKPath path, Color color, float thickness, LineCap cap, LineJoin join, float[]? dash, byte alpha, BlendMode blend)
    {
        if (dash != null && (dash.Length < 2 || dash.Length % 2 != 0))
            throw new ArgumentException("A dash pattern needs an even number of lengths (on, off, ...).", nameof(dash));

        using var effect = dash == null ? null : SKPathEffect.CreateDash(dash, 0);
        _stroke.Color = WithAlpha(color, alpha);
        _stroke.StrokeWidth = thickness;
        _stroke.StrokeCap = cap.ToSK();
        _stroke.StrokeJoin = join.ToSK();
        _stroke.BlendMode = blend.ToSK();
        _stroke.PathEffect = effect;
        try { _sk.DrawPath(path, _stroke); }
        finally
        {
            // The paint is shared: leave it as the other methods expect it.
            _stroke.PathEffect = null;
            _stroke.StrokeCap = SKStrokeCap.Butt;
            _stroke.StrokeJoin = SKStrokeJoin.Miter;
            _stroke.BlendMode = SKBlendMode.SrcOver;
        }
    }

    // ---- Gradients ----

    /// <summary>Fills a rectangle with a gradient (no anti-aliasing, like <see cref="FillRect(Rect, Color)"/>).</summary>
    public void FillRect(Rect r, Gradient gradient, byte alpha = 255, BlendMode blend = BlendMode.SrcOver)
    {
        if (r.Width <= 0 || r.Height <= 0 || alpha == 0) return;
        using var paint = GradientPaint(gradient, antialias: false, alpha, blend);
        _sk.DrawRect(ToSK(r), paint);
    }

    public void FillRoundRect(Rect r, float radius, Gradient gradient, byte alpha = 255, BlendMode blend = BlendMode.SrcOver)
    {
        if (r.Width <= 0 || r.Height <= 0 || alpha == 0) return;
        using var paint = GradientPaint(gradient, antialias: true, alpha, blend);
        _sk.DrawRoundRect(ToSK(r), radius, radius, paint);
    }

    public void FillEllipse(Rect r, Gradient gradient, byte alpha = 255, BlendMode blend = BlendMode.SrcOver)
    {
        if (r.Width <= 0 || r.Height <= 0 || alpha == 0) return;
        using var paint = GradientPaint(gradient, antialias: true, alpha, blend);
        _sk.DrawOval(ToSK(r), paint);
    }

    public void Fill(GraphicsPath path, Gradient gradient, byte alpha = 255, BlendMode blend = BlendMode.SrcOver)
    {
        if (alpha == 0) return;
        using var p = path.Snapshot();
        using var paint = GradientPaint(gradient, antialias: true, alpha, blend);
        _sk.DrawPath(p, paint);
    }

    // The paint's own colour only supplies the overall opacity; the shader supplies the colours.
    static SKPaint GradientPaint(Gradient gradient, bool antialias, byte alpha, BlendMode blend) => new()
    {
        Style = SKPaintStyle.Fill,
        IsAntialias = antialias,
        Shader = gradient.Shader,
        Color = new SKColor(0, 0, 0, alpha),
        BlendMode = blend.ToSK(),
    };

    /// <summary>Restricts drawing to a rounded rectangle (anti-aliased edge); undone by <see cref="Restore"/>.</summary>
    public void ClipRoundRect(Rect r, float radius)
    {
        using var rounded = new SKRoundRect(ToSK(r), radius, radius);
        _sk.ClipRoundRect(rounded, SKClipOperation.Intersect, true);
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
    public void DrawText(string text, RenderFont font, Color color, float x, float baseline)
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
    public void DrawText(string text, RenderFont font, Color color, Rect r, TextAlign align, VerticalAlign valign)
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

    // ---- Text (published Font descriptor: resolves the face and draws the underline, if any) ----

    /// <summary>Draws one line of text using a published <see cref="Font"/> descriptor (resolved via <see cref="Resolve(Font)"/>).</summary>
    public void DrawText(string text, Font font, Color color, float x, float baseline)
    {
        if (text.Length == 0 || color.A == 0) return;
        var render = Resolve(font);
        DrawText(text, render, color, x, baseline);
        if (font.Underline) DrawUnderline(render, text, color, x, baseline);
    }

    /// <summary>Draws one line of text using a published <see cref="Font"/> descriptor, aligned inside a rectangle.</summary>
    public void DrawText(string text, Font font, Color color, Rect r, TextAlign align, VerticalAlign valign)
    {
        if (text.Length == 0) return;
        var render = Resolve(font);
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
        float baseline = top + render.Ascent;
        DrawText(text, render, color, x, baseline);
        if (font.Underline) DrawUnderline(render, text, color, x, baseline);
    }

    void DrawUnderline(RenderFont font, string text, Color color, float x, float baseline)
    {
        float width = font.MeasureWidth(text);
        float y = MathF.Floor(baseline + Math.Max(1, font.Size * 0.08f)) + 0.5f;
        DrawLine(new PointF(x, y), new PointF(x + width, y), color, Math.Max(1, Px(1)));
    }

    static SKRect ToSK(Rect r) => new(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
}
