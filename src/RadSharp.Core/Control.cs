using System.Text.Json;

namespace RadSharp;

/// <summary>
/// Base class of every control: a node in the retained control tree with bounds, Align + Anchors
/// layout (spec, Appendix B) and published properties.
/// </summary>
public class Control
{
    readonly List<Control> _children = [];

    int _left, _top, _width = 100, _height = 30;
    Align _align;
    Anchors _anchors = Anchors.Left | Anchors.Top;
    Thickness _margin, _padding;
    int _minWidth, _minHeight, _maxWidth, _maxHeight;
    bool _visible = true;

    // Anchor capture: design rect and parent size (Appendix B, step 4).
    bool _captured;
    int _aL, _aT, _aW, _aH, _pw0, _ph0;

    bool _needsArrange = true;
    Rect _remaining;

    public string Name { get; set; } = "";
    public Control? Parent { get; private set; }
    public IReadOnlyList<Control> Children => _children;
    public Control Root => Parent?.Root ?? this;

    /// <summary>Event handler names by event property (<c>"onClick" → "btnSave_Click"</c>).</summary>
    public Dictionary<string, string> EventHandlers { get; } = [];

    public bool Enabled { get; set; } = true;
    public bool TabStop { get; set; }
    public int TabOrder { get; set; } = -1;
    public string Hint { get; set; } = "";

    // ---- Tree ----

    public void Add(Control child) => Insert(_children.Count, child);

    public void Insert(int index, Control child)
    {
        ArgumentNullException.ThrowIfNull(child);
        for (var p = this; p != null; p = p.Parent)
            if (p == child) throw new InvalidOperationException("A control can't be added to itself or its descendants.");
        child.Parent?.Remove(child);
        _children.Insert(index, child);
        child.Parent = this;
        child._captured = false;
        InvalidateLayout();
    }

    public bool Remove(Control child)
    {
        if (!_children.Remove(child)) return false;
        child.Parent = null;
        InvalidateLayout();
        return true;
    }

    /// <summary>Finds a control by name in this subtree (including this control).</summary>
    public Control? FindControl(string name)
    {
        if (Name == name) return this;
        foreach (var c in _children)
            if (c.FindControl(name) is { } found) return found;
        return null;
    }

    // ---- Bounds ----

    public int Left { get => _left; set => SetBounds(value, _top, _width, _height); }
    public int Top { get => _top; set => SetBounds(_left, value, _width, _height); }
    public int Width { get => _width; set => SetBounds(_left, _top, value, _height); }
    public int Height { get => _height; set => SetBounds(_left, _top, _width, value); }

    /// <summary>Bounds relative to the parent.</summary>
    public Rect Bounds => new(_left, _top, _width, _height);

    public int AbsoluteLeft { get; private set; }
    public int AbsoluteTop { get; private set; }

    /// <summary>
    /// Moves or resizes the control from code. Re-captures the anchors, so the new rect becomes the
    /// design rect at the parent's current size.
    /// </summary>
    public void SetBounds(int left, int top, int width, int height)
    {
        ApplyBounds(left, top, Math.Max(0, width), Math.Max(0, height));
        Recapture();
        Parent?.InvalidateLayout();
    }

    /// <summary>
    /// Captures the current rect and parent size as the anchor reference now, so a later parent
    /// resize is measured from this moment. Without a parent, capture waits for the first layout.
    /// </summary>
    void Recapture()
    {
        if (Parent is not { } p) { _captured = false; return; }
        _aL = _left; _aT = _top; _aW = _width; _aH = _height;
        _pw0 = p._width; _ph0 = p._height;
        _captured = true;
    }

    /// <summary>Sets a class default size without triggering layout; for constructors.</summary>
    protected void SetDefaultSize(int width, int height)
    {
        _width = width;
        _height = height;
    }

    /// <summary>Stores bounds computed by layout (no re-capture, no parent invalidation).</summary>
    void ApplyBounds(int left, int top, int width, int height)
    {
        _left = left;
        _top = top;
        if (width == _width && height == _height) return;
        _width = width;
        _height = height;
        _needsArrange = true;
        OnResize();
    }

    protected virtual void OnResize() { }

    // ---- Layout properties ----

    public Align Align
    {
        get => _align;
        set { if (_align == value) return; _align = value; Recapture(); Parent?.InvalidateLayout(); }
    }

    public Anchors Anchors
    {
        get => _anchors;
        set { if (_anchors == value) return; _anchors = value; Recapture(); Parent?.InvalidateLayout(); }
    }

    public Thickness Margin
    {
        get => _margin;
        set { if (_margin == value) return; _margin = value; Parent?.InvalidateLayout(); }
    }

    public Thickness Padding
    {
        get => _padding;
        set { if (_padding == value) return; _padding = value; InvalidateLayout(); }
    }

    public int MinWidth { get => _minWidth; set { _minWidth = Math.Max(0, value); Parent?.InvalidateLayout(); } }
    public int MinHeight { get => _minHeight; set { _minHeight = Math.Max(0, value); Parent?.InvalidateLayout(); } }
    public int MaxWidth { get => _maxWidth; set { _maxWidth = Math.Max(0, value); Parent?.InvalidateLayout(); } }
    public int MaxHeight { get => _maxHeight; set { _maxHeight = Math.Max(0, value); Parent?.InvalidateLayout(); } }

    public bool Visible
    {
        get => _visible;
        set { if (_visible == value) return; _visible = value; Parent?.InvalidateLayout(); }
    }

    // ---- Layout ----

    /// <summary>Marks this control's children for re-arranging on the next layout pass.</summary>
    public void InvalidateLayout() => _needsArrange = true;

    /// <summary>
    /// The area aligned children are placed in, relative to this control: its rect minus padding.
    /// Containers can override it (e.g. a tab control excludes its tab strip).
    /// </summary>
    protected virtual Rect GetClientRect() => new(
        _padding.L,
        _padding.T,
        Math.Max(0, _width - _padding.L - _padding.R),
        Math.Max(0, _height - _padding.T - _padding.B));

    /// <summary>
    /// The client rect left after the last arrange placed the top/bottom/left/right children,
    /// before client children. Splitters use it to limit how far they can grow their target.
    /// </summary>
    public Rect RemainingClientRect => _remaining;

    /// <summary>Lays out this subtree: arranges dirty containers top-down and refreshes absolute positions.</summary>
    public void PerformLayout()
    {
        if (_needsArrange)
        {
            _needsArrange = false;
            ArrangeChildren();
        }
        foreach (var c in _children)
        {
            if (!c._visible) continue;
            c.AbsoluteLeft = AbsoluteLeft + c._left;
            c.AbsoluteTop = AbsoluteTop + c._top;
            c.PerformLayout();
        }
    }

    int ConstrainWidth(int w)
    {
        if (_maxWidth > 0 && w > _maxWidth) w = _maxWidth;
        if (w < _minWidth) w = _minWidth;
        return Math.Max(0, w);
    }

    int ConstrainHeight(int h)
    {
        if (_maxHeight > 0 && h > _maxHeight) h = _maxHeight;
        if (h < _minHeight) h = _minHeight;
        return Math.Max(0, h);
    }

    void ArrangeChildren()
    {
        var r = GetClientRect();
        int rx = r.X, ry = r.Y, rw = r.Width, rh = r.Height;

        foreach (var c in _children)
        {
            if (!c._visible || c._align != Align.Top) continue;
            var m = c._margin;
            int h = c.ConstrainHeight(c._height);
            c.ApplyBounds(rx + m.L, ry + m.T, c.ConstrainWidth(rw - m.L - m.R), h);
            int used = h + m.T + m.B;
            ry += used;
            rh = Math.Max(0, rh - used);
        }
        foreach (var c in _children)
        {
            if (!c._visible || c._align != Align.Bottom) continue;
            var m = c._margin;
            int h = c.ConstrainHeight(c._height);
            c.ApplyBounds(rx + m.L, ry + rh - m.B - h, c.ConstrainWidth(rw - m.L - m.R), h);
            rh = Math.Max(0, rh - (h + m.T + m.B));
        }
        foreach (var c in _children)
        {
            if (!c._visible || c._align != Align.Left) continue;
            var m = c._margin;
            int w = c.ConstrainWidth(c._width);
            c.ApplyBounds(rx + m.L, ry + m.T, w, c.ConstrainHeight(rh - m.T - m.B));
            int used = w + m.L + m.R;
            rx += used;
            rw = Math.Max(0, rw - used);
        }
        foreach (var c in _children)
        {
            if (!c._visible || c._align != Align.Right) continue;
            var m = c._margin;
            int w = c.ConstrainWidth(c._width);
            c.ApplyBounds(rx + rw - m.R - w, ry + m.T, w, c.ConstrainHeight(rh - m.T - m.B));
            rw = Math.Max(0, rw - (w + m.L + m.R));
        }

        _remaining = new Rect(rx, ry, rw, rh);

        foreach (var c in _children)
        {
            if (!c._visible || c._align != Align.Client) continue;
            var m = c._margin;
            c.ApplyBounds(rx + m.L, ry + m.T,
                c.ConstrainWidth(rw - m.L - m.R), c.ConstrainHeight(rh - m.T - m.B));
        }
        foreach (var c in _children)
        {
            if (!c._visible || c._align != Align.None) continue;
            LayoutAnchored(c);
        }
    }

    void LayoutAnchored(Control c)
    {
        if (!c._captured) c.Recapture();
        var (x, w) = AnchorAxis(c._anchors.HasFlag(Anchors.Left), c._anchors.HasFlag(Anchors.Right),
            c._aL, c._aW, c._pw0, _width);
        var (y, h) = AnchorAxis(c._anchors.HasFlag(Anchors.Top), c._anchors.HasFlag(Anchors.Bottom),
            c._aT, c._aH, c._ph0, _height);
        c.ApplyBounds(x, y, c.ConstrainWidth(w), c.ConstrainHeight(h));
    }

    /// <summary>One axis of the anchor table in Appendix B.</summary>
    static (int Pos, int Size) AnchorAxis(bool near, bool far, int a, int size, int p0, int p)
    {
        int d = p - p0;
        if (near && far) return (a, size + d);
        if (far) return (a + d, size);
        if (near || p0 == 0) return (a, size);  // p0 == 0: zero-size guard, behave as near-only
        return (LayoutMath.Round((a + size / 2.0) * p / p0 - size / 2.0), size);
    }

    // ---- Published properties (hand-written until the source generator exists, roadmap M4) ----

    /// <summary>
    /// Sets a published property from its form-file JSON value. Returns false if this class doesn't
    /// publish a property with that name. Throws <see cref="FormatException"/> for a wrong value type.
    /// </summary>
    public virtual bool ReadProperty(string name, JsonElement value)
    {
        switch (name)
        {
            case "left": Left = JsonValues.Int(value); return true;
            case "top": Top = JsonValues.Int(value); return true;
            case "width": Width = JsonValues.Int(value); return true;
            case "height": Height = JsonValues.Int(value); return true;
            case "align": Align = JsonValues.Enum<Align>(value); return true;
            case "anchors": Anchors = JsonValues.AnchorSet(value); return true;
            case "margin": Margin = JsonValues.Thickness(value); return true;
            case "padding": Padding = JsonValues.Thickness(value); return true;
            case "minWidth": MinWidth = JsonValues.Int(value); return true;
            case "minHeight": MinHeight = JsonValues.Int(value); return true;
            case "maxWidth": MaxWidth = JsonValues.Int(value); return true;
            case "maxHeight": MaxHeight = JsonValues.Int(value); return true;
            case "visible": Visible = JsonValues.Bool(value); return true;
            case "enabled": Enabled = JsonValues.Bool(value); return true;
            case "tabStop": TabStop = JsonValues.Bool(value); return true;
            case "tabOrder": TabOrder = JsonValues.Int(value); return true;
            case "hint": Hint = JsonValues.String(value); return true;
        }
        if (IsEventName(name))
        {
            EventHandlers[name] = JsonValues.String(value);
            return true;
        }
        return false;
    }

    static bool IsEventName(string name) => name.Length > 2 && name.StartsWith("on", StringComparison.Ordinal) && char.IsUpper(name[2]);
}
