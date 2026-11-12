using RadSharp.Drawing;

namespace RadSharp;

public enum ScrollBarKind { Horizontal, Vertical }

/// <summary>
/// The model and geometry of one scroll axis, shared by <see cref="ScrollBar"/> and <see cref="ScrollBox"/>.
/// Positions run from <see cref="Min"/> to <see cref="MaxPosition"/>; <see cref="PageSize"/> is the visible
/// part of the range and sets the thumb length.
/// </summary>
public sealed class ScrollAxis
{
    public int Min { get; set; }
    public int Max { get; set; } = 100;
    public int PageSize { get; set; } = 10;
    public int Position { get; private set; }

    public int MaxPosition => Math.Max(Min, Max - PageSize);

    /// <summary>Sets the position, clamped to the range. Returns true if it changed.</summary>
    public bool SetPosition(int value)
    {
        value = Math.Clamp(value, Min, MaxPosition);
        if (value == Position) return false;
        Position = value;
        return true;
    }

    /// <summary>Re-clamps the position after the range changed. Returns true if it changed.</summary>
    public bool Clamp() => SetPosition(Position);

    /// <summary>Thumb start and length along a track of <paramref name="track"/> pixels.</summary>
    public (int Start, int Length) Thumb(int track, int minThumb)
    {
        int range = Math.Max(1, Max - Min);
        int length = PageSize <= 0 ? minThumb : (int)Math.Round((double)track * Math.Min(PageSize, range) / range);
        length = Math.Clamp(length, Math.Min(minThumb, track), track);
        int travel = track - length;
        int span = MaxPosition - Min;
        int start = span <= 0 ? 0 : (int)Math.Round((double)travel * (Position - Min) / span);
        return (start, length);
    }

    /// <summary>The position that puts the thumb's start at <paramref name="thumbStart"/>.</summary>
    public int PositionAt(int thumbStart, int track, int minThumb)
    {
        var (_, length) = Thumb(track, minThumb);
        int travel = track - length;
        int span = MaxPosition - Min;
        if (travel <= 0 || span <= 0) return Min;
        return Min + (int)Math.Round((double)Math.Clamp(thumbStart, 0, travel) * span / travel);
    }
}

/// <summary>
/// Scroll bar drag and paging logic for one <see cref="ScrollAxis"/>, used by <see cref="ScrollBar"/>,
/// <see cref="ScrollBox"/> and <see cref="ScrollView"/>, and usable by any control that draws its own bars:
/// call <see cref="Press"/>, <see cref="Move"/> and <see cref="Release"/> from its mouse handlers with the
/// pointer's distance along the track, and <see cref="Paint"/> to draw the bar.
/// </summary>
public sealed class ScrollBarDriver(ScrollAxis axis)
{
    int _dragOffset = -1;

    /// <summary>True between a press on the thumb and <see cref="Release"/>.</summary>
    public bool IsDragging => _dragOffset >= 0;

    /// <summary>
    /// A press at <paramref name="along"/> pixels along a track of <paramref name="track"/> pixels: on the thumb it
    /// starts a drag, on the track it pages by <paramref name="largeChange"/>. Returns true if the position changed.
    /// </summary>
    public bool Press(int along, int track, int minThumb, int largeChange)
    {
        var (start, length) = axis.Thumb(track, minThumb);
        if (along >= start && along < start + length)
        {
            _dragOffset = along - start;
            return false;
        }
        return axis.SetPosition(axis.Position + (along < start ? -largeChange : largeChange));
    }

    /// <summary>The pointer moved while dragging the thumb. Returns true if the position changed.</summary>
    public bool Move(int along, int track, int minThumb) =>
        IsDragging && axis.SetPosition(axis.PositionAt(along - _dragOffset, track, minThumb));

    /// <summary>The button was released: ends a drag.</summary>
    public void Release() => _dragOffset = -1;

    /// <summary>Draws a track and thumb in <paramref name="bar"/> (canvas coordinates) in the theme's scroll bar colours.</summary>
    public static void Paint(Canvas canvas, Rect bar, bool vertical, ScrollAxis axis, bool hot, int minThumb)
    {
        var t = canvas.Theme;
        canvas.FillRect(bar, t.ScrollTrack);
        int track = vertical ? bar.Height : bar.Width;
        var (start, length) = axis.Thumb(track, minThumb);
        int inset = Math.Max(1, canvas.Px(2));
        var thumb = vertical
            ? new Rect(bar.X + inset, bar.Y + start, bar.Width - 2 * inset, length)
            : new Rect(bar.X + start, bar.Y + inset, length, bar.Height - 2 * inset);
        float radius = (vertical ? thumb.Width : thumb.Height) / 2f;
        canvas.FillRoundRect(thumb, radius, hot ? t.ScrollThumbHover : t.ScrollThumb);
    }
}

public class ScrollBar : Control
{
    readonly ScrollAxis _axis = new();
    readonly ScrollBarDriver _driver;
    ScrollBarKind _kind;

    public ScrollBar()
    {
        _driver = new ScrollBarDriver(_axis);
        SetDefaultSize(200, 14);
        TabStop = true;
    }

    [Published(Category = "Appearance")] public ScrollBarKind Kind { get => _kind; set { _kind = value; Invalidate(); } }
    [Published(Category = "Behavior")] public int Min { get => _axis.Min; set { _axis.Min = value; Clamp(); } }
    [Published(Category = "Behavior")] public int Max { get => _axis.Max; set { _axis.Max = value; Clamp(); } }
    [Published(Category = "Behavior")] public int PageSize { get => _axis.PageSize; set { _axis.PageSize = Math.Max(0, value); Clamp(); } }
    [Published(Category = "Behavior")] public int SmallChange { get; set; } = 1;
    [Published(Category = "Behavior")] public int LargeChange { get; set; } = 10;

    [Published(Category = "Behavior")]
    public int Position
    {
        get => _axis.Position;
        set { if (_axis.SetPosition(value)) Changed(); }
    }

    /// <summary>Raised when <see cref="Position"/> changes.</summary>
    [Published] public event NotifyEvent? Change;

    void Clamp() { if (_axis.Clamp()) Changed(); else Invalidate(); }

    void Changed()
    {
        Invalidate();
        Change?.Invoke(this);
    }

    bool Vertical => _kind == ScrollBarKind.Vertical;
    int Track => Vertical ? Height : Width;
    int MinThumb => Px(16);

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButton.Left && _driver.Press(Vertical ? e.Y : e.X, Track, MinThumb, LargeChange)) Changed();
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_driver.Move(Vertical ? e.Y : e.X, Track, MinThumb)) Changed();
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _driver.Release();
        Invalidate();
    }

    protected internal override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled) return;
        float delta = Vertical ? e.DeltaY : (e.DeltaX != 0 ? -e.DeltaX : e.DeltaY);
        Position -= (int)Math.Round(delta * SmallChange * 3);
        e.Handled = true;
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Modifiers != KeyModifiers.None) return;
        int? target = e.Key switch
        {
            Key.Left or Key.Up => Position - SmallChange,
            Key.Right or Key.Down => Position + SmallChange,
            Key.PageUp => Position - LargeChange,
            Key.PageDown => Position + LargeChange,
            Key.Home => Min,
            Key.End => _axis.MaxPosition,
            _ => null,
        };
        if (target is { } p) { Position = p; e.Handled = true; }
    }

    protected override void Paint(Canvas canvas)
    {
        var r = new Rect(0, 0, Width, Height);
        ScrollBarDriver.Paint(canvas, r, Vertical, _axis, IsHovered || _driver.IsDragging, MinThumb);
        if (Focused && OwningForm is { ShowFocusCues: true }) canvas.DrawBorder(r, canvas.Theme.FocusRing, canvas.Px(1));
    }

}

/// <summary>
/// A container whose children can extend beyond its visible area. Scroll bars appear when the
/// children's extent is larger than the viewport; the wheel scrolls vertically (Shift: horizontally).
/// </summary>
public class ScrollBox : Control
{
    readonly ScrollAxis _h = new() { PageSize = 0, Max = 0 };
    readonly ScrollAxis _v = new() { PageSize = 0, Max = 0 };
    readonly ScrollBarDriver _hDriver, _vDriver;
    ScrollBarDriver? _active;
    bool _showH, _showV;

    public ScrollBox()
    {
        _hDriver = new ScrollBarDriver(_h);
        _vDriver = new ScrollBarDriver(_v);
        SetDefaultSize(200, 150);
    }

    [Published(Category = "Appearance")] public BorderStyle BorderStyle { get; set; } = BorderStyle.Single;
    [Published(Category = "Appearance")] public Color? Color { get; set; }

    public int ScrollX { get => _h.Position; set { if (_h.SetPosition(value)) Scrolled(); } }
    public int ScrollY { get => _v.Position; set { if (_v.SetPosition(value)) Scrolled(); } }

    public bool HorizontalBarVisible => _showH;
    public bool VerticalBarVisible => _showV;

    int Border => BorderStyle == BorderStyle.Single ? Px(1) : 0;
    int BarSize => Px(12);

    void Scrolled()
    {
        OwningForm?.InvalidateAll();
        Root.PerformLayout();  // refresh absolute positions of the content
    }

    Rect Inner => new(Border, Border, Math.Max(0, Width - 2 * Border), Math.Max(0, Height - 2 * Border));

    /// <summary>The visible content area, in this control's coordinates.</summary>
    public Rect Viewport
    {
        get
        {
            var r = Inner;
            return new Rect(r.X, r.Y, Math.Max(0, r.Width - (_showV ? BarSize : 0)), Math.Max(0, r.Height - (_showH ? BarSize : 0)));
        }
    }

    Rect VBar => new(Inner.X + Inner.Width - BarSize, Inner.Y, BarSize, Viewport.Height);
    Rect HBar => new(Inner.X, Inner.Y + Inner.Height - BarSize, Viewport.Width, BarSize);

    protected override Rect GetClientRect()
    {
        // Children use content coordinates: (0, 0) is the viewport's top-left corner when not scrolled.
        var v = Viewport;
        var p = Padding;
        return new Rect(p.L, p.T, Math.Max(0, v.Width - p.L - p.R), Math.Max(0, v.Height - p.T - p.B));
    }

    protected internal override (int X, int Y) ChildOffset => (Viewport.X - _h.Position, Viewport.Y - _v.Position);
    protected internal override Rect ChildClipRect => Viewport;

    protected override bool AfterArrange()
    {
        int extentW = 0, extentH = 0;
        foreach (var c in Children)
        {
            if (!c.Visible) continue;
            extentW = Math.Max(extentW, c.Left + c.Width + c.Margin.R + Padding.R);
            extentH = Math.Max(extentH, c.Top + c.Height + c.Margin.B + Padding.B);
        }
        var inner = Inner;
        bool needV = extentH > inner.Height;
        bool needH = extentW > inner.Width - (needV ? BarSize : 0);
        if (needH && !needV) needV = extentH > inner.Height - BarSize;

        bool changed = needH != _showH || needV != _showV;
        _showH = needH;
        _showV = needV;
        var view = Viewport;
        _h.Max = Math.Max(extentW, view.Width); _h.PageSize = view.Width;
        _v.Max = Math.Max(extentH, view.Height); _v.PageSize = view.Height;
        _h.Clamp();
        _v.Clamp();
        return changed;
    }

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButton.Left) return;
        if (_showV && Contains(VBar, e.X, e.Y))
        {
            _active = _vDriver;
            if (_vDriver.Press(e.Y - VBar.Y, VBar.Height, Px(16), Viewport.Height)) Scrolled();
        }
        else if (_showH && Contains(HBar, e.X, e.Y))
        {
            _active = _hDriver;
            if (_hDriver.Press(e.X - HBar.X, HBar.Width, Px(16), Viewport.Width)) Scrolled();
        }
        Invalidate();
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_active == _vDriver && _vDriver.Move(e.Y - VBar.Y, VBar.Height, Px(16))) Scrolled();
        else if (_active == _hDriver && _hDriver.Move(e.X - HBar.X, HBar.Width, Px(16))) Scrolled();
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _active?.Release();
        _active = null;
        Invalidate();
    }

    protected internal override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled) return;
        int step = Px(48);
        bool horizontal = e.Modifiers.HasFlag(KeyModifiers.Shift) || (e.DeltaX != 0 && e.DeltaY == 0);
        if (horizontal && _showH)
        {
            float d = e.DeltaX != 0 ? -e.DeltaX : e.DeltaY;
            ScrollX -= (int)Math.Round(d * step);
            e.Handled = true;
        }
        else if (!horizontal && _showV)
        {
            ScrollY -= (int)Math.Round(e.DeltaY * step);
            e.Handled = true;
        }
    }

    /// <summary>Scrolls so that <paramref name="child"/> (a descendant) is fully visible, if possible.</summary>
    public void ScrollInView(Control child)
    {
        Root.PerformLayout();
        var view = Viewport;
        int x = child.AbsoluteLeft - AbsoluteLeft - view.X + _h.Position;  // content coordinates
        int y = child.AbsoluteTop - AbsoluteTop - view.Y + _v.Position;
        int sx = _h.Position, sy = _v.Position;
        if (x < sx) sx = x; else if (x + child.Width > sx + view.Width) sx = x + child.Width - view.Width;
        if (y < sy) sy = y; else if (y + child.Height > sy + view.Height) sy = y + child.Height - view.Height;
        bool moved = _h.SetPosition(sx) | _v.SetPosition(sy);
        if (moved) Scrolled();
    }

    static bool Contains(Rect r, int x, int y) => x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;

    protected override void Paint(Canvas canvas)
    {
        var r = new Rect(0, 0, Width, Height);
        canvas.FillRect(r, Color ?? canvas.Theme.InputBackground);
        if (BorderStyle == BorderStyle.Single) canvas.DrawBorder(r, canvas.Theme.PanelBorder, Border);
        if (_showV) ScrollBarDriver.Paint(canvas, VBar, true, _v, _active == _vDriver, Px(16));
        if (_showH) ScrollBarDriver.Paint(canvas, HBar, false, _h, _active == _hDriver, Px(16));
        if (_showH && _showV)
            canvas.FillRect(new Rect(VBar.X, HBar.Y, BarSize, BarSize), canvas.Theme.ScrollTrack);
    }

}

/// <summary>
/// A base for custom-drawn content larger than the control: it owns the scroll bars, their dragging, the
/// wheel, and the offset, and calls <see cref="PaintContent"/> with the canvas already translated and clipped so
/// content coordinates start at (0, 0). Set <see cref="ContentWidth"/>/<see cref="ContentHeight"/> to the
/// content's size in pixels; bars appear when it exceeds the viewport. Unlike <see cref="ScrollBox"/> it holds
/// no child controls — it is for a grid or canvas that paints its own cells.
/// </summary>
/// <example>
/// <code>
/// sealed class Ledger : ScrollView
/// {
///     public Ledger() { ContentHeight = 5000; }
///     protected override void PaintContent(Canvas canvas, Rect visible)
///     {
///         // Only draw the rows in `visible` (content coordinates).
///         for (int y = visible.Y / 24 * 24; y &lt; visible.Y + visible.Height; y += 24)
///             canvas.DrawText($"Row {y / 24}", EffectiveFont, canvas.Theme.Text, 4, y + 16);
///     }
///     protected override void OnMouseDown(MouseEventArgs e)
///     {
///         base.OnMouseDown(e);                                  // lets the scroll bars take the press
///         if (IsOverScrollBar(e.X, e.Y)) return;
///         var (cx, cy) = ContentPoint(e.X, e.Y);                // control to content coordinates
///     }
/// }
/// </code>
/// </example>
public class ScrollView : Control
{
    readonly ScrollAxis _h = new() { PageSize = 0, Max = 0 };
    readonly ScrollAxis _v = new() { PageSize = 0, Max = 0 };
    readonly ScrollBarDriver _hDriver, _vDriver;
    ScrollBarDriver? _active;
    bool _showH, _showV;
    int _contentWidth, _contentHeight;

    public ScrollView()
    {
        _hDriver = new ScrollBarDriver(_h);
        _vDriver = new ScrollBarDriver(_v);
        SetDefaultSize(200, 150);
    }

    [Published(Category = "Appearance")] public BorderStyle BorderStyle { get; set; } = BorderStyle.Single;
    [Published(Category = "Appearance")] public Color? Color { get; set; }

    /// <summary>The content's width in pixels; the horizontal bar appears when it exceeds the viewport's.</summary>
    public int ContentWidth { get => _contentWidth; set { _contentWidth = Math.Max(0, value); ContentChanged(); } }

    /// <summary>The content's height in pixels; the vertical bar appears when it exceeds the viewport's.</summary>
    public int ContentHeight { get => _contentHeight; set { _contentHeight = Math.Max(0, value); ContentChanged(); } }

    /// <summary>The horizontal scroll position: the content x shown at the viewport's left edge.</summary>
    public int ScrollX { get => _h.Position; set { if (_h.SetPosition(value)) Scrolled(); } }

    /// <summary>The vertical scroll position: the content y shown at the viewport's top edge.</summary>
    public int ScrollY { get => _v.Position; set { if (_v.SetPosition(value)) Scrolled(); } }

    /// <summary>Pixels the wheel scrolls per notch; 0 (the default) means 48 at 100 % scale.</summary>
    public int WheelStep { get; set; }

    public bool HorizontalBarVisible => _showH;
    public bool VerticalBarVisible => _showV;

    /// <summary>True while a scroll bar thumb is being dragged.</summary>
    public bool IsScrolling => _active != null;

    /// <summary>Raised after <see cref="ScrollX"/> or <see cref="ScrollY"/> changed (including when the content shrank).</summary>
    public event NotifyEvent? Scroll;

    int Border => BorderStyle == BorderStyle.Single ? Px(1) : 0;
    int BarSize => Px(12);
    int MinThumb => Px(16);

    Rect Inner => new(Border, Border, Math.Max(0, Width - 2 * Border), Math.Max(0, Height - 2 * Border));

    /// <summary>The visible content area, in this control's coordinates (inside the border, minus any scroll bars).</summary>
    public Rect Viewport
    {
        get
        {
            var r = Inner;
            return new Rect(r.X, r.Y, Math.Max(0, r.Width - (_showV ? BarSize : 0)), Math.Max(0, r.Height - (_showH ? BarSize : 0)));
        }
    }

    Rect VBar => new(Inner.X + Inner.Width - BarSize, Inner.Y, BarSize, Viewport.Height);
    Rect HBar => new(Inner.X, Inner.Y + Inner.Height - BarSize, Viewport.Width, BarSize);

    /// <summary>Converts a point in this control's coordinates (as in mouse events) to content coordinates.</summary>
    public (int X, int Y) ContentPoint(int x, int y) => (x - Viewport.X + _h.Position, y - Viewport.Y + _v.Position);

    /// <summary>True if the point (this control's coordinates) is on a visible scroll bar.</summary>
    public bool IsOverScrollBar(int x, int y) =>
        (_showV && Contains(VBar, x, y)) || (_showH && Contains(HBar, x, y));

    /// <summary>Scrolls the minimum distance that makes <paramref name="area"/> (content coordinates) visible, if it fits.</summary>
    public void ScrollIntoView(Rect area)
    {
        var view = Viewport;
        int sx = _h.Position, sy = _v.Position;
        if (area.X < sx) sx = area.X; else if (area.X + area.Width > sx + view.Width) sx = area.X + area.Width - view.Width;
        if (area.Y < sy) sy = area.Y; else if (area.Y + area.Height > sy + view.Height) sy = area.Y + area.Height - view.Height;
        if (_h.SetPosition(sx) | _v.SetPosition(sy)) Scrolled();
    }

    /// <summary>
    /// Draws the content. The canvas is translated so (0, 0) is the content's top-left corner and clipped to the
    /// viewport; <paramref name="visible"/> is the part of the content in view (content coordinates), so only that
    /// needs drawing. The base class has already drawn the background.
    /// </summary>
    protected virtual void PaintContent(Canvas canvas, Rect visible) { }

    /// <summary>Called after the scroll position changed; the default raises <see cref="Scroll"/>.</summary>
    protected virtual void OnScrolled() => Scroll?.Invoke(this);

    void ContentChanged()
    {
        UpdateBars();   // at once, so scrolling right after a content change isn't clamped to the old size
        InvalidateLayout();
        Invalidate();
    }

    protected override void OnResize()
    {
        UpdateBars();   // before Resized/overrides run, so they see the new viewport and scroll range
        base.OnResize();
    }

    void Scrolled()
    {
        Invalidate();
        OnScrolled();
    }

    protected override bool AfterArrange() => UpdateBars();

    /// <summary>Recomputes which bars show and the scroll ranges from the content and control size; true if a bar appeared or went.</summary>
    bool UpdateBars()
    {
        var inner = Inner;
        bool needV = _contentHeight > inner.Height;
        bool needH = _contentWidth > inner.Width - (needV ? BarSize : 0);
        if (needH && !needV) needV = _contentHeight > inner.Height - BarSize;

        bool changed = needH != _showH || needV != _showV;
        _showH = needH;
        _showV = needV;
        var view = Viewport;
        _h.Max = Math.Max(_contentWidth, view.Width); _h.PageSize = view.Width;
        _v.Max = Math.Max(_contentHeight, view.Height); _v.PageSize = view.Height;
        bool moved = _h.Clamp() | _v.Clamp();
        if (moved || changed) Invalidate();
        if (moved) OnScrolled();
        return changed;
    }

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButton.Left) return;
        if (_showV && Contains(VBar, e.X, e.Y))
        {
            _active = _vDriver;
            if (_vDriver.Press(e.Y - VBar.Y, VBar.Height, MinThumb, Viewport.Height)) Scrolled();
        }
        else if (_showH && Contains(HBar, e.X, e.Y))
        {
            _active = _hDriver;
            if (_hDriver.Press(e.X - HBar.X, HBar.Width, MinThumb, Viewport.Width)) Scrolled();
        }
        Invalidate();
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_active == _vDriver && _vDriver.Move(e.Y - VBar.Y, VBar.Height, MinThumb)) Scrolled();
        else if (_active == _hDriver && _hDriver.Move(e.X - HBar.X, HBar.Width, MinThumb)) Scrolled();
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _active?.Release();
        _active = null;
        Invalidate();
    }

    protected internal override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled) return;
        int step = WheelStep > 0 ? WheelStep : Px(48);
        bool horizontal = e.Modifiers.HasFlag(KeyModifiers.Shift) || (e.DeltaX != 0 && e.DeltaY == 0);
        if (horizontal && _showH)
        {
            float d = e.DeltaX != 0 ? -e.DeltaX : e.DeltaY;
            ScrollX -= (int)Math.Round(d * step);
            e.Handled = true;
        }
        else if (!horizontal && _showV)
        {
            ScrollY -= (int)Math.Round(e.DeltaY * step);
            e.Handled = true;
        }
    }

    static bool Contains(Rect r, int x, int y) => x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;

    protected sealed override void Paint(Canvas canvas)
    {
        var r = new Rect(0, 0, Width, Height);
        canvas.FillRect(r, Color ?? canvas.Theme.InputBackground);

        var view = Viewport;
        if (view.Width > 0 && view.Height > 0)
        {
            canvas.Save();
            canvas.ClipRect(view);
            canvas.Translate(view.X - _h.Position, view.Y - _v.Position);
            PaintContent(canvas, new Rect(_h.Position, _v.Position, view.Width, view.Height));
            canvas.Restore();
        }

        if (BorderStyle == BorderStyle.Single) canvas.DrawBorder(r, canvas.Theme.PanelBorder, Border);
        if (_showV) ScrollBarDriver.Paint(canvas, VBar, true, _v, _active == _vDriver, MinThumb);
        if (_showH) ScrollBarDriver.Paint(canvas, HBar, false, _h, _active == _hDriver, MinThumb);
        if (_showH && _showV)
            canvas.FillRect(new Rect(VBar.X, HBar.Y, BarSize, BarSize), canvas.Theme.ScrollTrack);
    }
}
