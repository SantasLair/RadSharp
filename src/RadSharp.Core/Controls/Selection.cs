namespace RadSharp;

/// <summary>How the items of a list can be selected; the mode of an <see cref="ItemSelection"/>.</summary>
public enum SelectionMode
{
    /// <summary>Nothing is ever selected; only the current (focused) item moves.</summary>
    None,
    /// <summary>At most one item, and it is the current one (a list without MultiSelect).</summary>
    Single,
    /// <summary>
    /// Several items, each toggled on its own: a click or Space toggles one, and the arrow keys move the current
    /// item without selecting (Delphi's MultiSelect without ExtendedSelect).
    /// </summary>
    Multi,
    /// <summary>
    /// Several items, the usual desktop way: a click selects one, Ctrl/⌘+click toggles one, Shift+click and
    /// Shift+arrows select the range from the anchor, Ctrl/⌘+arrows move the current item alone, Ctrl/⌘+Space
    /// toggles it, Ctrl/⌘+A selects all (Delphi's MultiSelect with ExtendedSelect).
    /// </summary>
    Extended,
}

/// <summary>An inclusive range of item indices, <see cref="First"/> to <see cref="Last"/>.</summary>
public readonly record struct IndexRange(int First, int Last)
{
    public int Count => Last - First + 1;
    public bool Contains(int index) => index >= First && index <= Last;
}

/// <summary>
/// The selection of a list of <see cref="Count"/> items (rows of a list box, list view, tree or grid), without any
/// UI: which items are selected, the current (focused) item, and the anchor that Shift-ranges start from.
/// </summary>
/// <remarks>
/// <para>The selection is stored as sorted, disjoint ranges, never per item, so selecting all of a million items
/// is one range and <see cref="IsSelected"/> is a binary search over the ranges.</para>
/// <para>Two layers: the programmatic calls (<see cref="Select"/>, <see cref="SetSelected"/>, <see cref="SelectRange"/>,
/// <see cref="SelectAll"/>, <see cref="ClearSelection"/>) and the input calls a control forwards its mouse and keys to
/// (<see cref="Click"/>, <see cref="DragTo"/>, <see cref="MoveTo"/>, <see cref="ToggleCurrent"/>), which apply the
/// <see cref="Mode"/>'s rules for Ctrl (<c>toggle</c>) and Shift (<c>extend</c>). The control maps pixels and keys to
/// indices; the model maps them to a selection.</para>
/// <para>When items are inserted or removed, call <see cref="Insert"/>/<see cref="Remove"/> so the selection,
/// current item and anchor keep pointing at the same items; <see cref="Count"/> truncates or grows at the end.</para>
/// <para><see cref="Changed"/> is raised once per call that changed the selected items or the current item.</para>
/// </remarks>
public sealed class ItemSelection(SelectionMode mode = SelectionMode.Single)
{
    readonly List<IndexRange> _ranges = [];
    int _count, _current = -1, _anchor = -1;
    SelectionMode _mode = mode;
    int _version;   // bumped whenever the selected set changes

    // Ctrl+press then drag: the selection before the press, and whether the drag removes or adds the range.
    IndexRange[]? _dragBase;
    bool _dragRemoves;

    /// <summary>Raised once after a call changed the selected items or <see cref="Current"/>.</summary>
    public event Action? Changed;

    /// <summary>
    /// The selection rules. Switching to <see cref="SelectionMode.Single"/> keeps only the current item selected;
    /// switching to <see cref="SelectionMode.None"/> clears the selection.
    /// </summary>
    public SelectionMode Mode
    {
        get => _mode;
        set
        {
            if (value == _mode) return;
            var s = Begin();
            _mode = value;
            _dragBase = null;
            if (value == SelectionMode.None) ClearRanges();
            else if (value == SelectionMode.Single) SetOnly(_current);
            End(s);
        }
    }

    /// <summary>
    /// The number of items. Lowering it drops the selection beyond the end and moves <see cref="Current"/> to the new
    /// last item if it was past it (in <see cref="SelectionMode.Single"/> that item becomes the selected one).
    /// </summary>
    public int Count
    {
        get => _count;
        set
        {
            value = Math.Max(0, value);
            if (value == _count) return;
            var s = Begin();
            _dragBase = null;
            if (value < _count)
            {
                RemoveRange(value, _count - 1);
                if (_anchor >= value) _anchor = value - 1;
                if (_current >= value)
                {
                    _current = value - 1;
                    if (_mode == SelectionMode.Single) SetOnly(_current);
                }
            }
            _count = value;
            End(s);
        }
    }

    /// <summary>
    /// The current (focused) item, or -1: where the keyboard acts, and what a list's <c>ItemIndex</c> reports.
    /// Setting it moves only the focus, except in <see cref="SelectionMode.Single"/>, where the selection follows.
    /// </summary>
    public int Current
    {
        get => _current;
        set
        {
            value = Math.Clamp(value, -1, _count - 1);
            var s = Begin();
            if (_mode == SelectionMode.Single) { SetOnly(value); _anchor = value; }
            _current = value;
            End(s);
        }
    }

    /// <summary>Where a Shift-range starts: the last item clicked or moved to without Shift, or -1.</summary>
    public int Anchor => _anchor;

    /// <summary>The selected items as sorted, disjoint, non-adjacent ranges.</summary>
    public IReadOnlyList<IndexRange> Ranges => _ranges;

    /// <summary>How many items are selected (adds up the ranges).</summary>
    public int SelectedCount
    {
        get
        {
            int n = 0;
            foreach (var r in _ranges) n += r.Count;
            return n;
        }
    }

    public bool IsEmpty => _ranges.Count == 0;

    /// <summary>The selected indices in ascending order (enumerates the ranges; don't change the selection meanwhile).</summary>
    public IEnumerable<int> SelectedIndices
    {
        get
        {
            foreach (var r in _ranges)
                for (int i = r.First; i <= r.Last; i++) yield return i;
        }
    }

    public bool IsSelected(int index)
    {
        int i = FindFirstEndingAtOrAfter(index);
        return i < _ranges.Count && _ranges[i].First <= index;
    }

    // ---- Programmatic selection ----

    /// <summary>Selects only <paramref name="index"/> and makes it the current item and the anchor; -1 clears.</summary>
    public void Select(int index)
    {
        var s = Begin();
        DoSelect(Math.Clamp(index, -1, _count - 1));
        End(s);
    }

    /// <summary>
    /// Adds or removes one item without touching the others or the current item (Delphi's <c>Selected[i]</c>). In
    /// <see cref="SelectionMode.Single"/>, selecting makes it the only (and current) item, and deselecting the
    /// selected item clears the selection.
    /// </summary>
    public void SetSelected(int index, bool selected)
    {
        if (index < 0 || index >= _count || _mode == SelectionMode.None) return;
        var s = Begin();
        if (_mode == SelectionMode.Single)
        {
            if (selected) DoSelect(index);
            else if (IsSelected(index)) DoSelect(-1);
        }
        else if (selected) AddRange(index, index);
        else RemoveRange(index, index);
        End(s);
    }

    /// <summary>
    /// Selects <paramref name="first"/> to <paramref name="last"/> (either order), added to the selection or
    /// replacing it; <paramref name="first"/> becomes the anchor and <paramref name="last"/> the current item.
    /// In <see cref="SelectionMode.Single"/> it selects <paramref name="last"/> only.
    /// </summary>
    public void SelectRange(int first, int last, bool add = false)
    {
        if (_count == 0 || _mode == SelectionMode.None) return;
        first = Math.Clamp(first, 0, _count - 1);
        last = Math.Clamp(last, 0, _count - 1);
        var s = Begin();
        if (_mode == SelectionMode.Single) DoSelect(last);
        else
        {
            if (add) AddRange(Math.Min(first, last), Math.Max(first, last));
            else SetRange(Math.Min(first, last), Math.Max(first, last));
            _anchor = first;
            _current = last;
        }
        End(s);
    }

    /// <summary>Selects every item (one range, whatever the count); only in Multi and Extended modes.</summary>
    public void SelectAll()
    {
        if (_count == 0 || _mode is SelectionMode.None or SelectionMode.Single) return;
        var s = Begin();
        SetRange(0, _count - 1);
        End(s);
    }

    /// <summary>Deselects everything. In <see cref="SelectionMode.Single"/> the current item goes too (-1).</summary>
    public void ClearSelection()
    {
        var s = Begin();
        if (_mode == SelectionMode.Single) DoSelect(-1);
        else ClearRanges();
        End(s);
    }

    /// <summary>Flips one item and makes it the current item and the anchor.</summary>
    public void Toggle(int index)
    {
        if (index < 0 || index >= _count) return;
        var s = Begin();
        DoToggle(index);
        End(s);
    }

    // ---- Input ----

    /// <summary>
    /// A press on item <paramref name="index"/>, with Ctrl/⌘ (<paramref name="toggle"/>) and/or Shift
    /// (<paramref name="extend"/>) held, applied by the <see cref="Mode"/>'s rules. Follow it with
    /// <see cref="DragTo"/> while the button stays down.
    /// </summary>
    public void Click(int index, bool toggle = false, bool extend = false)
    {
        _dragBase = null;
        if (index < 0 || index >= _count) return;
        var s = Begin();
        switch (_mode)
        {
            case SelectionMode.None: _current = index; break;
            case SelectionMode.Single: DoSelect(index); break;
            case SelectionMode.Multi: DoToggle(index); break;
            case SelectionMode.Extended:
                if (extend)
                {
                    if (toggle) { _dragBase = [.. _ranges]; _dragRemoves = false; }
                    int a = EffectiveAnchor(index);
                    if (toggle) AddRange(Math.Min(a, index), Math.Max(a, index));
                    else SetRange(Math.Min(a, index), Math.Max(a, index));
                    _current = index;
                }
                else if (toggle)
                {
                    _dragBase = [.. _ranges];
                    DoToggle(index);
                    _dragRemoves = !IsSelected(index);
                }
                else DoSelect(index);
                break;
        }
        End(s);
    }

    /// <summary>
    /// The pointer moved to item <paramref name="index"/> (clamped to the items) with the button still down after
    /// <see cref="Click"/>: in Extended mode the range from the anchor follows it (a Ctrl+press adds or removes the
    /// range, depending on what the press did to its item); in Single mode the selection follows.
    /// </summary>
    public void DragTo(int index)
    {
        if (_count == 0) return;
        index = Math.Clamp(index, 0, _count - 1);
        var s = Begin();
        switch (_mode)
        {
            case SelectionMode.None: _current = index; break;
            case SelectionMode.Single: DoSelect(index); break;
            case SelectionMode.Multi: break;   // each click toggles one item; dragging doesn't sweep
            case SelectionMode.Extended:
                if (_anchor < 0) { DoSelect(index); break; }
                int lo = Math.Min(_anchor, index), hi = Math.Max(_anchor, index);
                if (_dragBase != null)
                {
                    // Rebuild from the selection before the press; only a net difference counts as a change.
                    int version = _version;
                    var before = _ranges.ToArray();
                    _ranges.Clear();
                    _ranges.AddRange(_dragBase);
                    if (_dragRemoves) RemoveRange(lo, hi); else AddRange(lo, hi);
                    _version = _ranges.SequenceEqual(before) ? version : version + 1;
                }
                else SetRange(lo, hi);
                _current = index;
                break;
        }
        End(s);
    }

    /// <summary>
    /// The keyboard moved to item <paramref name="index"/> (an arrow, Page or Home/End key): with Shift
    /// (<paramref name="extend"/>) Extended mode selects the range from the anchor; with Ctrl/⌘
    /// (<paramref name="focusOnly"/>) it moves only the current item (both: adds the range). Multi mode always moves
    /// only the current item; Single mode selects it.
    /// </summary>
    public void MoveTo(int index, bool extend = false, bool focusOnly = false)
    {
        if (index < 0 || index >= _count) return;
        var s = Begin();
        switch (_mode)
        {
            case SelectionMode.None:
            case SelectionMode.Multi: _current = index; break;
            case SelectionMode.Single: DoSelect(index); break;
            case SelectionMode.Extended:
                if (extend)
                {
                    int a = EffectiveAnchor(index);
                    if (focusOnly) AddRange(Math.Min(a, index), Math.Max(a, index));
                    else SetRange(Math.Min(a, index), Math.Max(a, index));
                    _current = index;
                }
                else if (focusOnly) _current = index;
                else DoSelect(index);
                break;
        }
        End(s);
    }

    /// <summary>Space (Multi) or Ctrl/⌘+Space (Extended): toggles the current item.</summary>
    public void ToggleCurrent()
    {
        if (_current < 0) return;
        var s = Begin();
        if (_mode is SelectionMode.Multi or SelectionMode.Extended) DoToggle(_current);
        else if (_mode == SelectionMode.Single) DoSelect(_current);
        End(s);
    }

    // ---- Item changes ----

    /// <summary>New items: <paramref name="count"/> of them, nothing selected, no current item.</summary>
    public void Reset(int count)
    {
        var s = Begin();
        _dragBase = null;
        ClearRanges();
        _count = Math.Max(0, count);
        _current = _anchor = -1;
        End(s);
    }

    /// <summary>
    /// <paramref name="count"/> unselected items were inserted at <paramref name="index"/>: later selected items,
    /// the current item and the anchor move down with their items.
    /// </summary>
    public void Insert(int index, int count = 1)
    {
        if (count <= 0) return;
        index = Math.Clamp(index, 0, _count);
        var s = Begin();
        _dragBase = null;   // a drag in progress restarts from the new layout
        var next = new List<IndexRange>(_ranges.Count + 1);
        foreach (var r in _ranges)
        {
            if (r.First >= index) next.Add(new IndexRange(r.First + count, r.Last + count));
            else if (r.Last >= index)
            {
                next.Add(new IndexRange(r.First, index - 1));
                next.Add(new IndexRange(index + count, r.Last + count));
            }
            else next.Add(r);
        }
        ReplaceRanges(next);
        _count += count;
        if (_current >= index) _current += count;
        if (_anchor >= index) _anchor += count;
        End(s);
    }

    /// <summary>
    /// <paramref name="count"/> items were removed at <paramref name="index"/>: their selection goes, later items move
    /// up. A removed current item is replaced by the item now at <paramref name="index"/> (or the new last one); in
    /// <see cref="SelectionMode.Single"/> that item becomes the selected one.
    /// </summary>
    public void Remove(int index, int count = 1)
    {
        if (index < 0 || index >= _count) return;
        count = Math.Min(count, _count - index);
        if (count <= 0) return;
        int end = index + count - 1, newCount = _count - count;
        var s = Begin();
        _dragBase = null;
        var next = new List<IndexRange>(_ranges.Count);
        foreach (var r in _ranges)
        {
            int lo = r.First < index ? r.First : r.First > end ? r.First - count : index;
            int hi = r.Last < index ? r.Last : r.Last > end ? r.Last - count : index - 1;
            if (lo > hi) continue;
            if (next.Count > 0 && next[^1].Last + 1 >= lo) next[^1] = new IndexRange(next[^1].First, Math.Max(next[^1].Last, hi));
            else next.Add(new IndexRange(lo, hi));
        }
        ReplaceRanges(next);
        _count = newCount;
        bool currentRemoved = _current >= index && _current <= end;
        _current = MapRemoved(_current, index, count, newCount);
        _anchor = MapRemoved(_anchor, index, count, newCount);
        if (currentRemoved && _mode == SelectionMode.Single) SetOnly(_current);
        End(s);
    }

    static int MapRemoved(int i, int index, int count, int newCount)
    {
        if (i < index) return i;
        if (i >= index + count) return i - count;
        return Math.Min(index, newCount - 1);
    }

    // ---- Internals ----

    readonly record struct State(int Version, int Current);

    State Begin() => new(_version, _current);

    void End(State s)
    {
        if (_version != s.Version || _current != s.Current) Changed?.Invoke();
    }

    int EffectiveAnchor(int fallback)
    {
        if (_anchor < 0 || _anchor >= _count) _anchor = _current >= 0 ? _current : fallback;
        return _anchor;
    }

    void DoSelect(int index)
    {
        if (_mode == SelectionMode.None) { _current = index; return; }
        SetOnly(index);
        _current = _anchor = index;
    }

    void DoToggle(int index)
    {
        if (_mode == SelectionMode.None) { _current = index; return; }
        if (_mode == SelectionMode.Single)
        {
            if (IsSelected(index)) { SetOnly(-1); _current = index; }
            else DoSelect(index);
            return;
        }
        if (IsSelected(index)) RemoveRange(index, index); else AddRange(index, index);
        _current = _anchor = index;
    }

    /// <summary>The first range whose Last is at or after <paramref name="x"/> (binary search); Count if none.</summary>
    int FindFirstEndingAtOrAfter(int x)
    {
        int lo = 0, hi = _ranges.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_ranges[mid].Last < x) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    void ClearRanges()
    {
        if (_ranges.Count == 0) return;
        _ranges.Clear();
        _version++;
    }

    void SetOnly(int index)
    {
        if (index < 0) ClearRanges();
        else SetRange(index, index);
    }

    /// <summary>Makes <paramref name="first"/>..<paramref name="last"/> the whole selection.</summary>
    void SetRange(int first, int last)
    {
        if (_ranges.Count == 1 && _ranges[0] == new IndexRange(first, last)) return;
        _ranges.Clear();
        _ranges.Add(new IndexRange(first, last));
        _version++;
    }

    void AddRange(int first, int last)
    {
        int i = FindFirstEndingAtOrAfter(first - 1), j = i;
        int lo = first, hi = last;
        while (j < _ranges.Count && _ranges[j].First <= last + 1)
        {
            lo = Math.Min(lo, _ranges[j].First);
            hi = Math.Max(hi, _ranges[j].Last);
            j++;
        }
        if (j == i + 1 && _ranges[i].First <= first && _ranges[i].Last >= last) return;   // already selected
        _ranges.RemoveRange(i, j - i);
        _ranges.Insert(i, new IndexRange(lo, hi));
        _version++;
    }

    void RemoveRange(int first, int last)
    {
        if (first > last) return;
        int i = FindFirstEndingAtOrAfter(first), j = i;
        IndexRange? before = null, after = null;
        while (j < _ranges.Count && _ranges[j].First <= last)
        {
            var r = _ranges[j];
            if (r.First < first) before = new IndexRange(r.First, first - 1);
            if (r.Last > last) after = new IndexRange(last + 1, r.Last);
            j++;
        }
        if (j == i) return;
        _ranges.RemoveRange(i, j - i);
        if (after is { } a) _ranges.Insert(i, a);
        if (before is { } b) _ranges.Insert(i, b);
        _version++;
    }

    void ReplaceRanges(List<IndexRange> next)
    {
        if (next.Count == _ranges.Count)
        {
            bool same = true;
            for (int i = 0; i < next.Count && same; i++) same = next[i] == _ranges[i];
            if (same) return;
        }
        _ranges.Clear();
        _ranges.AddRange(next);
        _version++;
    }
}

/// <summary>A cell of a grid: column and row (Delphi's TGridCoord).</summary>
public readonly record struct GridCoord(int Col, int Row);

/// <summary>An inclusive rectangle of grid cells (Delphi's TGridRect).</summary>
public readonly record struct GridRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>The rectangle spanned by two corner cells, in either order.</summary>
    public static GridRect FromCorners(GridCoord a, GridCoord b) =>
        new(Math.Min(a.Col, b.Col), Math.Min(a.Row, b.Row), Math.Max(a.Col, b.Col), Math.Max(a.Row, b.Row));

    public int ColCount => Right - Left + 1;
    public int RowCount => Bottom - Top + 1;
    public bool Contains(int col, int row) => col >= Left && col <= Right && row >= Top && row <= Bottom;
    public bool Contains(GridCoord cell) => Contains(cell.Col, cell.Row);
}

/// <summary>How the cells of a grid can be selected; the mode of a <see cref="CellSelection"/>.</summary>
public enum CellSelectionMode
{
    /// <summary>Only the current cell (Delphi's grid without goRangeSelect).</summary>
    Single,
    /// <summary>One rectangle: Shift+click, Shift+arrows and dragging span it from the anchor (goRangeSelect).</summary>
    Range,
    /// <summary>As <see cref="Range"/>, plus Ctrl/⌘+click starts another rectangle, kept alongside the others.</summary>
    MultiRange,
}

/// <summary>
/// The cell selection of a grid of <see cref="ColCount"/> × <see cref="RowCount"/> cells, without any UI: the current
/// cell, the anchor, and one or more selected rectangles.
/// </summary>
/// <remarks>
/// <para>Coordinates are the grid's selectable cells; a grid with fixed (header) rows or columns maps them out.
/// Ranges are rectangles, never per-cell flags, so selecting all of a 100 000-row grid is one rectangle.</para>
/// <para>While the grid has cells there is always a current cell and at least one range; the last range is the
/// active one, spanned by <see cref="Anchor"/> and <see cref="Current"/> whenever Shift or a drag extends it.
/// <see cref="CellSelectionMode.MultiRange"/> keeps earlier rectangles from Ctrl/⌘+clicks before it (a Ctrl+click
/// adds, it never removes).</para>
/// <para>Inserting rows or columns inside a range grows it (as in a spreadsheet); removing them shrinks it. After a
/// removal or <see cref="SetSize"/> shrink, if the active range went or no longer holds the current cell, the selection
/// collapses to the current cell alone (anchor = current), so the active range always contains the current cell.</para>
/// </remarks>
public sealed class CellSelection(CellSelectionMode mode = CellSelectionMode.Range)
{
    static readonly GridCoord NoCell = new(-1, -1);

    readonly List<GridRect> _ranges = [];
    int _cols, _rows;
    GridCoord _current = NoCell, _anchor = NoCell;
    CellSelectionMode _mode = mode;
    int _version;

    /// <summary>Raised once after a call changed the selected cells or <see cref="Current"/>.</summary>
    public event Action? Changed;

    /// <summary>The selection rules; switching to a narrower mode collapses the selection to the current cell.</summary>
    public CellSelectionMode Mode
    {
        get => _mode;
        set
        {
            if (value == _mode) return;
            var s = Begin();
            bool collapse = value == CellSelectionMode.Single || (value == CellSelectionMode.Range && _ranges.Count > 1);
            _mode = value;
            if (collapse && HasCells) DoSelect(_current);
            End(s);
        }
    }

    public int ColCount => _cols;
    public int RowCount => _rows;
    bool HasCells => _cols > 0 && _rows > 0;

    /// <summary>
    /// Sets the grid size. Ranges are clipped to it and the current cell clamped; a grid that gains its first cells
    /// gets (0, 0) as the current cell, and one that loses them has none (-1, -1).
    /// </summary>
    public void SetSize(int cols, int rows)
    {
        cols = Math.Max(0, cols);
        rows = Math.Max(0, rows);
        if (cols == _cols && rows == _rows) return;
        var s = Begin();
        bool had = HasCells;
        _cols = cols;
        _rows = rows;
        if (!HasCells)
        {
            ClearRanges();
            _current = _anchor = NoCell;
        }
        else if (!had) DoSelect(new GridCoord(0, 0));
        else
        {
            var next = new List<GridRect>(_ranges.Count);
            bool activeKept = false;
            for (int i = 0; i < _ranges.Count; i++)
            {
                var r = _ranges[i];
                if (r.Left >= cols || r.Top >= rows) continue;
                next.Add(new GridRect(r.Left, r.Top, Math.Min(r.Right, cols - 1), Math.Min(r.Bottom, rows - 1)));
                activeKept = i == _ranges.Count - 1;
            }
            ReplaceRanges(next);
            _current = Clamp(_current);
            _anchor = Clamp(_anchor);
            KeepCurrentInActiveRange(activeKept);
        }
        End(s);
    }

    /// <summary>The current cell, or (-1, -1) when the grid has none. Setting it selects only that cell (Delphi's Col/Row).</summary>
    public GridCoord Current
    {
        get => _current;
        set => Select(value.Col, value.Row);
    }

    /// <summary>The fixed corner of the active range.</summary>
    public GridCoord Anchor => _anchor;

    /// <summary>The selected rectangles; the last is the active one.</summary>
    public IReadOnlyList<GridRect> Ranges => _ranges;

    /// <summary>The range Shift and dragging change (the last one), or null when the grid has no cells.</summary>
    public GridRect? ActiveRange => _ranges.Count > 0 ? _ranges[^1] : null;

    public bool IsSelected(int col, int row)
    {
        foreach (var r in _ranges) if (r.Contains(col, row)) return true;
        return false;
    }

    /// <summary>Selects only one cell and makes it the current cell and the anchor (clamped to the grid).</summary>
    public void Select(int col, int row)
    {
        if (!HasCells) return;
        var s = Begin();
        DoSelect(Clamp(new GridCoord(col, row)));
        End(s);
    }

    /// <summary>
    /// Selects the rectangle between two corners (clamped): <paramref name="anchor"/> stays fixed and
    /// <paramref name="current"/> becomes the current cell. <paramref name="add"/> keeps the other ranges (MultiRange
    /// only). In Single mode it selects <paramref name="current"/> alone. Use it for a header click (a whole row or
    /// column) or Ctrl+A.
    /// </summary>
    public void SelectRange(GridCoord anchor, GridCoord current, bool add = false)
    {
        if (!HasCells) return;
        var s = Begin();
        anchor = Clamp(anchor);
        current = Clamp(current);
        if (_mode == CellSelectionMode.Single) DoSelect(current);
        else
        {
            if (!(add && _mode == CellSelectionMode.MultiRange)) ClearRanges();
            AddRect(GridRect.FromCorners(anchor, current));
            _anchor = anchor;
            _current = current;
        }
        End(s);
    }

    /// <summary>Selects every cell as one range, keeping the current cell (Range and MultiRange modes).</summary>
    public void SelectAll()
    {
        if (!HasCells || _mode == CellSelectionMode.Single) return;
        var s = Begin();
        ReplaceRanges([new GridRect(0, 0, _cols - 1, _rows - 1)]);
        End(s);
    }

    /// <summary>
    /// A press on a cell with Ctrl/⌘ (<paramref name="toggle"/>) and/or Shift (<paramref name="extend"/>): Shift spans
    /// the active range from the anchor; in MultiRange, Ctrl starts a new range there; otherwise the cell alone is
    /// selected. Follow it with <see cref="DragTo"/> while the button stays down.
    /// </summary>
    public void Click(int col, int row, bool toggle = false, bool extend = false)
    {
        if (!HasCells || col < 0 || row < 0 || col >= _cols || row >= _rows) return;
        var cell = new GridCoord(col, row);
        var s = Begin();
        if (extend && _mode != CellSelectionMode.Single) ExtendActive(cell);
        else if (toggle && _mode == CellSelectionMode.MultiRange)
        {
            AddRect(new GridRect(col, row, col, row));
            _current = _anchor = cell;
        }
        else DoSelect(cell);
        End(s);
    }

    /// <summary>The pointer moved to a cell (clamped to the grid) with the button down: the active range follows it.</summary>
    public void DragTo(int col, int row)
    {
        if (!HasCells) return;
        var cell = Clamp(new GridCoord(col, row));
        var s = Begin();
        if (_mode == CellSelectionMode.Single) DoSelect(cell);
        else ExtendActive(cell);
        End(s);
    }

    /// <summary>The keyboard moved to a cell: with Shift (<paramref name="extend"/>) the active range follows, otherwise the cell alone is selected.</summary>
    public void MoveTo(int col, int row, bool extend = false)
    {
        if (!HasCells) return;
        var cell = Clamp(new GridCoord(col, row));
        var s = Begin();
        if (extend && _mode != CellSelectionMode.Single) ExtendActive(cell);
        else DoSelect(cell);
        End(s);
    }

    /// <summary>Rows were inserted at <paramref name="index"/>: ranges spanning it grow, later ones and the current cell move down.</summary>
    public void InsertRows(int index, int count = 1) => Insert(rows: true, index, count);

    /// <summary>Rows were removed at <paramref name="index"/>: ranges shrink (and go when empty), later ones move up.</summary>
    public void RemoveRows(int index, int count = 1) => Remove(rows: true, index, count);

    public void InsertColumns(int index, int count = 1) => Insert(rows: false, index, count);
    public void RemoveColumns(int index, int count = 1) => Remove(rows: false, index, count);

    void Insert(bool rows, int index, int count)
    {
        if (count <= 0) return;
        int size = rows ? _rows : _cols;
        index = Math.Clamp(index, 0, size);
        var s = Begin();
        bool had = HasCells;
        if (rows) _rows += count; else _cols += count;
        if (!had)
        {
            if (HasCells) DoSelect(new GridCoord(0, 0));
            End(s);
            return;
        }
        static (int, int) Grow(int lo, int hi, int index, int count) =>
            lo >= index ? (lo + count, hi + count) : hi >= index ? (lo, hi + count) : (lo, hi);
        var next = new List<GridRect>(_ranges.Count);
        foreach (var r in _ranges)
        {
            if (rows) { var (t, b) = Grow(r.Top, r.Bottom, index, count); next.Add(r with { Top = t, Bottom = b }); }
            else { var (l, rr) = Grow(r.Left, r.Right, index, count); next.Add(r with { Left = l, Right = rr }); }
        }
        ReplaceRanges(next);
        GridCoord Shift(GridCoord c) => rows
            ? c with { Row = c.Row >= index ? c.Row + count : c.Row }
            : c with { Col = c.Col >= index ? c.Col + count : c.Col };
        _current = Shift(_current);
        _anchor = Shift(_anchor);
        End(s);
    }

    void Remove(bool rows, int index, int count)
    {
        int size = rows ? _rows : _cols;
        if (index < 0 || index >= size) return;
        count = Math.Min(count, size - index);
        if (count <= 0) return;
        int end = index + count - 1, newSize = size - count;
        var s = Begin();
        if (rows) _rows = newSize; else _cols = newSize;
        if (!HasCells)
        {
            ClearRanges();
            _current = _anchor = NoCell;
            End(s);
            return;
        }
        var next = new List<GridRect>(_ranges.Count);
        bool activeKept = false;
        for (int i = 0; i < _ranges.Count; i++)
        {
            var r = _ranges[i];
            int lo = rows ? r.Top : r.Left, hi = rows ? r.Bottom : r.Right;
            int nlo = lo < index ? lo : lo > end ? lo - count : index;
            int nhi = hi < index ? hi : hi > end ? hi - count : index - 1;
            if (nlo > nhi) continue;
            next.Add(rows ? r with { Top = nlo, Bottom = nhi } : r with { Left = nlo, Right = nhi });
            activeKept = i == _ranges.Count - 1;
        }
        ReplaceRanges(next);
        int Map(int i) => i < index ? i : i > end ? i - count : Math.Min(index, newSize - 1);
        GridCoord MapCell(GridCoord c) => rows ? c with { Row = Map(c.Row) } : c with { Col = Map(c.Col) };
        _current = MapCell(_current);
        _anchor = MapCell(_anchor);
        KeepCurrentInActiveRange(activeKept);
        End(s);
    }

    // ---- Internals ----

    readonly record struct State(int Version, GridCoord Current);

    State Begin() => new(_version, _current);

    void End(State s)
    {
        if (_version != s.Version || _current != s.Current) Changed?.Invoke();
    }

    GridCoord Clamp(GridCoord c) => new(Math.Clamp(c.Col, 0, Math.Max(0, _cols - 1)), Math.Clamp(c.Row, 0, Math.Max(0, _rows - 1)));

    void DoSelect(GridCoord cell)
    {
        ReplaceRanges([new GridRect(cell.Col, cell.Row, cell.Col, cell.Row)]);
        _current = _anchor = cell;
    }

    void ExtendActive(GridCoord cell)
    {
        if (_anchor == NoCell) _anchor = _current == NoCell ? cell : _current;
        var rect = GridRect.FromCorners(_anchor, cell);
        if (_ranges.Count == 0) AddRect(rect);
        else if (_ranges[^1] != rect) { _ranges[^1] = rect; _version++; }
        _current = cell;
    }

    /// <summary>
    /// After a removal or shrink, as a spreadsheet does: if the active range went, or the current cell is no longer
    /// inside it, the selection collapses to the current cell alone (anchor = current), so a later Shift+arrow never
    /// rewrites an earlier range. An active range that survived and still holds the current cell stays, with the
    /// anchor kept inside it.
    /// </summary>
    void KeepCurrentInActiveRange(bool activeKept)
    {
        if (!HasCells) return;
        if (_current == NoCell) _current = new GridCoord(0, 0);
        if (!activeKept || _ranges.Count == 0 || !_ranges[^1].Contains(_current))
        {
            DoSelect(_current);
            return;
        }
        var a = _ranges[^1];
        _anchor = new GridCoord(Math.Clamp(_anchor.Col, a.Left, a.Right), Math.Clamp(_anchor.Row, a.Top, a.Bottom));
    }

    void AddRect(GridRect r)
    {
        if (_ranges.Count > 0 && _ranges[^1] == r) return;   // already the active range
        int dup = _ranges.IndexOf(r);
        if (dup >= 0)
        {
            _ranges.RemoveAt(dup);   // same cells: only make it the active (last) range
            _ranges.Add(r);
            return;
        }
        _ranges.Add(r);
        _version++;
    }

    void ClearRanges()
    {
        if (_ranges.Count == 0) return;
        _ranges.Clear();
        _version++;
    }

    void ReplaceRanges(List<GridRect> next)
    {
        if (next.Count == _ranges.Count)
        {
            bool same = true;
            for (int i = 0; i < next.Count && same; i++) same = next[i] == _ranges[i];
            if (same) return;
        }
        _ranges.Clear();
        _ranges.AddRange(next);
        _version++;
    }
}
