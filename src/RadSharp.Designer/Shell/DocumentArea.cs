using RadSharp.Drawing;

namespace RadSharp.Designer;

/// <summary>
/// The IDE's document tabs: forms and code files share one strip. Each tab shows the document's title, a dirty
/// marker (<c>*</c>) and a close button; a middle-click closes it, dragging reorders, a right-click opens the tab
/// menu (<see cref="TabMenuFactory"/>). One tab at a time can be the <see cref="Preview"/> (italic): opened by a
/// single click in the Project window and replaced by the next one, until it is edited, double-clicked or opened
/// for real. With <see cref="SplitView"/> on, the active document is shown beside its partner
/// (<see cref="SplitPair"/>: a form and its code-behind).
/// <para>The area never prompts: closing goes through <see cref="CloseRequested"/>, so the shell can ask about
/// unsaved changes first and then call <see cref="Remove"/>.</para>
/// </summary>
public sealed class DocumentArea : Control
{
    readonly List<IDocumentView> _docs = [];
    readonly Dictionary<IDocumentView, Action> _subscriptions = [];
    readonly Panel _main, _side;
    readonly Splitter _splitter;
    IDocumentView? _active, _preview, _partner;
    bool _split;

    IDocumentView? _pressed, _menuDoc;
    bool _dragging;
    int _pressX;

    public DocumentArea()
    {
        Name = "docArea";
        _side = new Panel { Name = "pnlDocSide", Align = Align.Right, BorderStyle = BorderStyle.None, Visible = false };
        _splitter = new Splitter { Name = "splDocSide", Align = Align.Right, MinSize = 120, Visible = false };
        _splitter.SetBounds(0, 0, 4, 4);
        _main = new Panel { Name = "pnlDocMain", Align = Align.Client, BorderStyle = BorderStyle.None };
        Add(_side);
        Add(_splitter);
        Add(_main);
    }

    public IReadOnlyList<IDocumentView> Documents => _docs;
    public IDocumentView? Active => _active;
    public IDocumentView? Preview => _preview;

    /// <summary>The document shown beside the active one in split view, or null.</summary>
    public IDocumentView? SplitPartner => _partner;

    /// <summary>Raised when the active document changed (null when the last one closed).</summary>
    public event Action<IDocumentView?>? ActiveChanged;

    /// <summary>Raised when the user asked to close a tab (close button, middle-click). The handler decides.</summary>
    public event Action<IDocumentView>? CloseRequested;

    /// <summary>Raised when the tab order or the set of tabs changed (for the session).</summary>
    public event Action? TabsChanged;

    /// <summary>Builds a tab's context menu.</summary>
    public Func<IDocumentView, PopupMenu?>? TabMenuFactory { get; set; }

    /// <summary>For split view: given a document, the pair to show as (left, right), or null if it has no partner open.</summary>
    public Func<IDocumentView, (IDocumentView Left, IDocumentView Right)?>? SplitPair { get; set; }

    /// <summary>Split view: the active document beside its <see cref="SplitPair"/> partner.</summary>
    public bool SplitView
    {
        get => _split;
        set { _split = value; UpdatePanes(); }
    }

    public bool IsPreview(IDocumentView doc) => _preview == doc;

    /// <summary>Adds a tab (at the end). A preview tab replaces nothing by itself: the shell closes the old preview first.</summary>
    public void Add(IDocumentView doc, bool preview = false, bool activate = true)
    {
        if (_docs.Contains(doc)) { if (activate) Activate(doc); return; }
        _docs.Add(doc);
        var view = doc.View;
        view.Align = Align.Client;
        view.Visible = false;
        _main.Add(view);
        if (preview) _preview = doc;
        Action changed = () =>
        {
            if (doc.IsDirty && _preview == doc) _preview = null;   // editing a preview keeps it
            Invalidate();
        };
        _subscriptions[doc] = changed;
        doc.StateChanged += changed;
        TabsChanged?.Invoke();
        if (activate || _active == null) Activate(doc);
        else Invalidate();
    }

    /// <summary>Turns the preview tab into an ordinary one.</summary>
    public void MakePermanent(IDocumentView doc)
    {
        if (_preview != doc) return;
        _preview = null;
        Invalidate();
        TabsChanged?.Invoke();
    }

    /// <summary>Removes a tab without asking anything; the next tab (or the previous) becomes active.</summary>
    public void Remove(IDocumentView doc)
    {
        int i = _docs.IndexOf(doc);
        if (i < 0) return;
        if (OwningForm?.FocusedControl is { } f && f.IsSelfOrDescendantOf(doc.View)) OwningForm.SetFocus(null);
        _docs.RemoveAt(i);
        if (_subscriptions.Remove(doc, out var changed)) doc.StateChanged -= changed;
        doc.View.Parent?.Remove(doc.View);
        if (_preview == doc) _preview = null;
        if (_partner == doc) _partner = null;
        TabsChanged?.Invoke();
        if (_active == doc)
        {
            _active = null;
            if (_docs.Count > 0) Activate(_docs[Math.Min(i, _docs.Count - 1)]);
            else { UpdatePanes(); ActiveChanged?.Invoke(null); }
        }
        else UpdatePanes();
    }

    /// <summary>Makes a document the active tab and shows it (and its partner in split view).</summary>
    public void Activate(IDocumentView doc)
    {
        if (!_docs.Contains(doc)) return;
        bool changed = _active != doc;
        _active = doc;
        UpdatePanes();
        doc.Focus();
        if (changed) ActiveChanged?.Invoke(doc);
    }

    /// <summary>Moves a tab to position <paramref name="index"/>.</summary>
    public void Move(IDocumentView doc, int index)
    {
        int i = _docs.IndexOf(doc);
        if (i < 0) return;
        index = Math.Clamp(index, 0, _docs.Count - 1);
        if (index == i) return;
        _docs.RemoveAt(i);
        _docs.Insert(index, doc);
        Invalidate();
        TabsChanged?.Invoke();
    }

    void UpdatePanes()
    {
        IDocumentView? left = _active, right = null;
        if (_split && _active != null && SplitPair?.Invoke(_active) is { } pair && _docs.Contains(pair.Left) && _docs.Contains(pair.Right))
            (left, right) = pair;
        _partner = right == null ? null : left == _active ? right : left;
        foreach (var doc in _docs)
        {
            var view = doc.View;
            if (doc == right)
            {
                if (view.Parent != _side) _side.Add(view);
                view.Visible = true;
            }
            else
            {
                if (view.Parent != _main) _main.Add(view);
                view.Visible = doc == left;
            }
        }
        bool showSide = right != null;
        if (showSide && !_side.Visible)
        {
            // Half the area; before the first layout (session restore) that waits for a size.
            _sideSizePending = Width <= 0;
            if (!_sideSizePending) _side.Width = Math.Max(Px(200), (Width - Px(4)) / 2);
        }
        _side.Visible = showSide;
        _splitter.Visible = showSide;
        InvalidateLayout();
        OwningForm?.InvalidateAll();
    }

    bool _sideSizePending;

    protected override void OnResize()
    {
        if (_sideSizePending && Width > 0)
        {
            _sideSizePending = false;
            _side.Width = Math.Max(Px(200), (Width - Px(4)) / 2);
        }
        base.OnResize();
    }

    // ---- Tab strip ----

    int StripHeight => Px(30);

    protected override Rect GetClientRect() => new(0, StripHeight, Width, Math.Max(0, Height - StripHeight));

    // Measured bold (as the active tab is drawn), so a tab doesn't truncate when it becomes active.
    RenderFont TabFont(bool italic) =>
        new(FontFace.Resolve(EffectiveFont with { Italic = italic, Bold = true }), (float)(EffectiveFont.Size * UiScale));

    /// <summary>The caption a tab shows: the title, plus <c>" *"</c> when there are unsaved changes.</summary>
    public static string TabCaption(IDocumentView doc) => doc.Title + (doc.IsDirty ? " *" : "");

    /// <summary>Each tab's rectangle, in this control's coordinates.</summary>
    public List<Rect> TabRects()
    {
        var list = new List<Rect>();
        int x = 0;
        foreach (var doc in _docs)
        {
            int w = (int)Math.Ceiling(TabFont(IsPreview(doc)).MeasureWidth(TabCaption(doc))) + Px(16) + Px(20);
            list.Add(new Rect(x, 0, w, StripHeight));
            x += w;
        }
        return list;
    }

    /// <summary>The close button of tab <paramref name="index"/>.</summary>
    public Rect CloseButtonRect(int index)
    {
        var r = TabRects()[index];
        int s = Px(16);
        return new Rect(r.X + r.Width - s - Px(6), r.Y + (r.Height - s) / 2, s, s);
    }

    public int TabIndexAt(int x, int y)
    {
        if (y < 0 || y >= StripHeight) return -1;
        var rects = TabRects();
        for (int i = 0; i < rects.Count; i++)
            if (x >= rects[i].X && x < rects[i].X + rects[i].Width) return i;
        return -1;
    }

    static bool In(Rect r, int x, int y) => x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int i = TabIndexAt(e.X, e.Y);
        if (i < 0) return;
        var doc = _docs[i];
        switch (e.Button)
        {
            case MouseButton.Middle: CloseRequested?.Invoke(doc); return;
            case MouseButton.Right: _menuDoc = doc; return;
        }
        if (In(CloseButtonRect(i), e.X, e.Y)) { CloseRequested?.Invoke(doc); return; }
        if (e.Clicks >= 2) MakePermanent(doc);
        Activate(doc);
        _pressed = doc;
        _pressX = e.X;
        _dragging = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressed == null) return;
        if (!_dragging && Math.Abs(e.X - _pressX) > Px(5)) _dragging = true;
        if (!_dragging) return;
        var rects = TabRects();
        if (rects.Count == 0) return;
        int target = e.X < 0 ? 0 : rects.FindIndex(r => e.X >= r.X && e.X < r.X + r.Width);
        if (target < 0) target = rects.Count - 1;
        Move(_pressed, target);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = null;
        _dragging = false;
    }

    public override PopupMenu? ContextMenu
    {
        get
        {
            if (_menuDoc is not { } doc) return null;
            _menuDoc = null;
            return TabMenuFactory?.Invoke(doc);
        }
        set { }
    }

    public override string? GetHintAt(int x, int y, out Rect region)
    {
        int i = TabIndexAt(x, y);
        if (i >= 0)
        {
            region = TabRects()[i];
            return _docs[i].Path ?? _docs[i].Title;
        }
        region = new Rect(0, 0, Width, StripHeight);
        return null;
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        canvas.FillRect(new Rect(0, 0, Width, StripHeight), t.PanelBackground);
        canvas.FillRect(new Rect(0, StripHeight - canvas.Px(1), Width, canvas.Px(1)), t.PanelBorder);
        canvas.FillRect(new Rect(0, StripHeight, Width, Math.Max(0, Height - StripHeight)), t.FormBackground);
        var rects = TabRects();
        for (int i = 0; i < rects.Count; i++)
        {
            var doc = _docs[i];
            var r = rects[i];
            bool active = doc == _active, partner = doc == _partner;
            if (active || partner)
            {
                canvas.FillRect(new Rect(r.X, r.Y, r.Width, r.Height), t.FormBackground);
                if (active) canvas.FillRect(new Rect(r.X, r.Y, r.Width, canvas.Px(2)), t.Accent);
            }
            canvas.FillRect(new Rect(r.X + r.Width - canvas.Px(1), r.Y + canvas.Px(6), canvas.Px(1), r.Height - canvas.Px(12)), t.PanelBorder);
            var font = EffectiveFont with { Italic = IsPreview(doc), Bold = active };
            var text = new Rect(r.X + Px(8), r.Y, r.Width - Px(8) - Px(26), r.Height);
            canvas.DrawText(TabCaption(doc), font, t.Text, text, TextAlign.Left, VerticalAlign.Center, ellipsis: true);
            var c = CloseButtonRect(i);
            float pad = Px(4);
            var color = active ? t.Text : t.TextDisabled;
            canvas.DrawLine(new PointF(c.X + pad, c.Y + pad), new PointF(c.X + c.Width - pad, c.Y + c.Height - pad), color, canvas.Px(1) * 1.2f);
            canvas.DrawLine(new PointF(c.X + c.Width - pad, c.Y + pad), new PointF(c.X + pad, c.Y + c.Height - pad), color, canvas.Px(1) * 1.2f);
        }
    }
}
