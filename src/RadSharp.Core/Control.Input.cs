namespace RadSharp;

public partial class Control
{
    // ---- Events (sender first, Delphi style) ----

    [Published] public event NotifyEvent? Click;
    [Published] public event MouseEvent? MouseDown;
    [Published] public event MouseEvent? MouseUp;
    [Published] public event MouseEvent? MouseMove;
    [Published] public event NotifyEvent? MouseEnter;
    [Published] public event NotifyEvent? MouseLeave;
    [Published] public event MouseWheelEvent? MouseWheel;
    [Published] public event KeyEvent? KeyDown;
    [Published] public event KeyEvent? KeyUp;
    [Published] public event TextInputEvent? TextInput;
    [Published("onFocus")] public event NotifyEvent? GotFocus;
    [Published("onBlur")] public event NotifyEvent? LostFocus;
    [Published("onResize")] public event NotifyEvent? Resized;

    PopupMenu? _contextMenu;

    /// <summary>The name of the PopupMenu component shown when this control is right-clicked.</summary>
    [Published("popupMenu", Category = "Behavior")] public string PopupMenuName { get; set; } = "";

    /// <summary>The context menu: set from code, or found by <see cref="PopupMenuName"/> on the form.</summary>
    public PopupMenu? ContextMenu
    {
        get => _contextMenu ?? (PopupMenuName.Length > 0 ? OwningForm?.FindControl(PopupMenuName) as PopupMenu : null);
        set => _contextMenu = value;
    }

    /// <summary>The mouse cursor shown while the pointer is over this control.</summary>
    [Published(Category = "Appearance")] public Cursor Cursor { get; set; } = Cursor.Default;

    /// <summary>True while the pointer is over this control (and no other control has captured the mouse).</summary>
    public bool IsHovered { get; private set; }

    /// <summary>True if this control has keyboard focus.</summary>
    public bool Focused => OwningForm?.FocusedControl == this;

    /// <summary>
    /// If true, Tab and Shift+Tab reach this control's <see cref="OnKeyDown"/> instead of moving focus
    /// (e.g. a multi-line editor).
    /// </summary>
    public virtual bool AcceptsTab => false;

    /// <summary>Gives this control keyboard focus. Returns false if it's hidden, disabled or not on a form.</summary>
    public bool Focus() => OwningForm is { } f && f.SetFocus(this);

    // ---- Overridable handlers; the base versions raise the events ----

    protected internal virtual void OnClick() => Click?.Invoke(this);
    protected internal virtual void OnMouseDown(MouseEventArgs e) => MouseDown?.Invoke(this, e);
    protected internal virtual void OnMouseUp(MouseEventArgs e) => MouseUp?.Invoke(this, e);
    protected internal virtual void OnMouseMove(MouseEventArgs e) => MouseMove?.Invoke(this, e);
    protected internal virtual void OnMouseEnter() => MouseEnter?.Invoke(this);
    protected internal virtual void OnMouseLeave() => MouseLeave?.Invoke(this);
    protected internal virtual void OnMouseWheel(MouseWheelEventArgs e) => MouseWheel?.Invoke(this, e);
    protected internal virtual void OnKeyDown(KeyEventArgs e) => KeyDown?.Invoke(this, e);
    protected internal virtual void OnKeyUp(KeyEventArgs e) => KeyUp?.Invoke(this, e);
    protected internal virtual void OnTextInput(TextInputEventArgs e) => TextInput?.Invoke(this, e);
    protected internal virtual void OnGotFocus() { Invalidate(); GotFocus?.Invoke(this); }
    protected internal virtual void OnLostFocus() { Invalidate(); LostFocus?.Invoke(this); }

    /// <summary>An IME composition changed while this control has focus.</summary>
    protected internal virtual void OnTextComposition(TextCompositionEventArgs e) { }

    /// <summary>Called on every host tick (about 20+ times a second) while registered with <see cref="Form.AddTicker"/>.</summary>
    protected internal virtual void OnTick(double nowMs) { }

    /// <summary>Raised after the control's size changed during layout.</summary>
    protected virtual void OnResize() => Resized?.Invoke(this);

    /// <summary>
    /// Offered a Ctrl/Alt/Meta + key or F1–F12 press the focused control didn't handle (spec,
    /// Appendix C). Return true if handled; menus match their items' shortcuts here.
    /// </summary>
    protected internal virtual bool ProcessShortcut(KeyEventArgs e) => false;

    /// <summary>
    /// Offered an Enter or Escape press nobody handled, so a default or cancel button can click itself.
    /// </summary>
    protected internal virtual bool ProcessDialogKey(KeyEventArgs e) => false;

    /// <summary>Called when this control was shown in the popup layer and has now been closed.</summary>
    protected internal virtual void OnPopupClosed() { }

    internal void SetHovered(bool hovered)
    {
        if (IsHovered == hovered) return;
        IsHovered = hovered;
        Invalidate();
        if (hovered) OnMouseEnter(); else OnMouseLeave();
    }

    // ---- Hit testing ----

    /// <summary>
    /// The deepest visible control at (<paramref name="x"/>, <paramref name="y"/>) in this control's
    /// coordinates, assuming the point is inside this control. Children are clipped to
    /// <see cref="ChildClipRect"/>; later children are on top.
    /// </summary>
    public Control HitTest(int x, int y)
    {
        var clip = ChildClipRect;
        if (x < clip.X || y < clip.Y || x >= clip.X + clip.Width || y >= clip.Y + clip.Height) return this;
        var (ox, oy) = ChildOffset;
        for (int i = _children.Count - 1; i >= 0; i--)
        {
            var c = _children[i];
            if (!c.IsShown) continue;
            int cx = x - ox - c._left, cy = y - oy - c._top;
            if (cx >= 0 && cy >= 0 && cx < c._width && cy < c._height) return c.HitTest(cx, cy);
        }
        return this;
    }

    /// <summary>True if the form point lies inside this control's visible area.</summary>
    internal bool ContainsFormPoint(int x, int y) =>
        x >= AbsoluteLeft && y >= AbsoluteTop && x < AbsoluteLeft + _width && y < AbsoluteTop + _height;
}
