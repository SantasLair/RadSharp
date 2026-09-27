using SkiaSharp;

namespace RadSharp.Drawing;

public enum LineCap { Butt, Round, Square }
public enum LineJoin { Miter, Round, Bevel }
public enum BlendMode { SrcOver, Multiply, Screen, DstOver, Clear }

public sealed class Path
{
    readonly SKPathBuilder _builder = new();

    public Path MoveTo(PointF pt) { _builder.MoveTo(pt.X, pt.Y); return this; }
    public Path LineTo(PointF pt) { _builder.LineTo(pt.X, pt.Y); return this; }
    public Path QuadTo(PointF cp, PointF pt) { _builder.QuadTo(cp.X, cp.Y, pt.X, pt.Y); return this; }
    public Path CubicTo(PointF cp1, PointF cp2, PointF pt) { _builder.CubicTo(cp1.X, cp1.Y, cp2.X, cp2.Y, pt.X, pt.Y); return this; }

    /// <summary>Adds an arc to the path. Angles are in degrees; 0° is at 3 o'clock, 90° is at 6 o'clock (y-down).</summary>
    public Path ArcTo(Rect bounds, float startDeg, float sweepDeg)
    {
        var rect = new SKRect(bounds.X, bounds.Y, bounds.X + bounds.Width, bounds.Y + bounds.Height);
        using var arc = new SKPathBuilder();
        arc.AddArc(rect, startDeg, sweepDeg);
        _builder.AddPath(arc.Detach());
        return this;
    }

    public Path AddRect(Rect r) { _builder.AddRect(new SKRect(r.X, r.Y, r.X + r.Width, r.Y + r.Height)); return this; }
    public Path AddEllipse(Rect r) { _builder.AddOval(new SKRect(r.X, r.Y, r.X + r.Width, r.Y + r.Height)); return this; }
    public Path Close() { _builder.Close(); return this; }

    internal SKPath Detach() => _builder.Detach();
}

public sealed class Gradient
{
    readonly SKShader _shader;

    Gradient(SKShader shader) => _shader = shader;

    public static Gradient Linear(PointF start, PointF end, params (float Offset, Color Color)[] stops)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stops.Length, 2, nameof(stops));
        var colors = new SKColor[stops.Length];
        var offsets = new float[stops.Length];
        for (int i = 0; i < stops.Length; i++)
        {
            colors[i] = stops[i].Color.ToSK();
            offsets[i] = stops[i].Offset;
        }
        var shader = SKShader.CreateLinearGradient(
            new SKPoint(start.X, start.Y),
            new SKPoint(end.X, end.Y),
            colors,
            offsets,
            SKShaderTileMode.Clamp);
        return new Gradient(shader);
    }

    public static Gradient Radial(PointF center, float radius, params (float Offset, Color Color)[] stops)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stops.Length, 2, nameof(stops));
        var colors = new SKColor[stops.Length];
        var offsets = new float[stops.Length];
        for (int i = 0; i < stops.Length; i++)
        {
            colors[i] = stops[i].Color.ToSK();
            offsets[i] = stops[i].Offset;
        }
        var shader = SKShader.CreateRadialGradient(
            new SKPoint(center.X, center.Y),
            radius,
            colors,
            offsets,
            SKShaderTileMode.Clamp);
        return new Gradient(shader);
    }

    internal SKShader ToSKShader() => _shader;
}

public sealed class Bitmap : IDisposable
{
    readonly SKBitmap _bitmap;
    bool _disposed;

    Bitmap(SKBitmap bitmap) => _bitmap = bitmap;

    public static Bitmap Create(int width, int height) =>
        new(new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

    public void Paint(Action<Canvas> draw)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Bitmap));
        using var surface = SKSurface.Create(_bitmap.Info);
        surface.Canvas.DrawBitmap(_bitmap, SKPoint.Empty);
        var canvas = new Canvas(surface.Canvas, Theme.Default, 1.0);
        draw(canvas);
    }

    public Picture ToPicture()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Bitmap));
        var image = SKImage.FromBitmap(_bitmap);
        return Picture.FromBytes(_bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray())!;
    }

    public byte[] EncodePng()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Bitmap));
        using var data = _bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _bitmap.Dispose();
        _disposed = true;
    }
}
