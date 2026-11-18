using System.Collections;
using System.Globalization;
using System.Text.Json;
using RadSharp.Drawing;

namespace RadSharp;

/// <summary>A row of a <see cref="ListView"/>: a caption and one text per further column.</summary>
public sealed class ListViewItem(string caption, params string[] subItems)
{
    public string Caption { get; set; } = caption;
    public List<string> SubItems { get; } = [.. subItems];
    public object? Tag { get; set; }

    /// <summary>The state of the item's check box (with <see cref="ListView.Checkboxes"/>). Call <c>Invalidate</c> on the view after setting it from code.</summary>
    public bool Checked { get; set; }

    /// <summary>The picture from the view's image list drawn before the caption (<see cref="ListView.ImagesName"/>); -1 draws none.</summary>
    public int ImageIndex { get; set; } = -1;

    public string Text(int column) => column == 0 ? Caption : column - 1 < SubItems.Count ? SubItems[column - 1] : "";
}

public delegate void ColumnClickEvent(Control sender, int column);

/// <summary>Virtual mode: fill <see cref="Item"/> for row <see cref="Index"/> (Delphi's OnData).</summary>
public sealed class ListViewItemEventArgs(int index) : EventArgs
{
    public int Index { get; } = index;
    /// <summary>A blank item to fill: caption, sub-items, and with checkboxes or images its <c>Checked</c>/<c>ImageIndex</c>.</summary>
    public ListViewItem Item { get; } = new("");
}

public delegate void ListViewItemEvent(Control sender, ListViewItemEventArgs e);

/// <summary>Two items compared while a <see cref="ListView"/> sorts; set <see cref="Result"/> (negative, 0, positive).</summary>
public sealed class ListViewCompareEventArgs(int column, ListViewItem item1, ListViewItem item2, int result) : EventArgs
{
    public int Column { get; } = column;
    public ListViewItem Item1 { get; } = item1;
    public ListViewItem Item2 { get; } = item2;
    /// <summary>Pre-set to the default (numbers by value, else text ignoring case), ascending; the view reverses it for a descending sort.</summary>
    public int Result { get; set; } = result;
}

public delegate void ListViewCompareEvent(Control sender, ListViewCompareEventArgs e);

/// <summary>The user toggled an item's check box; <paramref name="isChecked"/> is the new state.</summary>
public delegate void ItemCheckedEvent(Control sender, int index, bool isChecked);

/// <summary>
/// A multi-column list with a header (Delphi's TListView in report style), built on <see cref="ScrollView"/>: both
/// scroll bars, a header that stays at the top and scrolls sideways with the rows, and the selection in
/// <see cref="Selection"/> (an <see cref="ItemSelection"/>, so selecting all of 100 000 rows is one range).
/// </summary>
/// <remarks>
/// <para>Rows come from <see cref="Items"/> or, in virtual mode (a <see cref="GetItem"/> handler, Delphi's OwnerData),
/// from the handler, asked only for the rows being painted; <see cref="ItemCount"/> is then the row count.</para>
/// <para>Mouse: a click selects; with <see cref="MultiSelect"/> Ctrl/⌘+click toggles, Shift+click selects a range and
/// dragging sweeps one. A click on a header raises <see cref="ColumnClick"/>; with <see cref="AutoSort"/> it first sets
/// <see cref="SortColumn"/> (again: flips <see cref="SortDescending"/>) and sorts the in-memory items (stable,
/// <see cref="Compare"/> overrides; in virtual mode the ColumnClick handler sorts). Dragging a header border resizes a
/// column. Keys: Up/Down, PageUp/PageDown, Home/End move the current item (Shift extends, Ctrl/⌘ moves alone),
/// Left/Right scroll sideways, Space toggles the check box (<see cref="Checkboxes"/>), Ctrl/⌘+Space the selection,
/// Ctrl/⌘+A selects all, Enter raises <see cref="DoubleClick"/>; typing jumps to the next caption starting with the
/// typed text.</para>
/// </remarks>
public class ListView : ScrollView, ICollectionOwner, IColumnHost, IFormCollections
{
    readonly GridColumns _columns;
    int _columnCount;                 // the column count the sort rule last saw
    bool _replacingColumns;
    int[] _colX = [0];                // content x of each column's left edge, scaled; [Count] = total width
    bool _colXDirty = true;
    double _colXScale;
    bool _multiSelect, _checkboxes, _autoSort, _sortDescending;
    int _sortColumn = -1;
    string _imagesName = "";
    int _virtualCount;
    int _lastCurrent = -1;
    bool _mouseSelecting, _noAutoScroll, _quiet;
    int _resizing = -1, _resizeStartX, _resizeStartWidth;
    bool _sizingCursor;
    Cursor _savedCursor;
    readonly TypeAhead _typeAhead = new();

    public ListView()
    {
        SetDefaultSize(300, 180);
        _columns = new GridColumns(this);
        Selection.Changed += SelectionChanged;
    }

    // ---- Columns ----

    /// <summary>
    /// The columns: title, width (0: 100), alignment and minimum width. Assigning replaces them, and a collection
    /// expression may use <c>"Title:width"</c> strings (<c>Columns = ["Name:120", "Size:60", "Kind"]</c>). Without
    /// columns, one untitled column fills the view. Saved in form files as <c>"columns"</c>.
    /// </summary>
    public GridColumns Columns
    {
        get => _columns;
        set
        {
            if (ReferenceEquals(value, _columns)) return;
            var source = value?.ToList() ?? [];
            _replacingColumns = true;
            try
            {
                _columns.Clear();
                foreach (var c in source)
                    _columns.Add(c.Host == null ? c : new GridColumn(c.Title, c.Width, c.Alignment) { MinWidth = c.MinWidth });
            }
            finally { _replacingColumns = false; }
            ColumnsUpdated();
        }
    }

    public string ColumnCaption(int i) => _columns[i].Title;

    /// <summary>A column's current width at 100 % (the user can drag header borders).</summary>
    public int ColumnWidth(int i) => _columns[i].Width > 0 ? _columns[i].Width : 100;

    void IColumnHost.ColumnsChanged(bool widths, bool structure, bool adopting)
    {
        if (!_replacingColumns) ColumnsUpdated();
    }

    void ColumnsUpdated()
    {
        int count = _columns.Count;
        if (count < _columnCount && _sortColumn >= count) _sortColumn = -1;   // the shrink rule, as in grids
        _columnCount = count;
        _colXDirty = true;
        Remeasure();
        Invalidate();
    }

    // ---- Items ----

    public List<ListViewItem> Items { get; } = [];

    /// <summary>Call after changing <see cref="Items"/> from code (the count is also picked up on the next layout).</summary>
    public void ItemsChanged()
    {
        SyncCount();
        Remeasure();
        Invalidate();
    }

    /// <summary>
    /// The number of rows: <c>Items.Count</c>, or in virtual mode (a <see cref="GetItem"/> handler) the count set here
    /// (Delphi's <c>Items.Count</c> with OwnerData). Setting it only matters in virtual mode.
    /// </summary>
    public int ItemCount
    {
        get => IsVirtual ? _virtualCount : Items.Count;
        set
        {
            _virtualCount = Math.Max(0, value);
            SyncCount();
            Remeasure();
            Invalidate();
        }
    }

    /// <summary>True when a <see cref="GetItem"/> handler supplies the rows.</summary>
    public bool IsVirtual => GetItem != null;

    /// <summary>The current item, or -1 (the selected one without <see cref="MultiSelect"/>). Setting it selects that item alone and scrolls it into view.</summary>
    public int ItemIndex
    {
        get { SyncCount(); return Selection.Current; }
        set { SyncCount(); Selection.Select(Math.Clamp(value, -1, ItemCount - 1)); }
    }

    /// <summary>The item at <see cref="ItemIndex"/> (in virtual mode a fresh one from <see cref="GetItem"/>), or null.</summary>
    public ListViewItem? Selected => ItemIndex >= 0 ? FetchItem(ItemIndex) : null;

    /// <summary>The selection model: selected ranges, the current item and the anchor.</summary>
    public ItemSelection Selection { get; } = new(SelectionMode.Single);

    [Published] public event NotifyEvent? Change;
    [Published("onDblClick")] public event NotifyEvent? DoubleClick;
    /// <summary>A header was clicked; with <see cref="AutoSort"/>, after the sort arrow moved and the items were sorted.</summary>
    [Published] public event ColumnClickEvent? ColumnClick;
    /// <summary>Virtual mode: supplies the row being painted (or read); the view keeps no items of its own.</summary>
    [Published] public event ListViewItemEvent? GetItem;
    /// <summary>Compares two items while sorting; the default compares numbers by value, otherwise text ignoring case.</summary>
    [Published] public event ListViewCompareEvent? Compare;
    /// <summary>The user toggled a check box (a click on it, or Space); in virtual mode the handler stores the new state.</summary>
    [Published] public event ItemCheckedEvent? ItemChecked;

    /// <summary>Several items can be selected (Delphi's MultiSelect): Ctrl/⌘+click toggles, Shift selects ranges.</summary>
    [Published(Category = "Behavior")]
    public bool MultiSelect
    {
        get => _multiSelect;
        set { _multiSelect = value; Selection.Mode = value ? SelectionMode.Extended : SelectionMode.Single; }
    }

    /// <summary>Each row shows a check box before its caption (<see cref="ListViewItem.Checked"/>).</summary>
    [Published(Category = "Appearance")]
    public bool Checkboxes { get => _checkboxes; set { _checkboxes = value; Invalidate(); } }

    /// <summary>The name of an ImageList whose pictures rows show by <see cref="ListViewItem.ImageIndex"/>.</summary>
    [Published("images", Category = "Appearance")]
    public string ImagesName { get => _imagesName; set { _imagesName = value ?? ""; Invalidate(); } }

    /// <summary>
    /// A header click sorts by that column: it sets <see cref="SortColumn"/> (a second click flips
    /// <see cref="SortDescending"/>) and sorts <see cref="Items"/> before <see cref="ColumnClick"/>; in virtual mode only
    /// the arrow moves and the ColumnClick handler sorts the data.
    /// </summary>
    [Published(Category = "Behavior")]
    public bool AutoSort { get => _autoSort; set => _autoSort = value; }

    /// <summary>
    /// The column whose header shows the sort arrow, or -1. Any value is kept, so it can be set before the columns
    /// exist; removing columns so that it is past the last one resets it to -1 (adding never brings it back).
    /// </summary>
    [Published(Category = "Appearance")]
    public int SortColumn { get => _sortColumn; set { _sortColumn = Math.Max(-1, value); Invalidate(); } }

    /// <summary>The sort arrow points down (descending) rather than up.</summary>
    [Published(Category = "Appearance")]
    public bool SortDescending { get => _sortDescending; set { _sortDescending = value; Invalidate(); } }

    // ---- Selection ----

    public bool IsSelected(int index) => Selection.IsSelected(index);
    public void SetSelected(int index, bool selected) { SyncCount(); Selection.SetSelected(index, selected); }
    public int SelectedCount => Selection.SelectedCount;
    public int[] SelectedIndices => [.. Selection.SelectedIndices];
    public void SelectAll() { SyncCount(); Selection.SelectAll(); }
    public void ClearSelection() => Selection.ClearSelection();

    // ---- Sorting ----

    /// <summary>
    /// Sorts <see cref="Items"/> by a column, stably (numbers by value, else text ignoring case; <see cref="Compare"/>
    /// overrides), and sets <see cref="SortColumn"/>/<see cref="SortDescending"/>. The selection stays on the same items.
    /// In virtual mode only the arrow changes.
    /// </summary>
    public void SortItems(int column, bool descending = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        SortColumn = column;
        SortDescending = descending;
        SyncCount();
        if (IsVirtual || Items.Count < 2) return;
        var selected = new HashSet<ListViewItem>(ReferenceEqualityComparer.Instance);
        foreach (var i in Selection.SelectedIndices) selected.Add(Items[i]);
        var current = Selection.Current >= 0 ? Items[Selection.Current] : null;
        var keyed = Items.Select((item, i) => (Item: item, Index: i)).ToArray();
        int Cmp((ListViewItem Item, int Index) a, (ListViewItem Item, int Index) b)
        {
            int result = StringGrid.DefaultCompare(a.Item.Text(column), b.Item.Text(column));
            if (Compare != null)
            {
                var args = new ListViewCompareEventArgs(column, a.Item, b.Item, result);
                Compare(this, args);
                result = args.Result;
            }
            if (descending) result = result < 0 ? 1 : result > 0 ? -1 : 0;
            return result != 0 ? result : a.Index.CompareTo(b.Index);   // stable
        }
        Array.Sort(keyed, Cmp);
        Items.Clear();
        Items.AddRange(keyed.Select(k => k.Item));

        int before = Selection.Current;
        _quiet = _noAutoScroll = true;
        try
        {
            int newCurrent = current == null ? -1 : Items.IndexOf(current);
            Selection.Reset(Items.Count);
            if (Selection.Mode is SelectionMode.Multi or SelectionMode.Extended)
                foreach (var (first, last) in Runs(Enumerable.Range(0, Items.Count).Where(i => selected.Contains(Items[i]))))
                    Selection.SelectRange(first, last, add: true);
            if (newCurrent >= 0) Selection.Current = newCurrent;
        }
        finally { _quiet = _noAutoScroll = false; }
        _lastCurrent = Selection.Current;
        Invalidate();
        if (Selection.Current != before) Change?.Invoke(this);
    }

    /// <summary>Consecutive runs of ascending indices.</summary>
    internal static IEnumerable<(int First, int Last)> Runs(IEnumerable<int> ascending)
    {
        int first = -1, last = -2;
        foreach (var i in ascending)
        {
            if (i == last + 1) { last = i; continue; }
            if (first >= 0) yield return (first, last);
            first = last = i;
        }
        if (first >= 0) yield return (first, last);
    }

    // ---- Form files and the designer: "columns" ----

    IReadOnlyList<string> ICollectionOwner.CollectionNames { get; } = ["columns"];

    string ICollectionOwner.DescribeCollection(string name) =>
        name == "columns" ? (_columns.Count == 1 ? "(1 column)" : $"({_columns.Count} columns)") : "";

    // "sortColumn" is checked against the columns, so both come after the other members (as in grids).
    IReadOnlyList<string> IFormCollections.DeferredMembers { get; } = ["columns", "sortColumn"];

    void IFormCollections.ReadDeferred(string name, JsonElement value, List<string> warnings, string here)
    {
        if (name == "columns")
        {
            var columns = new GridColumns();
            foreach (var c in FormCollections.ReadColumns(value, warnings, $"{here}.columns", allowSpecs: true)) columns.Add(c);
            Columns = columns;
        }
        else if (name == "sortColumn" && FormCollections.TrySetMember(this, name, value, warnings, here) && _sortColumn >= _columns.Count)
        {
            warnings.Add($"{here}.sortColumn: column {_sortColumn} doesn't exist ({(_columns.Count == 1 ? "1 column" : $"{_columns.Count} columns")}); ignored.");
            SortColumn = -1;
        }
    }

    void IFormCollections.WriteCollections(FormJsonWriter w) => FormCollections.WriteColumns(w, _columns);

    // ---- Geometry ----

    int HeaderHeight => Px(26);
    int RowHeight => Px(24);

    protected override int LineStepY => Math.Max(1, RowHeight);
    protected override int PageStepY => Math.Max(RowHeight, Viewport.Height - HeaderHeight);

    /// <summary>The first (possibly partly) visible row; setting it scrolls that row to just below the header.</summary>
    public int TopIndex
    {
        get => ScrollY / Math.Max(1, RowHeight);
        set => ScrollY = Math.Max(0, value) * RowHeight;
    }

    void EnsureColX()
    {
        int n = _columns.Count;
        if (!_colXDirty && _colXScale == UiScale && _colX.Length == n + 1) return;
        var x = new int[n + 1];
        for (int c = 0; c < n; c++) x[c + 1] = x[c] + Px(ColumnWidth(c));
        _colX = x;
        _colXDirty = false;
        _colXScale = UiScale;
    }

    void SyncCount()
    {
        int n = ItemCount;
        if (Selection.Count == n) return;
        _noAutoScroll = true;
        try { Selection.Count = n; }
        finally { _noAutoScroll = false; }
    }

    void Remeasure()
    {
        EnsureColX();
        ContentWidth = _colX[^1];
        ContentHeight = HeaderHeight + ItemCount * RowHeight;
    }

    protected override void MeasureContent()
    {
        SyncCount();
        EnsureColX();
        ContentWidth = _colX[^1];
        ContentHeight = HeaderHeight + ItemCount * RowHeight;
    }

    /// <summary>Scrolls the least distance that shows row <paramref name="index"/> in full below the header.</summary>
    public void ScrollIntoView(int index)
    {
        if (index < 0 || index >= ItemCount) return;
        // Inflating the row up by the header's height keeps it clear of the pinned header.
        ScrollIntoView(new Rect(ScrollX, index * RowHeight, 0, Math.Min(RowHeight + HeaderHeight, Math.Max(RowHeight, Viewport.Height))));
    }

    /// <summary>The row at a height in this control's coordinates (below the header), or -1.</summary>
    public int IndexAt(int y)
    {
        var view = Viewport;
        if (y < view.Y + HeaderHeight || y >= view.Y + view.Height) return -1;
        int i = (y - view.Y - HeaderHeight + ScrollY) / Math.Max(1, RowHeight);
        return i < ItemCount ? i : -1;
    }

    /// <summary>The row at a point in this control's coordinates, or -1.</summary>
    public int IndexAt(int x, int y)
    {
        var view = Viewport;
        return x < view.X || x >= view.X + view.Width ? -1 : IndexAt(y);
    }

    /// <summary>A row's rectangle in this control's coordinates (it may be out of view, or under the header).</summary>
    public Rect ItemRect(int index)
    {
        var view = Viewport;
        return new Rect(view.X, view.Y + HeaderHeight + index * RowHeight - ScrollY, view.Width, RowHeight);
    }

    bool InHeader(int x, int y)
    {
        var view = Viewport;
        return x >= view.X && x < view.X + view.Width && y >= view.Y && y < view.Y + Math.Min(HeaderHeight, view.Height);
    }

    /// <summary>The column at x (this control's coordinates), or -1.</summary>
    int ColumnAt(int x)
    {
        EnsureColX();
        int cx = x - Viewport.X + ScrollX;
        for (int c = 0; c < _columns.Count; c++)
            if (cx >= _colX[c] && cx < _colX[c + 1]) return c;
        return -1;
    }

    /// <summary>The column whose right border is near x (this control's coordinates), for resizing; -1 if none.</summary>
    int BorderAt(int x)
    {
        EnsureColX();
        var view = Viewport;
        int tol = Px(4), best = -1, bestDist = tol + 1;
        for (int c = 0; c < _columns.Count; c++)
        {
            int edge = view.X + _colX[c + 1] - ScrollX, d = Math.Abs(edge - x);
            if (d < bestDist && edge >= view.X && edge <= view.X + view.Width) { best = c; bestDist = d; }
        }
        return best;
    }

    /// <summary>True if x (this control's coordinates) is on the first column's check box.</summary>
    bool OnCheckBox(int x)
    {
        int cx = x - Viewport.X + ScrollX;
        return _checkboxes && cx >= Px(2) && cx < Px(24);
    }

    internal ListViewItem? FetchItem(int index)
    {
        if (index < 0) return null;
        if (GetItem is { } handler)
        {
            if (index >= _virtualCount) return null;
            var args = new ListViewItemEventArgs(index);
            handler(this, args);
            return args.Item;
        }
        return index < Items.Count ? Items[index] : null;
    }

    bool IsMulti => Selection.Mode is SelectionMode.Multi or SelectionMode.Extended;

    void SelectionChanged()
    {
        if (Selection.Current != _lastCurrent)
        {
            _lastCurrent = Selection.Current;
            if (!_noAutoScroll) ScrollIntoView(_lastCurrent);
        }
        Invalidate();
        if (!_quiet) Change?.Invoke(this);
    }

    /// <summary>Flips the check box of the current item, and of every selected item along with it if it is selected.</summary>
    void ToggleChecks(int index)
    {
        if (FetchItem(index) is not { } item) return;
        bool state = !item.Checked;
        IEnumerable<int> targets = Selection.IsSelected(index) && Selection.SelectedCount > 1 ? Selection.SelectedIndices.ToArray() : [index];
        foreach (var i in targets)
        {
            if (!IsVirtual && i < Items.Count) Items[i].Checked = state;
            ItemChecked?.Invoke(this, i, state);
        }
        Invalidate();
    }

    void HeaderClick(int column)
    {
        if (_autoSort)
        {
            bool descending = _sortColumn == column && !_sortDescending;
            if (IsVirtual) { SortColumn = column; SortDescending = descending; }
            else SortItems(column, descending);
        }
        ColumnClick?.Invoke(this, column);
    }

    // ---- Input ----

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);   // the scroll bars take their own presses
        if (IsScrolling || IsOverScrollBar(e.X, e.Y)) return;
        SyncCount();
        _typeAhead.Reset();
        if (InHeader(e.X, e.Y))
        {
            if (e.Button != MouseButton.Left) return;
            if (BorderAt(e.X) is var b and >= 0)
            {
                _resizing = b;
                _resizeStartX = e.X;
                _resizeStartWidth = ColumnWidth(b);
                return;
            }
            if (ColumnAt(e.X) is var c and >= 0) HeaderClick(c);
            return;
        }
        int i = IndexAt(e.X, e.Y);
        if (i < 0) return;
        if (e.Button != MouseButton.Left)
        {
            // Right-click selects too, before a context menu opens, but keeps a multi-selection it lands in.
            if (!Selection.IsSelected(i)) Selection.Click(i);
            return;
        }
        if (OnCheckBox(e.X) && e.Modifiers == KeyModifiers.None) { ToggleChecks(i); return; }
        Selection.Click(i, toggle: e.Modifiers.HasFlag(Shortcut.CommandModifier), extend: e.Modifiers.HasFlag(KeyModifiers.Shift));
        _mouseSelecting = true;
        if (e.Clicks == 2) DoubleClick?.Invoke(this);
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_resizing >= 0)
        {
            var column = _columns[_resizing];
            int min = column.MinWidth > 0 ? column.MinWidth : 20;
            column.Width = Math.Max(min, _resizeStartWidth + (int)Math.Round((e.X - _resizeStartX) / UiScale));
            return;
        }
        if (IsScrolling) return;
        if (_mouseSelecting)
        {
            var view = Viewport;
            int top = view.Y + Math.Min(HeaderHeight, view.Height), bottom = view.Y + view.Height - 1;
            if (bottom < top) return;
            int i = IndexAt(Math.Clamp(e.Y, top, bottom));
            if (i < 0) i = ItemCount - 1;   // below the last row
            if (i >= 0 && i != Selection.Current) Selection.DragTo(i);
            return;
        }
        UpdateSizingCursor(InHeader(e.X, e.Y) && BorderAt(e.X) >= 0);
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _mouseSelecting = false;
        _resizing = -1;
    }

    protected internal override void OnMouseLeave()
    {
        base.OnMouseLeave();
        UpdateSizingCursor(false);
    }

    /// <summary>Shows the resize cursor over a header border and, after it, gives back the cursor the app had set.</summary>
    void UpdateSizingCursor(bool over)
    {
        if (over)
        {
            if (!_sizingCursor) { _savedCursor = Cursor; _sizingCursor = true; }
            Cursor = Cursor.SizeWE;
        }
        else if (_sizingCursor)
        {
            _sizingCursor = false;
            Cursor = _savedCursor;
        }
    }

    protected override bool HandleKey(KeyEventArgs e)
    {
        SyncCount();
        int count = ItemCount;
        if (count == 0) return false;
        int current = Selection.Current;
        bool shift = e.Shift, command = e.Command;
        if (e.Key == Key.Enter && e.Modifiers == KeyModifiers.None)
        {
            if (current < 0) return false;
            DoubleClick?.Invoke(this);
            return true;
        }
        if ((e.Modifiers & ~(KeyModifiers.Shift | Shortcut.CommandModifier)) != 0) return false;
        if (command && !IsMulti) return false;   // a single-select list leaves Ctrl/⌘ keys to shortcuts

        if (command && !shift && e.Key == Key.A) { Selection.SelectAll(); return true; }
        if (e.Key == Key.Space && !shift)
        {
            if (command) { Selection.ToggleCurrent(); return true; }
            if (_checkboxes && current >= 0) { ToggleChecks(current); return true; }
            return false;
        }
        if (e.Key is Key.Left or Key.Right) return !shift && !command && base.HandleKey(e);

        int last = count - 1;
        int page = Math.Max(1, (Viewport.Height - HeaderHeight) / Math.Max(1, RowHeight) - 1);
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
        _typeAhead.Reset();
        Selection.MoveTo(t, extend: shift, focusOnly: command);
        ScrollIntoView(Selection.Current);   // also when it didn't move (scrolled away with the wheel)
        return true;
    }

    /// <summary>Typing moves to the next item whose caption starts with the typed text (typed within a second).</summary>
    protected internal override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || e.Text.Length == 0) return;
        SyncCount();
        int count = ItemCount;
        if (count == 0 || (_typeAhead.IsIdle(OwningForm?.Now ?? 0) && string.IsNullOrWhiteSpace(e.Text))) return;
        int i = _typeAhead.Find(e.Text, OwningForm?.Now ?? 0, count, Selection.Current, n => FetchItem(n)?.Caption ?? "");
        if (i >= 0) Selection.MoveTo(i);
        e.Handled = true;
    }

    // ---- Painting ----

    protected override Color GetBackgroundColor(Theme theme) => Color ?? theme.InputBackground;

    protected override Color GetBorderColor(Theme theme) => Focused ? theme.Accent : theme.InputBorder;

    protected override void PaintContent(Canvas canvas, Rect visible)
    {
        // Content coordinates: (0, 0) is the header's top-left corner when not scrolled; rows start below the header.
        var t = canvas.Theme;
        EnsureColX();
        int hh = HeaderHeight, rh = Math.Max(1, RowHeight);
        int count = Math.Min(ItemCount, Selection.Count);
        var images = _imagesName.Length > 0 ? OwningForm?.FindControl(_imagesName) as ImageList : null;
        bool enabled = IsEffectivelyEnabled;
        bool focusCue = Focused && OwningForm is { ShowFocusCues: true };
        int first = Math.Max(0, visible.Y / rh), last = Math.Min(count - 1, (visible.Y + visible.Height - 1 - hh) / rh);
        for (int i = first; i <= last; i++)
        {
            if (FetchItem(i) is not { } item) continue;
            var row = new Rect(visible.X, hh + i * rh, visible.Width, rh);
            bool selected = Selection.IsSelected(i);
            if (selected) canvas.FillRect(row, Focused ? t.Selection : t.SelectionInactive);
            int columns = Math.Max(1, _columns.Count);
            for (int c = 0; c < columns; c++)
            {
                int x0 = _columns.Count == 0 ? visible.X : _colX[c], x1 = _columns.Count == 0 ? visible.X + visible.Width : _colX[c + 1];
                if (x1 <= visible.X || x0 >= visible.X + visible.Width) continue;
                int x = x0 + canvas.Px(8);
                if (c == 0 && (_checkboxes || images != null))
                {
                    x = x0 + canvas.Px(6);
                    if (_checkboxes)
                    {
                        int s = canvas.Px(14);
                        ViewDrawing.DrawCheckBox(canvas, new Rect(x, row.Y + (rh - s) / 2, s, s), item.Checked, enabled);
                        x += s + canvas.Px(6);
                    }
                    if (images != null)
                    {
                        int s = canvas.Px(16);
                        if (images.Get(item.ImageIndex) is { } pic) canvas.DrawPicture(pic, new Rect(x, row.Y + (rh - s) / 2, s, s));
                        x += s + canvas.Px(4);
                    }
                }
                canvas.Save();
                canvas.ClipRect(new Rect(x0, row.Y, x1 - x0, rh));
                var align = _columns.Count == 0 ? TextAlign.Left : _columns[c].Alignment;
                canvas.DrawText(item.Text(c), EffectiveFont, enabled ? t.Text : t.TextDisabled,
                    new Rect(x, row.Y, Math.Max(0, x1 - x - canvas.Px(4)), rh), align, VerticalAlign.Center, ellipsis: true);
                canvas.Restore();
            }
            if (i == Selection.Current && focusCue) canvas.DrawBorder(row, t.FocusRing, canvas.Px(1));
        }
        PaintHeader(canvas, visible);
    }

    void PaintHeader(Canvas canvas, Rect visible)
    {
        var t = canvas.Theme;
        int hh = HeaderHeight;
        var header = new Rect(visible.X, visible.Y, visible.Width, hh);
        canvas.FillRect(header, t.PanelBackground);
        canvas.FillRect(new Rect(header.X, header.Y + hh - canvas.Px(1), header.Width, canvas.Px(1)), t.PanelBorder);
        var font = EffectiveFont with { Bold = true };
        for (int c = 0; c < _columns.Count; c++)
        {
            int x0 = _colX[c], x1 = _colX[c + 1];
            if (x1 <= visible.X || x0 >= visible.X + visible.Width) continue;
            int arrow = c == _sortColumn ? canvas.Px(14) : 0;
            canvas.DrawText(_columns[c].Title, font, IsEffectivelyEnabled ? t.Text : t.TextDisabled,
                new Rect(x0 + canvas.Px(8), header.Y, Math.Max(0, x1 - x0 - canvas.Px(12) - arrow), hh), _columns[c].Alignment, VerticalAlign.Center, ellipsis: true);
            if (arrow > 0) ViewDrawing.DrawSortArrow(canvas, new Rect(x0, header.Y, x1 - x0 - canvas.Px(1), hh), _sortDescending);
            canvas.FillRect(new Rect(x1 - canvas.Px(1), header.Y + canvas.Px(4), canvas.Px(1), hh - canvas.Px(8)), t.PanelBorder);
        }
    }
}

/// <summary>Drawing shared by the list views and trees.</summary>
internal static class ViewDrawing
{
    public static void DrawCheckBox(Canvas canvas, Rect box, bool isChecked, bool enabled)
    {
        var t = canvas.Theme;
        float radius = canvas.Px(3);
        if (isChecked)
        {
            canvas.FillRoundRect(box, radius, enabled ? t.Accent : t.TextDisabled);
            canvas.DrawCheckMark(box, t.OnAccent, Math.Max(1.5f, canvas.Px(2)));
        }
        else
        {
            canvas.FillRoundRect(box, radius, enabled ? t.InputBackground : t.FormBackground);
            canvas.DrawRoundBorder(box, radius, enabled ? t.InputBorder : t.TextDisabled, canvas.Px(1));
        }
    }

    /// <summary>The sort arrow at the right of a header cell, as grids draw it.</summary>
    public static void DrawSortArrow(Canvas canvas, Rect cell, bool descending)
    {
        float w = canvas.Px(8), h = canvas.Px(4);
        float x = cell.X + cell.Width - canvas.Px(6) - w, y = cell.Y + (cell.Height - h) / 2f;
        PointF[] arrow = descending
            ? [new(x, y), new(x + w, y), new(x + w / 2, y + h)]
            : [new(x, y + h), new(x + w, y + h), new(x + w / 2, y)];
        canvas.FillPolygon(arrow, canvas.Theme.TextMuted);
    }
}

/// <summary>
/// Keyboard type-ahead for lists and trees: characters typed within <see cref="TimeoutMs"/> of each other build a
/// prefix; the same letter typed again cycles through the items starting with it.
/// </summary>
internal sealed class TypeAhead
{
    public const double TimeoutMs = 1000;
    string _buffer = "";
    double _last = double.NegativeInfinity;

    /// <summary>No search is under way: nothing typed yet, or the last key was too long ago.</summary>
    public bool IsIdle(double now) => _buffer.Length == 0 || now - _last > TimeoutMs;

    public void Reset() => _buffer = "";

    /// <summary>Adds typed text and returns the item to move to, or -1.</summary>
    public int Find(string text, double now, int count, int current, Func<int, string> textOf)
    {
        if (now - _last > TimeoutMs) _buffer = "";
        _last = now;
        bool fresh = _buffer.Length == 0;
        _buffer += text;
        bool repeat = _buffer.Length > 1 && _buffer.All(ch => ch == _buffer[0]);   // "aaa": cycle the items starting with "a"
        string prefix = repeat ? _buffer[..1] : _buffer;
        // A new search, or a cycle, starts after the current item; a longer prefix may stay on it.
        int start = fresh || repeat || current < 0 ? 1 : 0;
        for (int n = start; n < count + start; n++)
        {
            int i = ((current + n) % count + count) % count;
            if (textOf(i).StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase)) return i;
        }
        return -1;
    }
}

// ================================================================================================================
// TreeView
// ================================================================================================================

/// <summary>
/// The child nodes of a <see cref="TreeNode"/>, or a <see cref="TreeView"/>'s top-level nodes (Delphi's TTreeNodes). Adding
/// a node sets its <see cref="TreeNode.Parent"/>; a node already elsewhere is moved. The tree follows every change.
/// </summary>
public sealed class TreeNodes : IList<TreeNode>, IReadOnlyList<TreeNode>
{
    readonly List<TreeNode> _list = [];
    readonly TreeNode? _owner;
    readonly TreeView? _view;

    internal TreeNodes(TreeNode owner) => _owner = owner;
    internal TreeNodes(TreeView view) => _view = view;

    /// <summary>The node these are the children of; null for a tree's top level.</summary>
    internal TreeNode? Owner => _owner;

    /// <summary>The tree these nodes are shown in, if any.</summary>
    internal TreeView? View => _view ?? _owner?.TreeView;

    public int Count => _list.Count;

    public TreeNode this[int index]
    {
        get => _list[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_list[index] == value) return;
            CheckNotAncestor(value);   // before anything is removed
            RemoveAt(index);
            Insert(Math.Min(index, _list.Count), value);
        }
    }

    /// <summary>Adds a node with this text at the end and returns it.</summary>
    public TreeNode Add(string text)
    {
        var node = new TreeNode(text);
        Add(node);
        return node;
    }

    public void Add(TreeNode node) => Insert(_list.Count, node);

    public void AddRange(IEnumerable<TreeNode> nodes)
    {
        foreach (var n in nodes.ToList()) Add(n);
    }

    /// <summary>Inserts a node; one that belongs to another parent (or elsewhere here) is moved.</summary>
    public void Insert(int index, TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if ((uint)index > (uint)_list.Count) throw new ArgumentOutOfRangeException(nameof(index));
        CheckNotAncestor(node);
        if (node.Collection is { } old)
        {
            int at = old._list.IndexOf(node);
            if (old == this && at < index) index--;
            old.RemoveAt(at);
        }
        _list.Insert(index, node);
        node.Attach(this);
        View?.NodesChanged(this);
    }

    void CheckNotAncestor(TreeNode node)
    {
        for (var p = _owner; p != null; p = p.Parent)
            if (p == node) throw new InvalidOperationException("A node can't be added under itself.");
    }

    public bool Remove(TreeNode node)
    {
        int i = _list.IndexOf(node);
        if (i < 0) return false;
        RemoveAt(i);
        return true;
    }

    public void RemoveAt(int index)
    {
        var node = _list[index];
        var view = View;
        view?.NodeRemoving(node);
        _list.RemoveAt(index);
        node.Detach();
        view?.NodesChanged(this);
    }

    public void Clear()
    {
        if (_list.Count == 0) return;
        var view = View;
        foreach (var n in _list) view?.NodeRemoving(n);
        foreach (var n in _list) n.Detach();
        _list.Clear();
        view?.NodesChanged(this);
    }

    /// <summary>Moves a node to another position among its siblings.</summary>
    public void Move(int oldIndex, int newIndex)
    {
        if ((uint)oldIndex >= (uint)_list.Count) throw new ArgumentOutOfRangeException(nameof(oldIndex));
        if ((uint)newIndex >= (uint)_list.Count) throw new ArgumentOutOfRangeException(nameof(newIndex));
        if (oldIndex == newIndex) return;
        var node = _list[oldIndex];
        _list.RemoveAt(oldIndex);
        _list.Insert(newIndex, node);
        View?.NodesChanged(this);
    }

    /// <summary>Sorts these nodes (not their children), stably.</summary>
    public void Sort(Comparison<TreeNode> comparison)
    {
        var sorted = _list.OrderBy(n => n, Comparer<TreeNode>.Create(comparison)).ToList();
        _list.Clear();
        _list.AddRange(sorted);
        View?.NodesChanged(this);
    }

    public int IndexOf(TreeNode node) => _list.IndexOf(node);
    public bool Contains(TreeNode node) => _list.Contains(node);
    public void CopyTo(TreeNode[] array, int arrayIndex) => _list.CopyTo(array, arrayIndex);
    bool ICollection<TreeNode>.IsReadOnly => false;

    public List<TreeNode>.Enumerator GetEnumerator() => _list.GetEnumerator();
    IEnumerator<TreeNode> IEnumerable<TreeNode>.GetEnumerator() => _list.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _list.GetEnumerator();

    // ---- Form files: "nodes": [ { "text", "checked", "expanded", "hasChildren", "imageIndex", "nodes" } ] ----

    internal void Write(FormJsonWriter w)
    {
        if (_list.Count == 0) return;
        w.Key("nodes");
        w.BeginArray();
        foreach (var node in _list) { w.NextElement(); node.Write(w); }
        w.EndArray();
    }

    internal void Read(JsonElement json, List<string> warnings, string path)
    {
        if (json.ValueKind != JsonValueKind.Array) { warnings.Add($"{path}: expected an array; ignored."); return; }
        int i = 0;
        foreach (var item in json.EnumerateArray())
            if (TreeNode.Read(item, warnings, $"{path}[{i++}]") is { } node) Add(node);
    }
}

/// <summary>A node of a <see cref="TreeView"/>.</summary>
public sealed class TreeNode
{
    string _text;
    bool _expanded, _hasChildren, _checked;
    int _imageIndex = -1;

    // The cached width of the text, for the tree's horizontal extent.
    internal float MeasuredWidth = -1;
    internal string? MeasuredText;
    internal RenderFont? MeasuredFont;

    public TreeNode(string text)
    {
        _text = text ?? "";
        Nodes = new TreeNodes(this);
    }

    public string Text
    {
        get => _text;
        set
        {
            value ??= "";
            if (value == _text) return;
            _text = value;
            TreeView?.NodeTextChanged(this);
        }
    }

    /// <summary>The child nodes.</summary>
    public TreeNodes Nodes { get; }

    /// <summary>
    /// Whether the children are shown. In a tree, changing it raises the tree's <see cref="TreeView.Expanding"/> or
    /// <see cref="TreeView.Collapsing"/> first, which can refuse it.
    /// </summary>
    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (value == _expanded) return;
            if (TreeView is { } tree) tree.SetExpanded(this, value, user: false);
            else _expanded = value;
        }
    }

    internal void SetExpandedFlag(bool value) => _expanded = value;

    /// <summary>
    /// Shows an expander even before the node has children (Delphi's HasChildren), so the tree's
    /// <see cref="TreeView.Expanding"/> handler can add them on demand. True whenever there are children. A user expand
    /// that brings no children clears it.
    /// </summary>
    public bool HasChildren
    {
        get => _hasChildren || Nodes.Count > 0;
        set { _hasChildren = value; TreeView?.Invalidate(); }
    }

    /// <summary>The state of the node's check box (with <see cref="TreeView.Checkboxes"/>).</summary>
    public bool Checked
    {
        get => _checked;
        set { _checked = value; TreeView?.Invalidate(); }
    }

    /// <summary>The picture from the tree's image list drawn before the text (<see cref="TreeView.ImagesName"/>); -1 draws none.</summary>
    public int ImageIndex
    {
        get => _imageIndex;
        set { _imageIndex = Math.Max(-1, value); TreeView?.Invalidate(); }
    }

    public object? Tag { get; set; }

    /// <summary>The node whose child this is; null for a top-level node (or one not in any list).</summary>
    public TreeNode? Parent => Collection?.Owner;

    /// <summary>The tree this node is shown in, if any.</summary>
    public TreeView? TreeView { get; private set; }

    /// <summary>The list holding this node (its parent's <see cref="Nodes"/> or a tree's top level), if any.</summary>
    internal TreeNodes? Collection { get; private set; }

    /// <summary>
    /// Draws a small icon before this node's text, in the space between the expand arrow and the label (a square
    /// roughly one row tall, minus a little padding). Null draws nothing. A picture from the tree's image list
    /// (<see cref="ImageIndex"/>) takes precedence.
    /// </summary>
    public Action<Canvas, Rect>? Icon { get; set; }

    /// <summary>Adds a child node with this text and returns it.</summary>
    public TreeNode Add(string text) => Nodes.Add(text);

    public int Level => Parent == null ? 0 : Parent.Level + 1;

    /// <summary>The position among its siblings, or -1.</summary>
    public int Index => Collection?.IndexOf(this) ?? -1;

    /// <summary>True if the node is a row of its tree: in a tree, with every ancestor expanded.</summary>
    public bool IsVisible
    {
        get
        {
            if (TreeView == null) return false;
            for (var p = Parent; p != null; p = p.Parent) if (!p._expanded) return false;
            return true;
        }
    }

    /// <summary>Expands the node (and with <paramref name="recurse"/> every node below it).</summary>
    public void Expand(bool recurse = false)
    {
        if (TreeView is { } tree && recurse) { tree.ExpandAll(this); return; }
        Expanded = true;
        if (recurse) foreach (var n in Nodes) n.Expand(true);
    }

    /// <summary>Collapses the node (and with <paramref name="recurse"/> every node below it).</summary>
    public void Collapse(bool recurse = false)
    {
        if (recurse) foreach (var n in Nodes) n.Collapse(true);
        Expanded = false;
    }

    internal void Attach(TreeNodes collection)
    {
        Collection = collection;
        SetTree(collection.View);
    }

    internal void Detach()
    {
        Collection = null;
        SetTree(null);
    }

    void SetTree(TreeView? tree)
    {
        if (TreeView == tree) return;
        TreeView = tree;
        foreach (var n in Nodes) n.SetTree(tree);
    }

    internal bool IsSelfOrDescendantOf(TreeNode ancestor)
    {
        for (var n = this; n != null; n = n.Parent) if (n == ancestor) return true;
        return false;
    }

    internal static TreeNode? Read(JsonElement json, List<string> warnings, string path)
    {
        if (json.ValueKind != JsonValueKind.Object) { warnings.Add($"{path}: a tree node must be an object; skipped."); return null; }
        var node = new TreeNode("");
        foreach (var p in json.EnumerateObject())
        {
            try
            {
                switch (p.Name)
                {
                    case "text": node.Text = Converters.String.Read(p.Value); break;
                    case "checked": node.Checked = Converters.Bool.Read(p.Value); break;
                    case "expanded": node.Expanded = Converters.Bool.Read(p.Value); break;
                    case "hasChildren": node.HasChildren = Converters.Bool.Read(p.Value); break;
                    case "imageIndex": node.ImageIndex = Converters.Int.Read(p.Value); break;
                    case "nodes": node.Nodes.Read(p.Value, warnings, $"{path}.nodes"); break;
                    default: warnings.Add($"{path}: tree nodes have no property \"{p.Name}\"; ignored."); break;
                }
            }
            catch (FormatException e) { warnings.Add($"{path}.{p.Name}: {e.Message}; ignored."); }
        }
        return node;
    }

    /// <summary>Writes the node: <c>text</c> first, then only non-default values in key order, children last.</summary>
    internal void Write(FormJsonWriter w)
    {
        w.BeginObject();
        if (_text.Length > 0) { w.Key("text"); w.WriteString(_text); }
        if (_checked) { w.Key("checked"); w.WriteBool(true); }
        if (_expanded) { w.Key("expanded"); w.WriteBool(true); }
        if (_hasChildren) { w.Key("hasChildren"); w.WriteBool(true); }
        if (_imageIndex >= 0) { w.Key("imageIndex"); w.WriteInt(_imageIndex); }
        Nodes.Write(w);
        w.EndObject();
    }
}

/// <summary>A node is about to expand or collapse; set <see cref="Cancel"/> to refuse.</summary>
public sealed class TreeNodeCancelEventArgs(TreeNode node) : EventArgs
{
    public TreeNode Node { get; } = node;
    public bool Cancel { get; set; }
}

public delegate void TreeNodeCancelEvent(Control sender, TreeNodeCancelEventArgs e);

/// <summary>A node's text going into or out of the rename editor.</summary>
public sealed class TreeEditEventArgs(TreeNode node, string text) : EventArgs
{
    public TreeNode Node { get; } = node;
    string _text = text ?? "";

    /// <summary>The editor's starting text (Editing) or the typed text (Edited); either may be changed.</summary>
    public string Text { get => _text; set => _text = value ?? ""; }

    /// <summary>False refuses to start the rename (Editing) or keeps the editor open (Edited).</summary>
    public bool Accept { get; set; } = true;
}

public delegate void TreeEditEvent(Control sender, TreeEditEventArgs e);

public delegate void TreeNodeEvent(Control sender, TreeNode node);

/// <summary>
/// A hierarchical list with expandable nodes (Delphi's TTreeView), built on <see cref="ScrollView"/>. Nodes are set
/// from code or in the form file (<c>"nodes"</c>). The rows are the visible (expanded) nodes, and the selection is an
/// <see cref="ItemSelection"/> over them (<see cref="Selection"/>): expanding inserts rows, collapsing removes them.
/// </summary>
/// <remarks>
/// <para>Mouse: a click on the arrow expands or collapses (a double click on the text too), a click selects; with
/// <see cref="MultiSelect"/> Ctrl/⌘+click toggles and Shift+click selects a range. With <see cref="ReadOnly"/> off, F2
/// or a slow second click on the selected node renames it in place (<see cref="Editing"/>, <see cref="Edited"/>).</para>
/// <para>Keys: Up/Down, PageUp/PageDown, Home/End; Right expands, then goes to the first child; Left collapses, then
/// goes to the parent; <c>+</c>/<c>-</c> expand/collapse and <c>*</c> expands everything below the current node; Space
/// toggles the check box (<see cref="Checkboxes"/>); typing jumps to the next visible node starting with the typed text.</para>
/// <para>Lazy loading: give a node <see cref="TreeNode.HasChildren"/> and add its children in <see cref="Expanding"/>.</para>
/// </remarks>
public class TreeView : ScrollView, ICollectionOwner, IFormCollections
{
    readonly List<TreeNode> _rows = [];   // the visible nodes, top to bottom
    bool _rowsDirty, _widthDirty = true, _inMeasure;
    int _updating;
    bool _multiSelect, _checkboxes, _readOnly = true;
    string _imagesName = "";
    int _lastCurrent = -1;
    bool _noAutoScroll, _quiet, _mouseSelecting;
    TreeNode? _currentFallback;            // where the current node goes if it is removed
    readonly HashSet<TreeNode> _raising = new(ReferenceEqualityComparer.Instance);   // nodes whose Expanding/Collapsing is running
    readonly TypeAhead _typeAhead = new();
    // Renaming
    InplaceEdit? _editor;
    TreeNode? _editNode;
    bool _endingEdit, _committing;
    TreeNode? _renameCandidate;
    double _renameAt = double.NaN;
    Form? _tickingOn;

    /// <summary>How long a second click waits for a double click before it starts a rename.</summary>
    public const double RenameDelayMs = 500;

    public TreeView()
    {
        SetDefaultSize(200, 200);
        Nodes = new TreeNodes(this);
        Selection.Changed += SelectionChanged;
    }

    /// <summary>The top-level nodes.</summary>
    public TreeNodes Nodes { get; }

    /// <summary>Adds a top-level node with this text and returns it.</summary>
    public TreeNode Add(string text) => Nodes.Add(text);

    /// <summary>The selection model over the visible rows (<see cref="VisibleNodes"/>): selected ranges, the current row, the anchor.</summary>
    public ItemSelection Selection { get; } = new(SelectionMode.Single);

    /// <summary>
    /// The current node (the selected one without <see cref="MultiSelect"/>), or null. Setting it expands its parents,
    /// selects it alone and scrolls it into view.
    /// </summary>
    public TreeNode? Selected
    {
        get
        {
            EnsureRows();
            int i = Selection.Current;
            return i >= 0 && i < _rows.Count ? _rows[i] : null;
        }
        set
        {
            if (value == null) { EnsureRows(); Selection.Select(-1); return; }
            if (value.TreeView != this) return;
            for (var p = value.Parent; p != null; p = p.Parent) p.Expanded = true;
            EnsureRows();
            int i = _rows.IndexOf(value);
            if (i >= 0) Selection.Select(i);
        }
    }

    [Published] public event NotifyEvent? Change;
    [Published("onDblClick")] public event NotifyEvent? DoubleClick;
    /// <summary>A node is about to expand (also from code); cancel to refuse, or add its children now (lazy loading).</summary>
    [Published] public event TreeNodeCancelEvent? Expanding;
    /// <summary>A node is about to collapse (also from code); cancel to refuse.</summary>
    [Published] public event TreeNodeCancelEvent? Collapsing;
    /// <summary>A rename is starting: change the editor's text, or clear Accept to refuse.</summary>
    [Published] public event TreeEditEvent? Editing;
    /// <summary>A rename is committed: change the text, or clear Accept to keep the editor open.</summary>
    [Published] public event TreeEditEvent? Edited;
    /// <summary>The user toggled a node's check box (a click on it, or Space).</summary>
    [Published] public event TreeNodeEvent? NodeChecked;

    /// <summary>Several nodes can be selected: Ctrl/⌘+click toggles, Shift selects ranges of visible rows.</summary>
    [Published(Category = "Behavior")]
    public bool MultiSelect
    {
        get => _multiSelect;
        set { _multiSelect = value; Selection.Mode = value ? SelectionMode.Extended : SelectionMode.Single; }
    }

    /// <summary>Each node shows a check box before its text (<see cref="TreeNode.Checked"/>).</summary>
    [Published(Category = "Appearance")]
    public bool Checkboxes { get => _checkboxes; set { _checkboxes = value; _widthDirty = true; Remeasure(); Invalidate(); } }

    /// <summary>The name of an ImageList whose pictures nodes show by <see cref="TreeNode.ImageIndex"/>.</summary>
    [Published("images", Category = "Appearance")]
    public string ImagesName { get => _imagesName; set { _imagesName = value ?? ""; _widthDirty = true; Remeasure(); Invalidate(); } }

    /// <summary>
    /// The user can't rename nodes (the default, unlike Delphi, so existing trees stay as they were). Off: F2 or a slow
    /// second click on the selected node opens an in-place editor.
    /// </summary>
    [Published(Category = "Behavior")]
    public bool ReadOnly { get => _readOnly; set { _readOnly = value; if (value) CloseEditor(); } }

    // ---- Rows ----

    /// <summary>The rows currently visible (expanded branches), top to bottom (a copy).</summary>
    public List<TreeNode> VisibleNodes()
    {
        EnsureRows();
        return [.. _rows];
    }

    /// <summary>How many rows are visible (expanded branches).</summary>
    public int VisibleCount { get { EnsureRows(); return _rows.Count; } }

    /// <summary>The row of a visible node, or -1.</summary>
    public int RowOf(TreeNode node)
    {
        EnsureRows();
        return node.TreeView == this ? _rows.IndexOf(node) : -1;
    }

    /// <summary>The node in a row (<see cref="VisibleNodes"/> order), or null.</summary>
    public TreeNode? NodeAtRow(int row)
    {
        EnsureRows();
        return row >= 0 && row < _rows.Count ? _rows[row] : null;
    }

    public bool IsSelected(TreeNode node) => RowOf(node) is var i and >= 0 && Selection.IsSelected(i);

    /// <summary>The selected nodes, top to bottom.</summary>
    public TreeNode[] SelectedNodes
    {
        get
        {
            EnsureRows();
            return [.. Selection.SelectedIndices.Where(i => i < _rows.Count).Select(i => _rows[i])];
        }
    }

    public int SelectionCount => Selection.SelectedCount;

    public void SelectAll() { EnsureRows(); Selection.SelectAll(); }

    public void ClearSelection() { EnsureRows(); Selection.ClearSelection(); }

    /// <summary>Expands or collapses a node, as a click on its arrow does (raising <see cref="Expanding"/>/<see cref="Collapsing"/>).</summary>
    public void Toggle(TreeNode node)
    {
        if (!node.HasChildren || node.TreeView != this) return;
        SetExpanded(node, !node.Expanded, user: true);
    }

    /// <summary>Expands every node.</summary>
    public void FullExpand()
    {
        foreach (var n in Nodes.ToList()) ExpandAll(n);
    }

    /// <summary>Collapses every node.</summary>
    public void FullCollapse()
    {
        _updating++;
        try { foreach (var n in Nodes.ToList()) n.Collapse(recurse: true); }
        finally { EndUpdate(); }
    }

    internal void ExpandAll(TreeNode node)
    {
        _updating++;
        try
        {
            void Walk(TreeNode n)
            {
                n.Expanded = true;
                if (n.Expanded) foreach (var c in n.Nodes.ToList()) Walk(c);
            }
            Walk(node);
        }
        finally { EndUpdate(); }
    }

    void EndUpdate()
    {
        if (--_updating == 0 && _rowsDirty) { EnsureRows(); Invalidate(); }
    }

    /// <summary>Scrolls the least distance that shows a row in full.</summary>
    public void ScrollIntoView(int row)
    {
        if (row < 0 || row >= _rows.Count) return;   // (no rebuild here: it runs from selection changes)
        ScrollIntoView(new Rect(ScrollX, row * RowHeight, 0, RowHeight));
    }

    /// <summary>Scrolls a visible node into view.</summary>
    public void ScrollIntoView(TreeNode node) => ScrollIntoView(RowOf(node));

    /// <summary>The node at a point in this control's coordinates, or null.</summary>
    public TreeNode? NodeAt(int x, int y)
    {
        var view = Viewport;
        if (x < view.X || x >= view.X + view.Width || y < view.Y || y >= view.Y + view.Height) return null;
        return NodeAtRow((y - view.Y + ScrollY) / Math.Max(1, RowHeight));
    }

    /// <summary>A row's rectangle in this control's coordinates (it may be out of view).</summary>
    public Rect RowRect(int row)
    {
        var view = Viewport;
        return new Rect(view.X, view.Y + row * RowHeight - ScrollY, view.Width, RowHeight);
    }

    /// <summary>The first (possibly partly) visible row; setting it scrolls that row to the top.</summary>
    public int TopIndex
    {
        get => ScrollY / Math.Max(1, RowHeight);
        set => ScrollY = Math.Max(0, value) * RowHeight;
    }

    void EnsureRows()
    {
        if (!_rowsDirty || _updating > 0) return;
        _rowsDirty = false;
        // The selection by node, before the rows change.
        int oldCount = _rows.Count;
        List<TreeNode>? selected = null;
        if (Selection.Mode != SelectionMode.Single && !Selection.IsEmpty)
        {
            selected = [];
            foreach (var r in Selection.Ranges)
                for (int i = r.First; i <= r.Last && i < oldCount; i++) selected.Add(_rows[i]);
        }
        var current = Selection.Current is var c and >= 0 && c < oldCount ? _rows[c] : null;
        int selectedBefore = Selection.SelectedCount;

        _rows.Clear();
        void Walk(TreeNodes nodes)
        {
            foreach (var n in nodes)
            {
                _rows.Add(n);
                if (n.Expanded) Walk(n.Nodes);
            }
        }
        Walk(Nodes);
        _widthDirty = true;

        Dictionary<TreeNode, int>? index = null;
        int RowOfNode(TreeNode n)
        {
            if (n.TreeView != this) return -1;
            if (index == null)
            {
                index = new Dictionary<TreeNode, int>(_rows.Count, ReferenceEqualityComparer.Instance);
                for (int i = 0; i < _rows.Count; i++) index[_rows[i]] = i;
            }
            return index.TryGetValue(n, out var row) ? row : -1;
        }
        int newCurrent = -1;
        if (current != null)
        {
            // A hidden current node gives way to its nearest visible ancestor; a removed one to its old parent.
            for (var n = current.TreeView == this ? current : _currentFallback; n != null && newCurrent < 0; n = n.Parent)
                newCurrent = RowOfNode(n);
        }
        _currentFallback = null;
        var currentAfter = newCurrent >= 0 ? _rows[newCurrent] : null;
        Quietly(() =>
        {
            Selection.Reset(_rows.Count);
            if (selected != null)
                foreach (var (first, last) in ListView.Runs(selected.Select(RowOfNode).Where(i => i >= 0).Order()))
                    Selection.SelectRange(first, last, add: true);
            if (newCurrent < 0) return;
            Selection.Current = newCurrent;
            // A hidden or removed current node gives way to an ancestor, selected as a collapse selects it.
            if (currentAfter != current) Selection.SetSelected(newCurrent, true);
        });
        if (!_inMeasure) Remeasure();
        CloseEditorIfMoved();
        if (currentAfter != current || Selection.SelectedCount != selectedBefore) Change?.Invoke(this);
    }

    void MarkRowsDirty()
    {
        _rowsDirty = true;
        _widthDirty = true;
        InvalidateLayout();
        Invalidate();
    }

    /// <summary>A list of nodes changed (added, removed, moved, cleared).</summary>
    internal void NodesChanged(TreeNodes collection)
    {
        // Children of a collapsed or hidden node add no rows, but the expander may appear or go.
        if (collection.Owner is { } owner && !(owner.Expanded && owner.IsVisible)) { Invalidate(); return; }
        MarkRowsDirty();
    }

    /// <summary>A node (and its subtree) is about to leave the tree: close an editor on it, remember where the current node goes.</summary>
    internal void NodeRemoving(TreeNode node)
    {
        if (_editNode != null && _editNode.IsSelfOrDescendantOf(node)) EndEdit(commit: false);
        if (_renameCandidate != null && _renameCandidate.IsSelfOrDescendantOf(node)) CancelRename();
        // (While the rows are stale, the selection still indexes the old rows, so this holds either way.)
        if (Selection.Current is var c and >= 0 && c < _rows.Count && _rows[c].IsSelfOrDescendantOf(node))
            _currentFallback = node.Parent;
        if (_currentFallback != null && _currentFallback.IsSelfOrDescendantOf(node))
            _currentFallback = node.Parent;   // removed in turn: one level further up
    }

    internal void NodeTextChanged(TreeNode node)
    {
        if (node.IsVisible) { _widthDirty = true; InvalidateLayout(); }
        if (node == _editNode) PlaceEditor();
        Invalidate();
    }

    /// <summary>Expands or collapses a node: the event first, then the rows (inserted or removed in the selection).</summary>
    internal void SetExpanded(TreeNode node, bool value, bool user)
    {
        if (node.Expanded == value) return;
        if (_raising.Add(node))
        {
            // A handler that expands (or collapses) this same node, directly or by selecting a child, isn't asked again.
            var args = new TreeNodeCancelEventArgs(node);
            try { (value ? Expanding : Collapsing)?.Invoke(this, args); }
            finally { _raising.Remove(node); }
            if (args.Cancel || node.Expanded == value) return;   // refused, or the handler did it
        }
        if (value && user && node.Nodes.Count == 0)
        {
            node.HasChildren = false;   // lazy loading brought nothing: the expander goes
            Invalidate();
            return;
        }
        if (!value && _editNode != null && _editNode != node && _editNode.IsSelfOrDescendantOf(node)) CloseEditor();
        if (_rowsDirty || _updating > 0 || !node.IsVisible)
        {
            bool visible = node.IsVisible;
            node.SetExpandedFlag(value);
            if (visible) MarkRowsDirty();
            return;
        }
        int r = _rows.IndexOf(node);
        var currentNode = Selection.Current >= 0 ? _rows[Selection.Current] : null;
        int selectedBefore = Selection.SelectedCount;
        if (value)
        {
            node.SetExpandedFlag(true);
            var added = new List<TreeNode>();
            void Walk(TreeNodes nodes)
            {
                foreach (var n in nodes)
                {
                    added.Add(n);
                    if (n.Expanded) Walk(n.Nodes);
                }
            }
            Walk(node.Nodes);
            _rows.InsertRange(r + 1, added);
            Quietly(() => Selection.Insert(r + 1, added.Count));
            _widthDirty = true;
            Remeasure();
            if (user && added.Count > 0)
            {
                ScrollIntoView(r + added.Count);   // show the children, but keep the node itself in view
                ScrollIntoView(r);
            }
        }
        else
        {
            int k = 0;
            while (r + 1 + k < _rows.Count && _rows[r + 1 + k].IsSelfOrDescendantOf(node)) k++;
            int cur = Selection.Current;
            if (cur > r && cur <= r + k)
            {
                // The current node is being hidden: the collapsed node takes its place.
                Quietly(() =>
                {
                    if (Selection.Mode == SelectionMode.Single) Selection.Select(r);
                    else { Selection.SetSelected(r, true); Selection.Current = r; }
                });
            }
            node.SetExpandedFlag(false);
            _rows.RemoveRange(r + 1, k);
            Quietly(() => Selection.Remove(r + 1, k));
            _widthDirty = true;
            Remeasure();
        }
        Invalidate();
        var currentAfter = Selection.Current >= 0 ? _rows[Selection.Current] : null;
        if (currentAfter != currentNode || Selection.SelectedCount != selectedBefore) Change?.Invoke(this);
    }

    void Quietly(Action action)
    {
        bool quiet = _quiet, noAutoScroll = _noAutoScroll;
        _quiet = _noAutoScroll = true;
        try { action(); }
        finally { _quiet = quiet; _noAutoScroll = noAutoScroll; }
        _lastCurrent = Selection.Current;
    }

    /// <summary>The rename editor belongs to the current node: once another one is current, the edit is committed.</summary>
    void CloseEditorIfMoved()
    {
        if (_editor == null || _endingEdit || _committing || _rowsDirty) return;
        int c = Selection.Current;
        if (c < 0 || c >= _rows.Count || _rows[c] != _editNode) CloseEditor();
    }

    // ---- Geometry ----

    int RowHeight => Px(24);
    int Indent => Px(18);
    int IconWidth => Px(20);
    int CheckWidth => Px(18);

    protected override int LineStepY => Math.Max(1, RowHeight);

    ImageList? Images => _imagesName.Length > 0 ? OwningForm?.FindControl(_imagesName) as ImageList : null;

    /// <summary>Where a node's text starts, in content coordinates.</summary>
    int TextLeft(TreeNode node, int level, bool images)
    {
        int x = level * Indent + Indent;
        if (_checkboxes) x += CheckWidth;
        if (images || node.Icon != null) x += IconWidth;
        return x;
    }

    void Remeasure()
    {
        EnsureRows();
        ContentHeight = _rows.Count * RowHeight;
        ContentWidth = MeasureWidth();
    }

    protected override void MeasureContent()
    {
        _inMeasure = true;
        try { EnsureRows(); }
        finally { _inMeasure = false; }
        ContentHeight = _rows.Count * RowHeight;
        ContentWidth = MeasureWidth();
    }

    int _measuredWidth;
    double _measuredScale;

    /// <summary>The widest row (indent, expander, check box, image and text), from each node's cached text width.</summary>
    int MeasureWidth()
    {
        if (!_widthDirty && _measuredScale == UiScale) return _measuredWidth;
        var font = EffectiveFont;
        var renderFont = new RenderFont(FontFace.Resolve(font), (float)(font.Size * UiScale));
        bool images = Images != null;
        int max = 0;
        foreach (var node in _rows)
        {
            if (node.MeasuredText != node.Text || node.MeasuredFont != renderFont)
            {
                node.MeasuredWidth = renderFont.MeasureWidth(node.Text);
                node.MeasuredText = node.Text;
                node.MeasuredFont = renderFont;
            }
            int w = TextLeft(node, node.Level, images) + (int)Math.Ceiling(node.MeasuredWidth) + Px(6);
            if (w > max) max = w;
        }
        _measuredWidth = max;
        _measuredScale = UiScale;
        _widthDirty = false;
        return max;
    }

    bool IsMulti => Selection.Mode is SelectionMode.Multi or SelectionMode.Extended;

    void SelectionChanged()
    {
        if (Selection.Current != _lastCurrent)
        {
            _lastCurrent = Selection.Current;
            if (!_noAutoScroll) ScrollIntoView(_lastCurrent);
            if (!_quiet) CloseEditorIfMoved();
        }
        Invalidate();
        if (!_quiet) Change?.Invoke(this);
    }

    void ToggleChecked(TreeNode node)
    {
        int row = RowOf(node);
        var targets = row >= 0 && Selection.IsSelected(row) && Selection.SelectedCount > 1 ? SelectedNodes : [node];
        bool state = !node.Checked;
        foreach (var n in targets)
        {
            n.Checked = state;
            NodeChecked?.Invoke(this, n);
        }
    }

    // ---- Input ----

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);   // the scroll bars take their own presses
        if (IsScrolling || IsOverScrollBar(e.X, e.Y)) return;
        EnsureRows();
        _typeAhead.Reset();
        bool wasCurrent = NodeAt(e.X, e.Y) is { } hit && hit == Selected && Focused && Selection.IsSelected(RowOf(hit));
        CancelRename();
        var view = Viewport;
        if (e.Y < view.Y || e.Y >= view.Y + view.Height || e.X < view.X || e.X >= view.X + view.Width) return;
        int i = (e.Y - view.Y + ScrollY) / Math.Max(1, RowHeight);
        if (i < 0 || i >= _rows.Count) return;
        var node = _rows[i];
        int cx = e.X - view.X + ScrollX, level = node.Level, x0 = level * Indent;
        if (e.Button == MouseButton.Left && cx >= x0 && cx < x0 + Indent)
        {
            Toggle(node);
            return;
        }
        if (e.Button == MouseButton.Left && _checkboxes && cx >= x0 + Indent && cx < x0 + Indent + CheckWidth && e.Modifiers == KeyModifiers.None)
        {
            ToggleChecked(node);
            return;
        }
        if (e.Button != MouseButton.Left)
        {
            if (!Selection.IsSelected(i)) Selection.Click(i);
            return;
        }
        Selection.Click(i, toggle: e.Modifiers.HasFlag(Shortcut.CommandModifier), extend: e.Modifiers.HasFlag(KeyModifiers.Shift));
        _mouseSelecting = true;
        if (e.Clicks == 2)
        {
            Toggle(node);
            DoubleClick?.Invoke(this);
        }
        else if (wasCurrent && e.Modifiers == KeyModifiers.None && !_readOnly && cx >= TextLeft(node, level, Images != null))
            _renameCandidate = node;   // a slow second click: the rename starts if no double click follows
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsScrolling || !_mouseSelecting || Selection.Mode != SelectionMode.Extended) return;
        var view = Viewport;
        if (view.Height <= 0) return;
        int y = Math.Clamp(e.Y, view.Y, view.Y + view.Height - 1);
        int i = Math.Min(VisibleCount - 1, (y - view.Y + ScrollY) / Math.Max(1, RowHeight));
        if (i >= 0 && i != Selection.Current) Selection.DragTo(i);
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _mouseSelecting = false;
        if (_renameCandidate != null && e.Button == MouseButton.Left && NodeAt(e.X, e.Y) == _renameCandidate && OwningForm is { } form)
        {
            _renameAt = form.Now + RenameDelayMs;
            _tickingOn = form;
            form.AddTicker(this);
        }
        else _renameCandidate = null;
    }

    protected internal override void OnTick(double nowMs)
    {
        base.OnTick(nowMs);
        if (double.IsNaN(_renameAt) || nowMs < _renameAt) return;
        var node = _renameCandidate;
        CancelRename();
        if (node != null && node == Selected) BeginEdit(node);
    }

    void CancelRename()
    {
        _renameCandidate = null;
        _renameAt = double.NaN;
        _tickingOn?.RemoveTicker(this);
        _tickingOn = null;
    }

    protected override bool HandleKey(KeyEventArgs e)
    {
        EnsureRows();
        if (_rows.Count == 0) return false;
        bool shift = e.Shift, command = e.Command;
        if ((e.Modifiers & ~(KeyModifiers.Shift | Shortcut.CommandModifier)) != 0) return false;
        if (command && !IsMulti) return false;   // a single-select tree leaves Ctrl/⌘ keys to shortcuts
        if (e.Key == Key.F2 && e.Modifiers == KeyModifiers.None) return BeginEdit();
        if (command && !shift && e.Key == Key.A) { Selection.SelectAll(); return true; }
        int current = Selection.Current;
        var node = current >= 0 ? _rows[current] : null;
        if (e.Key == Key.Space && !shift)
        {
            if (command) { Selection.ToggleCurrent(); return true; }
            if (_checkboxes && node != null) { ToggleChecked(node); return true; }
            return false;
        }
        if (e.Key is Key.Left or Key.Right)
        {
            if (shift || command) return false;
            if (node == null) return true;
            if (e.Key == Key.Right)
            {
                if (node.HasChildren && !node.Expanded) Toggle(node);
                else if (node.Expanded && node.Nodes.Count > 0) MoveCurrent(current + 1, false, false);
            }
            else if (node.Expanded && node.HasChildren) Toggle(node);
            else if (node.Parent is { } parent) MoveCurrent(RowOf(parent), false, false);
            return true;
        }
        int last = _rows.Count - 1;
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
        MoveCurrent(t, shift, command);
        return true;
    }

    void MoveCurrent(int row, bool extend, bool focusOnly)
    {
        if (row < 0) return;
        _typeAhead.Reset();
        Selection.MoveTo(row, extend, focusOnly);
        ScrollIntoView(Selection.Current);   // also when it didn't move (scrolled away with the wheel)
    }

    /// <summary><c>+</c>/<c>-</c>/<c>*</c> expand, collapse and expand all below the current node; other typing jumps to a matching visible node.</summary>
    protected internal override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || e.Text.Length == 0) return;
        EnsureRows();
        if (_rows.Count == 0) return;
        var node = Selected;
        if (_typeAhead.IsIdle(OwningForm?.Now ?? 0))
        {
            if (string.IsNullOrWhiteSpace(e.Text)) return;
            if (node != null && e.Text is "+" or "-" or "*")
            {
                if (e.Text == "+" && node.HasChildren && !node.Expanded) Toggle(node);
                else if (e.Text == "-" && node.Expanded) Toggle(node);
                else if (e.Text == "*") ExpandAll(node);
                e.Handled = true;
                return;
            }
        }
        int i = _typeAhead.Find(e.Text, OwningForm?.Now ?? 0, _rows.Count, Selection.Current, n => _rows[n].Text);
        if (i >= 0) Selection.MoveTo(i);   // (not MoveCurrent: that ends the search)
        e.Handled = true;
    }

    // ---- Renaming ----

    /// <summary>True while the in-place rename editor is open.</summary>
    public bool IsEditing => _editor != null;

    /// <summary>The rename editor while it is open (a child <see cref="Edit"/> over the node's text), else null.</summary>
    public Edit? InplaceEditor => _editor;

    /// <summary>The node being renamed, or null.</summary>
    public TreeNode? EditingNode => _editNode;

    /// <summary>
    /// Opens the rename editor on a node (default: <see cref="Selected"/>), after <see cref="Editing"/>; false if
    /// <see cref="ReadOnly"/>, refused, or there is no node.
    /// </summary>
    public bool BeginEdit(TreeNode? node = null)
    {
        node ??= Selected;
        if (node == null || node.TreeView != this || _readOnly || !IsEffectivelyEnabled || OwningForm == null) return false;
        if (_editor != null)
        {
            if (_editNode == node) return true;
            if (!EndEdit(commit: true)) return false;
        }
        var args = new TreeEditEventArgs(node, node.Text);
        Editing?.Invoke(this, args);
        if (!args.Accept || node.TreeView != this) return false;
        if (Selected != node) Selected = node;
        ScrollIntoView(node);
        _editNode = node;
        var editor = new InplaceEdit(this) { Text = args.Text, AutoSelect = false, IsTransient = true };
        _editor = editor;
        PlaceEditor();
        Add(editor);
        Root.PerformLayout();
        editor.Focus();
        editor.SelectAll();
        Invalidate();
        return true;
    }

    /// <summary>
    /// Closes the rename editor. Committing raises <see cref="Edited"/> and stores the text in the node; if a handler
    /// clears Accept the editor stays open and this returns false.
    /// </summary>
    public bool EndEdit(bool commit)
    {
        if (_editor is not { } editor || _endingEdit || _committing) return true;
        string? text = null;
        if (commit && _editNode is { } node)
        {
            var args = new TreeEditEventArgs(node, editor.Text);
            _committing = true;
            try { Edited?.Invoke(this, args); }
            finally { _committing = false; }
            if (!args.Accept) return false;
            if (_editor != editor) return true;   // a handler closed it
            text = args.Text;
        }
        _endingEdit = true;
        try
        {
            var edited = _editNode;
            bool hadFocus = editor.Focused;
            _editor = null;
            _editNode = null;
            Remove(editor);
            if (text != null && edited != null) edited.Text = text;   // after closing: the change may rebuild the rows
            if (hadFocus) Focus();
        }
        finally { _endingEdit = false; }
        Invalidate();
        return true;
    }

    void CloseEditor()
    {
        if (!EndEdit(commit: true)) EndEdit(commit: false);
    }

    void PlaceEditor()
    {
        if (_editor is not { } editor || _editNode is not { } node) return;
        int row = RowOf(node);
        if (row < 0) return;
        var view = Viewport;
        var r = RowRect(row);
        int x = view.X + TextLeft(node, node.Level, Images != null) - ScrollX - Px(4);
        int w = Math.Max(Px(80), (int)Math.Ceiling(Math.Max(0, node.MeasuredWidth)) + Px(32));
        w = Math.Max(Px(40), Math.Min(w, view.X + view.Width - x));
        editor.SetBounds(x, r.Y, w, r.Height);
    }

    protected internal override Rect ChildClipRect => Viewport;

    protected override void OnScrolled()
    {
        PlaceEditor();
        base.OnScrolled();
    }

    protected override void OnResize()
    {
        base.OnResize();
        PlaceEditor();
    }

    bool EditorKey(KeyEventArgs e)
    {
        if (e.Modifiers != KeyModifiers.None) return false;
        if (e.Key == Key.Enter) { EndEdit(commit: true); return true; }
        if (e.Key == Key.Escape) { EndEdit(commit: false); return true; }
        return false;
    }

    void EditorLostFocus(InplaceEdit editor)
    {
        if (_editor != editor || _endingEdit) return;
        if (!EndEdit(commit: true)) EndEdit(commit: false);   // focus left: a refused value is dropped
    }

    /// <summary>The rename editor: an <see cref="Edit"/> that hands Enter and Escape back to the tree.</summary>
    sealed class InplaceEdit(TreeView tree) : Edit
    {
        protected internal override void OnKeyDown(KeyEventArgs e)
        {
            if (tree.EditorKey(e)) { e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        protected internal override void OnLostFocus()
        {
            base.OnLostFocus();
            tree.EditorLostFocus(this);
        }
    }

    // ---- Form files and the designer: "nodes" ----

    IReadOnlyList<string> ICollectionOwner.CollectionNames { get; } = ["nodes"];

    string ICollectionOwner.DescribeCollection(string name)
    {
        if (name != "nodes") return "";
        int n = 0;
        void Count(TreeNodes nodes) { foreach (var c in nodes) { n++; Count(c.Nodes); } }
        Count(Nodes);
        return n == 1 ? "(1 node)" : $"({n} nodes)";
    }

    IReadOnlyList<string> IFormCollections.DeferredMembers { get; } = ["nodes"];

    void IFormCollections.ReadDeferred(string name, JsonElement value, List<string> warnings, string here)
    {
        if (name != "nodes") return;
        Nodes.Clear();
        Nodes.Read(value, warnings, $"{here}.nodes");
    }

    void IFormCollections.WriteCollections(FormJsonWriter w) => Nodes.Write(w);

    // ---- Painting ----

    protected override Color GetBackgroundColor(Theme theme) => Color ?? theme.InputBackground;

    protected override Color GetBorderColor(Theme theme) => Focused || _editor is { Focused: true } ? theme.Accent : theme.InputBorder;

    protected override void PaintContent(Canvas canvas, Rect visible)
    {
        EnsureRows();
        var t = canvas.Theme;
        int rh = Math.Max(1, RowHeight);
        var images = Images;
        bool enabled = IsEffectivelyEnabled;
        bool focusCue = Focused && OwningForm is { ShowFocusCues: true };
        var font = EffectiveFont;
        int first = Math.Max(0, visible.Y / rh), last = Math.Min(_rows.Count - 1, (visible.Y + visible.Height - 1) / rh);
        for (int i = first; i <= last; i++)
        {
            var node = _rows[i];
            int y = i * rh, level = node.Level, x = level * Indent;
            var row = new Rect(visible.X, y, visible.Width, rh);
            if (Selection.IsSelected(i)) canvas.FillRect(row, Focused || _editor is { Focused: true } ? t.Selection : t.SelectionInactive);
            if (node.HasChildren)
                canvas.DrawText(node.Expanded ? "▾" : "▸", canvas.DefaultFont, t.TextDisabled, new Rect(x, y, Indent, rh), TextAlign.Center, VerticalAlign.Center);
            int textX = x + Indent;
            if (_checkboxes)
            {
                int s = canvas.Px(14);
                ViewDrawing.DrawCheckBox(canvas, new Rect(textX + canvas.Px(1), y + (rh - s) / 2, s, s), node.Checked, enabled);
                textX += CheckWidth;
            }
            if (images != null || node.Icon != null)
            {
                if (images?.Get(node.ImageIndex) is { } pic)
                {
                    int s = canvas.Px(16);
                    canvas.DrawPicture(pic, new Rect(textX + (IconWidth - s) / 2, y + (rh - s) / 2, s, s));
                }
                else node.Icon?.Invoke(canvas, new Rect(textX, y, IconWidth, rh));
                textX += IconWidth;
            }
            if (node != _editNode)
                canvas.DrawText(node.Text, font, enabled ? t.Text : t.TextDisabled, new Rect(textX, y, Math.Max(0, visible.X + visible.Width - textX), rh), TextAlign.Left, VerticalAlign.Center);
            if (i == Selection.Current && focusCue) canvas.DrawBorder(row, t.FocusRing, canvas.Px(1));
        }
    }
}
