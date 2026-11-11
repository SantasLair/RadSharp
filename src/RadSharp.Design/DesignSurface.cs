using RadSharp.Drawing;

namespace RadSharp.Design;

/// <summary>
/// The design canvas: shows a <see cref="DesignDocument"/>'s form (a live instance that never receives
/// input) magnified to the designer's scale, and turns mouse and keyboard input into design actions:
/// select, move, resize, rubber-band select, create from the palette, nudge, delete, copy and paste.
/// Positions snap to a grid and to the edges of sibling controls (with guide lines).
/// </summary>
public class DesignSurface : Control
{
    enum Mode { None, Move, Resize, ResizeForm, Rubber, Create }
    enum Handle { None, N, NE, E, SE, S, SW, W, NW }

    DesignDocument? _doc;
    Mode _mode;
    Handle _handle;
    (int X, int Y) _start;                 // design coordinates of the press
    readonly Dictionary<Control, Rect> _startBounds = [];
    bool _changed;
    Rect? _rubber;                         // design coordinates
    Control? _createParent;
    readonly List<(bool Vertical, int At, int From, int To)> _guides = [];  // design (form) coordinates
    int _scrollX, _scrollY;

    public DesignSurface()
    {
        SetDefaultSize(400, 300);
        TabStop = true;
    }

    public DesignDocument? Document
    {
        get => _doc;
        set
        {
            if (_doc != null) { _doc.Changed -= Invalidate; _doc.SelectionChanged -= Invalidate; _doc.FormReplaced -= Invalidate; }
            _doc = value;
            if (_doc != null) { _doc.Changed += Invalidate; _doc.SelectionChanged += Invalidate; _doc.FormReplaced += Invalidate; }
            Invalidate();
        }
    }

    /// <summary>Snap positions and sizes to this grid (design pixels); 0 turns snapping off.</summary>
    [Published(Category = "Behavior")] public int GridSize { get; set; } = 8;
    [Published(Category = "Appearance")] public bool ShowGrid { get; set; } = true;

    /// <summary>
    /// Delphi's Lock Controls: while true, existing controls can still be selected and edited in the
    /// inspector, but can't be dragged, resized, or nudged with the keyboard. The form's own resize grip is
    /// unaffected (only controls are "locked"), and creating a new control from the palette still works.
    /// </summary>
    [Published(Category = "Behavior")] public bool Locked { get; set; }

    /// <summary>The class the next press creates (set by the component palette); cleared after creating.</summary>
    public ComponentClass? PendingClass { get; set; }

    /// <summary>Raised after a control was created from the palette.</summary>
    public event Action<Control>? Created;

    /// <summary>Raised when a control (or the form) is double-clicked: the designer opens its default event handler.</summary>
    public event Action<Control>? DoubleClicked;

    /// <summary>
    /// Raised on a right-click, after the selection was updated as a left click would (selects the control
    /// under the cursor if it wasn't already part of the selection, clears the selection over empty canvas).
    /// The designer builds and shows its context menu at these surface-local coordinates.
    /// </summary>
    public event Action<int, int>? ContextMenuRequested;

    double Zoom => UiScale;
    int OriginX => Px(16) - _scrollX;
    int OriginY => Px(16) - _scrollY;

    /// <summary>Converts design coordinates (on the designed form) to this control's coordinates.</summary>
    public (int X, int Y) DesignToScreen(int x, int y) =>
        (OriginX + (int)Math.Round(x * Zoom), OriginY + (int)Math.Round(y * Zoom));

    (int X, int Y) ScreenToDesign(int x, int y) =>
        ((int)Math.Floor((x - OriginX) / Zoom), (int)Math.Floor((y - OriginY) / Zoom));

    Rect ScreenRect(Rect design)
    {
        var (x, y) = DesignToScreen(design.X, design.Y);
        return new Rect(x, y, (int)Math.Round(design.Width * Zoom), (int)Math.Round(design.Height * Zoom));
    }

    /// <summary>A control's rectangle in design (form) coordinates.</summary>
    public static Rect DesignRect(Control c) =>
        c is Component ? new Rect(c.Left, c.Top, ComponentIconSize, ComponentIconSize) : new Rect(c.AbsoluteLeft, c.AbsoluteTop, c.Width, c.Height);

    const int ComponentIconSize = 28;

    int Snap(int v, bool free) => free || GridSize <= 1 ? v : (int)Math.Round((double)v / GridSize, MidpointRounding.AwayFromZero) * GridSize;

    // ---- Hit testing ----

    /// <summary>The designed control at design coordinates: component icons first, then the deepest control.</summary>
    public Control? ControlAt(int x, int y)
    {
        if (_doc == null) return null;
        var form = _doc.Form;
        foreach (var c in form.Descendants().OfType<Component>().Reverse())
        {
            var r = DesignRect(c);
            if (x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height) return c;
        }
        if (x < 0 || y < 0 || x >= form.Width || y >= form.Height) return null;
        return form.HitTestForm(x, y);
    }

    IEnumerable<(Handle Handle, Rect Rect)> HandleRects(Control c)
    {
        var r = ScreenRect(DesignRect(c));
        int s = Px(7), h = s / 2;
        int l = r.X - h, t = r.Y - h, cx = r.X + r.Width / 2 - h, cy = r.Y + r.Height / 2 - h, rr = r.X + r.Width - h, b = r.Y + r.Height - h;
        var free = ResizableEdges(c);
        if (free.Top && free.Left) yield return (Handle.NW, new Rect(l, t, s, s));
        if (free.Top) yield return (Handle.N, new Rect(cx, t, s, s));
        if (free.Top && free.Right) yield return (Handle.NE, new Rect(rr, t, s, s));
        if (free.Right) yield return (Handle.E, new Rect(rr, cy, s, s));
        if (free.Bottom && free.Right) yield return (Handle.SE, new Rect(rr, b, s, s));
        if (free.Bottom) yield return (Handle.S, new Rect(cx, b, s, s));
        if (free.Bottom && free.Left) yield return (Handle.SW, new Rect(l, b, s, s));
        if (free.Left) yield return (Handle.W, new Rect(l, cy, s, s));
    }

    /// <summary>Which edges can be dragged: aligned controls only resize along the axis their align leaves free.</summary>
    static (bool Left, bool Top, bool Right, bool Bottom) ResizableEdges(Control c) => c is Component ? default : c.Align switch
    {
        Align.Top => (false, false, false, true),
        Align.Bottom => (false, true, false, false),
        Align.Left => (false, false, true, false),
        Align.Right => (true, false, false, false),
        Align.Client => default,
        _ => (true, true, true, true),
    };

    Handle HandleAt(int x, int y)
    {
        if (_doc?.Selection.Count != 1 || Locked) return Handle.None;
        foreach (var (h, r) in HandleRects(_doc.Primary))
            if (x >= r.X - Px(2) && y >= r.Y - Px(2) && x < r.X + r.Width + Px(2) && y < r.Y + r.Height + Px(2)) return h;
        return Handle.None;
    }

    bool AtFormResizeHandle(int x, int y)
    {
        if (_doc == null) return false;
        var r = ScreenRect(new Rect(0, 0, _doc.Form.Width, _doc.Form.Height));
        return Math.Abs(x - (r.X + r.Width)) <= Px(6) && Math.Abs(y - (r.Y + r.Height)) <= Px(6);
    }

    /// <summary>The deepest container at a design point that can hold a new <paramref name="cls"/>.</summary>
    Control ContainerAt(int x, int y, ComponentClass cls)
    {
        var form = _doc!.Form;
        for (Control? c = form.HitTestForm(Math.Clamp(x, 0, form.Width - 1), Math.Clamp(y, 0, form.Height - 1)); c != null; c = c.Parent)
            if (DesignDocument.AcceptsChild(c, cls)) return c;
        return form;
    }

    /// <summary>
    /// Creates a control of <paramref name="cls"/> at a surface-local point, default-sized and grid-snapped,
    /// same placement rule as a plain click from the palette (<c>Mode.Create</c> in <see cref="OnMouseUp"/>).
    /// For drag-and-drop from the palette: the surface itself doesn't know where a drag's class comes from
    /// (it has no idea what a <c>TreeView</c> or a palette even is) — the designer resolves that from
    /// <c>DragEventArgs.Source</c> and calls this. Returns null if there's no open document.
    /// </summary>
    public Control? CreateAt(ComponentClass cls, int screenX, int screenY)
    {
        if (_doc == null) return null;
        var (dx, dy) = ScreenToDesign(screenX, screenY);
        int sx = Snap(dx, free: false), sy = Snap(dy, free: false);
        var parent = ContainerAt(sx, sy, cls);
        var (ox, oy) = ParentOrigin(parent);
        var control = _doc.Create(cls, typeof(Component).IsAssignableFrom(cls.Type) ? _doc.Form : parent, sx - ox, sy - oy);
        Created?.Invoke(control);
        return control;
    }

    // ---- Mouse ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_doc == null) return;
        if (e.Button == MouseButton.Right) { HandleContextMenu(e); return; }
        if (e.Button != MouseButton.Left) return;
        var (dx, dy) = ScreenToDesign(e.X, e.Y);
        _start = (dx, dy);
        _changed = false;
        _guides.Clear();
        bool free = e.Modifiers.HasFlag(KeyModifiers.Alt);

        if (PendingClass is { } cls)
        {
            _mode = Mode.Create;
            _createParent = ContainerAt(dx, dy, cls);
            _start = (Snap(dx, free), Snap(dy, free));
            _rubber = new Rect(_start.X, _start.Y, 0, 0);
            return;
        }
        if (HandleAt(e.X, e.Y) is var h and not Handle.None)
        {
            _mode = Mode.Resize;
            _handle = h;
            Remember(_doc.Primary);
            return;
        }
        if (AtFormResizeHandle(e.X, e.Y))
        {
            _mode = Mode.ResizeForm;
            _startBounds[_doc.Form] = _doc.Form.Bounds;
            return;
        }

        var hit = ControlAt(dx, dy);
        if (e.Clicks == 2 && (hit ?? _doc.Form) is { } target)
        {
            _mode = Mode.None;
            DoubleClicked?.Invoke(target);
            return;
        }
        if (hit is PageControl pages && pages.TabRects() is var tabs)
        {
            // Clicking a tab in the designer switches the page shown.
            int lx = dx - pages.AbsoluteLeft, ly = dy - pages.AbsoluteTop;
            for (int i = 0; i < tabs.Count; i++)
                if (lx >= tabs[i].X && lx < tabs[i].X + tabs[i].Width && ly < tabs[i].Height) { pages.ActivePageIndex = i; _doc.Form.PerformLayout(); }
        }
        if (hit == null || hit == _doc.Form)
        {
            if (!e.Modifiers.HasFlag(KeyModifiers.Shift)) _doc.Select(null);
            _mode = Mode.Rubber;
            _rubber = new Rect(dx, dy, 0, 0);
            return;
        }
        if (e.Modifiers.HasFlag(KeyModifiers.Shift)) { _doc.Select(hit, add: true); _mode = Mode.None; return; }
        if (!_doc.IsSelected(hit)) _doc.Select(hit);
        if (Locked) { _mode = Mode.None; return; }   // still selectable and editable, just not draggable
        _mode = Mode.Move;
        _startBounds.Clear();
        foreach (var c in _doc.Selection) Remember(c);
    }

    void Remember(Control c) => _startBounds[c] = c.Bounds;

    /// <summary>Selects like a left click would (without starting a move), then raises <see cref="ContextMenuRequested"/>.</summary>
    void HandleContextMenu(MouseEventArgs e)
    {
        if (_doc == null) return;
        var (dx, dy) = ScreenToDesign(e.X, e.Y);
        var hit = ControlAt(dx, dy);
        if (hit == null || hit == _doc.Form) _doc.Select(null);
        else if (!_doc.IsSelected(hit)) _doc.Select(hit);
        Invalidate();
        ContextMenuRequested?.Invoke(e.X, e.Y);
    }

    void BeginChange()
    {
        if (_changed) return;
        _changed = true;
        _doc!.Snapshot();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_doc == null) return;
        var (dx, dy) = ScreenToDesign(e.X, e.Y);
        bool free = e.Modifiers.HasFlag(KeyModifiers.Alt);
        int ddx = dx - _start.X, ddy = dy - _start.Y;

        switch (_mode)
        {
            case Mode.None:
                Cursor = HandleAt(e.X, e.Y) switch
                {
                    Handle.N or Handle.S => Cursor.SizeNS,
                    Handle.E or Handle.W => Cursor.SizeWE,
                    Handle.NW or Handle.SE => Cursor.SizeNWSE,
                    Handle.NE or Handle.SW => Cursor.SizeNESW,
                    _ => AtFormResizeHandle(e.X, e.Y) ? Cursor.SizeNWSE : PendingClass != null ? Cursor.Crosshair : Cursor.Default,
                };
                return;
            case Mode.Move:
                if (!_changed && Math.Abs(ddx) < 3 && Math.Abs(ddy) < 3) return;
                MoveSelection(ddx, ddy, free);
                break;
            case Mode.Resize:
                ResizePrimary(ddx, ddy, free);
                break;
            case Mode.ResizeForm:
            {
                BeginChange();
                var b = _startBounds[_doc.Form];
                int w = Math.Max(Px(1), Snap(b.Width + ddx, free)), h = Math.Max(Px(1), Snap(b.Height + ddy, free));
                _doc.Form.Resize(Math.Max(64, w), Math.Max(48, h));
                _doc.Form.DesignWidth = _doc.Form.Width;
                _doc.Form.DesignHeight = _doc.Form.Height;
                break;
            }
            case Mode.Rubber or Mode.Create:
            {
                int x2 = _mode == Mode.Create ? Snap(dx, free) : dx, y2 = _mode == Mode.Create ? Snap(dy, free) : dy;
                _rubber = new Rect(Math.Min(_start.X, x2), Math.Min(_start.Y, y2), Math.Abs(x2 - _start.X), Math.Abs(y2 - _start.Y));
                break;
            }
        }
        _doc.Form.PerformLayout();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_doc == null) return;
        switch (_mode)
        {
            case Mode.Rubber when _rubber is { } band:
            {
                // Select the form's direct children the band touches.
                var hits = _doc.Form.Children.Where(c => c.IsShown && Intersects(DesignRect(c), band)).ToList();
                if (hits.Count > 0 && band.Width + band.Height > 2) _doc.SelectMany(hits);
                break;
            }
            case Mode.Create when _rubber is { } r && PendingClass is { } cls && _createParent is { } parent:
            {
                bool sized = r.Width >= 4 && r.Height >= 4;
                var (ox, oy) = ParentOrigin(parent);
                var created = cls.Create();  // for its default size only
                var control = _doc.Create(cls, typeof(Component).IsAssignableFrom(cls.Type) ? _doc.Form : parent,
                    r.X - ox, r.Y - oy, sized ? r.Width : created.Width, sized ? r.Height : created.Height);
                if (!e.Modifiers.HasFlag(KeyModifiers.Shift)) PendingClass = null;
                Created?.Invoke(control);
                break;
            }
        }
        if (_changed) _doc.NotifyChanged();
        _mode = Mode.None;
        _rubber = null;
        _guides.Clear();
        _startBounds.Clear();
        Invalidate();
    }

    /// <summary>The design position of a container's child coordinate origin.</summary>
    static (int X, int Y) ParentOrigin(Control parent)
    {
        if (parent is Component) return (0, 0);
        var (ox, oy) = parent.ContentOffset;
        return (parent.AbsoluteLeft + ox, parent.AbsoluteTop + oy);
    }

    static bool Intersects(Rect a, Rect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    void MoveSelection(int ddx, int ddy, bool free)
    {
        var movable = _startBounds.Keys.Where(c => c.Align == Align.None).ToList();
        if (movable.Count == 0) return;
        BeginChange();
        _guides.Clear();

        // Snap the primary control; move the others by the same amount.
        var lead = movable.Contains(_doc!.Primary) ? _doc.Primary : movable[0];
        var s = _startBounds[lead];
        int nx = s.X + ddx, ny = s.Y + ddy;
        var (gx, gy) = movable.Count == 1 && !free ? GuideSnap(lead, nx, ny, s.Width, s.Height) : (null, null);
        nx = gx ?? Snap(nx, free);
        ny = gy ?? Snap(ny, free);
        int mx = nx - s.X, my = ny - s.Y;
        foreach (var c in movable)
        {
            var b = _startBounds[c];
            c.SetBounds(b.X + mx, b.Y + my, b.Width, b.Height);
        }
    }

    /// <summary>
    /// Snaps a moving control's edges or centre to its siblings' edges or centres (and its parent's
    /// edges) within a few pixels, recording guide lines. Returns the snapped left/top, or null per axis.
    /// </summary>
    (int? X, int? Y) GuideSnap(Control c, int x, int y, int w, int h)
    {
        var parent = c.Parent;
        if (parent == null) return (null, null);
        int threshold = Math.Max(2, (int)Math.Round(5 / Zoom));
        var xs = new List<int> { 0, parent.Width };
        var ys = new List<int> { 0, parent.Height };
        foreach (var s in parent.Children)
        {
            if (s == c || !s.IsShown || s is Component) continue;
            xs.AddRange([s.Left, s.Left + s.Width, s.Left + s.Width / 2]);
            ys.AddRange([s.Top, s.Top + s.Height, s.Top + s.Height / 2]);
        }
        var (px, py) = ParentOrigin(parent);
        int? sx = Best(xs, [x, x + w, x + w / 2], threshold, out int gxAt);
        int? sy = Best(ys, [y, y + h, y + h / 2], threshold, out int gyAt);
        int? rx = sx is { } dxs ? x + dxs : null, ry = sy is { } dys ? y + dys : null;
        if (rx != null) _guides.Add((true, px + gxAt, py, py + parent.Height));
        if (ry != null) _guides.Add((false, py + gyAt, px, px + parent.Width));
        return (rx, ry);
    }

    /// <summary>The smallest shift (within the threshold) that lines one of <paramref name="edges"/> up with a target.</summary>
    static int? Best(List<int> targets, int[] edges, int threshold, out int at)
    {
        int? best = null;
        at = 0;
        foreach (var t in targets)
            foreach (var e in edges)
            {
                int d = t - e;
                if (Math.Abs(d) <= threshold && (best == null || Math.Abs(d) < Math.Abs(best.Value))) { best = d; at = t; }
            }
        return best;
    }

    void ResizePrimary(int ddx, int ddy, bool free)
    {
        var c = _doc!.Primary;
        if (!_startBounds.TryGetValue(c, out var b)) return;
        BeginChange();
        int l = b.X, t = b.Y, r = b.X + b.Width, btm = b.Y + b.Height;
        if (_handle is Handle.W or Handle.NW or Handle.SW) l = Snap(b.X + ddx, free);
        if (_handle is Handle.E or Handle.NE or Handle.SE) r = Snap(r + ddx, free);
        if (_handle is Handle.N or Handle.NW or Handle.NE) t = Snap(b.Y + ddy, free);
        if (_handle is Handle.S or Handle.SW or Handle.SE) btm = Snap(btm + ddy, free);
        const int min = 4;
        if (r - l < min) { if (_handle is Handle.W or Handle.NW or Handle.SW) l = r - min; else r = l + min; }
        if (btm - t < min) { if (_handle is Handle.N or Handle.NW or Handle.NE) t = btm - min; else btm = t + min; }
        if (c.Align == Align.None) c.SetBounds(l, t, r - l, btm - t);
        else if (c.Align is Align.Top or Align.Bottom) c.Height = btm - t;
        else if (c.Align is Align.Left or Align.Right) c.Width = r - l;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled || _doc == null) return;
        int step = (int)Math.Round(e.DeltaY * Px(48));
        if (e.Modifiers.HasFlag(KeyModifiers.Shift)) _scrollX = Math.Clamp(_scrollX - step, 0, MaxScrollX);
        else _scrollY = Math.Clamp(_scrollY - step, 0, MaxScrollY);
        Invalidate();
        e.Handled = true;
    }

    int MaxScrollX => _doc == null ? 0 : Math.Max(0, (int)(_doc.Form.Width * Zoom) + Px(32) - Width);
    int MaxScrollY => _doc == null ? 0 : Math.Max(0, (int)(_doc.Form.Height * Zoom) + Px(32) - Height);

    // ---- Keyboard ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _doc == null) return;
        var mods = e.Modifiers & ~KeyModifiers.Shift;
        bool cmd = mods == Shortcut.CommandModifier;
        switch (e.Key)
        {
            case Key.Left or Key.Right or Key.Up or Key.Down when mods is KeyModifiers.None or KeyModifiers.Ctrl:
            {
                int step = mods == KeyModifiers.Ctrl ? Math.Max(1, GridSize) : 1;
                int dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
                int dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                Nudge(dx, dy, resize: e.Shift);
                break;
            }
            case Key.Delete when mods == KeyModifiers.None: _doc.DeleteSelection(); break;
            case Key.Escape when mods == KeyModifiers.None:
                PendingClass = null;
                _doc.Select(_doc.Primary.Parent is { } p && p != _doc.Form ? p : null);
                break;
            case Key.A when cmd: _doc.SelectMany(_doc.PasteTarget().Children.Where(c => c.IsShown)); break;
            case Key.C when cmd: CopySelection(); break;
            case Key.X when cmd: CopySelection(); _doc.DeleteSelection(); break;
            case Key.V when cmd: PasteClipboard(); break;
            case Key.Z when cmd: if (e.Shift) _doc.Redo(); else _doc.Undo(); break;
            case Key.Y when mods == KeyModifiers.Ctrl: _doc.Redo(); break;
            default: return;
        }
        e.Handled = true;
        Invalidate();
    }

    public void CopySelection()
    {
        if (_doc is { Selection.Count: > 0 }) RadSharp.Text.Clipboard.SetText(_doc.CopySelection());
    }

    public void PasteClipboard()
    {
        if (_doc == null || RadSharp.Text.Clipboard.GetText() is not { } text || !text.TrimStart().StartsWith('[')) return;
        try { _doc.Paste(text, _doc.PasteTarget()); }
        catch (FormFileException) { }
    }

    void Nudge(int dx, int dy, bool resize)
    {
        if (Locked) return;
        var targets = _doc!.Selection.Where(c => resize ? c == _doc.Primary : c.Align == Align.None).ToList();
        if (targets.Count == 0) return;
        _doc.Snapshot();
        foreach (var c in targets)
        {
            if (!resize) c.SetBounds(c.Left + dx, c.Top + dy, c.Width, c.Height);
            else if (c.Align is Align.None) c.SetBounds(c.Left, c.Top, Math.Max(1, c.Width + dx), Math.Max(1, c.Height + dy));
            else if (c.Align is Align.Top or Align.Bottom) c.Height = Math.Max(1, c.Height + dy);
            else if (c.Align is Align.Left or Align.Right) c.Width = Math.Max(1, c.Width + dx);
        }
        _doc.NotifyChanged();
    }

    // ---- Painting ----

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        canvas.FillRect(new Rect(0, 0, Width, Height), t.SplitterFace);
        if (_doc == null) return;
        var form = _doc.Form;
        form.PerformLayout();
        var formRect = ScreenRect(new Rect(0, 0, form.Width, form.Height));
        canvas.FillRect(new Rect(formRect.X + Px(3), formRect.Y + Px(3), formRect.Width, formRect.Height), new Color(0, 0, 0, 30));
        canvas.DrawForm(form, formRect.X, formRect.Y, Zoom);
        canvas.DrawBorder(Canvas.Inflate(formRect, Px(1)), t.PanelBorder, Px(1));

        if (ShowGrid && GridSize >= 4)
        {
            var dot = new Color(0, 0, 0, 38);
            int d = Math.Max(1, Px(1));
            for (int gy = GridSize; gy < form.Height; gy += GridSize)
                for (int gx = GridSize; gx < form.Width; gx += GridSize)
                {
                    var (sx, sy) = DesignToScreen(gx, gy);
                    canvas.FillRect(new Rect(sx, sy, d, d), dot);
                }
        }

        foreach (var c in form.Descendants().OfType<Component>()) PaintComponentIcon(canvas, c);

        foreach (var c in _doc.Selection)
        {
            if (!c.IsEffectivelyVisible && c is not Component) continue;
            var r = ScreenRect(DesignRect(c));
            canvas.DrawBorder(Canvas.Inflate(r, Px(1)), c == _doc.Primary ? t.Accent : t.TextDisabled, Math.Max(1, Px(1)));
        }
        if (!Locked && _doc.Selection.Count == 1)
            foreach (var (_, hr) in HandleRects(_doc.Primary))
            {
                canvas.FillRect(hr, t.InputBackground);
                canvas.DrawBorder(hr, t.Accent, Math.Max(1, Px(1)));
            }

        var grip = new Rect(formRect.X + formRect.Width - Px(4), formRect.Y + formRect.Height - Px(4), Px(8), Px(8));
        canvas.FillRect(grip, t.InputBackground);
        canvas.DrawBorder(grip, t.TextDisabled, Math.Max(1, Px(1)));

        var guideColor = Color.FromHex("#E0338E");
        foreach (var (vertical, at, from, to) in _guides)
        {
            var (x1, y1) = vertical ? DesignToScreen(at, from) : DesignToScreen(from, at);
            var (x2, y2) = vertical ? DesignToScreen(at, to) : DesignToScreen(to, at);
            canvas.FillRect(vertical ? new Rect(x1, y1, Math.Max(1, Px(1)), y2 - y1) : new Rect(x1, y1, x2 - x1, Math.Max(1, Px(1))), guideColor);
        }

        if (_rubber is { } band)
        {
            var r = ScreenRect(band);
            canvas.FillRect(r, new Color(t.Accent.R, t.Accent.G, t.Accent.B, 32));
            canvas.DrawBorder(r, t.Accent, Math.Max(1, Px(1)));
        }
    }

    void PaintComponentIcon(Canvas canvas, Control c)
    {
        var t = canvas.Theme;
        var r = ScreenRect(DesignRect(c));
        canvas.FillRoundRect(r, Px(4), t.InputBackground);
        canvas.DrawRoundBorder(r, Px(4), t.InputBorder, Math.Max(1, Px(1)));
        var name = ComponentRegistry.Of(c).Name;
        if (PaletteIcons.HasArt(name))
        {
            // The toolbox picture, at 16/28 of the tile so it lands on whole pixels at 100 %.
            int size = r.Width * 16 / ComponentIconSize;
            PaletteIcons.Paint(canvas, name, new Rect(r.X + (r.Width - size) / 2, r.Y + (r.Height - size) / 2, size, size));
        }
        else
        {
            canvas.DrawText(PaletteIcons.Initials(name), canvas.DefaultBoldFont, t.Accent, r, TextAlign.Center, VerticalAlign.Center);
        }
        var label = new Rect(r.X - Px(20), r.Y + r.Height, r.Width + Px(40), Px(16));
        canvas.DrawText(c.Name, canvas.DefaultFont.WithSize(canvas.DefaultFont.Size * 0.8f), t.Text, label, TextAlign.Center, VerticalAlign.Top);
    }
}
