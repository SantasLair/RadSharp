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
        canvas.DrawText(Caption, EffectiveFont, canvas.Theme.Text, r, TextAlign.Center, VerticalAlign.Center);
    }

}

public class Label : Control
{
    string _caption = "";

    public Label() => SetDefaultSize(80, 20);

    [Published(Category = "Appearance")] public string Caption { get => _caption; set { _caption = value; Invalidate(); } }
    [Published(Category = "Appearance")] public TextAlign Alignment { get; set; }
    [Published(Category = "Appearance")] public VerticalAlign Layout { get; set; }

    /// <summary>Text size in pixels at 100 %; 0 uses <see cref="Control.Font"/>'s (sugar over it; kept for old files).</summary>
    [Published(Category = "Appearance")] public int FontSize { get; set; }
    /// <summary>Sugar over <see cref="Control.Font"/>'s Bold, kept for old files: true forces bold on top of it.</summary>
    [Published(Category = "Appearance")] public bool FontBold { get; set; }
    /// <summary>Text colour; null uses the theme's.</summary>
    [Published(Category = "Appearance")] public Color? FontColor { get; set; }

    bool _wordWrap, _ellipsis;

    /// <summary>Wraps the caption onto several lines at the label's width (and at explicit newlines).</summary>
    [Published(Category = "Appearance")] public bool WordWrap { get => _wordWrap; set { _wordWrap = value; Invalidate(); } }

    /// <summary>A single-line caption wider than the label ends in "…" instead of being cut off. Ignored with <see cref="WordWrap"/>.</summary>
    [Published(Category = "Appearance")] public bool Ellipsis { get => _ellipsis; set { _ellipsis = value; Invalidate(); } }

    protected override void Paint(Canvas canvas)
    {
        var color = !IsEffectivelyEnabled ? canvas.Theme.TextDisabled : FontColor ?? canvas.Theme.Text;
        var font = Font ?? EffectiveFont with
        {
            Size = FontSize > 0 ? FontSize : EffectiveFont.Size,
            Bold = FontBold || EffectiveFont.Bold,
        };
        var rect = new Rect(0, 0, Width, Height);
        if (WordWrap) canvas.DrawWrappedText(Caption, font, color, rect, Alignment, Layout);
        else canvas.DrawText(Caption, font, color, rect, Alignment, Layout, Ellipsis);
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

    bool _flat;

    /// <summary>
    /// Draws the button without a border and without a face until the pointer is over it or it is pressed
    /// (a toolbar-style button). The face then comes from <see cref="Color"/>/<see cref="HoverColor"/> or the theme.
    /// </summary>
    [Published(Category = "Appearance")] public bool Flat { get => _flat; set { _flat = value; Invalidate(); } }

    /// <summary>Face colour; null uses the theme's (or, for a <see cref="Flat"/> button, no face at rest).</summary>
    [Published(Category = "Appearance")] public Color? Color { get; set; }

    /// <summary>Caption colour; null uses the theme's text colour.</summary>
    [Published(Category = "Appearance")] public Color? FontColor { get; set; }

    /// <summary>
    /// Face colour while the pointer is over the button; null derives it from <see cref="Color"/> (blended a little
    /// towards the text colour) or, without a colour, uses the theme's hover face. A pressed button is shaded the same way.
    /// </summary>
    [Published(Category = "Appearance")] public Color? HoverColor { get; set; }

    /// <summary>Clicking the button sets its form's <see cref="Form.ModalResult"/>, which closes a modal dialog.</summary>
    [Published(Category = "Behavior")] public ModalResult ModalResult { get; set; }

    /// <summary>The name of an ImageList whose pictures this button shows by <see cref="ImageIndex"/>.</summary>
    [Published("images", Category = "Appearance")] public string ImagesName { get; set; } = "";
    [Published(Category = "Appearance")] public int ImageIndex { get; set; } = -1;

    /// <summary>The icon to draw, if any. Overridden by <see cref="ToolButton"/> to fall back to its toolbar's image list.</summary>
    protected virtual Picture? ResolveIcon() => ImageList.Resolve(this, ImagesName, ImageIndex);

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
        var custom = Color;
        var rest = custom ?? (Flat ? null : t.ButtonFace);
        var hover = HoverColor ?? (custom is { } c ? c.Blend(t.Text, 0.12) : t.ButtonFaceHover);
        var pressed = (HoverColor ?? custom) is { } p ? p.Blend(t.Text, HoverColor is null ? 0.22 : 0.10) : t.ButtonFacePressed;
        var face = !enabled ? custom ?? (Flat ? null : t.FormBackground) : IsPressed ? pressed : IsHovered ? hover : rest;
        if (face is { } f) canvas.FillRoundRect(r, radius, f);
        if (!Flat)
        {
            var border = enabled && (IsHovered || Default) ? t.Accent : t.ButtonBorder;
            canvas.DrawRoundBorder(r, radius, border, canvas.Px(1));
        }
        if (Focused && OwningForm is { ShowFocusCues: true }) canvas.DrawFocusRing(r, radius);
        var textRect = r;
        if (ResolveIcon() is { } pic)
        {
            int s = canvas.Px(16);
            int x = Caption.Length > 0 ? canvas.Px(10) : (Width - s) / 2;
            canvas.DrawPicture(pic, new Rect(x, (Height - s) / 2, s, s));
            if (Caption.Length > 0) textRect = new Rect(x + s + canvas.Px(6), 0, Width - x - s - canvas.Px(16), Height);
        }
        canvas.DrawText(Caption, EffectiveFont, enabled ? FontColor ?? t.Text : t.TextDisabled, textRect,
            textRect == r ? TextAlign.Center : TextAlign.Left, VerticalAlign.Center);
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
