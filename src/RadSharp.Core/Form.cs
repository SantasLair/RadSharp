using RadSharp.Drawing;

namespace RadSharp;

/// <summary>
/// The root of a form: owns input routing, focus, the popup layer and repaint tracking.
/// Its width/height in the form file are the design size.
/// </summary>
public class Form : Control
{
    bool _dirtyAll = true;
    Rect? _dirty;
    string _caption = "";

    Control? _hover, _capture, _focused;
    MouseButton _captureButton;
    readonly List<Control> _popups = [];
    readonly List<Control?> _popupOwners = [];
    bool _showFocusCues;

    public Form()
    {
        SetDefaultSize(640, 480);
        Simulate = new InputSimulator(this);
    }

    [Published(Category = "Appearance")]
    public string Caption { get => _caption; set { _caption = value; CaptionChanged?.Invoke(this); } }

    /// <summary>Raised when <see cref="Caption"/> changes, so a host can retitle its window.</summary>
    public event Action<Form>? CaptionChanged;

    /// <summary>Background colour; null uses the theme's.</summary>
    [Published(Category = "Appearance")] public Color? Color { get; set; }

    /// <summary>The form's size as designed (at 100 %); set when the form is loaded.</summary>
    public int DesignWidth { get; internal set; }
    public int DesignHeight { get; internal set; }

    /// <summary>The UI scale factor lengths were multiplied by when the form was loaded.</summary>
    public double Scale { get; internal set; } = 1;

    public Theme Theme { get; set; } = Theme.Default;

    /// <summary>
    /// The scale generated <c>InitializeComponent()</c> code loads forms at. Hosts set it at startup
    /// (the SDL host uses the display's content scale).
    /// </summary>
    public static double DefaultScale { get; set; } = 1;

    /// <summary>
    /// For a form class generated from a form file: the root class the file was designed as (e.g. "Form").
    /// The writer saves that class name and compares against its defaults, not the generated subclass's.
    /// </summary>
    protected virtual string? DesignedClassName => null;

    internal string? DesignedClass => DesignedClassName;

    /// <summary>Feeds synthetic input through the same code paths as real input (for tests).</summary>
    public InputSimulator Simulate { get; }

    /// <summary>Resizes the form (e.g. to its window) and lays it out.</summary>
    public void Resize(int width, int height)
    {
        SetBounds(Left, Top, width, height);
        PerformLayout();
    }

    /// <summary>Lays out the form and every open popup.</summary>
    public new void PerformLayout()
    {
        base.PerformLayout();
        foreach (var p in _popups) p.PerformLayout();
    }

    // =====================================================================
    // Time, text input area
    // =====================================================================

    readonly List<Control> _tickers = [];

    /// <summary>The host's clock in milliseconds, as of the last <see cref="ProcessTick"/>.</summary>
    public double Now { get; private set; }

    /// <summary>Registers a control for <see cref="Control.OnTick"/> calls (caret blink, timers).</summary>
    public void AddTicker(Control control)
    {
        if (!_tickers.Contains(control)) _tickers.Add(control);
    }

    public void RemoveTicker(Control control) => _tickers.Remove(control);

    /// <summary>Called by the host on every loop iteration with its monotonic clock.</summary>
    public void ProcessTick(double nowMs)
    {
        Now = nowMs;
        foreach (var t in _tickers.ToArray()) t.OnTick(nowMs);
    }

    /// <summary>
    /// Where text is being entered (form pixels): the host positions the IME candidate window there.
    /// Set by the focused text control; null when no text control has focus.
    /// </summary>
    public Rect? TextInputArea { get; set; }

    protected override void Paint(Canvas canvas) =>
        canvas.FillRect(new Rect(0, 0, Width, Height), Color ?? canvas.Theme.FormBackground);


    // =====================================================================
    // Repaint tracking (form coordinates)
    // =====================================================================

    public void InvalidateAll() => _dirtyAll = true;

    public void InvalidateRect(Rect r)
    {
        if (_dirtyAll || r.Width <= 0 || r.Height <= 0) return;
        _dirty = _dirty is { } d ? Union(d, r) : r;
    }

    /// <summary>Returns the area needing a repaint since the last call, clipped to the form, and clears it.</summary>
    public bool TryTakeDirtyRect(out Rect rect)
    {
        var full = new Rect(0, 0, Width, Height);
        Rect? result = _dirtyAll ? full : _dirty is { } d ? Intersect(d, full) : null;
        _dirtyAll = false;
        _dirty = null;
        rect = result ?? default;
        return result is { Width: > 0, Height: > 0 };
    }

    static Rect Union(Rect a, Rect b)
    {
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new Rect(x, y, Math.Max(a.X + a.Width, b.X + b.Width) - x, Math.Max(a.Y + a.Height, b.Y + b.Height) - y);
    }

    static Rect? Intersect(Rect a, Rect b)
    {
        int x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        int r = Math.Min(a.X + a.Width, b.X + b.Width), bt = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return r > x && bt > y ? new Rect(x, y, r - x, bt - y) : null;
    }

    // =====================================================================
    // Focus (spec, Appendix C)
    // =====================================================================

    /// <summary>The control with keyboard focus, or null.</summary>
    public Control? FocusedControl => _focused;

    /// <summary>
    /// True after keyboard navigation (Tab), false after a mouse press: controls draw focus rings
    /// only while this is set.
    /// </summary>
    public bool ShowFocusCues
    {
        get => _showFocusCues;
        private set { if (_showFocusCues == value) return; _showFocusCues = value; _focused?.Invalidate(); }
    }

    /// <summary>Moves focus to <paramref name="control"/> (null clears it). Returns false if it can't take focus.</summary>
    public bool SetFocus(Control? control)
    {
        if (control != null && (control.OwningForm != this || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled))
            return false;
        if (control == _focused) return true;
        var old = _focused;
        _focused = control;
        old?.OnLostFocus();
        control?.OnGotFocus();
        return true;
    }

    /// <summary>Visible, enabled tab stops in Tab order: depth-first; among siblings, tabOrder ≥ 0 ascending, then -1 in child order.</summary>
    public List<Control> GetTabOrderList()
    {
        var list = new List<Control>();
        Collect(this, list);
        return list;

        static void Collect(Control parent, List<Control> list)
        {
            var ordered = parent.Children
                .Select((c, i) => (c, i))
                .OrderBy(t => t.c.TabOrder < 0 ? 1 : 0)
                .ThenBy(t => t.c.TabOrder < 0 ? t.i : t.c.TabOrder)
                .ThenBy(t => t.i);
            foreach (var (c, _) in ordered)
            {
                if (!c.Visible || !c.Enabled) continue;
                if (c.TabStop) list.Add(c);
                Collect(c, list);
            }
        }
    }

    /// <summary>Moves focus to the next (or previous) tab stop, wrapping around.</summary>
    public void SelectNext(bool forward = true)
    {
        var list = GetTabOrderList();
        if (list.Count == 0) return;
        int i = _focused == null ? -1 : list.IndexOf(_focused);
        int next = i < 0 ? (forward ? 0 : list.Count - 1) : (i + (forward ? 1 : -1) + list.Count) % list.Count;
        SetFocus(list[next]);
    }

    /// <summary>
    /// Hiding, disabling or removing a control clears hover, capture and focus inside it, and closes
    /// popups it (or anything inside it) opened or contains.
    /// </summary>
    internal void ControlBecameUnavailable(Control control)
    {
        if (_hover != null && _hover.IsSelfOrDescendantOf(control)) { var h = _hover; _hover = null; h.SetHovered(false); }
        if (_capture != null && _capture.IsSelfOrDescendantOf(control)) _capture = null;
        if (_focused != null && _focused.IsSelfOrDescendantOf(control)) SetFocus(null);
        for (int i = _popups.Count - 1; i >= 0 && i < _popups.Count; i--)
        {
            var owner = _popupOwners[i];
            if (_popups[i].IsSelfOrDescendantOf(control) || (owner != null && owner.IsSelfOrDescendantOf(control)))
                ClosePopupAt(i);
        }
    }

    // =====================================================================
    // Popup layer
    // =====================================================================

    /// <summary>Open popups, bottom to top.</summary>
    public IReadOnlyList<Control> Popups => _popups;

    public bool IsPopupOpen(Control popup) => _popups.Contains(popup);

    /// <summary>
    /// Shows <paramref name="popup"/> above the form at (<paramref name="x"/>, <paramref name="y"/>) in form
    /// coordinates, moved if needed to fit inside the form. <paramref name="owner"/> is the control that
    /// opened it; hiding, disabling or removing the owner closes the popup.
    /// </summary>
    public void OpenPopup(Control popup, int x, int y, Control? owner = null)
    {
        if (popup.Parent != null) throw new InvalidOperationException("A popup must not have a parent.");
        if (_popups.Contains(popup)) ClosePopup(popup);
        x = Math.Max(0, Math.Min(x, Width - popup.Width));
        y = Math.Max(0, Math.Min(y, Height - popup.Height));
        popup.PopupForm = this;
        popup.SetBounds(x, y, popup.Width, popup.Height);
        _popups.Add(popup);
        _popupOwners.Add(owner);
        popup.PerformLayout();
        InvalidateAll();
    }

    /// <summary>Closes <paramref name="popup"/> and every popup opened above it.</summary>
    public void ClosePopup(Control popup)
    {
        int i = _popups.IndexOf(popup);
        if (i >= 0) ClosePopupAt(i);
    }

    public void CloseAllPopups()
    {
        if (_popups.Count > 0) ClosePopupAt(0);
    }

    void ClosePopupAt(int index)
    {
        for (int i = _popups.Count - 1; i >= index; i--)
        {
            var p = _popups[i];
            _popups.RemoveAt(i);
            _popupOwners.RemoveAt(i);
            if (_hover != null && _hover.IsSelfOrDescendantOf(p)) { var h = _hover; _hover = null; h.SetHovered(false); }
            if (_capture != null && _capture.IsSelfOrDescendantOf(p)) _capture = null;
            if (_focused != null && _focused.IsSelfOrDescendantOf(p)) SetFocus(null);
            p.PopupForm = null;
            p.OnPopupClosed();
        }
        InvalidateAll();
    }

    // =====================================================================
    // Input routing (spec, Appendix C). Coordinates are form pixels.
    // =====================================================================

    /// <summary>The cursor for the control under the pointer (or the one capturing the mouse).</summary>
    public Cursor CurrentCursor => (_capture ?? _hover)?.Cursor ?? Cursor.Default;

    /// <summary>The deepest control at a form point: open popups first (topmost first), then the form.</summary>
    public Control HitTestForm(int x, int y)
    {
        for (int i = _popups.Count - 1; i >= 0; i--)
        {
            var p = _popups[i];
            if (x >= p.Left && y >= p.Top && x < p.Left + p.Width && y < p.Top + p.Height)
                return p.HitTest(x - p.Left, y - p.Top);
        }
        return HitTest(x, y);
    }

    bool InAnyPopup(Control c) => _popups.Any(c.IsSelfOrDescendantOf);

    static MouseEventArgs Local(Control c, int x, int y, MouseButton b, KeyModifiers m, int clicks = 1) =>
        new(x - c.AbsoluteLeft, y - c.AbsoluteTop, b, m, clicks);

    void UpdateHover(int x, int y)
    {
        var hit = HitTestForm(x, y);
        // While the mouse is captured, only the capturing control can be hovered.
        Control? hover = _capture == null ? hit : hit.IsSelfOrDescendantOf(_capture) ? _capture : null;
        if (hover != null && !hover.IsEffectivelyEnabled) hover = null;
        if (hover == _hover) return;
        var old = _hover;
        _hover = hover;
        old?.SetHovered(false);
        hover?.SetHovered(true);
    }

    public void ProcessMouseMove(int x, int y, KeyModifiers modifiers = KeyModifiers.None)
    {
        PerformLayout();
        UpdateHover(x, y);
        var target = _capture ?? _hover;
        if (target != null && target.IsEffectivelyEnabled)
            target.OnMouseMove(Local(target, x, y, MouseButton.Left, modifiers));
    }

    public void ProcessMouseDown(int x, int y, MouseButton button, KeyModifiers modifiers = KeyModifiers.None, int clicks = 1)
    {
        PerformLayout();
        var hit = HitTestForm(x, y);

        // A press outside every popup closes them all and is consumed.
        if (_popups.Count > 0 && !InAnyPopup(hit))
        {
            CloseAllPopups();
            UpdateHover(x, y);
            return;
        }
        if (_capture != null) return;  // another button is already down
        if (!hit.IsEffectivelyEnabled) return;

        ShowFocusCues = false;
        if (hit.TabStop) SetFocus(hit);
        _capture = hit;
        _captureButton = button;
        UpdateHover(x, y);
        hit.OnMouseDown(Local(hit, x, y, button, modifiers, clicks));
    }

    public void ProcessMouseUp(int x, int y, MouseButton button, KeyModifiers modifiers = KeyModifiers.None)
    {
        PerformLayout();
        if (_capture == null || button != _captureButton) return;
        var target = _capture;
        _capture = null;
        target.OnMouseUp(Local(target, x, y, button, modifiers));
        // A click fires on a left release only if the pointer is still over the captured control.
        if (button == MouseButton.Left && target.OwningForm == this && target.IsEffectivelyEnabled
            && HitTestForm(x, y).IsSelfOrDescendantOf(target))
            target.OnClick();
        UpdateHover(x, y);
    }

    /// <summary>Wheel events bubble from the control under the pointer up through its parents until handled.</summary>
    public void ProcessMouseWheel(int x, int y, float deltaX, float deltaY, KeyModifiers modifiers = KeyModifiers.None)
    {
        PerformLayout();
        for (Control? c = HitTestForm(x, y); c != null; c = c.Parent)
        {
            if (!c.IsEffectivelyEnabled) continue;
            var e = new MouseWheelEventArgs(x - c.AbsoluteLeft, y - c.AbsoluteTop, deltaX, deltaY, modifiers);
            c.OnMouseWheel(e);
            if (e.Handled) break;
        }
    }

    public void ProcessKeyDown(Key key, KeyModifiers modifiers = KeyModifiers.None, bool repeat = false)
    {
        PerformLayout();
        var e = new KeyEventArgs(key, modifiers, repeat);
        bool plainTab = key == Key.Tab && (modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Meta)) == 0;

        // Tab closes all popups, then moves focus (unless the focused control wants tabs).
        if (plainTab && !(_popups.Count == 0 && _focused is { AcceptsTab: true }))
        {
            CloseAllPopups();
            ShowFocusCues = true;
            SelectNext(forward: !e.Shift);
            return;
        }

        // While a popup is open, keys go to the topmost popup; Escape closes it if unhandled.
        if (_popups.Count > 0)
        {
            var top = _popups[^1];
            var target = _focused != null && _focused.IsSelfOrDescendantOf(top) ? _focused : top;
            target.OnKeyDown(e);
            if (!e.Handled && key == Key.Escape) ClosePopup(top);
            return;
        }

        var focusTarget = _focused ?? this;
        if (focusTarget.IsEffectivelyEnabled) focusTarget.OnKeyDown(e);
        if (e.Handled) return;

        if (IsShortcutCandidate(e) && OfferShortcut(this, e)) return;
        if (key is Key.Enter or Key.Escape && modifiers == KeyModifiers.None) OfferDialogKey(this, e);
    }

    public void ProcessKeyUp(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var e = new KeyEventArgs(key, modifiers);
        var target = _popups.Count > 0 ? _popups[^1] : _focused ?? this;
        if (target.IsEffectivelyEnabled) target.OnKeyUp(e);
    }

    /// <summary>Typed text goes to the focused control; it is discarded while a popup is open.</summary>
    public void ProcessTextInput(string text)
    {
        if (_popups.Count > 0 || string.IsNullOrEmpty(text)) return;
        var target = _focused ?? this;
        if (target.IsEffectivelyEnabled) target.OnTextInput(new TextInputEventArgs(text));
    }

    /// <summary>An IME composition update for the focused control (discarded while a popup is open).</summary>
    public void ProcessTextComposition(string text, int cursor, int selectionLength = 0)
    {
        if (_popups.Count > 0) return;
        var target = _focused ?? this;
        if (target.IsEffectivelyEnabled) target.OnTextComposition(new TextCompositionEventArgs(text, cursor, selectionLength));
    }

    static bool IsShortcutCandidate(KeyEventArgs e) =>
        (e.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Meta)) != 0
        || e.Key is >= Key.F1 and <= Key.F12;

    static bool OfferShortcut(Control c, KeyEventArgs e)
    {
        if (!c.Visible || !c.Enabled) return false;
        if (c.ProcessShortcut(e)) { e.Handled = true; return true; }
        foreach (var child in c.Children)
            if (OfferShortcut(child, e)) return true;
        return false;
    }

    static bool OfferDialogKey(Control c, KeyEventArgs e)
    {
        if (!c.Visible || !c.Enabled) return false;
        if (c.ProcessDialogKey(e)) { e.Handled = true; return true; }
        foreach (var child in c.Children)
            if (OfferDialogKey(child, e)) return true;
        return false;
    }
}

/// <summary>Synthetic input for tests; it goes through exactly the same routines as real input.</summary>
public sealed class InputSimulator(Form form)
{
    public void MoveTo(int x, int y, KeyModifiers modifiers = KeyModifiers.None) => form.ProcessMouseMove(x, y, modifiers);

    public void Press(int x, int y, MouseButton button = MouseButton.Left, KeyModifiers modifiers = KeyModifiers.None, int clicks = 1)
    {
        form.ProcessMouseMove(x, y, modifiers);
        form.ProcessMouseDown(x, y, button, modifiers, clicks);
    }

    /// <summary>A double click: two press/release pairs, the second reporting two clicks.</summary>
    public void DoubleClick(int x, int y)
    {
        Click(x, y);
        Press(x, y, clicks: 2);
        Release(x, y);
    }

    /// <summary>An IME composition update, e.g. <c>Compose("にほ", 2)</c>; <c>Compose("", 0)</c> ends it.</summary>
    public void Compose(string text, int cursor) => form.ProcessTextComposition(text, cursor);

    public void Release(int x, int y, MouseButton button = MouseButton.Left, KeyModifiers modifiers = KeyModifiers.None) =>
        form.ProcessMouseUp(x, y, button, modifiers);

    public void Click(int x, int y, MouseButton button = MouseButton.Left, KeyModifiers modifiers = KeyModifiers.None)
    {
        Press(x, y, button, modifiers);
        Release(x, y, button, modifiers);
    }

    /// <summary>Clicks the centre of a control.</summary>
    public void Click(Control control, MouseButton button = MouseButton.Left)
    {
        form.PerformLayout();
        Click(control.AbsoluteLeft + control.Width / 2, control.AbsoluteTop + control.Height / 2, button);
    }

    public void Drag(int x1, int y1, int x2, int y2, int steps = 4, MouseButton button = MouseButton.Left)
    {
        Press(x1, y1, button);
        for (int i = 1; i <= steps; i++)
            form.ProcessMouseMove(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
        Release(x2, y2, button);
    }

    public void Wheel(int x, int y, float deltaY, float deltaX = 0) => form.ProcessMouseWheel(x, y, deltaX, deltaY);

    public void Key(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        form.ProcessKeyDown(key, modifiers);
        form.ProcessKeyUp(key, modifiers);
    }

    /// <summary>Presses a shortcut given as text, e.g. <c>"Mod+S"</c>.</summary>
    public void Key(string shortcut)
    {
        var s = Shortcut.Parse(shortcut);
        Key(s.Key, s.Modifiers);
    }

    public void Type(string text) => form.ProcessTextInput(text);
}
