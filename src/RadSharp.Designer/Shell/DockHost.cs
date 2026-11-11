using System.Text.Json.Nodes;
using RadSharp.Drawing;

namespace RadSharp.Designer;

/// <summary>The edge of a <see cref="DockHost"/> a tool window is docked to.</summary>
public enum DockSide { Left, Right, Bottom }

/// <summary>
/// A tool window (Project, Inspector, Output…): a titled piece of content the <see cref="DockHost"/> docks to a
/// side, groups with others as tabs, hides and shows. The content control is created by the owner and only
/// moves between parents; it keeps its name, so <c>FindControl</c> still finds it while hidden.
/// </summary>
public sealed class ToolWindow(string id, string title, Control content)
{
    /// <summary>Stable identifier, saved in the layout.</summary>
    public string Id { get; } = id;
    public string Title { get; set; } = title;
    public Control Content { get; } = content;

    /// <summary>The region showing this window as a tab, or null while it is hidden.</summary>
    public DockRegion? Region { get; internal set; }

    /// <summary>The side the window is docked to, or was docked to before it was hidden.</summary>
    public DockSide Side { get; internal set; }

    public bool IsShown => Region != null;
}

/// <summary>
/// Docks <see cref="ToolWindow"/>s to the left, right and bottom of a centre control (the document area): each
/// side is a <see cref="DockRegion"/> showing its windows as tabs, resized with a splitter, collapsible to an edge
/// strip. A window moves to another side by dragging its tab there (onto a region, or near an edge of the
/// centre) or from its tab's context menu. The arrangement round-trips through <see cref="SaveLayout"/> /
/// <see cref="RestoreLayout"/>.
/// <para>Floating windows (phase B) will come in through <see cref="CanFloat"/>/<see cref="Float"/>: a subclass
/// or a later version shows the content in its own <see cref="Form"/> via <c>IPlatformServices.ShowForm</c>; the
/// layout already reserves a <c>"floating"</c> list.</para>
/// </summary>
public class DockHost : Control
{
    readonly Dictionary<DockSide, DockRegion> _regions = [];
    readonly Dictionary<DockSide, Splitter> _splitters = [];
    readonly List<ToolWindow> _windows = [];
    readonly Control _parking = new Panel { Name = "pnlDockParking", Visible = false };
    readonly double _scale;

    /// <param name="center">The control in the middle (the document area); it is given <see cref="Align.Client"/>.</param>
    /// <param name="scale">The UI scale the default region sizes are multiplied by.</param>
    public DockHost(Control center, double scale = 1)
    {
        _scale = scale;
        Name = "dockHost";
        Align = Align.Client;
        Center = center;
        // Order matters for Align: bottom first (it spans the full width), then left and right; each splitter
        // follows its region, since a splitter resizes the nearest earlier sibling with the same align.
        foreach (var side in new[] { DockSide.Bottom, DockSide.Left, DockSide.Right })
        {
            var region = new DockRegion(this, side) { Name = $"dock{side}" };
            var splitter = new Splitter
            {
                Name = $"splDock{side}",
                Align = region.Align,
                MinSize = S(side == DockSide.Bottom ? 60 : 120),
            };
            splitter.SetBounds(0, 0, S(4), S(4));
            splitter.MouseUp += (_, _) => OnLayoutChanged();
            region.SetExpandedSize(S(side switch { DockSide.Left => 200, DockSide.Right => 340, _ => 150 }));
            _regions[side] = region;
            _splitters[side] = splitter;
            Add(region);
            Add(splitter);
        }
        center.Align = Align.Client;
        Add(center);
        Add(_parking);
        UpdateVisibility();
    }

    int S(int v) => R(v * _scale);
    static int R(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    public Control Center { get; }
    public IReadOnlyList<ToolWindow> Windows => _windows;
    public DockRegion Region(DockSide side) => _regions[side];
    public Splitter SplitterOf(DockSide side) => _splitters[side];
    public ToolWindow? Find(string id) => _windows.FirstOrDefault(w => w.Id == id);

    /// <summary>Raised when the user changed the arrangement (moved, hid, collapsed or resized something).</summary>
    public event Action? LayoutChanged;

    /// <summary>Builds the context menu of a tool window's tab; by default Dock Left/Right/Bottom and Hide.</summary>
    public Func<ToolWindow, PopupMenu>? WindowMenuFactory { get; set; }

    internal void OnLayoutChanged() => LayoutChanged?.Invoke();

    /// <summary>Adds a tool window docked to <paramref name="side"/>, as the last tab there.</summary>
    public ToolWindow Add(ToolWindow window, DockSide side)
    {
        if (_windows.Contains(window)) throw new InvalidOperationException($"{window.Id} is already docked.");
        _windows.Add(window);
        window.Content.Align = Align.Client;
        Place(window, side, -1);
        return window;
    }

    void Place(ToolWindow window, DockSide side, int index)
    {
        window.Side = side;
        var region = _regions[side];
        region.InsertWindow(window, index);
        window.Region = region;
        UpdateVisibility();
    }

    /// <summary>Docks a window to <paramref name="side"/> (at tab <paramref name="index"/>; -1 = last), shows and activates it.</summary>
    public void Move(ToolWindow window, DockSide side, int index = -1)
    {
        var target = _regions[side];
        if (window.Region == target)
        {
            if (index >= 0) target.MoveWindow(window, index);
        }
        else
        {
            window.Region?.RemoveWindow(window);
            window.Region = null;
            Place(window, side, index);
        }
        target.Activate(window);
        if (target.Collapsed) target.SetCollapsed(false);
        UpdateVisibility();
        OnLayoutChanged();
    }

    /// <summary>Shows a window (on the side it was last docked to, if hidden), expands its region and makes it the active tab.</summary>
    public void Show(ToolWindow window)
    {
        if (window.Region == null) Place(window, window.Side, -1);
        var region = window.Region!;
        region.Activate(window);
        if (region.Collapsed) region.SetCollapsed(false);
        UpdateVisibility();
        OnLayoutChanged();
    }

    public void Show(string id)
    {
        if (Find(id) is { } w) Show(w);
    }

    /// <summary>Removes a window from its region; <see cref="Show(ToolWindow)"/> brings it back on the same side.</summary>
    public void Hide(ToolWindow window)
    {
        if (window.Region is not { } region) return;
        region.RemoveWindow(window);
        window.Region = null;
        _parking.Add(window.Content);
        UpdateVisibility();
        OnLayoutChanged();
    }

    /// <summary>Collapses a region to a strip along its edge (or expands it again).</summary>
    public void SetCollapsed(DockSide side, bool collapsed)
    {
        _regions[side].SetCollapsed(collapsed);
        UpdateVisibility();
        OnLayoutChanged();
    }

    void UpdateVisibility()
    {
        foreach (var (side, region) in _regions)
        {
            region.Visible = region.Windows.Count > 0;
            _splitters[side].Visible = region.Visible && !region.Collapsed;
        }
        InvalidateLayout();
        Invalidate();
    }

    // ---- Dragging a tab to another side ----

    /// <summary>The side a dragged tab would dock to, while a drag is on (regions highlight themselves).</summary>
    public DockSide? DropSide { get; private set; }

    /// <summary>Where a tab dropped at form point (<paramref name="x"/>, <paramref name="y"/>) docks: onto a
    /// region (at the tab under the point), or to a side when it lands near that edge of the centre.</summary>
    public DockSide? SideAt(int x, int y, out int index)
    {
        index = -1;
        foreach (var region in _regions.Values)
        {
            if (!region.IsEffectivelyVisible || !Contains(region, x, y)) continue;
            if (!region.Collapsed) index = region.TabIndexAt(x - region.AbsoluteLeft, y - region.AbsoluteTop);
            return region.Side;
        }
        if (Center.IsEffectivelyVisible && Contains(Center, x, y) && Center.Width > 0 && Center.Height > 0)
        {
            double rx = (x - Center.AbsoluteLeft) / (double)Center.Width;
            double ry = (y - Center.AbsoluteTop) / (double)Center.Height;
            if (rx < 0.25) return DockSide.Left;
            if (rx > 0.75) return DockSide.Right;
            if (ry > 0.6) return DockSide.Bottom;
        }
        return null;
    }

    static bool Contains(Control c, int x, int y) =>
        x >= c.AbsoluteLeft && y >= c.AbsoluteTop && x < c.AbsoluteLeft + c.Width && y < c.AbsoluteTop + c.Height;

    internal void TrackDrop(int x, int y)
    {
        var side = SideAt(x, y, out _);
        if (side == DropSide) return;
        DropSide = side;
        Invalidate();
        foreach (var r in _regions.Values) r.Invalidate();
    }

    internal void Drop(ToolWindow window, int x, int y)
    {
        DropSide = null;
        foreach (var r in _regions.Values) r.Invalidate();
        if (SideAt(x, y, out int index) is { } side) Move(window, side, index);
    }

    // ---- Tab context menu ----

    internal PopupMenu BuildWindowMenu(ToolWindow window)
    {
        if (WindowMenuFactory is { } factory) return factory(window);
        var menu = new PopupMenu();
        foreach (var side in new[] { DockSide.Left, DockSide.Right, DockSide.Bottom })
        {
            var s = side;
            menu.Add($"Dock {side}", _ => Move(window, s)).Enabled = window.Region?.Side != side;
        }
        menu.Items.Add(new MenuItem { Caption = "-", Separator = true });
        menu.Add("Hide", _ => Hide(window));
        return menu;
    }

    // ---- Floating (phase B) ----

    /// <summary>Whether <see cref="Float"/> is supported. Phase B: floating windows on their own <see cref="Form"/>.</summary>
    public virtual bool CanFloat => false;

    /// <summary>Phase B seam: shows a window in a floating form of its own. Not supported yet: returns false.</summary>
    public virtual bool Float(ToolWindow window) => false;

    // ---- Layout persistence ----

    /// <summary>The arrangement: per side, its windows in tab order, the active one, size (at 100 %) and collapsed state.</summary>
    public DockLayout SaveLayout()
    {
        var layout = new DockLayout();
        foreach (var (side, region) in _regions)
            layout.Regions[side] = new DockLayout.RegionState
            {
                Size = R(region.ExpandedSize / _scale),
                Collapsed = region.Collapsed,
                Windows = [.. region.Windows.Select(w => w.Id)],
                Active = region.ActiveWindow?.Id,
            };
        layout.Hidden.AddRange(_windows.Where(w => !w.IsShown).Select(w => $"{w.Id}@{w.Side}"));
        return layout;
    }

    /// <summary>Applies a saved arrangement. Windows it doesn't mention (added in a newer version) stay where they are.</summary>
    public void RestoreLayout(DockLayout layout)
    {
        foreach (var entry in layout.Hidden)
        {
            var parts = entry.Split('@');
            if (Find(parts[0]) is not { } w) continue;
            if (parts.Length > 1 && Enum.TryParse<DockSide>(parts[1], out var side)) w.Side = side;
            if (w.Region is { } r) { r.RemoveWindow(w); w.Region = null; _parking.Add(w.Content); }
        }
        foreach (var (side, state) in layout.Regions)
        {
            var region = _regions[side];
            int i = 0;
            foreach (var id in state.Windows)
            {
                if (Find(id) is not { } w) continue;
                if (w.Region != region)
                {
                    w.Region?.RemoveWindow(w);
                    w.Region = null;
                    Place(w, side, i);
                }
                else region.MoveWindow(w, i);
                i++;
            }
            if (state.Size > 0) region.SetExpandedSize(S(state.Size));
            if (state.Active != null && Find(state.Active) is { } active && active.Region == region) region.Activate(active);
            region.SetCollapsed(state.Collapsed);
        }
        UpdateVisibility();
    }
}

/// <summary>A saved <see cref="DockHost"/> arrangement, as JSON.</summary>
public sealed class DockLayout
{
    public sealed class RegionState
    {
        public int Size { get; set; }
        public bool Collapsed { get; set; }
        public List<string> Windows { get; set; } = [];
        public string? Active { get; set; }
    }

    public Dictionary<DockSide, RegionState> Regions { get; } = [];

    /// <summary>Hidden windows as <c>id@Side</c> (the side they come back on).</summary>
    public List<string> Hidden { get; } = [];

    public JsonObject ToJson()
    {
        var regions = new JsonObject();
        foreach (var (side, r) in Regions)
            regions[side.ToString()] = new JsonObject
            {
                ["size"] = r.Size,
                ["collapsed"] = r.Collapsed,
                ["windows"] = new JsonArray([.. r.Windows.Select(w => (JsonNode)JsonValue.Create(w))]),
                ["active"] = r.Active,
            };
        return new JsonObject
        {
            ["regions"] = regions,
            ["hidden"] = new JsonArray([.. Hidden.Select(h => (JsonNode)JsonValue.Create(h))]),
            ["floating"] = new JsonArray(),   // phase B
        };
    }

    public static DockLayout? FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        var layout = new DockLayout();
        try
        {
            if (obj["regions"] is JsonObject regions)
                foreach (var (name, value) in regions)
                {
                    if (!Enum.TryParse<DockSide>(name, out var side) || value is not JsonObject r) continue;
                    layout.Regions[side] = new RegionState
                    {
                        Size = r["size"]?.GetValue<int>() ?? 0,
                        Collapsed = r["collapsed"]?.GetValue<bool>() ?? false,
                        Windows = r["windows"] is JsonArray ws ? [.. ws.Select(w => w?.GetValue<string>()).OfType<string>()] : [],
                        Active = r["active"]?.GetValue<string>(),
                    };
                }
            if (obj["hidden"] is JsonArray hidden) layout.Hidden.AddRange(hidden.Select(h => h?.GetValue<string>()).OfType<string>());
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return null; }
        return layout;
    }
}

/// <summary>
/// One side of a <see cref="DockHost"/>: its tool windows as tabs along the top, the active one's content
/// below. The arrow at the strip's right end collapses the region to a narrow strip of buttons along the edge;
/// clicking a button expands it again. Tabs can be dragged to another region, and right-clicked for the
/// window's menu; a middle-click hides the window.
/// </summary>
public sealed class DockRegion : Control
{
    readonly DockHost _host;
    readonly List<ToolWindow> _windows = [];
    int _active;
    bool _collapsed;
    int _expandedSize;

    // A tab being pressed or dragged.
    ToolWindow? _pressed;
    bool _dragging;
    int _pressX, _pressY;
    ToolWindow? _menuWindow;

    internal DockRegion(DockHost host, DockSide side)
    {
        _host = host;
        Side = side;
        Align = side switch { DockSide.Left => Align.Left, DockSide.Right => Align.Right, _ => Align.Bottom };
    }

    public DockSide Side { get; }
    public IReadOnlyList<ToolWindow> Windows => _windows;
    public ToolWindow? ActiveWindow => _windows.Count > 0 ? _windows[Math.Clamp(_active, 0, _windows.Count - 1)] : null;
    public bool Collapsed => _collapsed;

    /// <summary>The width (left/right) or height (bottom) the region has when expanded.</summary>
    public int ExpandedSize => _expandedSize;

    bool Vertical => Side != DockSide.Bottom;
    int StripSize => Px(26);
    int CollapsedSize => Px(24);

    internal void SetExpandedSize(int size)
    {
        _expandedSize = Math.Max(Px(40), size);
        if (_collapsed) return;
        if (Vertical) Width = _expandedSize; else Height = _expandedSize;
    }

    internal void SetCollapsed(bool collapsed)
    {
        if (collapsed && OwningForm?.FocusedControl is { } f && f.IsSelfOrDescendantOf(this)) OwningForm.SetFocus(null);
        _collapsed = collapsed;
        int size = collapsed ? CollapsedSize : _expandedSize;
        if (Vertical) Width = size; else Height = size;
        Invalidate();
    }

    protected override void OnResize()
    {
        if (!_collapsed) _expandedSize = Vertical ? Width : Height;
        base.OnResize();
    }

    internal void InsertWindow(ToolWindow window, int index)
    {
        if (index < 0 || index > _windows.Count) index = _windows.Count;
        _windows.Insert(index, window);
        Add(window.Content);
        _active = index;
        Invalidate();
    }

    internal void RemoveWindow(ToolWindow window)
    {
        var current = ActiveWindow;
        int i = _windows.IndexOf(window);
        if (i < 0) return;
        if (OwningForm?.FocusedControl is { } f && f.IsSelfOrDescendantOf(window.Content)) OwningForm.SetFocus(null);
        _windows.RemoveAt(i);
        Remove(window.Content);
        _active = current == window ? Math.Min(i, _windows.Count - 1) : Math.Max(0, _windows.IndexOf(current!));
        Invalidate();
    }

    internal void MoveWindow(ToolWindow window, int index)
    {
        var current = ActiveWindow;
        int i = _windows.IndexOf(window);
        if (i < 0) return;
        _windows.RemoveAt(i);
        index = Math.Clamp(index, 0, _windows.Count);
        _windows.Insert(index, window);
        _active = Math.Max(0, _windows.IndexOf(current!));
        Invalidate();
    }

    /// <summary>Makes <paramref name="window"/> the visible tab.</summary>
    public void Activate(ToolWindow window)
    {
        int i = _windows.IndexOf(window);
        if (i < 0 || i == _active) return;
        if (ActiveWindow is { } old && OwningForm?.FocusedControl is { } f && f.IsSelfOrDescendantOf(old.Content)) OwningForm.SetFocus(null);
        _active = i;
        InvalidateLayout();
        OwningForm?.InvalidateAll();
    }

    protected override bool ShowsChild(Control child) => !_collapsed && child == ActiveWindow?.Content;

    protected override Rect GetClientRect() =>
        _collapsed ? new Rect(0, 0, 0, 0)
                   : new Rect(Px(1), StripSize, Math.Max(0, Width - Px(2)), Math.Max(0, Height - StripSize - Px(1)));

    RenderFont StripFont => new(FontFace.Resolve(EffectiveFont), (float)(EffectiveFont.Size * UiScale));

    /// <summary>The tab rectangles along the strip (expanded) or the buttons along the edge (collapsed), in this control's coordinates.</summary>
    public List<Rect> TabRects()
    {
        var font = StripFont;
        var list = new List<Rect>();
        if (_collapsed)
        {
            int pos = Px(4);
            foreach (var w in _windows)
            {
                if (Vertical)
                {
                    int h = w.Title.Length * (int)Math.Ceiling(font.LineHeight * 0.8) + Px(12);
                    list.Add(new Rect(Px(2), pos, Width - Px(4), h));
                    pos += h + Px(4);
                }
                else
                {
                    int wd = (int)Math.Ceiling(font.MeasureWidth(w.Title)) + Px(16);
                    list.Add(new Rect(pos, Px(2), wd, Height - Px(4)));
                    pos += wd + Px(4);
                }
            }
            return list;
        }
        int x = Px(2);
        int limit = CollapseButtonRect.X;
        foreach (var w in _windows)
        {
            int wd = (int)Math.Ceiling(font.MeasureWidth(w.Title)) + Px(16);
            wd = Math.Max(Px(24), Math.Min(wd, limit - x));
            list.Add(new Rect(x, 0, wd, StripSize));
            x += wd;
        }
        return list;
    }

    Rect CollapseButtonRect => new(Width - Px(24), Px(2), Px(22), StripSize - Px(4));

    /// <summary>The tab index under a point in this control's coordinates, or -1.</summary>
    public int TabIndexAt(int x, int y)
    {
        var rects = TabRects();
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            if (x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height) return i;
        }
        return -1;
    }

    static bool In(Rect r, int x, int y) => x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int i = TabIndexAt(e.X, e.Y);
        var window = i >= 0 ? _windows[i] : null;
        switch (e.Button)
        {
            case MouseButton.Right:
                _menuWindow = window;
                return;
            case MouseButton.Middle:
                if (window != null) _host.Hide(window);
                return;
        }
        if (_collapsed)
        {
            if (window != null) _host.Show(window);
            return;
        }
        if (In(CollapseButtonRect, e.X, e.Y))
        {
            _host.SetCollapsed(Side, true);
            return;
        }
        if (window == null) return;
        if (window != ActiveWindow) { Activate(window); _host.OnLayoutChanged(); }
        _pressed = window;
        _dragging = false;
        _pressX = AbsoluteLeft + e.X;
        _pressY = AbsoluteTop + e.Y;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressed == null) return;
        int x = AbsoluteLeft + e.X, y = AbsoluteTop + e.Y;
        if (!_dragging && (Math.Abs(x - _pressX) > Px(5) || Math.Abs(y - _pressY) > Px(5))) _dragging = true;
        if (_dragging) _host.TrackDrop(x, y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var window = _pressed;
        bool dragging = _dragging;
        _pressed = null;
        _dragging = false;
        if (window != null && dragging) _host.Drop(window, AbsoluteLeft + e.X, AbsoluteTop + e.Y);
    }

    public override PopupMenu? ContextMenu
    {
        get
        {
            if (_menuWindow is not { } w) return null;
            _menuWindow = null;
            return _host.BuildWindowMenu(w);
        }
        set { }
    }

    public override string? GetHintAt(int x, int y, out Rect region)
    {
        int i = TabIndexAt(x, y);
        if (i >= 0)
        {
            region = TabRects()[i];
            return _collapsed ? _windows[i].Title : null;
        }
        if (!_collapsed && In(CollapseButtonRect, x, y)) { region = CollapseButtonRect; return "Collapse"; }
        region = new Rect(0, 0, Width, Height);
        return null;
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var all = new Rect(0, 0, Width, Height);
        canvas.FillRect(all, t.PanelBackground);
        var font = EffectiveFont;
        var rects = TabRects();
        if (_collapsed)
        {
            var render = StripFont;
            for (int i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                canvas.FillRoundRect(r, canvas.Px(3), t.ButtonFaceHover);
                var title = _windows[i].Title;
                if (Vertical)
                {
                    int lh = (int)Math.Ceiling(render.LineHeight * 0.8);
                    for (int c = 0; c < title.Length; c++)
                        canvas.DrawText(title[c].ToString(), font, t.Text, new Rect(r.X, r.Y + Px(6) + c * lh, r.Width, lh), TextAlign.Center, VerticalAlign.Center);
                }
                else canvas.DrawText(title, font, t.Text, r, TextAlign.Center, VerticalAlign.Center);
            }
        }
        else
        {
            canvas.DrawBorder(new Rect(0, StripSize - Px(1), Width, Height - StripSize + Px(1)), t.PanelBorder, canvas.Px(1));
            for (int i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                bool active = _windows[i] == ActiveWindow;
                if (active)
                {
                    canvas.FillRect(new Rect(r.X, r.Y, r.Width, r.Height), t.FormBackground);
                    canvas.FillRect(new Rect(r.X, r.Y, r.Width, canvas.Px(2)), t.Accent);
                }
                canvas.DrawText(_windows[i].Title, active ? font with { Bold = true } : font, t.Text, r, TextAlign.Center, VerticalAlign.Center, ellipsis: true);
            }
            // The collapse arrow points at the edge the region collapses to.
            var b = CollapseButtonRect;
            float cx = b.X + b.Width / 2f, cy = b.Y + b.Height / 2f, a = Px(4);
            var color = t.TextDisabled;
            (PointF, PointF, PointF) chevron = Side switch
            {
                DockSide.Left => (new(cx + a / 2, cy - a), new(cx - a / 2, cy), new(cx + a / 2, cy + a)),
                DockSide.Right => (new(cx - a / 2, cy - a), new(cx + a / 2, cy), new(cx - a / 2, cy + a)),
                _ => (new(cx - a, cy - a / 2), new(cx, cy + a / 2), new(cx + a, cy - a / 2)),
            };
            canvas.DrawPolyline([chevron.Item1, chevron.Item2, chevron.Item3], color, canvas.Px(1) * 1.5f);
        }
        if (_host.DropSide == Side) canvas.DrawBorder(all, t.Accent, canvas.Px(2));
    }
}
