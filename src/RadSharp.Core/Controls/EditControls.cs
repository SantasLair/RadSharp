using RadSharp.Drawing;
using RadSharp.Text;

namespace RadSharp;

/// <summary>
/// Shared behaviour of <see cref="Edit"/> and <see cref="Memo"/>: caret, selection, keyboard and mouse
/// editing, clipboard, undo, IME composition and scrolling. Layout works on the <i>displayed</i> text
/// (bullets for passwords, the committed text with any IME composition spliced in), so caret,
/// selection and hit testing are the same code in every case.
/// </summary>
public abstract class EditBase : Control
{
    protected readonly TextEditor Editor = new();

    string _composition = "";
    int _compCursor;
    double _blinkStart;
    bool _caretVisible = true;
    bool _dragging;
    float? _desiredX;
    bool _keepDesiredX;
    protected int ScrollX, ScrollY;

    List<Line>? _lines;
    (string Text, int Width, RenderFont Font) _linesKey;

    /// <summary>One visual line: a range of the displayed text, its layout and its top (content pixels).</summary>
    protected sealed record Line(int Start, int End, TextLayout Layout, int Top);

    PopupMenu? _defaultContextMenu;
    MenuItem? _mnuUndo, _mnuCut, _mnuCopy, _mnuPaste, _mnuDelete, _mnuSelectAll;

    protected EditBase()
    {
        TabStop = true;
        Cursor = Cursor.IBeam;
        Editor.TextChanged += OnEditorTextChanged;
        Editor.SelectionChanged += OnEditorSelectionChanged;
    }

    /// <summary>
    /// The built-in Undo/Cut/Copy/Paste/Delete/Select All menu, unless a <c>PopupMenu</c> is assigned or
    /// named via <see cref="Control.PopupMenuName"/>, which replaces it.
    /// </summary>
    public override PopupMenu? ContextMenu
    {
        get
        {
            if (base.ContextMenu is { } explicitMenu) return explicitMenu;
            _defaultContextMenu ??= BuildDefaultContextMenu();
            UpdateDefaultContextMenuState();
            return _defaultContextMenu;
        }
        set => base.ContextMenu = value;
    }

    PopupMenu BuildDefaultContextMenu()
    {
        var menu = new PopupMenu();
        _mnuUndo = menu.Add("Undo", _ => { if (CanUndo) Undo(); });
        _mnuCut = menu.Add("Cut", _ => CutToClipboard());
        _mnuCopy = menu.Add("Copy", _ => CopyToClipboard());
        _mnuPaste = menu.Add("Paste", _ => PasteFromClipboard());
        _mnuDelete = menu.Add("Delete", _ => Editor.DeleteForward());
        menu.Items.Add(new MenuItem { Caption = "-", Separator = true });
        _mnuSelectAll = menu.Add("Select All", _ => SelectAll());
        return menu;
    }

    void UpdateDefaultContextMenuState()
    {
        _mnuUndo!.Enabled = !ReadOnly && CanUndo;
        _mnuCut!.Enabled = !ReadOnly && Editor.HasSelection && !IsPassword;
        _mnuCopy!.Enabled = Editor.HasSelection && !IsPassword;
        _mnuPaste!.Enabled = !ReadOnly && Clipboard.GetText()?.Length > 0;
        _mnuDelete!.Enabled = !ReadOnly && Editor.HasSelection;
        _mnuSelectAll!.Enabled = Editor.Text.Length > 0;
    }

    // ---- Published ----

    [Published(Category = "Behavior")]
    public string Text
    {
        get => Editor.Text;
        set
        {
            Editor.SetText(value ?? "");
            // Like Delphi: a memo shows its first line after Text is set; an edit keeps the caret at the end.
            if (Editor.Multiline) { Editor.MoveTo(0); ScrollY = 0; }
        }
    }

    /// <summary>The most characters (graphemes) the user can enter; 0 = no limit.</summary>
    [Published(Category = "Behavior")]
    public int MaxLength { get => Editor.MaxLength; set => Editor.MaxLength = Math.Max(0, value); }

    [Published(Category = "Behavior")] public bool ReadOnly { get; set; }

    /// <summary>Grey text shown while the control is empty.</summary>
    [Published(Category = "Appearance")] public string Placeholder { get; set; } = "";

    /// <summary>Hide the selection highlight while the control doesn't have focus.</summary>
    [Published(Category = "Behavior")] public bool HideSelection { get; set; } = true;

    [Published(Category = "Appearance")] public BorderStyle BorderStyle { get; set; } = BorderStyle.Single;

    /// <summary>Background colour; null uses the theme's input background.</summary>
    [Published(Category = "Appearance")] public Color? Color { get; set; }

    /// <summary>Text colour; null uses the theme's text colour.</summary>
    [Published(Category = "Appearance")] public Color? FontColor { get; set; }

    /// <summary>Raised when <see cref="Text"/> changes (typing, pasting, undo, or from code).</summary>
    [Published] public event NotifyEvent? Change;

    // ---- Public API ----

    public int SelectionStart { get => Editor.SelectionStart; set => Editor.Select(value, 0); }
    public int SelectionLength { get => Editor.SelectionLength; set => Editor.Select(Editor.SelectionStart, value); }
    public string SelectedText => Editor.SelectedText;
    /// <summary>The caret's UTF-16 index in <see cref="Text"/>.</summary>
    public int CaretPosition => Editor.Caret;
    public bool IsComposing => _composition.Length > 0;
    public bool CanUndo => Editor.CanUndo;

    public void Select(int start, int length) => Editor.Select(start, length);
    public void SelectAll() => Editor.SelectAll();
    public void Clear() => Editor.SetText("");
    public bool Undo() => !ReadOnly && Editor.Undo();
    public bool Redo() => !ReadOnly && Editor.Redo();

    public void CopyToClipboard()
    {
        if (Editor.HasSelection && !IsPassword) Clipboard.SetText(Editor.SelectedText);
    }

    public void CutToClipboard()
    {
        if (!Editor.HasSelection || IsPassword || ReadOnly) return;
        Clipboard.SetText(Editor.SelectedText);
        Editor.DeleteBackward();
    }

    public void PasteFromClipboard()
    {
        if (ReadOnly) return;
        if (Clipboard.GetText() is { Length: > 0 } text) Editor.Insert(text);
    }

    // ---- Subclass hooks ----

    protected virtual bool IsPassword => false;
    protected virtual char PasswordGlyph => '•';

    /// <summary>Splits the displayed text into visual lines that fit <paramref name="width"/>.</summary>
    protected abstract List<Line> BuildLines(string text, RenderFont font, int width, int lineHeight);

    /// <summary>Vertical offset of the first line inside the text area (an Edit centres its line).</summary>
    protected virtual int FirstLineOffset(int innerHeight, int lineHeight) => 0;

    /// <summary>Width reserved inside the border for a vertical scroll bar.</summary>
    protected virtual int ReservedRight => 0;

    // ---- Displayed text ----

    /// <summary>The text font: the control's <see cref="Control.EffectiveFont"/> (the theme's face when nothing overrides it) at the form's scale.</summary>
    protected RenderFont TextFont
    {
        get
        {
            var f = EffectiveFont;
            var theme = CurrentTheme;
            if (f == theme.DefaultFont) return new(theme.FontFace, (float)(theme.FontSize * UiScale));
            return new(FontFace.Resolve(f), (float)(f.Size * UiScale));
        }
    }

    /// <summary>How far a line is shifted right inside the text area (a right-aligned edit); 0 for left-aligned text.</summary>
    protected virtual int LineOffsetX(Line line, int areaWidth) => 0;

    /// <summary>The horizontal alignment used for the placeholder text.</summary>
    protected virtual TextAlign TextAlignment => TextAlign.Left;
    protected int LineHeight => (int)Math.Ceiling(TextFont.LineHeight);

    string DisplayText
    {
        get
        {
            if (IsPassword) return new string(PasswordGlyph, Editor.GraphemeCount);
            if (!IsComposing) return Editor.Text;
            int s = Editor.SelectionStart, e = s + Editor.SelectionLength;
            return string.Concat(Editor.Text.AsSpan(0, s), _composition, Editor.Text.AsSpan(e));
        }
    }

    int ToDisplay(int index)
    {
        if (IsPassword) return Editor.GraphemeIndexOf(index);
        if (!IsComposing) return index;
        int s = Editor.SelectionStart, e = s + Editor.SelectionLength;
        return index <= s ? index : index >= e ? index - (e - s) + _composition.Length : s;
    }

    int FromDisplay(int d)
    {
        if (IsPassword) return Editor.BoundaryOfGrapheme(d);
        if (!IsComposing) return d;
        int s = Editor.SelectionStart, e = s + Editor.SelectionLength;
        return d <= s ? d : d >= s + _composition.Length ? d - _composition.Length + (e - s) : s;
    }

    int CaretDisplay => IsComposing ? Editor.SelectionStart + _compCursor : ToDisplay(Editor.Caret);

    int Border => BorderStyle == BorderStyle.Single ? Px(1) : 0;

    /// <summary>The text area inside border and padding, in this control's coordinates.</summary>
    protected Rect TextArea
    {
        get
        {
            int b = Border, px = Px(6), py = Px(3);
            return new Rect(b + px, b + py, Math.Max(0, Width - 2 * (b + px) - ReservedRight), Math.Max(0, Height - 2 * (b + py)));
        }
    }

    protected List<Line> Lines
    {
        get
        {
            var key = (DisplayText, TextArea.Width, TextFont);
            if (_lines == null || _linesKey != key)
            {
                _lines = BuildLines(key.Item1, key.Item3, key.Item2, LineHeight);
                _linesKey = (key.Item1, TextArea.Width, key.Item3);  // TextArea may change once in BuildLines (scroll bar)
            }
            return _lines;
        }
    }

    protected void InvalidateLines() => _lines = null;

    protected int ContentHeight => Lines.Count * LineHeight;

    int LineIndexOf(int d)
    {
        var lines = Lines;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (d < l.Start || d > l.End) continue;
            // At a soft wrap the caret belongs to the start of the next line.
            if (d == l.End && i + 1 < lines.Count && lines[i + 1].Start == d) continue;
            return i;
        }
        return lines.Count - 1;
    }

    float CaretXOf(int d)
    {
        var l = Lines[LineIndexOf(d)];
        return l.Layout.CaretX(d - l.Start);
    }

    /// <summary>The caret rectangle in this control's coordinates.</summary>
    public Rect CaretRect
    {
        get
        {
            var area = TextArea;
            int d = CaretDisplay;
            var line = Lines[LineIndexOf(d)];
            int x = area.X + LineOffsetX(line, area.Width) + (int)MathF.Round(line.Layout.CaretX(d - line.Start)) - ScrollX;
            int y = area.Y + FirstLineOffset(area.Height, LineHeight) + line.Top - ScrollY;
            return new Rect(x, y, Math.Max(1, Px(1)), LineHeight);
        }
    }

    /// <summary>The displayed-text index nearest a point in this control's coordinates.</summary>
    int HitTestDisplay(int x, int y)
    {
        var area = TextArea;
        var lines = Lines;
        int cy = y - area.Y - FirstLineOffset(area.Height, LineHeight) + ScrollY;
        int li = Math.Clamp(cy < 0 ? 0 : cy / Math.Max(1, LineHeight), 0, lines.Count - 1);
        var line = lines[li];
        return line.Start + line.Layout.HitTest(x - area.X - LineOffsetX(line, area.Width) + ScrollX);
    }

    /// <summary>The text index nearest a point in this control's coordinates.</summary>
    public int IndexAtPoint(int x, int y) => FromDisplay(HitTestDisplay(x, y));

    // ---- Scrolling ----

    protected void EnsureCaretVisible()
    {
        var area = TextArea;
        int d = CaretDisplay;
        int x = (int)MathF.Round(CaretXOf(d));
        int y = Lines[LineIndexOf(d)].Top;
        if (x - ScrollX > area.Width - 1) ScrollX = x - area.Width + 1;
        if (x < ScrollX) ScrollX = x;
        if (y < ScrollY) ScrollY = y;
        if (y + LineHeight > ScrollY + area.Height) ScrollY = y + LineHeight - area.Height;
        ClampScroll();
    }

    protected void ClampScroll()
    {
        var area = TextArea;
        float widest = 0;
        foreach (var l in Lines) widest = Math.Max(widest, l.Layout.Width);
        ScrollX = Math.Clamp(ScrollX, 0, Math.Max(0, (int)Math.Ceiling(widest) + Px(1) - area.Width));
        ScrollY = Math.Clamp(ScrollY, 0, Math.Max(0, ContentHeight - area.Height));
    }

    protected override void OnResize()
    {
        InvalidateLines();
        ClampScroll();
        base.OnResize();
    }

    // ---- Editor events ----

    void OnEditorTextChanged()
    {
        InvalidateLines();
        Invalidate();
        OnChange();
    }

    protected virtual void OnChange() => Change?.Invoke(this);

    void OnEditorSelectionChanged()
    {
        if (!_keepDesiredX) _desiredX = null;
        ResetBlink();
        EnsureCaretVisible();
        UpdateTextInputArea();
        Invalidate();
    }

    void ResetBlink()
    {
        _blinkStart = OwningForm?.Now ?? 0;
        _caretVisible = true;
    }

    protected internal override void OnTick(double nowMs)
    {
        bool visible = (nowMs - _blinkStart) % 1060 < 530;
        if (visible == _caretVisible) return;
        _caretVisible = visible;
        Invalidate();
    }

    void UpdateTextInputArea()
    {
        if (!Focused || OwningForm is not { } form) return;
        var c = CaretRect;
        form.TextInputArea = new Rect(AbsoluteLeft + c.X, AbsoluteTop + c.Y, c.Width, c.Height);
    }

    /// <summary>Called when this editor, shown in the popup layer (an inline editor), is closed.</summary>
    internal Action? PopupClosedHook { get; set; }

    protected internal override void OnPopupClosed()
    {
        base.OnPopupClosed();
        PopupClosedHook?.Invoke();
    }

    // ---- Focus ----

    protected internal override void OnGotFocus()
    {
        OwningForm?.AddTicker(this);
        ResetBlink();
        UpdateTextInputArea();
        base.OnGotFocus();
    }

    protected internal override void OnLostFocus()
    {
        _composition = "";
        _dragging = false;
        if (OwningForm is { } form)
        {
            form.RemoveTicker(this);
            form.TextInputArea = null;
        }
        InvalidateLines();
        base.OnLostFocus();
    }

    // ---- Text input and IME ----

    protected internal override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled) return;
        e.Handled = true;
        _composition = "";
        InvalidateLines();
        if (!ReadOnly) Editor.Insert(e.Text);
    }

    protected internal override void OnTextComposition(TextCompositionEventArgs e)
    {
        base.OnTextComposition(e);
        if (ReadOnly || IsPassword) return;
        _composition = e.Text;
        _compCursor = Math.Clamp(e.Cursor, 0, e.Text.Length);
        InvalidateLines();
        ResetBlink();
        EnsureCaretVisible();
        UpdateTextInputArea();
        Invalidate();
    }

    // ---- Keyboard ----

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (IsComposing)
        {
            // The input method owns editing keys while it composes.
            if (e.Key is Key.Backspace or Key.Delete or Key.Enter or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Escape or Key.Tab)
                e.Handled = true;
            return;
        }
        e.Handled = HandleKey(e);
        if (e.Handled) EnsureCaretVisible();
    }

    bool HandleKey(KeyEventArgs e)
    {
        bool mac = Shortcut.CommandModifier == KeyModifiers.Meta;
        bool shift = e.Shift;
        var mods = e.Modifiers & ~KeyModifiers.Shift;
        var wordMod = mac ? KeyModifiers.Alt : KeyModifiers.Ctrl;
        var cmd = Shortcut.CommandModifier;
        int caret = Editor.Caret;

        switch (e.Key)
        {
            case Key.Left or Key.Right:
            {
                bool left = e.Key == Key.Left;
                if (mac && mods == KeyModifiers.Meta) { MoveLineEdge(start: left, shift); return true; }
                if (mods == wordMod)
                {
                    Editor.MoveTo(left ? Editor.PreviousWordStart(caret) : Editor.NextWordStart(caret), shift);
                    return true;
                }
                if (mods != KeyModifiers.None) return false;
                if (Editor.HasSelection && !shift)
                    Editor.MoveTo(left ? Editor.SelectionStart : Editor.SelectionStart + Editor.SelectionLength);
                else
                    Editor.MoveTo(left ? Editor.PreviousBoundary(caret) : Editor.NextBoundary(caret), shift);
                return true;
            }
            case Key.Home or Key.End:
            {
                bool home = e.Key == Key.Home;
                if (mods == KeyModifiers.Ctrl) { Editor.MoveTo(home ? 0 : Editor.Text.Length, shift); return true; }
                if (mods != KeyModifiers.None) return false;
                MoveLineEdge(home, shift);
                return true;
            }
            case Key.Up or Key.Down:
            {
                if (!Editor.Multiline) return false;
                bool up = e.Key == Key.Up;
                if (mac && mods == KeyModifiers.Meta) { Editor.MoveTo(up ? 0 : Editor.Text.Length, shift); return true; }
                if (mods != KeyModifiers.None) return false;
                MoveVertical(up ? -1 : 1, shift);
                return true;
            }
            case Key.PageUp or Key.PageDown:
            {
                if (!Editor.Multiline || mods != KeyModifiers.None) return false;
                int page = Math.Max(1, TextArea.Height / Math.Max(1, LineHeight) - 1);
                MoveVertical(e.Key == Key.PageUp ? -page : page, shift);
                return true;
            }
            case Key.Backspace:
                if (ReadOnly) return true;
                if (mac && mods == KeyModifiers.Meta) Editor.DeleteToLineStart();
                else Editor.DeleteBackward(word: mods == wordMod);
                return true;
            case Key.Delete:
                if (mods == KeyModifiers.None && shift) { CutToClipboard(); return true; }
                if (ReadOnly) return true;
                Editor.DeleteForward(word: mods == wordMod);
                return true;
            case Key.Insert when mods == KeyModifiers.Ctrl && !shift:
                CopyToClipboard();
                return true;
            case Key.Insert when mods == KeyModifiers.None && shift:
                PasteFromClipboard();
                return true;
            case Key.A when mods == cmd && !shift: Editor.SelectAll(); return true;
            case Key.C when mods == cmd && !shift: CopyToClipboard(); return true;
            case Key.X when mods == cmd && !shift: CutToClipboard(); return true;
            case Key.V when mods == cmd && !shift: PasteFromClipboard(); return true;
            case Key.Z when mods == cmd: if (shift) Redo(); else Undo(); return true;
            case Key.Y when mods == KeyModifiers.Ctrl && !mac && !shift: Redo(); return true;
        }
        return HandleOtherKey(e);
    }

    /// <summary>Keys only some editors handle (Enter and Tab in a Memo).</summary>
    protected virtual bool HandleOtherKey(KeyEventArgs e) => false;

    /// <summary>Inserts text as if typed (unless read-only).</summary>
    protected void InsertTyped(string text)
    {
        if (!ReadOnly) Editor.Insert(text);
    }

    void MoveLineEdge(bool start, bool extend)
    {
        var lines = Lines;
        int li = LineIndexOf(CaretDisplay);
        var line = lines[li];
        int d = start ? line.Start : line.End;
        int index = FromDisplay(d);
        // At a soft wrap, End stays on this line: before the wrapped space.
        if (!start && li + 1 < lines.Count && lines[li + 1].Start == line.End && line.End > line.Start)
            index = Editor.PreviousBoundary(index);
        Editor.MoveTo(index, extend);
    }

    void MoveVertical(int delta, bool extend)
    {
        var lines = Lines;
        int d = CaretDisplay;
        int li = LineIndexOf(d);
        float x = _desiredX ?? CaretXOf(d);
        int target = li + delta;
        int index;
        if (target < 0) index = 0;
        else if (target >= lines.Count) index = Editor.Text.Length;
        else index = FromDisplay(lines[target].Start + lines[target].Layout.HitTest(x));
        _keepDesiredX = true;
        try { Editor.MoveTo(index, extend); }
        finally { _keepDesiredX = false; }
        _desiredX = x;
    }

    // ---- Mouse ----

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButton.Left) return;
        int index = IndexAtPoint(e.X, e.Y);
        if (e.Clicks >= 3)
        {
            if (Editor.Multiline) Editor.Select(Editor.LineStart(index), Editor.LineEnd(index) - Editor.LineStart(index));
            else Editor.SelectAll();
        }
        else if (e.Clicks == 2 && !IsPassword)
        {
            var (s, end) = Editor.WordAt(index);
            Editor.Select(s, end - s);
        }
        else
        {
            Editor.MoveTo(index, extend: e.Modifiers.HasFlag(KeyModifiers.Shift));
            _dragging = true;
        }
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Editor.MoveTo(IndexAtPoint(e.X, e.Y), extend: true);
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    // ---- Painting ----

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var r = new Rect(0, 0, Width, Height);
        bool enabled = IsEffectivelyEnabled;
        float radius = canvas.Px(3);
        canvas.FillRoundRect(r, radius, enabled && !ReadOnly ? Color ?? t.InputBackground : t.FormBackground);
        if (BorderStyle == BorderStyle.Single)
            canvas.DrawRoundBorder(r, radius, Focused ? t.Accent : IsHovered && enabled ? t.ButtonBorder : t.InputBorder, Border);

        var area = TextArea;
        var font = TextFont;
        int lh = LineHeight;
        int top0 = area.Y + FirstLineOffset(area.Height, lh) - ScrollY;

        canvas.Save();
        canvas.ClipRect(new Rect(area.X - Px(1), Border, area.Width + Px(2), Height - 2 * Border));

        var text = DisplayText;
        if (text.Length == 0 && Placeholder.Length > 0)
            canvas.DrawText(Placeholder, font, t.TextDisabled,
                area.X - ScrollX + (TextAlignment == TextAlign.Left ? 0 : (int)(Math.Max(0, area.Width - font.MeasureWidth(Placeholder)) / (TextAlignment == TextAlign.Center ? 2 : 1))),
                top0 + font.Ascent);

        bool showSelection = !IsComposing && Editor.HasSelection && (Focused || !HideSelection);
        int selA = ToDisplay(Editor.SelectionStart), selB = ToDisplay(Editor.SelectionStart + Editor.SelectionLength);
        var textColor = enabled ? FontColor ?? t.Text : t.TextDisabled;

        foreach (var line in Lines)
        {
            int y = top0 + line.Top;
            int x0 = area.X - ScrollX + LineOffsetX(line, area.Width);
            if (y + lh < 0 || y > Height) continue;
            if (showSelection)
            {
                int a = Math.Max(selA, line.Start), b = Math.Min(selB, line.End);
                bool pastEnd = selB > line.End && line.End < text.Length && text[line.End] == '\n';
                if (a < b || (pastEnd && a <= line.End))
                {
                    float xa = line.Layout.CaretX(a - line.Start), xb = line.Layout.CaretX(b - line.Start);
                    int w = (int)MathF.Round(xb - xa) + (pastEnd ? Px(5) : 0);
                    canvas.FillRect(new Rect(x0 + (int)MathF.Round(xa), y, w, lh), Focused ? t.Selection : t.SelectionInactive);
                }
            }
            canvas.DrawText(line.Layout, textColor, x0, y + font.Ascent);

            if (IsComposing)
            {
                int cs = Editor.SelectionStart, ce = cs + _composition.Length;
                int a = Math.Max(cs, line.Start), b = Math.Min(ce, line.End);
                if (a < b)
                {
                    float xa = line.Layout.CaretX(a - line.Start), xb = line.Layout.CaretX(b - line.Start);
                    canvas.FillRect(new Rect(x0 + (int)MathF.Round(xa), y + lh - Math.Max(1, Px(1)) - Px(1),
                        (int)MathF.Round(xb - xa), Math.Max(1, Px(1))), textColor);
                }
            }
        }

        if (Focused && _caretVisible) canvas.FillRect(CaretRect, t.Caret);
        canvas.Restore();

        PaintOverlay(canvas);
    }

    /// <summary>Paints things outside the text area, such as a scroll bar.</summary>
    protected virtual void PaintOverlay(Canvas canvas) { }
}

/// <summary>A single-line text box.</summary>
public class Edit : EditBase
{
    public Edit() => SetDefaultSize(120, 24);

    /// <summary>If set (e.g. "•"), every character is shown as this one: a password box.</summary>
    [Published(Category = "Behavior")] public string PasswordChar { get; set; } = "";

    /// <summary>Where the text sits when it is narrower than the box (a money field is right-aligned).</summary>
    [Published(Category = "Appearance")] public TextAlign Alignment { get => _alignment; set { _alignment = value; Invalidate(); } }
    TextAlign _alignment;

    protected override TextAlign TextAlignment => _alignment;

    protected override int LineOffsetX(Line line, int areaWidth)
    {
        if (_alignment == TextAlign.Left) return 0;
        int room = Math.Max(0, areaWidth - Px(1) - (int)MathF.Ceiling(line.Layout.Width));
        return _alignment == TextAlign.Center ? room / 2 : room;
    }

    /// <summary>Select all text when the box is entered with Tab.</summary>
    [Published(Category = "Behavior")] public bool AutoSelect { get; set; } = true;

    protected override bool IsPassword => PasswordChar.Length > 0;
    protected override char PasswordGlyph => PasswordChar.Length > 0 ? PasswordChar[0] : '•';

    protected override List<Line> BuildLines(string text, RenderFont font, int width, int lineHeight) =>
        [new Line(0, text.Length, TextLayout.Get(text, font), 0)];

    protected override int FirstLineOffset(int innerHeight, int lineHeight) => Math.Max(0, (innerHeight - lineHeight) / 2);

    protected internal override void OnGotFocus()
    {
        if (AutoSelect && OwningForm is { ShowFocusCues: true }) SelectAll();
        base.OnGotFocus();
    }
}

/// <summary>A multi-line text box with word wrap and a vertical scroll bar.</summary>
public class Memo : EditBase
{
    readonly ScrollAxis _vAxis = new() { PageSize = 0, Max = 0 };
    readonly ScrollBarDriver _vDriver;
    bool _showBar, _barDrag;
    bool _wordWrap = true;

    public Memo()
    {
        _vDriver = new ScrollBarDriver(_vAxis);
        SetDefaultSize(200, 100);
        Editor.Multiline = true;
    }

    /// <summary>Wrap long lines at word boundaries (otherwise scroll horizontally).</summary>
    [Published(Category = "Behavior")]
    public bool WordWrap { get => _wordWrap; set { _wordWrap = value; InvalidateLines(); Invalidate(); } }

    /// <summary>Tab inserts a tab character instead of moving focus.</summary>
    [Published(Category = "Behavior")] public bool WantTabs { get; set; }

    /// <summary>Enter inserts a line break (otherwise it reaches the form's default button).</summary>
    [Published(Category = "Behavior")] public bool WantReturns { get; set; } = true;

    public override bool AcceptsTab => WantTabs;

    /// <summary>The text split into lines at line breaks.</summary>
    public string[] TextLines
    {
        get => Text.Split('\n');
        set => Text = string.Join('\n', value);
    }

    int BarSize => Px(12);
    protected override int ReservedRight => _showBar ? BarSize : 0;

    protected override bool HandleOtherKey(KeyEventArgs e)
    {
        if (e.Modifiers != KeyModifiers.None && e.Modifiers != KeyModifiers.Shift) return false;
        if (e.Key == Key.Enter && WantReturns) { InsertTyped("\n"); return true; }
        if (e.Key == Key.Tab && WantTabs && e.Modifiers == KeyModifiers.None) { InsertTyped("\t"); return true; }
        return false;
    }

    protected override List<Line> BuildLines(string text, RenderFont font, int width, int lineHeight)
    {
        var lines = Wrap(text, font, width, lineHeight);
        // Show the scroll bar when the content is taller than the box; that narrows the text area.
        int innerHeight = TextArea.Height;
        bool need = lines.Count * lineHeight > innerHeight;
        if (need != _showBar)
        {
            _showBar = need;
            lines = Wrap(text, font, TextArea.Width, lineHeight);
        }
        return lines;
    }

    List<Line> Wrap(string text, RenderFont font, int width, int lineHeight)
    {
        var lines = new List<Line>();
        int start = 0;
        while (true)
        {
            int nl = text.IndexOf('\n', start);
            int end = nl < 0 ? text.Length : nl;
            WrapParagraph(text, start, end, font, width, lineHeight, lines);
            if (nl < 0) break;
            start = nl + 1;
        }
        return lines;
    }

    void WrapParagraph(string text, int start, int end, RenderFont font, int width, int lineHeight, List<Line> lines)
    {
        var paragraph = text[start..end];
        var full = TextLayout.Get(paragraph, font);
        if (!WordWrap || width <= 0 || full.Width <= width)
        {
            lines.Add(new Line(start, end, full, lines.Count * lineHeight));
            return;
        }

        var bounds = full.Boundaries;
        int lineStart = 0;  // relative to the paragraph
        int bi = 0;
        while (lineStart < paragraph.Length)
        {
            float origin = full.CaretX(lineStart);
            int lastFit = -1, lastSpace = -1;
            for (; bi < bounds.Count; bi++)
            {
                int b = bounds[bi];
                if (b <= lineStart) continue;
                if (full.CaretX(b) - origin > width) break;
                lastFit = b;
                if (char.IsWhiteSpace(paragraph[b - 1])) lastSpace = b;
            }
            int breakAt;
            if (bi >= bounds.Count) breakAt = paragraph.Length;            // the rest fits
            else if (lastSpace > lineStart) breakAt = lastSpace;             // wrap after a space
            else if (lastFit > lineStart) breakAt = lastFit;                 // a long word: break inside it
            else breakAt = bounds.First(b => b > lineStart);                // at least one grapheme per line
            var sub = paragraph[lineStart..breakAt];
            lines.Add(new Line(start + lineStart, start + breakAt, TextLayout.Get(sub, font), lines.Count * lineHeight));
            lineStart = breakAt;
            bi = 0;
        }
    }

    Rect BarRect
    {
        get
        {
            int b = BorderStyle == BorderStyle.Single ? Px(1) : 0;
            return new Rect(Width - b - BarSize, b, BarSize, Math.Max(0, Height - 2 * b));
        }
    }

    void SyncBar()
    {
        _ = Lines;
        _vAxis.Max = Math.Max(ContentHeight, TextArea.Height);
        _vAxis.PageSize = TextArea.Height;
        _vAxis.SetPosition(ScrollY);
    }

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        var bar = BarRect;
        if (_showBar && e.X >= bar.X && e.Button == MouseButton.Left)
        {
            SyncBar();
            _barDrag = true;
            if (_vDriver.Press(e.Y - bar.Y, bar.Height, Px(16), TextArea.Height)) { ScrollY = _vAxis.Position; Invalidate(); }
            return;
        }
        base.OnMouseDown(e);
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        if (_barDrag)
        {
            if (_vDriver.Move(e.Y - BarRect.Y, BarRect.Height, Px(16))) { ScrollY = _vAxis.Position; Invalidate(); }
            return;
        }
        base.OnMouseMove(e);
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        if (_barDrag) { _barDrag = false; _vDriver.Release(); Invalidate(); return; }
        base.OnMouseUp(e);
    }

    protected internal override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled || ContentHeight <= TextArea.Height) return;
        int before = ScrollY;
        ScrollY -= (int)Math.Round(e.DeltaY * LineHeight * 3);
        ClampScroll();
        if (ScrollY != before) Invalidate();
        e.Handled = true;
    }

    protected override void PaintOverlay(Canvas canvas)
    {
        if (!_showBar) return;
        SyncBar();
        ScrollBarDriver.Paint(canvas, BarRect, vertical: true, _vAxis, _barDrag, Px(16));
    }
}
