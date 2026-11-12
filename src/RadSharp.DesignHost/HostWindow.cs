using System.Runtime.InteropServices;
using RadSharp.DesignHost.Protocol;
using RadSharp.Drawing;
using SkiaSharp;

namespace RadSharp.DesignHost;

/// <summary>
/// One host window: a form the client shows in a view of its own. Its pixels live in a back buffer the size
/// of the client view (device pixels); only dirty rectangles are rendered and sent.
/// </summary>
internal sealed class HostWindow : IDisposable
{
    /// <summary>Largest accepted window side, in device pixels.</summary>
    public const int MaxDimension = 16384;

    /// <summary>Largest accepted window area, in pixels (a 256 MB back buffer).</summary>
    public const long MaxPixels = 64L * 1024 * 1024;

    /// <summary>Most pixel bytes carried by one <see cref="Protocol.Frame"/>; bigger rectangles are sent as horizontal bands.</summary>
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    SKBitmap? _back;

    // Dirty pixels rendered into the back buffer but not yet handed to the writer (union, back-buffer coordinates).
    int _px0, _py0, _px1, _py1;
    bool _hasPending;

    int _lastLeft, _lastTop;
    (int, int)? _rejectedSize;

    public HostWindow(int id, WindowRole role, int docId, Form form, int ownerId = 0)
    {
        Id = id;
        Role = role;
        DocId = docId;
        Form = form;
        OwnerId = ownerId;
        _lastLeft = form.Left;
        _lastTop = form.Top;
    }

    public int Id { get; }
    public WindowRole Role { get; }
    public int DocId { get; }
    public int OwnerId { get; }
    public Form Form { get; set; }

    /// <summary>A client view is bound to this window (frames are only sent then).</summary>
    public bool Attached { get; set; }

    /// <summary>Shown modally (RunModal / ShowModal): other windows get no input meanwhile.</summary>
    public bool Modal { get; init; }

    /// <summary>The DPI scale the form was built or last resized at.</summary>
    public double Scale { get; set; } = 1;

    /// <summary>The position last announced to the client (dialogs and popups).</summary>
    public int X { get; set; }
    public int Y { get; set; }

    public int Width => _back?.Width ?? 0;
    public int Height => _back?.Height ?? 0;

    /// <summary>The cursor last sent to the client.</summary>
    public Cursor SentCursor { get; set; } = Cursor.Default;

    /// <summary>A frame for this window is in the outbox and the writer has not finished with it yet.</summary>
    public volatile bool FrameInFlight;

    /// <summary>Whether a window of this size is acceptable (0 x 0 means hidden).</summary>
    public static bool IsValidSize(int width, int height) =>
        width >= 0 && height >= 0 && width <= MaxDimension && height <= MaxDimension && (long)width * height <= MaxPixels;

    /// <summary>
    /// Resizes the form and the back buffer to the client view; the next frame is full. Returns false, leaving
    /// everything as it was, if the size is out of range or the buffer could not be allocated.
    /// </summary>
    public bool Resize(int width, int height)
    {
        if (!IsValidSize(width, height)) return false;
        if (!SetBuffer(width, height)) return false;
        Form.Resize(width, height);
        Form.InvalidateAll();
        return true;
    }

    /// <summary>Allocates the new buffer first, then swaps and disposes the old one, so a failure keeps the old buffer.</summary>
    bool SetBuffer(int width, int height)
    {
        if (_back != null ? _back.Width == width && _back.Height == height : width == 0 || height == 0) return true;
        SKBitmap? fresh = null;
        if (width > 0 && height > 0)
        {
            try
            {
                fresh = new SKBitmap(FormRenderer.FrameInfo(width, height));
                if (fresh.GetPixels() == 0) { fresh.Dispose(); return false; }
            }
            catch (Exception)
            {
                fresh?.Dispose();
                return false;
            }
        }
        var old = _back;
        _back = fresh;
        old?.Dispose();
        _hasPending = false;
        return true;
    }

    /// <summary>
    /// Dialogs and popups size and place themselves (the client does not drive them): when the form's size or
    /// position changed since the last tick, resizes the back buffer to it and reports it. Panes are sized by the
    /// client and are never touched here.
    /// </summary>
    public GeometryChange TrackFormGeometry()
    {
        if (Role is not (WindowRole.Dialog or WindowRole.Popup)) return default;
        int w = Math.Max(1, Form.Width), h = Math.Max(1, Form.Height);
        bool moved = Form.Left != _lastLeft || Form.Top != _lastTop;
        bool resized = w != Width || h != Height;
        if (!moved && !resized) return default;
        string? warning = null;
        if (resized)
        {
            if (_rejectedSize == (w, h)) resized = false;
            else if (!IsValidSize(w, h) || !SetBuffer(w, h))
            {
                _rejectedSize = (w, h);
                resized = false;
                warning = $"Window {Id} grew to {w} x {h}, which is out of range or could not be allocated; keeping {Width} x {Height}.";
            }
            else
            {
                _rejectedSize = null;
                Form.InvalidateAll();
            }
        }
        if (moved) { _lastLeft = Form.Left; _lastTop = Form.Top; X = Form.Left; Y = Form.Top; }
        return new GeometryChange(moved || resized, warning);
    }

    /// <summary>Renders the form's dirty rectangle into the back buffer and adds it to the pending region.</summary>
    public void Render()
    {
        if (_back == null) return;
        Form.PerformLayout();
        if (!Form.TryTakeDirtyRect(out var dirty)) return;
        // The form may be larger than the buffer (a dialog that resized itself): clip to what exists.
        int x0 = Math.Max(0, dirty.X), y0 = Math.Max(0, dirty.Y);
        int x1 = Math.Min(_back.Width, dirty.X + dirty.Width), y1 = Math.Min(_back.Height, dirty.Y + dirty.Height);
        if (x1 <= x0 || y1 <= y0) return;
        using (var canvas = new SKCanvas(_back))
            FormRenderer.Render(Form, canvas, new Rect(x0, y0, x1 - x0, y1 - y0));
        if (_hasPending)
        {
            _px0 = Math.Min(_px0, x0); _py0 = Math.Min(_py0, y0);
            _px1 = Math.Max(_px1, x1); _py1 = Math.Max(_py1, y1);
        }
        else
        {
            (_px0, _py0, _px1, _py1) = (x0, y0, x1, y1);
            _hasPending = true;
        }
    }

    /// <summary>
    /// Takes the pending region as frames (horizontal bands of at most <see cref="MaxFrameBytes"/> pixel bytes),
    /// or null if there is none.
    /// </summary>
    public List<Protocol.Frame>? TakePending()
    {
        if (!_hasPending || _back == null) return null;
        _hasPending = false;
        int width = _px1 - _px0, height = _py1 - _py0;
        int rowBytes = width * 4;
        int rowsPerBand = Math.Max(1, MaxFrameBytes / rowBytes);
        var frames = new List<Protocol.Frame>();
        for (int y = 0; y < height; y += rowsPerBand)
            frames.Add(Extract(_px0, _py0 + y, width, Math.Min(rowsPerBand, height - y)));
        return frames;
    }

    Protocol.Frame Extract(int x, int y0, int width, int height)
    {
        int rowBytes = width * 4;
        var pixels = new byte[rowBytes * height];
        nint src = _back!.GetPixels();
        int stride = _back.RowBytes;
        for (int y = 0; y < height; y++)
            Marshal.Copy(src + (y0 + y) * stride + x * 4, pixels, y * rowBytes, rowBytes);
        return new Protocol.Frame { WindowId = Id, X = x, Y = y0, Width = width, Height = height, Pixels = pixels };
    }

    /// <summary>The back buffer as tightly packed BGRA bytes (for tests), or null when the window is hidden.</summary>
    public byte[]? SnapshotPixels()
    {
        if (_back == null) return null;
        var all = Extract(0, 0, _back.Width, _back.Height);
        return all.Pixels;
    }

    public void Dispose()
    {
        _back?.Dispose();
        _back = null;
        _hasPending = false;
    }

    /// <summary>The result of <see cref="TrackFormGeometry"/>.</summary>
    public readonly record struct GeometryChange(bool Changed, string? Warning);
}
