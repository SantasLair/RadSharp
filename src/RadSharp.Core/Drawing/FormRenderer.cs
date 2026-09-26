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
        PaintTree(form, c, translate: false);
        foreach (var popup in form.Popups)
        {
            PaintShadow(c, popup.Bounds);
            PaintTree(popup, c, translate: true);
        }
        c.Restore();
    }

    static void PaintTree(Control control, Canvas c, bool translate)
    {
        if (!control.IsShown) return;
        var local = new Rect(0, 0, control.Width, control.Height);
        c.Save();
        if (translate) c.Translate(control.Left, control.Top);
        if (!c.IsClippedOut(local))
        {
            c.ClipRect(local);
            control.PaintControl(c);
            if (control.Children.Count > 0)
            {
                c.Save();
                c.ClipRect(control.ChildClipRect);
                var (ox, oy) = control.ChildOffset;
                if (ox != 0 || oy != 0) c.Translate(ox, oy);
                foreach (var child in control.Children) PaintTree(child, c, translate: true);
                c.Restore();
            }
        }
        c.Restore();
    }

    static void PaintShadow(Canvas c, Rect r)
    {
        int d = Math.Max(1, c.Px(1));
        c.FillRect(new Rect(r.X + d, r.Y + r.Height, r.Width, 2 * d), new Color(0, 0, 0, 24));
        c.FillRect(new Rect(r.X + r.Width, r.Y + d, 2 * d, r.Height + d), new Color(0, 0, 0, 24));
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
