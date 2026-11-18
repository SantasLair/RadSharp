using RadSharp.Drawing;

namespace RadSharp;

public delegate void DrawItemEvent(Control sender, Canvas canvas, int index, Rect rect, bool selected);

/// <summary>How a <see cref="ListBox"/> sizes its rows (Delphi's lbStandard/lbOwnerDrawVariable, without the owner-draw split).</summary>
public enum ListBoxStyle
{
    /// <summary>Every row is <see cref="ListBox.ItemHeight"/> tall.</summary>
    Fixed,
    /// <summary>Each row's height comes from <see cref="ListBox.MeasureItem"/> (default <see cref="ListBox.ItemHeight"/>).</summary>
    Variable,
}

/// <summary>Arguments of <see cref="ListBox.MeasureItem"/>: set <see cref="Height"/> (at 100 % scale) for row <see cref="Index"/>.</summary>
public sealed class MeasureItemEventArgs(int index, int height) : EventArgs
{
    public int Index { get; } = index;
    /// <summary>The row's height at 100 %; starts as <see cref="ListBox.ItemHeight"/>. Values below 1 count as 1.</summary>
    public int Height { get; set; } = height;
}

public delegate void MeasureItemEvent(Control sender, MeasureItemEventArgs e);

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
    ListBoxStyle _style;
    int[]? _tops;          // variable style: _tops[i] is row i's top in pixels, _tops[Count] the total height
    int _topsScale;        // Px(1000) when _tops was built, to notice a change of UI scale
    bool _buildingTops;
    MeasureItemEvent? _measureItem;

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
            _tops = null;
            ContentHeight = TotalHeight;   // first, so the selection scrolls within the new size
            Selection.Count = _items.Count;
            OnItemsReplaced();
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
        get => _loadedItemIndex is int held ? Math.Clamp(held, -1, _items.Count - 1) : Selection.Current;
        set
        {
            if (Loading) { _loadedItemIndex = value; return; }   // items may come later in the file; applied in Loaded
            Selection.Select(Math.Clamp(value, -1, _items.Count - 1));
        }
    }

    int? _loadedItemIndex;

    protected internal override void Loaded()
    {
        base.Loaded();
        if (_loadedItemIndex is int index)
        {
            _loadedItemIndex = null;
            // Scrolling now would use the unscaled, unarranged size; do it at the first arrange instead.
            int at = Math.Clamp(index, -1, _items.Count - 1);
            _lastCurrent = at;
            _scrollCurrentPending = at >= 0;
            Selection.Select(at);
        }
    }

    bool _scrollCurrentPending;

    protected override bool AfterArrange()
    {
        bool again = base.AfterArrange();
        if (_scrollCurrentPending) { _scrollCurrentPending = false; ScrollIntoView(Selection.Current); }
        return again;
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
            InvalidateItemHeights();
        }
    }

    /// <summary>
    /// <see cref="ListBoxStyle.Fixed"/> (the default): every row is <see cref="ItemHeight"/> tall. <see cref="ListBoxStyle.Variable"/>:
    /// each row's height comes from <see cref="MeasureItem"/>; finding a row, scrolling and paging then use a cached table
    /// of row positions (rebuilt after the items or heights change), while a fixed list stays arithmetic.
    /// </summary>
    [Published(Category = "Appearance")]
    public ListBoxStyle Style
    {
        get => _style;
        set { if (_style == value) return; _style = value; InvalidateItemHeights(); }
    }

    /// <summary>The number of items (Delphi's <c>Items.Count</c>).</summary>
    public int Count => _items.Count;

    /// <summary>Raised when the selection or <see cref="ItemIndex"/> changes.</summary>
    [Published] public event NotifyEvent? Change;
    [Published("onDblClick")] public event NotifyEvent? DoubleClick;
    /// <summary>Paint rows yourself (the default drawing is skipped while this has handlers).</summary>
    [Published] public event DrawItemEvent? DrawItem;

    /// <summary>
    /// With <see cref="ListBoxStyle.Variable"/>: asked once per row (when the table of row heights is built) for the row's
    /// height at 100 % scale. The handler must not call back into the list's geometry (<see cref="ItemRect"/>, <see cref="IndexAt"/>...).
    /// Adding or removing a handler rebuilds the table; call <see cref="InvalidateItemHeights"/> after the data it reads changes.
    /// </summary>
    [Published]
    public event MeasureItemEvent? MeasureItem
    {
        // Lazy: a form's generated wiring adds handlers before its code-behind is ready to be called back.
        add { _measureItem += value; ForgetItemHeights(); }
        remove { _measureItem -= value; ForgetItemHeights(); }
    }

    void ForgetItemHeights()
    {
        _tops = null;
        InvalidateLayout();   // the content height is measured again by the next layout pass
        Invalidate();
    }

    /// <summary>Forgets the measured row heights: the next layout or query asks <see cref="MeasureItem"/> again.</summary>
    public void InvalidateItemHeights()
    {
        _tops = null;
        ContentHeight = TotalHeight;
        Invalidate();
    }

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
        _tops = null;
        ContentHeight = TotalHeight;
        OnItemInserted(index);
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
        _tops = null;
        ContentHeight = TotalHeight;
        OnItemDeleted(index);
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

    bool IsVariable => _style == ListBoxStyle.Variable;

    /// <summary>Row tops in pixels, length Count + 1 (the last is the total height); variable style only.</summary>
    int[] Tops()
    {
        int scale = Px(1000);
        if (_tops != null && _tops.Length == _items.Count + 1 && _topsScale == scale) return _tops;
        var tops = new int[_items.Count + 1];
        if (_buildingTops)
        {
            // A MeasureItem handler asked for geometry: answer with uniform rows rather than recursing.
            for (int i = 1; i < tops.Length; i++) tops[i] = i * RowHeight;
            return tops;
        }
        _buildingTops = true;
        try
        {
            for (int i = 0; i < _items.Count; i++)
            {
                int h = _itemHeight;
                if (_measureItem != null)
                {
                    var args = new MeasureItemEventArgs(i, h);
                    _measureItem(this, args);
                    h = args.Height;
                }
                tops[i + 1] = tops[i] + Px(Math.Max(1, h));
            }
        }
        finally { _buildingTops = false; }
        _tops = tops;
        _topsScale = scale;
        return tops;
    }

    /// <summary>The height of all rows in pixels.</summary>
    int TotalHeight => IsVariable ? Tops()[^1] : _items.Count * RowHeight;

    /// <summary>Row <paramref name="index"/>'s top in content pixels (rows outside the list continue at the default height).</summary>
    int TopOf(int index)
    {
        if (!IsVariable || index < 0) return index * RowHeight;
        var tops = Tops();
        return index <= _items.Count ? tops[index] : tops[^1] + (index - _items.Count) * RowHeight;
    }

    int HeightOf(int index)
    {
        if (!IsVariable) return RowHeight;
        var tops = Tops();
        return index >= 0 && index < _items.Count ? tops[index + 1] - tops[index] : RowHeight;
    }

    /// <summary>The row containing content y, or -1 above the first or below the last row.</summary>
    int RowAtContentY(int y)
    {
        if (y < 0 || _items.Count == 0) return -1;
        if (!IsVariable)
        {
            int i = y / Math.Max(1, RowHeight);
            return i < _items.Count ? i : -1;
        }
        var tops = Tops();
        if (y >= tops[^1]) return -1;
        int lo = 0, hi = _items.Count - 1;   // the last row whose top is <= y
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (tops[mid] <= y) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>The scroll offset of the rows, in pixels (the same as <see cref="ScrollView.ScrollY"/>).</summary>
    public int TopOffset => ScrollY;

    /// <summary>The first (possibly partly) visible row; setting it scrolls that row to the top.</summary>
    public int TopIndex
    {
        get => IsVariable ? Math.Max(0, RowAtContentY(ScrollY)) : ScrollY / Math.Max(1, RowHeight);
        set => ScrollY = IsVariable ? TopOf(Math.Clamp(value, 0, _items.Count)) : Math.Max(0, value) * RowHeight;
    }

    protected override int LineStepY => Math.Max(1, RowHeight);

    protected override void MeasureContent() => ContentHeight = TotalHeight;

    /// <summary>Scrolls the least distance that shows row <paramref name="index"/> in full.</summary>
    public void ScrollIntoView(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        ScrollIntoView(new Rect(ScrollX, TopOf(index), 0, HeightOf(index)));
    }

    /// <summary>The row at a point in this control's coordinates, or -1.</summary>
    public int IndexAt(int x, int y)
    {
        var view = Viewport;
        if (x < view.X || x >= view.X + view.Width || y < view.Y || y >= view.Y + view.Height) return -1;
        return RowAtContentY(y - view.Y + ScrollY);
    }

    /// <summary>A row's rectangle in this control's coordinates.</summary>
    public Rect ItemRect(int index)
    {
        var view = Viewport;
        return new Rect(view.X, view.Y + TopOf(index) - ScrollY, view.Width, HeightOf(index));
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
        int pageUp, pageDown;
        if (IsVariable)
        {
            // About a viewport of rows, keeping the current row at the same place; always at least one row.
            int cur = Math.Max(0, current), vh = Viewport.Height;
            pageUp = Math.Min(cur - 1, RowAtContentY(Math.Max(0, TopOf(cur) - vh + HeightOf(cur))));
            int down = RowAtContentY(TopOf(cur) + vh - HeightOf(cur));
            pageDown = Math.Max(cur + 1, down >= 0 ? down : last);
        }
        else
        {
            int page = Math.Max(1, Viewport.Height / Math.Max(1, RowHeight) - 1);
            pageUp = current - page;
            pageDown = current + page;
        }
        int? target = e.Key switch
        {
            Key.Up => Math.Max(0, current - 1),
            Key.Down => Math.Min(last, current + 1),
            Key.Home => 0,
            Key.End => last,
            Key.PageUp => Math.Max(0, pageUp),
            Key.PageDown => Math.Min(last, pageDown),
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

    /// <summary>Draws one row's content (after its selection background, before its focus cue): by default the item's text.</summary>
    protected virtual void PaintItemContent(Canvas canvas, int index, Rect textRect, Color textColor)
    {
        if (ItemPainter != null) ItemPainter(canvas, index, textRect, textColor);
        else canvas.DrawText(_items[index], EffectiveFont, textColor, textRect, TextAlign.Left, VerticalAlign.Center);
    }

    /// <summary>The whole item list was replaced (<see cref="Items"/> was set).</summary>
    protected virtual void OnItemsReplaced() { }

    /// <summary>An item was inserted at <paramref name="index"/> (before the selection is adjusted); a subclass keeps its per-item data in step.</summary>
    protected virtual void OnItemInserted(int index) { }

    /// <summary>The item at <paramref name="index"/> was removed (before the selection is adjusted).</summary>
    protected virtual void OnItemDeleted(int index) { }

    protected override void PaintContent(Canvas canvas, Rect visible)
    {
        // Rows are drawn in this control's coordinates, like ItemRect and the rect DrawItem has always received.
        var view = Viewport;
        canvas.Translate(visible.X - view.X, visible.Y - view.Y);
        var t = canvas.Theme;
        bool enabled = IsEffectivelyEnabled;
        bool focusCue = Focused && OwningForm is { ShowFocusCues: true };
        int first = IsVariable
            ? (visible.Y >= TotalHeight ? _items.Count : Math.Max(0, RowAtContentY(Math.Max(0, visible.Y))))
            : visible.Y / Math.Max(1, RowHeight);
        for (int i = first; i < _items.Count; i++)
        {
            var row = ItemRect(i);
            if (row.Y >= view.Y + view.Height) break;
            bool selected = Selection.IsSelected(i);
            if (DrawItem != null) { DrawItem(this, canvas, i, row, selected); continue; }
            if (selected) canvas.FillRect(row, Focused || HotTrack ? t.Selection : t.SelectionInactive);
            var textColor = enabled ? t.Text : t.TextDisabled;
            var textRect = new Rect(row.X + canvas.Px(8), row.Y, row.Width - canvas.Px(12), row.Height);
            PaintItemContent(canvas, i, textRect, textColor);
            if (i == Selection.Current && focusCue) canvas.DrawBorder(row, t.FocusRing, canvas.Px(1));
        }
    }
}

/// <summary>Whether a <see cref="ComboBox"/> lets the user type (Delphi's TComboBoxStyle csDropDown / csDropDownList).</summary>
public enum ComboBoxStyle
{
    /// <summary>Only the items can be chosen (the default, and how a combo box always behaved).</summary>
    DropDownList,
    /// <summary>The text part is an editable box: type any text, or pick an item from the list.</summary>
    DropDown,
}

/// <summary>
/// A drop-down list (Delphi's TComboBox): shows the selected item and opens a list below. With
/// <see cref="Style"/> = <see cref="ComboBoxStyle.DropDown"/> the text part is editable and can complete what you type
/// from the items (<see cref="AutoComplete"/>).
/// </summary>
/// <remarks>
/// <para>Editable style hosts a transient child <see cref="Edit"/> (<see cref="EditPart"/>), so caret, selection, clipboard,
/// undo and the context menu are the edit's. <see cref="Text"/> is the edit's text; <see cref="ItemIndex"/> follows it (the
/// item equal to the text, else -1), and choosing an item (list, Up/Down, <see cref="ItemIndex"/>) puts its text in the edit.</para>
/// <para>Alt+Down or F4 opens the list; while it is open typing and caret keys still go to the edit, Up/Down/PageUp/PageDown
/// move through the list, Enter chooses (or just closes if nothing is highlighted) and Esc closes. A closed drop-down-list
/// combo box jumps to the next item starting with the typed letter.</para>
/// <para>Autocomplete follows Delphi: after you type a character the first item starting with the text so far fills the edit
/// (in the item's own letter case) with the added part selected, so the next character replaces it. Backspace, Delete, paste,
/// and setting <see cref="Text"/> from code never complete.</para>
/// </remarks>
public class ComboBox : Control
{
    string[] _items = [];
    int _itemIndex = -1;
    int? _loadedItemIndex;

    protected internal override void Loaded()
    {
        base.Loaded();
        // In a drop-down (editable) box the file's text wins over an index that disagrees with it.
        bool textRead = _textRead;
        _textRead = false;
        if (_loadedItemIndex is int index) { _loadedItemIndex = null; if (!(Editable && textRead)) ItemIndex = index; }
    }

    bool _textRead;   // a text was read while loading
    ListBox? _dropDown;
    ComboBoxStyle _style;
    ComboEdit? _edit;
    string _text = "";      // the form file's text while the style isn't DropDown (the edit holds it otherwise)
    bool _updating;         // the combo box itself is changing the edit's text
    int _typing;            // > 0 while typed input and its completion are processed as one change
    bool _typingDirty;

    public ComboBox()
    {
        SetDefaultSize(160, 26);
        TabStop = true;
    }

    [Published(Category = "Behavior")]
    public string[] Items
    {
        get => _items;
        set
        {
            _items = value ?? [];
            if (Editable) _itemIndex = ExactIndex(_edit!.Text);
            else if (_itemIndex >= _items.Length) ItemIndex = _items.Length - 1;
            Invalidate();
        }
    }

    [Published(Category = "Behavior")]
    public int ItemIndex
    {
        get => _loadedItemIndex is int held ? Math.Clamp(held, -1, _items.Length - 1) : _itemIndex;
        set
        {
            if (Loading) { _loadedItemIndex = value; return; }   // items may come later in the file; applied in Loaded
            value = Math.Clamp(value, -1, _items.Length - 1);
            if (value == _itemIndex) return;
            _itemIndex = value;
            if (Editable && value >= 0)
            {
                _updating = true;
                try { _edit!.Text = _items[value]; }
                finally { _updating = false; }
                _edit.SelectAll();
            }
            SyncPopup();
            Invalidate();
            OnChange();
        }
    }

    /// <summary>
    /// <see cref="ComboBoxStyle.DropDownList"/> (the default) or <see cref="ComboBoxStyle.DropDown"/>, whose text part is
    /// editable. A <see cref="ColorBox"/> stays a drop-down list.
    /// </summary>
    [Published(Category = "Behavior")]
    public ComboBoxStyle Style
    {
        get => _style;
        set
        {
            if (!AllowEditing) value = ComboBoxStyle.DropDownList;
            if (_style == value) return;
            _style = value;
            if (value == ComboBoxStyle.DropDown) CreateEdit();
            else RemoveEdit();
            Invalidate();
        }
    }

    /// <summary>With <see cref="ComboBoxStyle.DropDown"/>: complete what is typed from the items (Delphi's AutoComplete).</summary>
    [Published(Category = "Behavior")] public bool AutoComplete { get; set; } = true;

    /// <summary>How many rows the drop-down shows before it scrolls.</summary>
    [Published(Category = "Behavior")] public int DropDownCount { get; set; } = 8;

    /// <summary>
    /// The text a drop-down combo box starts with, as saved in the form file ("text"); only meaningful with
    /// <see cref="ComboBoxStyle.DropDown"/>. Use <see cref="Text"/> in code.
    /// </summary>
    [Published("text", Category = "Behavior")]
    public string EditText
    {
        get => Editable ? _edit!.Text : _text;
        set
        {
            _text = value ?? "";
            if (Loading) _textRead = true;
            if (Editable) SetEditText(_text);
        }
    }

    /// <summary>Raised when the text or the selected item changes (typing, choosing, or from code).</summary>
    [Published] public event NotifyEvent? Change;

    /// <summary>
    /// The text shown: the edit's text with <see cref="ComboBoxStyle.DropDown"/>, else the selected item. Setting it in an
    /// editable box replaces the text; in a list box it selects the item equal to the value (ignored when there is none).
    /// </summary>
    public string Text
    {
        get => Editable ? _edit!.Text : _itemIndex >= 0 ? _items[_itemIndex] : "";
        set
        {
            value ??= "";
            if (Editable) SetEditText(value);
            else if (ExactIndex(value) is >= 0 and var i) ItemIndex = i;
        }
    }

    public bool IsDroppedDown => _dropDown != null;

    /// <summary>The editable text part (a child <see cref="Edit"/>) with <see cref="ComboBoxStyle.DropDown"/>, else null.</summary>
    public Edit? EditPart => _edit;

    public int SelectionStart { get => _edit?.SelectionStart ?? 0; set => _edit?.Select(value, 0); }
    public int SelectionLength { get => _edit?.SelectionLength ?? 0; set { if (_edit != null) _edit.Select(_edit.SelectionStart, value); } }
    public string SelectedText => _edit?.SelectedText ?? "";
    public void SelectAll() => _edit?.SelectAll();

    /// <summary>False in a subclass whose face isn't text (<see cref="ColorBox"/>): <see cref="Style"/> then stays DropDownList.</summary>
    protected virtual bool AllowEditing => true;

    bool Editable => _edit != null;

    protected virtual void OnChange() => Change?.Invoke(this);

    /// <summary>Draws item <paramref name="index"/> (the closed face and the list rows use this).</summary>
    protected virtual void DrawItemContent(Canvas canvas, int index, Rect rect, Color textColor) =>
        canvas.DrawText(_items[index], EffectiveFont, textColor, rect, TextAlign.Left, VerticalAlign.Center);

    // ---- The editable text part ----

    int ExactIndex(string text) => Array.IndexOf(_items, text);

    void CreateEdit()
    {
        string initial = _text.Length > 0 ? _text : _itemIndex >= 0 ? _items[_itemIndex] : "";
        _edit = new ComboEdit(this) { BorderStyle = BorderStyle.None, IsTransient = true };
        _updating = true;
        try { _edit.Text = initial; }
        finally { _updating = false; }
        _text = "";
        _itemIndex = ExactIndex(initial);
        Add(_edit);
        PlaceEdit();
    }

    void RemoveEdit()
    {
        if (_edit is not { } edit) return;
        bool hadFocus = edit.Focused;
        CloseUp();
        _edit = null;
        Remove(edit);
        if (hadFocus) Focus();
    }

    /// <summary>Puts the edit left of the arrow button, inside the border.</summary>
    void PlaceEdit()
    {
        if (_edit is not { } e) return;
        int b = Px(1), arrow = Px(22);
        int w = Math.Max(0, Width - 2 * b - arrow), h = Math.Max(0, Height - 2 * b);
        if (e.Left != b || e.Top != b || e.Width != w || e.Height != h) e.SetBounds(b, b, w, h);
    }

    protected override void OnResize()
    {
        PlaceEdit();
        base.OnResize();
    }

    protected override bool AfterArrange()
    {
        PlaceEdit();   // also after the combo box joined a form with another UI scale
        return false;
    }

    /// <summary>The edit is the tab stop of an editable combo box; the combo box itself then isn't one.</summary>
    protected internal override bool IncludeInTabOrder => TabStop && !Editable;

    /// <summary>Replaces the edit's text from code (no completion); raises <see cref="Change"/> once if it differs.</summary>
    void SetEditText(string text)
    {
        if (_edit is not { } edit || edit.Text == text) return;
        _updating = true;
        try { edit.Text = text; }
        finally { _updating = false; }
        TextChanged();
    }

    /// <summary>The edit's text changed (typing, paste, undo, the context menu).</summary>
    void EditChanged()
    {
        if (_updating) return;
        if (_typing > 0) { _typingDirty = true; return; }
        TextChanged();
    }

    void TextChanged()
    {
        _itemIndex = ExactIndex(_edit!.Text);
        SyncPopup();
        Invalidate();
        OnChange();
    }

    /// <summary>The open list highlights the item that matches the text.</summary>
    void SyncPopup()
    {
        if (_dropDown is { } list && list.ItemIndex != _itemIndex) list.ItemIndex = _itemIndex;
    }

    void BeginTyping() => _typing++;

    void EndTyping()
    {
        if (--_typing > 0 || !_typingDirty) return;
        _typingDirty = false;
        TextChanged();
    }

    /// <summary>After a typed character: fills in the first item that starts with the text, selecting the added part.</summary>
    void CompleteTyped()
    {
        if (!AutoComplete || _edit is not { IsComposing: false } edit) return;
        var text = edit.Text;
        if (text.Length == 0 || edit.SelectionLength != 0 || edit.CaretPosition != text.Length) return;
        if (Array.Exists(_items, it => string.Equals(it, text, StringComparison.CurrentCultureIgnoreCase))) return;   // already an item: don't run past it
        int match = Array.FindIndex(_items, it => it.Length > text.Length && it.StartsWith(text, StringComparison.CurrentCultureIgnoreCase));
        if (match < 0) return;
        edit.CompleteWith(_items[match], text.Length);
    }

    // ---- Popup ----

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
            // A click in the list gave it the focus, and closing it dropped that: the edit takes it back.
            if (_edit != null && OwningForm is { FocusedControl: null }) _edit.Focus();
        };
        list.PopupClosed += () =>
        {
            if (_dropDown == list) _dropDown = null;
            Invalidate();
        };
        if (Editable)
        {
            list.PopupTextTarget = _edit;
            list.KeyDown += (_, e) => ForwardPopupKey(list, e);
        }
        _dropDown = list;
        form.OpenPopup(list, AbsoluteLeft, AbsoluteTop + Height, this);
        list.ScrollIntoView(_itemIndex);
        Invalidate();
    }

    /// <summary>While the list is open the form sends it every key: the ones the edit uses go on to it.</summary>
    void ForwardPopupKey(ListBox list, KeyEventArgs e)
    {
        if (e.Handled || _edit == null) return;
        if ((e.Key == Key.Down && e.Modifiers == KeyModifiers.Alt) || (e.Key == Key.F4 && e.Modifiers == KeyModifiers.None))
        {
            CloseUp();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.Modifiers == KeyModifiers.None && list.ItemIndex < 0)
        {
            CloseUp();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Enter or Key.Escape or Key.Tab) { }
        else _edit.OnKeyDown(e);
    }

    public void CloseUp()
    {
        if (_dropDown is { } list && OwningForm is { } form) form.ClosePopup(list);
        _dropDown = null;
        Invalidate();
    }

    // ---- Input ----

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButton.Left) DropDown();
    }

    protected internal override void OnGotFocus()
    {
        base.OnGotFocus();
        _edit?.Focus();   // the combo box is the tab stop; typing goes to its edit
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
            Key.Home when !Editable => 0,   // an editable box leaves Home/End to its caret
            Key.End when !Editable => _items.Length - 1,
            _ => null,
        };
        if (target is { } t) { ItemIndex = t; e.Handled = true; }
    }

    /// <summary>A closed drop-down list jumps to the next item that starts with the typed letter.</summary>
    protected internal override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || Editable || e.Text.Length == 0 || string.IsNullOrWhiteSpace(e.Text) || _items.Length == 0) return;
        for (int n = 1; n <= _items.Length; n++)
        {
            int i = (_itemIndex + n + _items.Length) % _items.Length;
            if (_items[i].StartsWith(e.Text, StringComparison.CurrentCultureIgnoreCase)) { ItemIndex = i; break; }
        }
        e.Handled = true;
    }

    // ---- Painting ----

    bool HasFocus => Focused || (_edit?.Focused ?? false);

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var r = new Rect(0, 0, Width, Height);
        bool enabled = IsEffectivelyEnabled;
        float radius = canvas.Px(3);
        canvas.FillRoundRect(r, radius, enabled ? t.InputBackground : t.FormBackground);
        bool hovered = IsHovered || (_edit?.IsHovered ?? false);
        canvas.DrawRoundBorder(r, radius, HasFocus || IsDroppedDown ? t.Accent : hovered && enabled ? t.ButtonBorder : t.InputBorder, canvas.Px(1));
        int arrowW = canvas.Px(22);
        var textRect = new Rect(canvas.Px(8), 0, Width - canvas.Px(8) - arrowW, Height);
        if (_itemIndex >= 0 && !Editable)
        {
            canvas.Save();
            canvas.ClipRect(textRect);
            DrawItemContent(canvas, _itemIndex, textRect, enabled ? t.Text : t.TextDisabled);
            canvas.Restore();
        }
        canvas.DrawText("⌄", canvas.DefaultFont, enabled ? t.Text : t.TextDisabled,
            new Rect(Width - arrowW, -canvas.Px(3), arrowW - canvas.Px(4), Height), TextAlign.Center, VerticalAlign.Center);
        if (Focused && !Editable && OwningForm is { ShowFocusCues: true }) canvas.DrawFocusRing(r, radius);
    }

    /// <summary>The text part of a drop-down combo box: a borderless <see cref="Edit"/> that reports to its combo box.</summary>
    sealed class ComboEdit : Edit
    {
        readonly ComboBox _owner;

        /// <summary>Follows the combo box's own <see cref="Control.TabStop"/>.</summary>
        protected internal override bool IncludeInTabOrder => _owner.TabStop;

        public ComboEdit(ComboBox owner)
        {
            _owner = owner;
            Change += _ => _owner.EditChanged();
        }

        protected internal override void OnKeyDown(KeyEventArgs e)
        {
            _owner.OnKeyDown(e);   // the combo box's own keys (open the list, Up/Down through the items) and its KeyDown event
            if (!e.Handled) base.OnKeyDown(e);
        }

        protected internal override void OnTextInput(TextInputEventArgs e)
        {
            _owner.BeginTyping();
            try
            {
                bool typed = !e.Handled && !ReadOnly && !IsComposing;
                base.OnTextInput(e);
                if (typed) _owner.CompleteTyped();
            }
            finally { _owner.EndTyping(); }
        }

        protected internal override void OnGotFocus()
        {
            base.OnGotFocus();
            _owner.Invalidate();
        }

        protected internal override void OnLostFocus()
        {
            base.OnLostFocus();
            _owner.Invalidate();
        }

        /// <summary>Makes the text <paramref name="full"/> (an item that starts with the typed text) with the part after <paramref name="typed"/> selected.</summary>
        public void CompleteWith(string full, int typed)
        {
            if (full.StartsWith(Text, StringComparison.Ordinal))
            {
                Select(typed, 0);
                InsertTyped(full[typed..]);   // keeps the typed letters as typed, and the undo history
            }
            else Text = full;                 // the item's own letter case replaces what was typed
            Select(typed, full.Length - typed);
        }
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

    protected override bool AllowEditing => false;

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
