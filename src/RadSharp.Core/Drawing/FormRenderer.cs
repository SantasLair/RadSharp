using SkiaSharp;

namespace RadSharp.Drawing;

/// <summary>Paints a form's control tree with Skia's CPU rasterizer.</summary>
public static class FormRenderer
{
    /// <summary>The pixel format of every frame: BGRA, premultiplied (SDL's ARGB8888 on little-endian).</summary>
    public static SKImageInfo FrameInfo(int width, int height) =>
        new(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

    /// <summary>Lays out the form if needed, then paints it (or only <paramref name="clip"/>) onto <paramref name="canvas"/>.</summary>
    public static void Render(Form form, SKCanvas canvas, Rect? clip = null)
    {
        form.PerformLayout();
        var c = new Canvas(canvas, form.Theme, form.Scale);
        c.Save();
        if (clip is { } r) c.ClipRect(r);
        PaintTree(form, c, isRoot: true);
        c.Restore();
    }

    static void PaintTree(Control control, Canvas c, bool isRoot)
    {
        if (!control.Visible) return;
        var local = new Rect(0, 0, control.Width, control.Height);
        c.Save();
        if (!isRoot) c.Translate(control.Left, control.Top);
        if (!c.IsClippedOut(local))
        {
            c.ClipRect(local);
            control.PaintControl(c);
            foreach (var child in control.Children) PaintTree(child, c, isRoot: false);
        }
        c.Restore();
    }

    /// <summary>Renders the whole form into a new bitmap at its current size.</summary>
    public static SKBitmap RenderToBitmap(Form form)
    {
        form.PerformLayout();
        var bitmap = new SKBitmap(FrameInfo(Math.Max(1, form.Width), Math.Max(1, form.Height)));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        Render(form, canvas);
        return bitmap;
    }

    public static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
