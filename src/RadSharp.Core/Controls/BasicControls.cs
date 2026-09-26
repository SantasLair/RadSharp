using RadSharp.Drawing;

namespace RadSharp;

public enum BorderStyle { None, Single }

public class Panel : Control
{
    string _caption = "";

    [Published(Category = "Appearance")] public string Caption { get => _caption; set { _caption = value; Invalidate(); } }
    [Published(Category = "Appearance")] public Color? Color { get; set; }
    [Published(Category = "Appearance")] public BorderStyle BorderStyle { get; set; } = BorderStyle.Single;

    protected override void Paint(Canvas canvas)
    {
        var r = new Rect(0, 0, Width, Height);
        canvas.FillRect(r, Color ?? canvas.Theme.PanelBackground);
        if (BorderStyle == BorderStyle.Single) canvas.DrawBorder(r, canvas.Theme.PanelBorder, canvas.Px(1));
        canvas.DrawText(Caption, canvas.DefaultFont, canvas.Theme.Text, r, TextAlign.Center, VerticalAlign.Center);
    }

}

public class Label : Control
{
    string _caption = "";

    public Label() => SetDefaultSize(80, 20);

    [Published(Category = "Appearance")] public string Caption { get => _caption; set { _caption = value; Invalidate(); } }
    [Published(Category = "Appearance")] public TextAlign Alignment { get; set; }
    [Published(Category = "Appearance")] public VerticalAlign Layout { get; set; }

    protected override void Paint(Canvas canvas)
    {
        var color = IsEffectivelyEnabled ? canvas.Theme.Text : canvas.Theme.TextDisabled;
        canvas.DrawText(Caption, canvas.DefaultFont, color, new Rect(0, 0, Width, Height), Alignment, Layout);
    }

}

public class Button : Control
{
    string _caption = "";
    bool _mouseDown, _spaceDown;

    public Button()
    {
        SetDefaultSize(80, 28);
        TabStop = true;
    }

    [Published(Category = "Appearance")] public string Caption { get => _caption; set { _caption = value; Invalidate(); } }

    /// <summary>Enter clicks this button when no control handled the key (Delphi's <c>Default</c>).</summary>
    [Published(Category = "Behavior")] public bool Default { get; set; }

    /// <summary>Escape clicks this button when no control handled the key (Delphi's <c>Cancel</c>).</summary>
    [Published(Category = "Behavior")] public bool Cancel { get; set; }

    /// <summary>Clicking the button sets its form's <see cref="Form.ModalResult"/>, which closes a modal dialog.</summary>
    [Published(Category = "Behavior")] public ModalResult ModalResult { get; set; }

    protected internal override void OnClick()
    {
        base.OnClick();
        if (ModalResult != ModalResult.None && OwningForm is { } form) form.ModalResult = ModalResult;
    }

    /// <summary>True while the button is shown pushed in (mouse held over it, or Space held).</summary>
    public bool IsPressed => (_mouseDown && IsHovered) || _spaceDown;

    /// <summary>Clicks the button from code, if it is enabled.</summary>
    public void PerformClick()
    {
        if (IsEffectivelyEnabled) OnClick();
    }

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButton.Left) { _mouseDown = true; Invalidate(); }
        base.OnMouseDown(e);
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButton.Left) { _mouseDown = false; Invalidate(); }
        base.OnMouseUp(e);
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Modifiers != KeyModifiers.None) return;
        if (e.Key == Key.Space) { _spaceDown = true; Invalidate(); e.Handled = true; }
        else if (e.Key == Key.Enter) { PerformClick(); e.Handled = true; }
    }

    protected internal override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space && _spaceDown)
        {
            _spaceDown = false;
            Invalidate();
            PerformClick();
        }
    }

    protected internal override void OnLostFocus()
    {
        _spaceDown = false;
        base.OnLostFocus();
    }

    protected internal override bool ProcessDialogKey(KeyEventArgs e)
    {
        if ((e.Key == Key.Enter && Default) || (e.Key == Key.Escape && Cancel))
        {
            PerformClick();
            return true;
        }
        return false;
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var r = new Rect(0, 0, Width, Height);
        bool enabled = IsEffectivelyEnabled;
        float radius = canvas.Px(t.ButtonRadius);
        var face = !enabled ? t.FormBackground : IsPressed ? t.ButtonFacePressed : IsHovered ? t.ButtonFaceHover : t.ButtonFace;
        canvas.FillRoundRect(r, radius, face);
        var border = enabled && (IsHovered || Default) ? t.Accent : t.ButtonBorder;
        canvas.DrawRoundBorder(r, radius, border, canvas.Px(1));
        if (Focused && OwningForm is { ShowFocusCues: true }) canvas.DrawFocusRing(r, radius);
        canvas.DrawText(Caption, canvas.DefaultFont, enabled ? t.Text : t.TextDisabled, r, TextAlign.Center, VerticalAlign.Center);
    }

}

/// <summary>
/// Resizes the nearest earlier visible sibling with the same align (Delphi's rule; spec, Appendix B).
/// </summary>
public class Splitter : Control
{
    int _startSize, _startRemaining, _startPointer;
    bool _dragging;

    public Splitter()
    {
        SetDefaultSize(4, 100);
        Align = Align.Left;
    }

    /// <summary>The smallest size of the target, and of the client space left beside it.</summary>
    [Published(Category = "Behavior")] public int MinSize { get; set; } = 30;

    bool Horizontal => Align is Align.Left or Align.Right;

    public Control? FindTarget()
    {
        if (Parent == null || Align is Align.None or Align.Client) return null;
        var siblings = Parent.Children;
        int self = -1;
        for (int i = 0; i < siblings.Count; i++) if (siblings[i] == this) self = i;
        for (int i = self - 1; i >= 0; i--)
            if (siblings[i].Visible && siblings[i].Align == Align) return siblings[i];
        return null;
    }

    /// <summary>
    /// Drags the splitter by <paramref name="delta"/> pixels along its axis (positive = right/down).
    /// Dragging away from the target grows it; toward it shrinks it.
    /// </summary>
    public void DragBy(int delta)
    {
        if (BeginDrag()) ApplyDrag(delta);
    }

    bool BeginDrag()
    {
        if (FindTarget() is not { } target || Parent is not { } parent) return false;
        Root.PerformLayout();  // RemainingClientRect must be current
        _startSize = Horizontal ? target.Width : target.Height;
        _startRemaining = Horizontal ? parent.RemainingClientRect.Width : parent.RemainingClientRect.Height;
        return true;
    }

    void ApplyDrag(int delta)
    {
        if (FindTarget() is not { } target) return;
        int sign = Align is Align.Left or Align.Top ? 1 : -1;
        int max = _startSize + _startRemaining - MinSize;
        int newSize = Math.Max(MinSize, Math.Min(_startSize + sign * delta, max));
        if (Horizontal) target.Width = newSize;
        else target.Height = newSize;
    }

    protected internal override void OnMouseEnter()
    {
        Cursor = Horizontal ? Cursor.SizeWE : Cursor.SizeNS;
        base.OnMouseEnter();
    }

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButton.Left || !BeginDrag()) return;
        _dragging = true;
        _startPointer = Horizontal ? AbsoluteLeft + e.X : AbsoluteTop + e.Y;
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) ApplyDrag((Horizontal ? AbsoluteLeft + e.X : AbsoluteTop + e.Y) - _startPointer);
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected override void Paint(Canvas canvas) =>
        canvas.FillRect(new Rect(0, 0, Width, Height), IsHovered ? canvas.Theme.ScrollThumb : canvas.Theme.SplitterFace);

    protected override void ScaleOwnLengths(double scale) => MinSize = LayoutMath.Round(MinSize * scale);

}
