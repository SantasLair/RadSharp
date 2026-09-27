using SkiaSharp;

namespace RadSharp.Drawing;

public enum LineCap { Butt, Round, Square }
public enum LineJoin { Miter, Round, Bevel }
public enum BlendMode { SrcOver, Multiply, Screen, DstOver, Clear }

// Explicit maps, not casts: our enum values are not Skia's.
internal static class SkiaMaps
{
    public static SKStrokeCap ToSK(this LineCap cap) => cap switch
    {
        LineCap.Round => SKStrokeCap.Round,
        LineCap.Square => SKStrokeCap.Square,
        _ => SKStrokeCap.Butt,
    };

    public static SKStrokeJoin ToSK(this LineJoin join) => join switch
    {
        LineJoin.Round => SKStrokeJoin.Round,
        LineJoin.Bevel => SKStrokeJoin.Bevel,
        _ => SKStrokeJoin.Miter,
    };

    public static SKBlendMode ToSK(this BlendMode mode) => mode switch
    {
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.DstOver => SKBlendMode.DstOver,
        BlendMode.Clear => SKBlendMode.Clear,
        _ => SKBlendMode.SrcOver,
    };
}

/// <summary>
/// A sequence of lines and curves to fill or stroke with <see cref="Canvas"/>. Can be drawn any number of times.
/// (Named to stay clear of <see cref="System.IO.Path"/> in files that import this namespace.)
/// </summary>
public sealed class GraphicsPath
{
    readonly SKPathBuilder _builder = new();

    public GraphicsPath MoveTo(PointF pt) { _builder.MoveTo(pt.X, pt.Y); return this; }
    public GraphicsPath LineTo(PointF pt) { _builder.LineTo(pt.X, pt.Y); return this; }
    public GraphicsPath QuadTo(PointF control, PointF pt) { _builder.QuadTo(control.X, control.Y, pt.X, pt.Y); return this; }
    public GraphicsPath CubicTo(PointF control1, PointF control2, PointF pt) { _builder.CubicTo(control1.X, control1.Y, control2.X, control2.Y, pt.X, pt.Y); return this; }

    /// <summary>Adds an elliptical arc inside <paramref name="bounds"/>. Degrees; 0° is 3 o'clock and positive sweeps run clockwise (y down).</summary>
    public GraphicsPath ArcTo(Rect bounds, float startDeg, float sweepDeg)
    {
        _builder.AddArc(new SKRect(bounds.X, bounds.Y, bounds.X + bounds.Width, bounds.Y + bounds.Height), startDeg, sweepDeg);
        return this;
    }

    public GraphicsPath AddRect(Rect r) { _builder.AddRect(new SKRect(r.X, r.Y, r.X + r.Width, r.Y + r.Height)); return this; }
    public GraphicsPath AddEllipse(Rect r) { _builder.AddOval(new SKRect(r.X, r.Y, r.X + r.Width, r.Y + r.Height)); return this; }
    public GraphicsPath Close() { _builder.Close(); return this; }

    /// <summary>A copy of the path so far, for one draw call; the builder keeps its contents.</summary>
    internal SKPath Snapshot() => _builder.Snapshot();
}

/// <summary>A colour gradient to fill shapes with, in the coordinates of the canvas it is drawn on.</summary>
public sealed class Gradient
{
    readonly SKShader _shader;

    Gradient(SKShader shader) => _shader = shader;

    /// <summary>Colours blend along the line from <paramref name="start"/> to <paramref name="end"/>; offsets run 0 to 1.</summary>
    public static Gradient Linear(PointF start, PointF end, params (float Offset, Color Color)[] stops)
    {
        var (colors, offsets) = Split(stops);
        return new Gradient(SKShader.CreateLinearGradient(
            new SKPoint(start.X, start.Y), new SKPoint(end.X, end.Y), colors, offsets, SKShaderTileMode.Clamp));
    }

    /// <summary>Colours blend outward from <paramref name="center"/> to <paramref name="radius"/>; offsets run 0 to 1.</summary>
    public static Gradient Radial(PointF center, float radius, params (float Offset, Color Color)[] stops)
    {
        var (colors, offsets) = Split(stops);
        return new Gradient(SKShader.CreateRadialGradient(
            new SKPoint(center.X, center.Y), radius, colors, offsets, SKShaderTileMode.Clamp));
    }

    static (SKColor[] Colors, float[] Offsets) Split((float Offset, Color Color)[] stops)
    {
        if (stops.Length < 2) throw new ArgumentException("A gradient needs at least two stops.", nameof(stops));
        var colors = new SKColor[stops.Length];
        var offsets = new float[stops.Length];
        for (int i = 0; i < stops.Length; i++)
        {
            colors[i] = stops[i].Color.ToSK();
            offsets[i] = stops[i].Offset;
        }
        return (colors, offsets);
    }

    internal SKShader Shader => _shader;
}

/// <summary>An off-screen image of whole physical pixels that a <see cref="Canvas"/> can paint on.</summary>
public sealed class Bitmap : IDisposable
{
    readonly SKBitmap _bitmap;
    bool _disposed;

    Bitmap(SKBitmap bitmap) => _bitmap = bitmap;

    public int Width => _bitmap.Width;
    public int Height => _bitmap.Height;

    /// <summary>A new, fully transparent bitmap.</summary>
    public static Bitmap Create(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        return new Bitmap(bitmap);
    }

    /// <summary>Paints on the bitmap (at 100 % scale, default theme); what was already there stays underneath.</summary>
    public void Paint(Action<Canvas> draw)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var sk = new SKCanvas(_bitmap);
        draw(new Canvas(sk, Theme.Default, 1.0));
    }

    /// <summary>An immutable snapshot of the pixels now, for <see cref="Canvas.DrawPicture(Picture, Rect)"/>.</summary>
    public Picture ToPicture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Picture.FromImage(SKImage.FromBitmap(_bitmap));
    }

    public byte[] EncodePng()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var data = _bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bitmap.Dispose();
    }
}
