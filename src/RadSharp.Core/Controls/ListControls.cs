using RadSharp.Drawing;

namespace RadSharp;

public delegate void DrawItemEvent(Control sender, Canvas canvas, int index, Rect rect, bool selected);

/// <summary>
/// A scrolling list of strings (Delphi's TListBox), built on <see cref="ScrollView"/>. One item is selected, or with
/// <see cref="MultiSelect"/> several; the selection lives in <see cref="Selection"/>, an <see cref="ItemSelection"/>
/// that stores ranges, so even a huge list selects all in one step.
/// </summary>
/// <remarks>
/// Keys: Up/Down, PageUp/PageDown, Home/End move the current item (<see cref="ItemIndex"/>) and keep it in view.
/// With MultiSelect and ExtendedSelect (the default) a click selects one item, Ctrl/⌘+click toggles one, Shift+click
/// and Shift+arrows select the range from the anchor, Ctrl/⌘+arrows move without selecting, Ctrl/⌘+Space toggles,
/// Ctrl/⌘+A selects all, and dragging sweeps a range. With MultiSelect alone, each click (or Space) toggles one item.
/// Typing a letter moves to the next item that starts with it.
/// </remarks>
public class ListBox : ScrollView
{
    readonly List<string> _items = [];
    readonly List<object?> _tags = [];
    string[]? _itemsArray = [];   // the Items snapshot, rebuilt lazily after single-item edits
    int _itemHeight = 24;
    bool _multiSelect, _extendedSelect = true;
    bool _mouseSelecting;
    int _lastCurrent = -1;

    public ListBox()
    {
        SetDefaultSize(160, 120);
        Selection.Changed += SelectionChanged;
    }

    /// <summary>
    /// The selection model: selected ranges, the current item and the anchor. Use it for <c>Ranges</c>,
    /// <c>SelectRange</c> and the like; the list's own members below cover the common cases.
    /// </summary>
    public ItemSelection Selection { get; } = new(SelectionMode.Single);

    /// <summary>The items. Setting them keeps <see cref="ItemIndex"/> (clamped to the new count) and clears item tags.</summary>
    [Published(Category = "Behavior")]
    public string[] Items
    {
        get => _itemsArray ??= [.. _items];
        set
        {
            value ??= [];
            _items.Clear();
            _items.AddRange(value);
            _itemsArray = value;
            _tags.Clear();
            _tags.AddRange(new object?[value.Length]);
            ContentHeight = _items.Count * RowHeight;   // first, so the selection scrolls within the new size
            Selection.Count = _items.Count;
            Invalidate();
        }
    }

    /// <summary>
    /// The current item, or -1: the selected one in a single-select list, the focused one with
    /// <see cref="MultiSelect"/>. Setting it selects that item alone (as a plain click does) and scrolls it into view.
    /// </summary>
    [Published(Category = "Behavior")]
    public int ItemIndex
    {
        get => Selection.Current;
        set => Selection.Select(Math.Clamp(value, -1, _items.Count - 1));
    }

    /// <summary>Several items can be selected at once (Delphi's MultiSelect); <see cref="ExtendedSelect"/> says how.</summary>
    [Published(Category = "Behavior")]
    public bool MultiSelect { get => _multiSelect; set { _multiSelect = value; UpdateMode(); } }

    /// <summary>
    /// With <see cref="MultiSelect"/>: Shift selects ranges and Ctrl/⌘ toggles single items (true, the default), or
    /// every click toggles one item (false). Delphi's ExtendedSelect.
    /// </summary>
    [Published(Category = "Behavior")]
    public bool ExtendedSelect { get => _extendedSelect; set { _extendedSelect = value; UpdateMode(); } }

    /// <summary>Row height at 100 %.</summary>
    [Published(Category = "Appearance")]
    public int ItemHeight
    {
        get => _itemHeight;
        set
        {
            _itemHeight = Math.Max(1, value);
            ContentHeight = _items.Count * RowHeight;
            Invalidate();
        }
    }

    /// <summary>Raised when the selection or <see cref="ItemIndex"/> changes.</summary>
    [Published] public event NotifyEvent? Change;
    [Published("onDblClick")] public event NotifyEvent? DoubleClick;
    /// <summary>Paint rows yourself (the default drawing is skipped while this has handlers).</summary>
    [Published] public event DrawItemEvent? DrawItem;

    /// <summary>The item at <see cref="ItemIndex"/>, or null.</summary>
    public string? SelectedItem => ItemIndex >= 0 ? _items[ItemIndex] : null;

    /// <summary>True if the item is selected (Delphi's <c>Selected[i]</c>).</summary>
    public bool IsSelected(int index) => Selection.IsSelected(index);

    /// <summary>Selects or deselects one item, leaving the others (with <see cref="MultiSelect"/>; otherwise it selects that item alone).</summary>
    public void SetSelected(int index, bool selected) => Selection.SetSelected(index, selected);

    /// <summary>How many items are selected (Delphi's SelCount).</summary>
    public int SelectedCount => Selection.SelectedCount;

    /// <summary>The selected indices, ascending.</summary>
    public int[] SelectedIndices => [.. Selection.SelectedIndices];

    /// <summary>The selected items, in list order.</summary>
    public string[] SelectedItems => [.. Selection.SelectedIndices.Select(i => _items[i])];

    /// <summary>Selects every item (with <see cref="MultiSelect"/>).</summary>
    public void SelectAll() => Selection.SelectAll();

    /// <summary>Deselects every item (in a single-select list <see cref="ItemIndex"/> becomes -1).</summary>
    public void ClearSelection() => Selection.ClearSelection();

    // ---- Items with tags ----

    /// <summary>Adds an item at the end with an optional tag (Delphi's AddItem); returns its index.</summary>
    public int AddItem(string item, object? tag = null)
    {
        InsertItem(_items.Count, item, tag);
        return _items.Count - 1;
    }

    /// <summary>Inserts an item; the selection and <see cref="ItemIndex"/> stay on the same items.</summary>
    public void InsertItem(int index, string item, object? tag = null)
    {
        index = Math.Clamp(index, 0, _items.Count);
        _items.Insert(index, item);
        _itemsArray = null;
        _tags.Insert(index, tag);
        ContentHeight = _items.Count * RowHeight;
        Selection.Insert(index);
        Invalidate();
    }

    /// <summary>Removes an item; a removed <see cref="ItemIndex"/> moves to the item that takes its place.</summary>
    public void DeleteItem(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _items.RemoveAt(index);
        _itemsArray = null;
        _tags.RemoveAt(index);
        ContentHeight = _items.Count * RowHeight;
        Selection.Remove(index);
        Invalidate();
    }

    /// <summary>The object attached to an item (Delphi's <c>Items.Objects[i]</c>), or null.</summary>
    public object? GetItemTag(int index) => index >= 0 && index < _tags.Count ? _tags[index] : null;

    public void SetItemTag(int index, object? tag)
    {
        if (index >= 0 && index < _tags.Count) _tags[index] = tag;
    }

    // ---- Popup (combo box) support ----

    /// <summary>In a dropdown: the hovered row is selected, and a click or Enter chooses it.</summary>
    internal bool HotTrack { get; set; }
    internal event Action<int>? ItemChosen;
    internal event Action? PopupClosed;

    protected internal override void OnPopupClosed()
    {
        base.OnPopupClosed();
        PopupClosed?.Invoke();
    }
    internal Action<Canvas, int, Rect, Color>? ItemPainter { get; set; }

    // ---- Geometry ----

    int RowHeight => Px(_itemHeight);

    /// <summary>The scroll offset of the rows, in pixels (the same as <see cref="ScrollView.ScrollY"/>).</summary>
    public int TopOffset => ScrollY;

    /// <summary>The first (possibly partly) visible row; setting it scrolls that row to the top.</summary>
    public int TopIndex
    {
        get => ScrollY / Math.Max(1, RowHeight);
        set => ScrollY = Math.Max(0, value) * RowHeight;
    }

    protected override int LineStepY => Math.Max(1, RowHeight);

    protected override void MeasureContent() => ContentHeight = _items.Count * RowHeight;

    /// <summary>Scrolls the least distance that shows row <paramref name="index"/> in full.</summary>
    public void ScrollIntoView(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        ScrollIntoView(new Rect(ScrollX, index * RowHeight, 0, RowHeight));
    }

    /// <summary>The row at a point in this control's coordinates, or -1.</summary>
    public int IndexAt(int x, int y)
    {
        var view = Viewport;
        if (x < view.X || x >= view.X + view.Width || y < view.Y || y >= view.Y + view.Height) return -1;
        int i = (y - view.Y + ScrollY) / Math.Max(1, RowHeight);
        return i < _items.Count ? i : -1;
    }

    /// <summary>A row's rectangle in this control's coordinates.</summary>
    public Rect ItemRect(int index)
    {
        var view = Viewport;
        return new Rect(view.X, view.Y + index * RowHeight - ScrollY, view.Width, RowHeight);
    }

    void UpdateMode() =>
        Selection.Mode = !_multiSelect ? SelectionMode.Single : _extendedSelect ? SelectionMode.Extended : SelectionMode.Multi;

    bool IsMulti => Selection.Mode is SelectionMode.Multi or SelectionMode.Extended;

    void SelectionChanged()
    {
        if (Selection.Current != _lastCurrent)
        {
            _lastCurrent = Selection.Current;
            ScrollIntoView(_lastCurrent);
        }
        Invalidate();
        Change?.Invoke(this);
    }

    // ---- Input ----

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);   // the scroll bars take their own presses
        if (IsScrolling || IsOverScrollBar(e.X, e.Y)) return;
        int i = IndexAt(e.X, e.Y);
        if (i < 0) return;
        if (e.Button != MouseButton.Left)
        {
            // Right-click selects too, before a context menu opens, but keeps a multi-selection it lands in.
            if (!Selection.IsSelected(i)) Selection.Click(i);
            return;
        }
        Selection.Click(i, toggle: e.Modifiers.HasFlag(Shortcut.CommandModifier), extend: e.Modifiers.HasFlag(KeyModifiers.Shift));
        _mouseSelecting = true;
        if (e.Clicks == 2) DoubleClick?.Invoke(this);
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsScrolling || !(_mouseSelecting || HotTrack)) return;
        var view = Viewport;
        int x = _mouseSelecting ? Math.Clamp(e.X, view.X, Math.Max(view.X, view.X + view.Width - 1)) : e.X;
        int i = IndexAt(x, Math.Clamp(e.Y, view.Y, Math.Max(view.Y, view.Y + view.Height - 1)));
        if (i < 0) return;
        if (_mouseSelecting) Selection.DragTo(i);
        else Selection.Select(i);
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        bool wasScrolling = IsScrolling;
        base.OnMouseUp(e);
        if (wasScrolling) return;
        _mouseSelecting = false;
        if (HotTrack && e.Button == MouseButton.Left && IndexAt(e.X, e.Y) is var i and >= 0) ItemChosen?.Invoke(i);
    }

    protected override bool HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.Modifiers == KeyModifiers.None && HotTrack && ItemIndex >= 0)
        {
            ItemChosen?.Invoke(ItemIndex);
            return true;
        }
        if (_items.Count == 0) return false;
        bool shift = e.Shift, command = e.Command;
        if ((e.Modifiers & ~(KeyModifiers.Shift | Shortcut.CommandModifier)) != 0) return false;
        if (command && !IsMulti) return false;   // a single-select list leaves Ctrl/⌘ keys to shortcuts

        if (command && !shift && e.Key == Key.A) { Selection.SelectAll(); return true; }
        if (e.Key == Key.Space && !shift && IsMulti && command == (Selection.Mode == SelectionMode.Extended))
        {
            Selection.ToggleCurrent();
            return true;
        }

        int current = Selection.Current, last = _items.Count - 1;
        int page = Math.Max(1, Viewport.Height / Math.Max(1, RowHeight) - 1);
        int? target = e.Key switch
        {
            Key.Up => Math.Max(0, current - 1),
            Key.Down => Math.Min(last, current + 1),
            Key.Home => 0,
            Key.End => last,
            Key.PageUp => Math.Max(0, current - page),
            Key.PageDown => Math.Min(last, current + page),
            _ => null,
        };
        if (target is not { } t) return false;
        Selection.MoveTo(t, extend: shift, focusOnly: command);
        ScrollIntoView(Selection.Current);   // also when it didn't move (scrolled away with the wheel)
        return true;
    }

    /// <summary>Typing a letter moves to the next item that starts with it.</summary>
    protected internal override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || e.Text.Length == 0 || _items.Count == 0) return;
        if (IsMulti && string.IsNullOrWhiteSpace(e.Text)) return;   // Space toggles in a multi-select list
        int current = Selection.Current;
        for (int n = 1; n <= _items.Count; n++)
        {
            int i = (current + n) % _items.Count;
            if (_items[i].StartsWith(e.Text, StringComparison.CurrentCultureIgnoreCase)) { Selection.MoveTo(i); break; }
        }
        e.Handled = true;
    }

    // ---- Painting ----

    protected override Color GetBackgroundColor(Theme theme) =>
        Color ?? (IsEffectivelyEnabled ? theme.InputBackground : theme.FormBackground);

    protected override Color GetBorderColor(Theme theme) => Focused ? theme.Accent : theme.InputBorder;

    protected override void PaintContent(Canvas canvas, Rect visible)
    {
        // Rows are drawn in this control's coordinates, like ItemRect and the rect DrawItem has always received.
        var view = Viewport;
        canvas.Translate(visible.X - view.X, visible.Y - view.Y);
        var t = canvas.Theme;
        bool enabled = IsEffectivelyEnabled;
        bool focusCue = Focused && OwningForm is { ShowFocusCues: true };
        for (int i = visible.Y / Math.Max(1, RowHeight); i < _items.Count; i++)
        {
            var row = ItemRect(i);
            if (row.Y >= view.Y + view.Height) break;
            bool selected = Selection.IsSelected(i);
            if (DrawItem != null) { DrawItem(this, canvas, i, row, selected); continue; }
            if (selected) canvas.FillRect(row, Focused || HotTrack ? t.Selection : t.SelectionInactive);
            var textColor = enabled ? t.Text : t.TextDisabled;
            var textRect = new Rect(row.X + canvas.Px(8), row.Y, row.Width - canvas.Px(12), row.Height);
            if (ItemPainter != null) ItemPainter(canvas, i, textRect, textColor);
            else canvas.DrawText(_items[i], EffectiveFont, textColor, textRect, TextAlign.Left, VerticalAlign.Center);
            if (i == Selection.Current && focusCue) canvas.DrawBorder(row, t.FocusRing, canvas.Px(1));
        }
    }
}

/// <summary>A drop-down list (Delphi's csDropDownList combo box): shows the selected item, opens a list below.</summary>
public class ComboBox : Control
{
    string[] _items = [];
    int _itemIndex = -1;
    ListBox? _dropDown;

    public ComboBox()
    {
        SetDefaultSize(160, 26);
        TabStop = true;
    }

    [Published(Category = "Behavior")]
    public string[] Items
    {
        get => _items;
        set { _items = value ?? []; if (_itemIndex >= _items.Length) ItemIndex = _items.Length - 1; Invalidate(); }
    }

    [Published(Category = "Behavior")]
    public int ItemIndex
    {
        get => _itemIndex;
        set
        {
            value = Math.Clamp(value, -1, _items.Length - 1);
            if (value == _itemIndex) return;
            _itemIndex = value;
            Invalidate();
            OnChange();
        }
    }

    /// <summary>How many rows the drop-down shows before it scrolls.</summary>
    [Published(Category = "Behavior")] public int DropDownCount { get; set; } = 8;

    [Published] public event NotifyEvent? Change;

    public string Text => _itemIndex >= 0 ? _items[_itemIndex] : "";
    public bool IsDroppedDown => _dropDown != null;

    protected virtual void OnChange() => Change?.Invoke(this);

    /// <summary>Draws item <paramref name="index"/> (the closed face and the list rows use this).</summary>
    protected virtual void DrawItemContent(Canvas canvas, int index, Rect rect, Color textColor) =>
        canvas.DrawText(_items[index], EffectiveFont, textColor, rect, TextAlign.Left, VerticalAlign.Center);

    public void DropDown()
    {
        if (OwningForm is not { } form || _dropDown != null || _items.Length == 0) return;
        var list = new ListBox
        {
            Items = _items,
            HotTrack = true,
            ItemPainter = DrawItemContent,
        };
        list.ItemIndex = _itemIndex;
        int rows = Math.Clamp(_items.Length, 1, Math.Max(1, DropDownCount));
        list.SetBounds(0, 0, Width, rows * Px(24) + 2 * Px(1));
        list.ItemChosen += i =>
        {
            ItemIndex = i;
            CloseUp();
        };
        list.PopupClosed += () =>
        {
            if (_dropDown == list) _dropDown = null;
            Invalidate();
        };
        _dropDown = list;
        form.OpenPopup(list, AbsoluteLeft, AbsoluteTop + Height, this);
        list.ScrollIntoView(_itemIndex);
        Invalidate();
    }

    public void CloseUp()
    {
        if (_dropDown is { } list && OwningForm is { } form) form.ClosePopup(list);
        _dropDown = null;
        Invalidate();
    }

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButton.Left) DropDown();
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if ((e.Key == Key.Down && e.Modifiers == KeyModifiers.Alt) || (e.Key == Key.F4 && e.Modifiers == KeyModifiers.None))
        {
            DropDown();
            e.Handled = true;
            return;
        }
        if (e.Modifiers != KeyModifiers.None || _items.Length == 0) return;
        int? target = e.Key switch
        {
            Key.Up => Math.Max(0, _itemIndex - 1),
            Key.Down => Math.Min(_items.Length - 1, _itemIndex + 1),
            Key.Home => 0,
            Key.End => _items.Length - 1,
            _ => null,
        };
        if (target is { } t) { ItemIndex = t; e.Handled = true; }
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var r = new Rect(0, 0, Width, Height);
        bool enabled = IsEffectivelyEnabled;
        float radius = canvas.Px(3);
        canvas.FillRoundRect(r, radius, enabled ? t.InputBackground : t.FormBackground);
        canvas.DrawRoundBorder(r, radius, Focused || IsDroppedDown ? t.Accent : IsHovered && enabled ? t.ButtonBorder : t.InputBorder, canvas.Px(1));
        int arrowW = canvas.Px(22);
        var textRect = new Rect(canvas.Px(8), 0, Width - canvas.Px(8) - arrowW, Height);
        if (_itemIndex >= 0)
        {
            canvas.Save();
            canvas.ClipRect(textRect);
            DrawItemContent(canvas, _itemIndex, textRect, enabled ? t.Text : t.TextDisabled);
            canvas.Restore();
        }
        canvas.DrawText("⌄", canvas.DefaultFont, enabled ? t.Text : t.TextDisabled,
            new Rect(Width - arrowW, -canvas.Px(3), arrowW - canvas.Px(4), Height), TextAlign.Center, VerticalAlign.Center);
        if (Focused && OwningForm is { ShowFocusCues: true }) canvas.DrawFocusRing(r, radius);
    }
}

/// <summary>A drop-down of named colours with swatches (Delphi's TColorBox).</summary>
public class ColorBox : ComboBox
{
    static readonly (string Name, Color Color)[] Palette =
    [
        ("Black", Color.FromHex("#000000")), ("Maroon", Color.FromHex("#800000")), ("Green", Color.FromHex("#008000")),
        ("Olive", Color.FromHex("#808000")), ("Navy", Color.FromHex("#000080")), ("Purple", Color.FromHex("#800080")),
        ("Teal", Color.FromHex("#008080")), ("Gray", Color.FromHex("#808080")), ("Silver", Color.FromHex("#C0C0C0")),
        ("Red", Color.FromHex("#FF0000")), ("Lime", Color.FromHex("#00FF00")), ("Yellow", Color.FromHex("#FFFF00")),
        ("Blue", Color.FromHex("#0000FF")), ("Fuchsia", Color.FromHex("#FF00FF")), ("Aqua", Color.FromHex("#00FFFF")),
        ("White", Color.FromHex("#FFFFFF")),
    ];

    public ColorBox()
    {
        Items = [.. Palette.Select(p => p.Name)];
        ItemIndex = 0;
    }

    /// <summary>The selected colour (the nearest palette entry is selected when set).</summary>
    [Published(Category = "Behavior")]
    public Color Selected
    {
        get => ItemIndex >= 0 ? Palette[ItemIndex].Color : Color.Black;
        set
        {
            int i = Array.FindIndex(Palette, p => p.Color == value);
            if (i >= 0) ItemIndex = i;
        }
    }

    protected override void DrawItemContent(Canvas canvas, int index, Rect rect, Color textColor)
    {
        int s = canvas.Px(14);
        var swatch = new Rect(rect.X, rect.Y + (rect.Height - s) / 2, s, s);
        canvas.FillRect(swatch, Palette[index].Color);
        canvas.DrawBorder(swatch, canvas.Theme.InputBorder, canvas.Px(1));
        int gap = s + canvas.Px(8);
        canvas.DrawText(Palette[index].Name, EffectiveFont, textColor,
            new Rect(rect.X + gap, rect.Y, rect.Width - gap, rect.Height), TextAlign.Left, VerticalAlign.Center);
    }
}
