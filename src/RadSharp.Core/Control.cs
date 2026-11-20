using System.Text.Json;
using RadSharp.Drawing;

namespace RadSharp;

/// <summary>
/// Base class of every control: a node in the retained control tree with bounds, Align + Anchors
/// layout (spec, Appendix B) and published properties.
/// </summary>
public partial class Control
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

    /// <summary>
    /// A child a control adds for its own use (a grid's in-place editor, or any child its constructor creates: see
    /// <see cref="ComponentClass.Create"/>): never written to a form file, and a designer selects its parent instead.
    /// </summary>
    public bool IsTransient { get; internal set; }
    /// <summary>For a popup: the control that receives typed text while the popup is open, if it has focus (an editable combo box's edit); null means none.</summary>
    internal Control? PopupTextTarget { get; set; }
    public Control Root => Parent?.Root ?? this;

    [ThreadStatic] static int t_designModeDepth;

    /// <summary>
    /// True for a control the designer created: a form being designed and everything on it, from the control's
    /// constructor on (Delphi's <c>csDesigning in ComponentState</c>). A control the application defines can check
    /// it to skip, at design time, what needs the running application: opening a database or a file, starting a
    /// thread, reading settings. At design time a control's constructor, published property setters, layout and
    /// painting run; its event handlers never do (the designer only stores their names) and its timers don't tick.
    /// </summary>
    public bool DesignMode { get; } = t_designModeDepth > 0;

    /// <summary>
    /// For designers: every control created on this thread until the returned scope is disposed is in
    /// <see cref="DesignMode"/>. Scopes nest.
    /// </summary>
    public static IDisposable EnterDesignMode()
    {
        t_designModeDepth++;
        return new DesignModeScope();
    }

    sealed class DesignModeScope : IDisposable
    {
        bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            t_designModeDepth--;
        }
    }

    /// <summary>Event handler names by event property (<c>"onClick" → "btnSave_Click"</c>).</summary>
    public Dictionary<string, string> EventHandlers { get; } = [];

    bool _enabled = true;

    /// <summary>Disabled controls (and everything inside them) get no input and paint greyed.</summary>
    [Published(Category = "Behavior")]
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            InvalidateForm();
            if (!value) OwningForm?.ControlBecameUnavailable(this);
        }
    }

    [Published(Category = "Behavior")] public bool TabStop { get; set; }

    /// <summary>Whether Tab visits this control: <see cref="TabStop"/> by default. A control that hosts its own tab stop (an editable combo box's edit) answers false for itself.</summary>
    protected internal virtual bool IncludeInTabOrder => TabStop;
    [Published(Category = "Behavior")] public int TabOrder { get; set; } = -1;
    /// <summary>Anything the application wants to remember about this control (Delphi's <c>Tag</c>); never read or saved by the framework.</summary>
    public object? Tag { get; set; }

    /// <summary>The tooltip text shown after the pointer rests on the control (see <see cref="GetHintAt"/>). Newlines make multi-line tooltips.</summary>
    [Published(Category = "Behavior")] public string Hint { get; set; } = "";

    /// <summary>
    /// The tooltip for the point (<paramref name="x"/>, <paramref name="y"/>) in this control's own coordinates, or
    /// null/empty for none. <paramref name="region"/> is the part of the control (also in its coordinates) the text
    /// applies to: moving the pointer out of it, or to a point whose text differs, hides the tooltip and shows the
    /// new one. The default returns <see cref="Hint"/> for the whole control. Override it in a custom-drawn control
    /// whose parts have their own hints (a grid cell, a chart bar, a toolbar of drawn icons).
    /// </summary>
    /// <example>
    /// <code>
    /// public override string? GetHintAt(int x, int y, out Rect region)
    /// {
    ///     var cell = CellAt(x, y);                       // your own hit test
    ///     region = CellRect(cell);                       // local coordinates
    ///     return cell.IsValid ? $"{cell.Title}\n{cell.Detail}" : null;
    /// }
    /// </code>
    /// </example>
    public virtual string? GetHintAt(int x, int y, out Rect region)
    {
        region = new Rect(0, 0, _width, _height);
        return Hint;
    }

    Font? _font;

    /// <summary>
    /// This control's own font, or <c>null</c> to inherit the parent's (Delphi's <i>ParentFont</i>).
    /// Paint with <see cref="EffectiveFont"/>, never this property directly.
    /// </summary>
    [Published(Category = "Appearance")]
    public Font? Font
    {
        get => _font;
        set
        {
            if (_font == value) return;
            _font = value;
            InvalidateForm();
        }
    }

    /// <summary>This control's own <see cref="Font"/>, or its parent's <see cref="EffectiveFont"/>, or the theme's at the root.</summary>
    public Font EffectiveFont => _font ?? Parent?.EffectiveFont ?? CurrentTheme.DefaultFont;

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
        InvalidateForm();
        if (OwningForm is { } form)
        {
            form.TreeChanged();
            form.ControlAdded(child);
        }
    }

    public bool Remove(Control child)
    {
        var form = OwningForm;
        if (!_children.Remove(child)) return false;
        child.Parent = null;
        InvalidateLayout();
        InvalidateForm();
        form?.ControlBecameUnavailable(child);
        form?.TreeChanged();
        form?.ControlRemoved(child);
        return true;
    }

    /// <summary>Every control below this one, depth-first.</summary>
    public IEnumerable<Control> Descendants()
    {
        foreach (var c in _children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
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

    [Published(Category = "Layout")] public int Left { get => _left; set => SetBounds(value, _top, _width, _height); }
    [Published(Category = "Layout")] public int Top { get => _top; set => SetBounds(_left, value, _width, _height); }
    [Published(Category = "Layout")] public int Width { get => _width; set => SetBounds(_left, _top, value, _height); }
    [Published(Category = "Layout")] public int Height { get => _height; set => SetBounds(_left, _top, _width, value); }

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
        _captureRescaled = false;
        RecordCaptureDesign(p);
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
        if (left == _left && top == _top && width == _width && height == _height) return;
        InvalidateForm();
        _left = left;
        _top = top;
        if (width == _width && height == _height) return;
        _width = width;
        _height = height;
        _needsArrange = true;
        OnResize();
    }

    // ---- Layout properties ----

    [Published(Category = "Layout")]
    public Align Align
    {
        get => _align;
        set { if (_align == value) return; _align = value; Recapture(); Parent?.InvalidateLayout(); }
    }

    [Published(Category = "Layout")]
    public Anchors Anchors
    {
        get => _anchors;
        set { if (_anchors == value) return; _anchors = value; Recapture(); Parent?.InvalidateLayout(); }
    }

    [Published(Category = "Layout")]
    public Thickness Margin
    {
        get => _margin;
        set { if (_margin == value) return; _margin = value; Parent?.InvalidateLayout(); }
    }

    [Published(Category = "Layout")]
    public Thickness Padding
    {
        get => _padding;
        set { if (_padding == value) return; _padding = value; InvalidateLayout(); }
    }

    [Published(Category = "Layout")] public int MinWidth { get => _minWidth; set { _minWidth = Math.Max(0, value); Parent?.InvalidateLayout(); } }
    [Published(Category = "Layout")] public int MinHeight { get => _minHeight; set { _minHeight = Math.Max(0, value); Parent?.InvalidateLayout(); } }
    [Published(Category = "Layout")] public int MaxWidth { get => _maxWidth; set { _maxWidth = Math.Max(0, value); Parent?.InvalidateLayout(); } }
    [Published(Category = "Layout")] public int MaxHeight { get => _maxHeight; set { _maxHeight = Math.Max(0, value); Parent?.InvalidateLayout(); } }

    [Published(Category = "Behavior")]
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            Parent?.InvalidateLayout();
            InvalidateForm();
            if (!value) OwningForm?.ControlBecameUnavailable(this);
        }
    }

    // ---- Painting ----

    /// <summary>Requests a repaint of this control's area.</summary>
    public void Invalidate()
    {
        OwningForm?.InvalidateRect(new Rect(AbsoluteLeft, AbsoluteTop, _width, _height));
    }

    /// <summary>Requests a repaint of the whole form (bounds or tree changed).</summary>
    void InvalidateForm()
    {
        OwningForm?.InvalidateAll();
    }

    /// <summary>
    /// Paints the control in its own coordinates (0, 0 is its top-left corner), clipped to its bounds.
    /// Children are painted afterwards, on top.
    /// </summary>
    protected virtual void Paint(Canvas canvas) { }

    internal void PaintControl(Canvas canvas) => Paint(canvas);

    // ---- Loading ----

    /// <summary>
    /// True while the form reader applies this control's members and children (Delphi's <c>csLoading</c>). A property
    /// that depends on something read later, such as an index into items or pages, can hold its value while this is
    /// true and apply it in <see cref="Loaded"/>.
    /// </summary>
    public bool Loading { get; internal set; }

    /// <summary>
    /// Called by the form reader once this control's members and children are read, before its parent's
    /// <see cref="Loaded"/> (Delphi's <c>Loaded</c>). <see cref="Loading"/> is already false. Overrides call the base.
    /// </summary>
    protected internal virtual void Loaded() { }

    // ---- Scaling ----

    // Lengths are stored in form pixels. _design remembers, per length, the 100 % value it was scaled from (see
    // ScaleChange.Apply): it is trusted only while the pixel value is still what it gives at _lengthScale, so a length
    // changed since (from code, or by layout) is converted back from its pixels instead. Allocated the first time the
    // control is scaled; slots: left, top, width, height, margin l/t/r/b, padding l/t/r/b, min width/height,
    // max width/height, then the anchor capture: left, top, width, height, parent width, parent height.
    double[]? _design;
    double _lengthScale = 1;
    const int DesignSlots = 22, CaptureSlot = 16;
    // After a scale change, for a capture whose parent was still at its captured size (it hadn't grown or shrunk since):
    // the next layout takes PW0/PH0 from the parent's size laid out at the new scale, as a fresh load captures it
    // (see ResolveRescaledCapture).
    bool _captureRescaled, _rescaledAtParentW, _rescaledAtParentH;

    /// <summary>
    /// Multiplies every stored length in this subtree by <paramref name="scale"/> and rounds (spec, Appendix B,
    /// "Scaling"). Runs once on a freshly built tree, before its first layout; for a whole code-built form
    /// use <see cref="Form.ApplyScale"/>, which also records the scale. Calling it twice scales twice.
    /// Each control remembers the 100 % lengths it was scaled from, so a later <see cref="Form.ChangeScale"/>
    /// recomputes them instead of rounding the rounded values again.
    /// </summary>
    public void ScaleLengths(double scale)
    {
        if (!(scale > 0) || !double.IsFinite(scale)) throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be positive.");
        var change = new ScaleChange(_lengthScale, _lengthScale * scale);
        RescaleSelf(change, keepCapture: false, 0, 0);
        foreach (var c in _children) c.ScaleLengths(scale);
    }

    /// <summary>
    /// Moves every length in this subtree from <paramref name="from"/> to <paramref name="to"/> (the form's old and
    /// new scale) through the remembered 100 % values. Anchor captures are converted rather than dropped, so an
    /// anchored control keeps its designed distances whatever size the parent has now.
    /// </summary>
    internal void RescaleTree(double from, double to) =>
        RescaleTree(new ScaleChange(from, to), Parent?._width ?? 0, Parent?._height ?? 0);

    void RescaleTree(ScaleChange change, int parentWidth, int parentHeight)
    {
        int w = _width, h = _height;   // the size the children's anchor captures are measured against, before it changes
        RescaleSelf(change, keepCapture: true, parentWidth, parentHeight);
        foreach (var c in _children) c.RescaleTree(change, w, h);
    }

    /// <summary>
    /// Completes a capture converted by a scale change: where the parent was still at its captured size, the captured
    /// parent size is the parent's size now, laid out at the new scale. A parent whose size is computed by layout (an
    /// aligned panel) can be a pixel off the converted old size, because its siblings round separately; taking its laid
    /// out size keeps the control where a fresh load at the new scale puts it. The 100 % value stays the one converted,
    /// so the capture still comes back exactly.
    /// </summary>
    void ResolveRescaledCapture(int parentWidth, int parentHeight)
    {
        if (!_captureRescaled) return;
        _captureRescaled = false;
        if (_rescaledAtParentW) _pw0 = parentWidth;
        if (_rescaledAtParentH) _ph0 = parentHeight;
    }

    /// <summary>After a scale change's layout: resolves the captures of controls the layout didn't place (invisible ones).</summary>
    internal void ResolveRescaledCaptures()
    {
        foreach (var c in _children)
        {
            c.ResolveRescaledCapture(_width, _height);
            c.ResolveRescaledCaptures();
        }
    }

    /// <summary>Marks this subtree's lengths as being at 100 %, forgetting what earlier scaling remembered.</summary>
    internal void ResetLengthScale()
    {
        _design = null;
        _lengthScale = 1;
        foreach (var c in _children) c.ResetLengthScale();
    }

    void RescaleSelf(ScaleChange change, bool keepCapture, int parentWidth, int parentHeight)
    {
        var d = _design ??= NewDesign();
        int S(int v, int slot) => change.Apply(v, ref d[slot]);
        Thickness T(Thickness t, int slot) => new(S(t.L, slot), S(t.T, slot + 1), S(t.R, slot + 2), S(t.B, slot + 3));
        _left = S(_left, 0); _top = S(_top, 1);
        _width = S(_width, 2); _height = S(_height, 3);
        _margin = T(_margin, 4); _padding = T(_padding, 8);
        _minWidth = S(_minWidth, 12); _minHeight = S(_minHeight, 13);
        _maxWidth = S(_maxWidth, 14); _maxHeight = S(_maxHeight, 15);
        if (keepCapture && _captured)
        {
            _aL = S(_aL, CaptureSlot); _aT = S(_aT, CaptureSlot + 1);
            _aW = S(_aW, CaptureSlot + 2); _aH = S(_aH, CaptureSlot + 3);
            // Two changes before a layout: the parent's size wasn't compared since; keep what the first one found.
            if (!_captureRescaled) { _rescaledAtParentW = parentWidth == _pw0; _rescaledAtParentH = parentHeight == _ph0; }
            // The captured parent size goes through its 100 % value without checking it against the pixels: it is set
            // only by a capture (which records it) and by ResolveRescaledCapture, which may move it a pixel on purpose.
            _pw0 = ConvertCaptured(_pw0, ref d[CaptureSlot + 4], change);
            _ph0 = ConvertCaptured(_ph0, ref d[CaptureSlot + 5], change);
            _captureRescaled = true;
        }
        else { _captured = false; _captureRescaled = false; }
        _lengthScale = change.To;
        _needsArrange = true;
        RescaleOwnLengths(change);
    }

    static int ConvertCaptured(int pixels, ref double design, ScaleChange change)
    {
        if (double.IsNaN(design)) design = pixels / change.From;
        return LayoutMath.Round(design * change.To);
    }

    static double[] NewDesign()
    {
        var d = new double[DesignSlots];
        Array.Fill(d, double.NaN);
        return d;
    }

    /// <summary>
    /// Records the anchor capture's 100 % values from this control's (and its parent's) remembered lengths, while those
    /// still match, so converting the capture at a later scale change is as exact as the lengths themselves.
    /// </summary>
    void RecordCaptureDesign(Control parent)
    {
        if (_design is not { } d) return;
        double s = _lengthScale;
        d[CaptureSlot] = ScaleChange.DesignOf(_aL, d[0], s);
        d[CaptureSlot + 1] = ScaleChange.DesignOf(_aT, d[1], s);
        d[CaptureSlot + 2] = ScaleChange.DesignOf(_aW, d[2], s);
        d[CaptureSlot + 3] = ScaleChange.DesignOf(_aH, d[3], s);
        var pd = parent._design;
        d[CaptureSlot + 4] = pd != null ? ScaleChange.DesignOf(_pw0, pd[2], parent._lengthScale) : double.NaN;
        d[CaptureSlot + 5] = pd != null ? ScaleChange.DesignOf(_ph0, pd[3], parent._lengthScale) : double.NaN;
    }

    /// <summary>
    /// Scales lengths a subclass adds (e.g. a splitter's minimum size) when the control moves to another scale. Use
    /// <see cref="ScaleChange.Apply"/> with a field per length that starts at <see cref="double.NaN"/>, so the length
    /// comes back exactly after 100 % → 150 % → 100 %. The default calls <see cref="ScaleOwnLengths"/> with
    /// <see cref="ScaleChange.Ratio"/>.
    /// </summary>
    protected virtual void RescaleOwnLengths(ScaleChange change) => ScaleOwnLengths(change.Ratio);

    /// <summary>
    /// Scales lengths a subclass adds by <paramref name="scale"/> (a ratio). Kept for existing subclasses; prefer
    /// <see cref="RescaleOwnLengths"/>, which doesn't drift when the scale changes back and forth at run time.
    /// </summary>
    protected virtual void ScaleOwnLengths(double scale) { }

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
        // A root's absolute position is where it is drawn in form coordinates: a popup sits at its Left/Top, but a
        // form is the coordinate space itself (its Left/Top are the window's screen position, not an offset).
        if (Parent == null)
        {
            bool isForm = this is Form;
            AbsoluteLeft = isForm ? 0 : _left;
            AbsoluteTop = isForm ? 0 : _top;
        }
        for (int pass = 0; _needsArrange && pass < 3; pass++)
        {
            _needsArrange = false;
            ArrangeChildren();
            if (AfterArrange()) _needsArrange = true;
        }
        _needsArrange = false;
        var (ox, oy) = ChildOffset;
        foreach (var c in _children)
        {
            if (!c._visible) continue;
            c.AbsoluteLeft = AbsoluteLeft + ox + c._left;
            c.AbsoluteTop = AbsoluteTop + oy + c._top;
            c.PerformLayout();
        }
    }

    /// <summary>
    /// Called after the children were arranged. Return true to arrange once more (e.g. a scroll box
    /// whose scroll bars appeared and shrank its client area).
    /// </summary>
    protected virtual bool AfterArrange() => false;

    /// <summary>Offset applied to children when positioning, painting and hit testing (scrolling).</summary>
    protected internal virtual (int X, int Y) ChildOffset => (0, 0);

    /// <summary>Where this control's child coordinates start, relative to its top-left (scrolling, viewports).</summary>
    public (int X, int Y) ContentOffset => ChildOffset;

    /// <summary>The area (in this control's coordinates) children are clipped to.</summary>
    protected internal virtual Rect ChildClipRect => new(0, 0, _width, _height);

    // ---- Form and theme ----

    internal Form? PopupForm;

    /// <summary>The form this control is on (also for controls shown in the form's popup layer).</summary>
    public Form? OwningForm
    {
        get
        {
            var root = Root;
            return root as Form ?? root.PopupForm;
        }
    }

    /// <summary>The owning form's theme, or the default theme.</summary>
    protected Theme CurrentTheme => OwningForm?.Theme ?? Theme.Default;

    /// <summary>The owning form's UI scale factor.</summary>
    protected double UiScale => OwningForm?.Scale ?? 1;

    /// <summary>Scales a length given at 100 % to whole pixels at the owning form's scale (1 when not on a form).</summary>
    public int Px(int length) => LayoutMath.Round(length * UiScale);

    /// <summary>True if this control and all its ancestors are visible.</summary>
    public bool IsEffectivelyVisible
    {
        get { for (var c = this; c != null; c = c.Parent) if (!c.IsShown) return false; return true; }
    }

    /// <summary>
    /// Visible, and shown by its parent: a page control shows only its active sheet without changing
    /// the other sheets' <see cref="Visible"/> (which is saved in the form file).
    /// </summary>
    public bool IsShown => _visible && (Parent?.ShowsChild(this) ?? true);

    /// <summary>Whether this container currently shows <paramref name="child"/> (see <see cref="IsShown"/>).</summary>
    protected internal virtual bool ShowsChild(Control child) => true;

    /// <summary>True if this control and all its ancestors are enabled.</summary>
    public bool IsEffectivelyEnabled
    {
        get { for (var c = this; c != null; c = c.Parent) if (!c._enabled) return false; return true; }
    }

    /// <summary>True if <paramref name="ancestor"/> is this control or one of its ancestors.</summary>
    public bool IsSelfOrDescendantOf(Control ancestor)
    {
        for (var c = this; c != null; c = c.Parent) if (c == ancestor) return true;
        return false;
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
        else c.ResolveRescaledCapture(_width, _height);
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

    // ---- Published properties ----

    /// <summary>Sets a published property or event handler name from form-file JSON (see <see cref="FormReader.SetProperty"/>).</summary>
    public bool ReadProperty(string name, JsonElement value) => FormReader.SetProperty(this, name, value);
}
