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
        LayoutForPainting(form);
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

    /// <summary>
    /// Lays <paramref name="form"/> out before it is painted. A form being designed (<see cref="Control.DesignMode"/>)
    /// may hold an application's control whose layout code throws at design time: it is painted as far as it got,
    /// rather than the exception taking the designer down (the change that caused it reports it).
    /// </summary>
    public static void LayoutForPainting(Form form)
    {
        if (!form.DesignMode) { form.PerformLayout(); return; }
        try { form.PerformLayout(); }
        catch (Exception e) when (e is not OutOfMemoryException) { }
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
            if (control.DesignMode) PaintAtDesignTime(control, c, local);
            else control.PaintControl(c);
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

    /// <summary>
    /// A control the designer created (<see cref="Control.DesignMode"/>) may run an application's own painting code
    /// inside the designer: if that throws (it needs what only the running application has, say), the control is drawn
    /// as a box with the error, as Delphi's designer does, instead of the exception taking the designer down with its
    /// unsaved work. Its children are still painted.
    /// </summary>
    static void PaintAtDesignTime(Control control, Canvas c, Rect local)
    {
        int saved = c.SaveCount;
        try { control.PaintControl(c); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            c.RestoreToCount(saved);
            c.FillRect(local, new Color(255, 236, 236));
            c.DrawBorder(local, new Color(200, 0, 0));
            var inner = e is System.Reflection.TargetInvocationException { InnerException: { } i } ? i : e;
            var message = $"{inner.GetType().Name}: {inner.Message}";
            c.DrawText(message, c.DefaultFont, new Color(160, 0, 0), Canvas.Inflate(local, -c.Px(4)),
                TextAlign.Left, VerticalAlign.Top, ellipsis: true);
            // Painting repeats on every frame: tell the designer once per control and message, not 60 times a second.
            if (!Reported.TryGetValue(control, out var last) || last != message)
            {
                Reported.AddOrUpdate(control, message);
                PaintError?.Invoke(control, inner);
            }
        }
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, string> Reported = new();

    /// <summary>
    /// A control the designer created threw while it painted (it is drawn as an error box instead): raised the first
    /// time a control fails with a given message, on the painting thread, so a designer can log it (the Visual Studio
    /// design host sends it to the IDE's Output pane).
    /// </summary>
    public static event Action<Control, Exception>? PaintError;

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
