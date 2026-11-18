using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text;
using RadSharp.Drawing;
using RadSharp.Text;

namespace RadSharp;

/// <summary>How a cell is being drawn (Delphi's TGridDrawState).</summary>
[Flags]
public enum GridDrawState
{
    None = 0,
    /// <summary>The cell is in a selected range (or row, with RowSelect).</summary>
    Selected = 1,
    /// <summary>The cell is the current cell and the grid has the keyboard focus.</summary>
    Focused = 2,
    /// <summary>The cell is in a fixed (header) row or column.</summary>
    Fixed = 4,
}

/// <summary>Paints one cell (Delphi's OnDrawCell). <paramref name="rect"/> is in the grid's coordinates, inside the grid lines.</summary>
public delegate void DrawCellEvent(Control sender, Canvas canvas, int col, int row, Rect rect, GridDrawState state);

/// <summary>A cell's text going in or out: <see cref="CustomGrid.GetEditText"/>, <see cref="CustomGrid.SetEditText"/>, <see cref="StringGrid.GetCell"/>.</summary>
public sealed class GridTextEventArgs(int col, int row, string text) : EventArgs
{
    public int Col { get; } = col;
    public int Row { get; } = row;
    string _text = text ?? "";

    /// <summary>The text: supply it (GetCell, GetEditText) or read what was typed (SetEditText); null reads as "".</summary>
    public string Text { get => _text; set => _text = value ?? ""; }
    /// <summary>For the edit events only: false refuses to start editing (GetEditText) or keeps the editor open (SetEditText).</summary>
    public bool Accept { get; set; } = true;
}

public delegate void GridTextEvent(Control sender, GridTextEventArgs e);

/// <summary>The user is about to make a cell current (Delphi's OnSelectCell); clear <see cref="CanSelect"/> to refuse.</summary>
public sealed class SelectCellEventArgs(int col, int row) : EventArgs
{
    public int Col { get; } = col;
    public int Row { get; } = row;
    public bool CanSelect { get; set; } = true;
}

public delegate void SelectCellEvent(Control sender, SelectCellEventArgs e);

/// <summary>Two rows compared while a <see cref="StringGrid"/> sorts; set <see cref="Result"/> (negative, 0, positive).</summary>
public sealed class GridCompareEventArgs(int col, int row1, int row2, string text1, string text2, int result) : EventArgs
{
    public int Col { get; } = col;
    /// <summary>The rows being compared, as they were before the sort started.</summary>
    public int Row1 { get; } = row1;
    public int Row2 { get; } = row2;
    public string Text1 { get; } = text1;
    public string Text2 { get; } = text2;
    /// <summary>Pre-set to the default (numbers by value, else text ignoring case), ascending; the grid reverses it for a descending sort.</summary>
    public int Result { get; set; } = result;
}

public delegate void GridCompareEvent(Control sender, GridCompareEventArgs e);

/// <summary>
/// A column of a grid (like a TDBGrid column): the header title, width, text alignment and the narrowest width
/// the user can drag it to. Plain properties only, so it can be written to a form file.
/// </summary>
public sealed class GridColumn
{
    string _title = "";
    int _width, _minWidth;
    TextAlign _alignment;
    internal CustomGrid? Grid;

    public GridColumn() { }

    public GridColumn(string title, int width = 0, TextAlign alignment = TextAlign.Left)
    {
        _title = title ?? "";
        _width = Math.Max(0, width);
        _alignment = alignment;
    }

    /// <summary>The text in the header (the grid's first fixed row).</summary>
    public string Title { get => _title; set { _title = value ?? ""; Grid?.ColumnsChanged(widths: false); } }

    /// <summary>The width at 100 %; 0 uses the grid's <see cref="CustomGrid.DefaultColWidth"/>.</summary>
    public int Width { get => _width; set { _width = Math.Max(0, value); Grid?.ColumnsChanged(widths: true); } }

    /// <summary>How the column's text sits in its cells, the header included.</summary>
    public TextAlign Alignment { get => _alignment; set { _alignment = value; Grid?.ColumnsChanged(widths: false); } }

    /// <summary>The narrowest the user can drag the column, at 100 %; 0 means 8.</summary>
    public int MinWidth { get => _minWidth; set => _minWidth = Math.Max(0, value); }

    // ---- Form files: { "alignment", "minWidth", "title", "width" }, only non-default values, in that order ----

    internal static GridColumn Read(JsonElement json, List<string> warnings, string path)
    {
        var column = new GridColumn();
        if (json.ValueKind != JsonValueKind.Object) { warnings.Add($"{path}: a column must be an object; replaced by a blank column."); return column; }
        foreach (var p in json.EnumerateObject())
        {
            try
            {
                switch (p.Name)
                {
                    case "title": column.Title = Converters.String.Read(p.Value); break;
                    case "width": column.Width = Converters.Int.Read(p.Value); break;
                    case "alignment": column.Alignment = Converters.Enum<TextAlign>().Read(p.Value); break;
                    case "minWidth": column.MinWidth = Converters.Int.Read(p.Value); break;
                    default: warnings.Add($"{path}: columns have no property \"{p.Name}\"; ignored."); break;
                }
            }
            catch (FormatException e) { warnings.Add($"{path}.{p.Name}: {e.Message}; ignored."); }
        }
        return column;
    }

    internal void Write(FormJsonWriter w)
    {
        w.BeginObject();
        if (_alignment != TextAlign.Left) { w.Key("alignment"); Converters.Enum<TextAlign>().Write(w, _alignment); }
        if (_minWidth > 0) { w.Key("minWidth"); w.WriteInt(_minWidth); }
        if (_title.Length > 0) { w.Key("title"); w.WriteString(_title); }
        if (_width > 0) { w.Key("width"); w.WriteInt(_width); }
        w.EndObject();
    }
}

/// <summary>A grid's <see cref="CustomGrid.Columns"/>: while it holds any, the grid's ColCount follows its count.</summary>
public sealed class GridColumns : Collection<GridColumn>
{
    readonly CustomGrid _grid;

    internal GridColumns(CustomGrid grid) => _grid = grid;

    /// <summary>Adds a column and returns it.</summary>
    public GridColumn Add(string title, int width = 0, TextAlign alignment = TextAlign.Left)
    {
        var column = new GridColumn(title, width, alignment);
        Add(column);
        return column;
    }

    /// <summary>
    /// Moves a column to another position. Unlike removing and inserting it, the grid's <see cref="CustomGrid.ColCount"/>,
    /// <see cref="CustomGrid.SortColumn"/> and cell data stay as they are (they follow positions, not columns).
    /// </summary>
    public void Move(int oldIndex, int newIndex)
    {
        if ((uint)oldIndex >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(oldIndex));
        if ((uint)newIndex >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(newIndex));
        if (oldIndex == newIndex) return;
        var column = Items[oldIndex];
        Items.RemoveAt(oldIndex);
        Items.Insert(newIndex, column);
        _grid.ColumnsChanged(widths: true);
    }

    protected override void InsertItem(int index, GridColumn item)
    {
        Adopt(item);
        base.InsertItem(index, item);
        _grid.ColumnsChanged(widths: true, structure: true, adopting: Count == 1);
    }

    protected override void SetItem(int index, GridColumn item)
    {
        Adopt(item);
        this[index].Grid = null;
        base.SetItem(index, item);
        _grid.ColumnsChanged(widths: true);
    }

    protected override void RemoveItem(int index)
    {
        this[index].Grid = null;
        base.RemoveItem(index);
        _grid.ColumnsChanged(widths: true, structure: true);
    }

    protected override void ClearItems()
    {
        foreach (var c in this) c.Grid = null;
        base.ClearItems();
        _grid.ColumnsChanged(widths: true, structure: true);
    }

    void Adopt(GridColumn item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Grid != null) throw new InvalidOperationException("The column already belongs to a grid.");
        item.Grid = _grid;
    }
}

/// <summary>Per-column widths at 100 % (Delphi's <c>ColWidths[i]</c>).</summary>
public sealed class GridColWidths
{
    readonly CustomGrid _grid;
    internal GridColWidths(CustomGrid grid) => _grid = grid;
    public int this[int col] { get => _grid.GetColWidth(col); set => _grid.SetColWidth(col, value); }
    public int Count => _grid.ColCount;
}

/// <summary>
/// The shared base of <see cref="DrawGrid"/> and <see cref="StringGrid"/> (Delphi's TCustomGrid), on
/// <see cref="ScrollView"/>: a table of <see cref="ColCount"/> × <see cref="RowCount"/> cells whose first
/// <see cref="FixedCols"/> columns and <see cref="FixedRows"/> rows are headers that stay put while the rest scrolls.
/// </summary>
/// <remarks>
/// <para>Rows share one height (<see cref="DefaultRowHeight"/>); columns have their own (<see cref="ColWidths"/>, or the
/// <see cref="Columns"/> collection, which also gives the header row its titles and the columns their alignment).
/// Painting only visits the cells in view, so a grid of 100 000 rows paints as fast as one of ten.</para>
/// <para>The selection is a <see cref="CellSelection"/> (<see cref="Selection"/>) over the non-fixed cells; its
/// coordinates leave out the fixed ones (its column 0 is grid column <see cref="FixedCols"/>). <see cref="Col"/>,
/// <see cref="Row"/>, <see cref="CellRect"/>, <see cref="CellAt"/>, <see cref="IsCellSelected"/> and every event use
/// grid coordinates.</para>
/// <para>Mouse: a click selects a cell, dragging or Shift+click spans a range from the anchor, Ctrl/⌘+click adds a
/// range (<see cref="MultiSelect"/>); a click on a header cell selects its column, on a row header its row, on the corner
/// everything (<see cref="RangeSelect"/>), or with <see cref="AutoSort"/> a header click sorts by that column. Dragging
/// a header border resizes a column (<see cref="ColSizing"/>). Keys: arrows, Home/End (row), Ctrl+Home/End (grid),
/// PageUp/PageDown, Tab/Shift+Tab (<see cref="WantTabs"/>), Shift extends; Ctrl/⌘+C copies the selection as
/// tab-separated text, Ctrl/⌘+A selects all. With <see cref="Editing"/>, Enter, F2, a double click or typing opens an
/// in-place <see cref="Edit"/> on the current cell: Enter commits, Escape cancels, Up/Down/Tab commit and move.</para>
/// </remarks>
public abstract class CustomGrid : ScrollView, ICollectionOwner
{
    int _colCount = 5, _rowCount = 5, _fixedCols = 1, _fixedRows = 1;
    int _defaultColWidth = 64, _defaultRowHeight = 24;
    bool _gridLines = true, _editing, _rangeSelect = true, _multiSelect, _rowSelect, _colSizing, _wantTabs, _autoSort;
    int _sortColumn = -1;
    bool _sortDescending;
    readonly List<int> _colWidths = [];   // at 100 %; 0 = default; only as long as the widest column set
    int[] _colX = [0];                     // content x of each column's left edge, scaled; [ColCount] = total width
    bool _colXDirty = true;
    double _colXScale;
    GridCoord _lastCurrent = new(-1, -1);
    bool _mouseSelecting, _noAutoScroll;
    int _resizeCol = -1, _resizeStartX, _resizeStartWidth;
    InplaceEdit? _editor;
    int _editCol, _editRow;
    bool _endingEdit, _committing;
    bool _sizingCursor;
    Cursor _savedCursor;

    protected CustomGrid()
    {
        SetDefaultSize(320, 160);
        Columns = new GridColumns(this);
        ColWidths = new GridColWidths(this);
        Selection.Changed += SelectionChanged;
        UpdateSelectionSize();
        Remeasure();
    }

    // ---- Published ----

    /// <summary>Columns, fixed ones included. Adding or removing <see cref="Columns"/> sets it to their count.</summary>
    [Published(Category = "Behavior")]
    public int ColCount { get => _colCount; set => SetCounts(Math.Max(0, value), _rowCount); }

    /// <summary>Rows, fixed ones included.</summary>
    [Published(Category = "Behavior")]
    public int RowCount { get => _rowCount; set => SetCounts(_colCount, Math.Max(0, value)); }

    /// <summary>Header columns at the left that don't scroll horizontally and can't be selected.</summary>
    [Published(Category = "Behavior")]
    public int FixedCols { get => _fixedCols; set { if (value == _fixedCols) return; CloseEditor(); int old = FC; _fixedCols = Math.Max(0, value); ShiftSelection(cols: true, FC - old); Structure(); } }

    /// <summary>Header rows at the top that don't scroll vertically; the first one shows the <see cref="Columns"/> titles.</summary>
    [Published(Category = "Behavior")]
    public int FixedRows { get => _fixedRows; set { if (value == _fixedRows) return; CloseEditor(); int old = FR; _fixedRows = Math.Max(0, value); ShiftSelection(cols: false, FR - old); Structure(); } }

    /// <summary>The width of a column without its own (<see cref="ColWidths"/>, <see cref="GridColumn.Width"/>), at 100 %.</summary>
    [Published(Category = "Appearance")]
    public int DefaultColWidth { get => _defaultColWidth; set { _defaultColWidth = Math.Max(1, value); WidthsChanged(); } }

    /// <summary>The height of every row at 100 % (rows share one height).</summary>
    [Published(Category = "Appearance")]
    public int DefaultRowHeight { get => _defaultRowHeight; set { _defaultRowHeight = Math.Max(1, value); Remeasure(); PlaceEditor(); } }

    /// <summary>Draw lines between the cells (Delphi's goVertLine/goHorzLine and their fixed variants together).</summary>
    [Published(Category = "Appearance")]
    public bool GridLines { get => _gridLines; set { _gridLines = value; Invalidate(); PlaceEditor(); } }

    /// <summary>Cells can be edited in place (goEditing): Enter, F2, a double click or typing opens the editor.</summary>
    [Published(Category = "Behavior")]
    public bool Editing { get => _editing; set { _editing = value; if (!value) CloseEditor(); } }

    /// <summary>Shift, dragging and header clicks select rectangles of cells (goRangeSelect); otherwise only the current cell is selected.</summary>
    [Published(Category = "Behavior")]
    public bool RangeSelect { get => _rangeSelect; set { _rangeSelect = value; UpdateMode(); } }

    /// <summary>Ctrl/⌘+click adds further ranges to the selection (implies range selection).</summary>
    [Published(Category = "Behavior")]
    public bool MultiSelect { get => _multiSelect; set { _multiSelect = value; UpdateMode(); } }

    /// <summary>Selection covers whole rows (goRowSelect); in-place editing is off while it is on.</summary>
    [Published(Category = "Behavior")]
    public bool RowSelect { get => _rowSelect; set { _rowSelect = value; if (value) CloseEditor(); Invalidate(); } }

    /// <summary>The user can drag the borders of header cells to resize columns (goColSizing).</summary>
    [Published(Category = "Behavior")]
    public bool ColSizing { get => _colSizing; set => _colSizing = value; }

    /// <summary>Tab and Shift+Tab move between cells instead of to the next control (goTabs).</summary>
    [Published(Category = "Behavior")]
    public bool WantTabs { get => _wantTabs; set => _wantTabs = value; }

    /// <summary>
    /// A header click sorts by that column instead of selecting it: it sets <see cref="SortColumn"/> (a second click
    /// flips <see cref="SortDescending"/>) before <see cref="ColumnClick"/>. A StringGrid then reorders its own rows; a
    /// DrawGrid, or a StringGrid in virtual mode, leaves the sorting to the ColumnClick handler.
    /// </summary>
    [Published(Category = "Behavior")]
    public bool AutoSort { get => _autoSort; set => _autoSort = value; }

    /// <summary>
    /// The column whose header shows the sort arrow, or -1. Setting it only draws the arrow; any value is kept, so it can
    /// be set before the columns exist. Shrinking <see cref="ColCount"/> to or below it resets it to -1 (growing never
    /// does), so the arrow can't come back on a column whose data is gone. The one exception: adding the first
    /// <see cref="Columns"/> entry adopts the grid's columns and leaves it alone.
    /// </summary>
    [Published(Category = "Appearance")]
    public int SortColumn { get => _sortColumn; set { _sortColumn = Math.Max(-1, value); Invalidate(); } }

    /// <summary>The sort arrow points down (descending) rather than up.</summary>
    [Published(Category = "Appearance")]
    public bool SortDescending { get => _sortDescending; set { _sortDescending = value; Invalidate(); } }

    /// <summary>Paints a cell after the grid drew its background (and, in a StringGrid, its text).</summary>
    [Published] public event DrawCellEvent? DrawCell;
    /// <summary>The user is about to move to a cell; clear CanSelect to refuse (Delphi's OnSelectCell).</summary>
    [Published] public event SelectCellEvent? SelectCell;
    /// <summary>Editing starts: supply (or change) the editor's text, or clear Accept to refuse.</summary>
    [Published] public event GridTextEvent? GetEditText;
    /// <summary>An edit is committed: store the text, or clear Accept to keep the editor open.</summary>
    [Published] public event GridTextEvent? SetEditText;
    /// <summary>A header cell (first fixed rows, a non-fixed column) was clicked; with AutoSort, after SortColumn changed.</summary>
    [Published] public event ColumnClickEvent? ColumnClick;
    /// <summary>The selection or the current cell changed.</summary>
    [Published] public event NotifyEvent? Change;
    [Published("onDblClick")] public event NotifyEvent? DoubleClick;

    // ---- Runtime API ----

    /// <summary>Column titles, widths, alignment and minimum widths; while it holds any, <see cref="ColCount"/> follows its count.</summary>
    public GridColumns Columns { get; }

    /// <summary>Per-column widths at 100 %; a column in <see cref="Columns"/> reads and writes its <see cref="GridColumn.Width"/>.</summary>
    public GridColWidths ColWidths { get; }

    // ---- Form files and the designer: "columns" ----

    IReadOnlyList<string> ICollectionOwner.CollectionNames { get; } = ["columns"];

    string ICollectionOwner.DescribeCollection(string name) =>
        name == "columns" ? (Columns.Count == 1 ? "(1 column)" : $"({Columns.Count} columns)") : "";

    /// <summary>Writes <c>"columns": [...]</c> (nothing when there are none), one object per column holding only its non-default values.</summary>
    internal void WriteColumns(FormJsonWriter w)
    {
        if (Columns.Count == 0) return;
        w.Key("columns");
        w.BeginArray();
        foreach (var column in Columns) { w.NextElement(); column.Write(w); }
        w.EndArray();
    }

    /// <summary>Replaces <see cref="Columns"/> with the columns of a form file's <c>"columns"</c> array; <see cref="ColCount"/> follows their count.</summary>
    internal void ReadColumns(JsonElement json, List<string> warnings, string path)
    {
        Columns.Clear();
        if (json.ValueKind != JsonValueKind.Array) { warnings.Add($"{path}: expected an array; ignored."); return; }
        int i = 0;
        foreach (var item in json.EnumerateArray()) Columns.Add(GridColumn.Read(item, warnings, $"{path}[{i++}]"));
    }

    public override bool AcceptsTab => _wantTabs;

    /// <summary>The selection model over the non-fixed cells (its column 0 is grid column <see cref="FixedCols"/>, its row 0 grid row <see cref="FixedRows"/>).</summary>
    public CellSelection Selection { get; } = new(CellSelectionMode.Range);

    /// <summary>The current cell's column (grid coordinates), or -1. Setting it moves there and selects that cell alone.</summary>
    public int Col { get => Selection.Current.Col < 0 ? -1 : Selection.Current.Col + FC; set => Selection.Select(value - FC, Selection.Current.Row); }

    /// <summary>The current cell's row (grid coordinates), or -1. Setting it moves there and selects that cell alone.</summary>
    public int Row { get => Selection.Current.Row < 0 ? -1 : Selection.Current.Row + FR; set => Selection.Select(Selection.Current.Col, value - FR); }

    /// <summary>Makes a cell current and selects it alone (grid coordinates, clamped to the non-fixed cells).</summary>
    public void FocusCell(int col, int row) => Selection.Select(col - FC, row - FR);

    /// <summary>The selected rectangles in grid coordinates (whole rows with <see cref="RowSelect"/>); the last is the active one.</summary>
    public IEnumerable<GridRect> SelectedRanges
    {
        get
        {
            foreach (var r in Selection.Ranges)
                yield return _rowSelect
                    ? new GridRect(FC, r.Top + FR, _colCount - 1, r.Bottom + FR)
                    : new GridRect(r.Left + FC, r.Top + FR, r.Right + FC, r.Bottom + FR);
        }
    }

    /// <summary>True if the cell (grid coordinates) is selected; fixed cells never are.</summary>
    public bool IsCellSelected(int col, int row)
    {
        if (col < FC || row < FR || col >= _colCount || row >= _rowCount) return false;
        int sc = col - FC, sr = row - FR;
        if (!_rowSelect) return Selection.IsSelected(sc, sr);
        foreach (var r in Selection.Ranges) if (sr >= r.Top && sr <= r.Bottom) return true;
        return false;
    }

    /// <summary>Selects every non-fixed cell (with range selection).</summary>
    public void SelectAll() => Selection.SelectAll();

    /// <summary>The first non-fixed row in view; setting it scrolls that row to just below the fixed rows.</summary>
    public int TopRow
    {
        get => Math.Min(Math.Max(FR, _rowCount - 1), FR + ScrollY / RowH);
        set => ScrollY = Math.Max(0, value - FR) * RowH;
    }

    /// <summary>The first non-fixed column in view.</summary>
    public int LeftCol => Math.Max(FC, ColAtContentX(ScrollX + FixedWidth));

    /// <summary>How many non-fixed rows fit fully in view.</summary>
    public int VisibleRowCount => Math.Max(0, (Viewport.Height - FixedHeight) / RowH);

    /// <summary>The cell at a point in this control's coordinates (as in mouse events), fixed cells included, or (-1, -1).</summary>
    public GridCoord CellAt(int x, int y)
    {
        var view = Viewport;
        if (x < view.X || y < view.Y || x >= view.X + view.Width || y >= view.Y + view.Height) return new(-1, -1);
        EnsureColX();
        int vx = x - view.X, vy = y - view.Y;
        int col = vx < FixedWidth ? ColAtContentX(vx) : ColAtContentX(vx + ScrollX);
        int row = vy < FixedHeight ? vy / RowH : (vy + ScrollY) / RowH;
        if (col < 0 || col >= _colCount || row >= _rowCount) return new(-1, -1);
        return new(col, row);
    }

    /// <summary>
    /// A cell's rectangle in this control's coordinates, grid lines included, where it is drawn now (fixed cells stay
    /// put; others move with the scroll position, and may be out of view).
    /// </summary>
    public Rect CellRect(int col, int row)
    {
        EnsureColX();
        var view = Viewport;
        col = Math.Clamp(col, 0, Math.Max(0, _colCount - 1));
        int x = view.X + _colX[col] - (col >= FC ? ScrollX : 0);
        int y = view.Y + row * RowH - (row >= FR ? ScrollY : 0);
        return new Rect(x, y, _colCount == 0 ? 0 : ColW(col), RowH);
    }

    /// <summary>Scrolls the least distance that shows a cell in full beside the fixed rows and columns.</summary>
    public void ScrollCellIntoView(int col, int row)
    {
        if (col < 0 || row < 0 || col >= _colCount || row >= _rowCount) return;
        EnsureColX();
        var view = Viewport;
        int fw = FixedWidth, fh = FixedHeight;
        // Inflating the cell left/up by the fixed area makes the base keep it clear of the pinned headers.
        int x = ScrollX, w = 0, y = ScrollY, h = 0;
        if (col >= FC) { x = _colX[col] - fw; w = Math.Min(ColW(col) + fw, view.Width); }
        if (row >= FR) { y = row * RowH - fh; h = Math.Min(RowH + fh, view.Height); }
        ScrollIntoView(new Rect(x, y, w, h));
    }

    /// <summary>The selection as tab-separated text, one line per row: the bounding box of every range, with cells outside them left empty.</summary>
    public string SelectionAsText()
    {
        var ranges = SelectedRanges.ToList();
        if (ranges.Count == 0) return "";
        int l = ranges.Min(r => r.Left), t = ranges.Min(r => r.Top), rr = ranges.Max(r => r.Right), b = ranges.Max(r => r.Bottom);
        var sb = new StringBuilder();
        for (int row = t; row <= b; row++)
        {
            if (row > t) sb.Append('\n');
            for (int col = l; col <= rr; col++)
            {
                if (col > l) sb.Append('\t');
                bool inside = ranges.Count == 1;
                foreach (var r in ranges) if (inside || r.Contains(col, row)) { inside = true; break; }
                if (inside) AppendTsv(sb, GetCellText(col, row));
            }
        }
        return sb.ToString();
    }

    /// <summary>Copies <see cref="SelectionAsText"/> to the clipboard (Ctrl/⌘+C).</summary>
    public void CopyToClipboard()
    {
        if (Selection.Ranges.Count > 0) Clipboard.SetText(SelectionAsText());
    }

    static void AppendTsv(StringBuilder sb, string text)
    {
        if (text.AsSpan().IndexOfAny('\t', '\n', '\r') < 0 && !text.Contains('"')) { sb.Append(text); return; }
        sb.Append('"').Append(text.Replace("\"", "\"\"")).Append('"');   // the spreadsheet convention
    }

    // ---- Editing ----

    /// <summary>True while the in-place editor is open.</summary>
    public bool IsEditing => _editor != null;

    /// <summary>The in-place editor while it is open (a child <see cref="Edit"/> over the cell), else null.</summary>
    public Edit? InplaceEditor => _editor;

    /// <summary>Opens the in-place editor on the current cell (with <see cref="Editing"/>); false if it didn't open.</summary>
    public bool BeginEdit() => BeginEdit(null);

    bool BeginEdit(string? typed)
    {
        if (!_editing || _rowSelect || !IsEffectivelyEnabled || Col < 0 || Row < 0 || OwningForm == null) return false;
        if (_editor != null) return true;
        int col = Col, row = Row;
        var args = new GridTextEventArgs(col, row, GetEditSeed(col, row));
        GetEditText?.Invoke(this, args);
        if (!args.Accept || col != Col || row != Row) return false;   // refused, or the handler moved or resized the grid
        ScrollCellIntoView(col, row);
        _editCol = col;
        _editRow = row;
        var editor = new InplaceEdit(this) { Text = typed ?? args.Text, BorderStyle = BorderStyle.None, AutoSelect = false, IsTransient = true };
        if (col < Columns.Count) editor.Alignment = Columns[col].Alignment;
        _editor = editor;
        PlaceEditor();
        Add(editor);
        Root.PerformLayout();
        editor.Focus();
        if (typed == null) editor.SelectAll();
        else editor.Select(editor.Text.Length, 0);
        Invalidate();
        return true;
    }

    /// <summary>
    /// Closes the in-place editor. Committing raises <see cref="SetEditText"/> (a StringGrid then stores the text);
    /// if a handler clears Accept the editor stays open and this returns false.
    /// </summary>
    public bool EndEdit(bool commit)
    {
        if (_editor is not { } editor || _endingEdit || _committing) return true;   // a handler asking again from inside the commit
        if (commit)
        {
            _committing = true;
            bool accepted;
            try { accepted = CommitEditText(_editCol, _editRow, editor.Text); }
            finally { _committing = false; }
            if (!accepted) return false;
            if (_editor != editor) return true;   // a handler closed it
        }
        _endingEdit = true;
        try
        {
            bool hadFocus = editor.Focused;
            _editor = null;
            Remove(editor);
            if (hadFocus) Focus();
        }
        finally { _endingEdit = false; }
        Invalidate();
        return true;
    }

    /// <summary>Closes the editor before the grid's shape changes: commits, and drops the text if a handler refuses it.</summary>
    private protected void CloseEditor()
    {
        if (!EndEdit(commit: true)) EndEdit(commit: false);
    }

    /// <summary>The text the editor starts with, before <see cref="GetEditText"/>: "" here, the cell's text in a StringGrid.</summary>
    protected virtual string GetEditSeed(int col, int row) => "";

    /// <summary>Raises <see cref="SetEditText"/>; returns false if a handler refused the text. A StringGrid also stores it.</summary>
    protected virtual bool CommitEditText(int col, int row, string text)
    {
        var args = new GridTextEventArgs(col, row, text);
        SetEditText?.Invoke(this, args);
        return args.Accept;
    }

    /// <summary>Like <see cref="CommitEditText"/> but returns the event's args, for a subclass that stores the text.</summary>
    protected GridTextEventArgs RaiseSetEditText(int col, int row, string text)
    {
        var args = new GridTextEventArgs(col, row, text);
        SetEditText?.Invoke(this, args);
        return args;
    }

    /// <summary>
    /// The text of a cell, for copying and (in a StringGrid) drawing: a header cell's <see cref="GridColumn.Title"/>,
    /// otherwise what <see cref="GetEditText"/> supplies (a DrawGrid has no text of its own).
    /// </summary>
    protected virtual string GetCellText(int col, int row)
    {
        if (HeaderTitle(col, row) is { } title) return title;
        if (col < FC || row < FR || GetEditText == null) return "";
        var args = new GridTextEventArgs(col, row, "");
        GetEditText(this, args);
        return args.Text;
    }

    /// <summary>The title shown in a header cell from <see cref="Columns"/>, or null.</summary>
    protected string? HeaderTitle(int col, int row) => row == 0 && _fixedRows > 0 && col < Columns.Count ? Columns[col].Title : null;

    /// <summary>The text alignment of a column (from <see cref="Columns"/>, else left).</summary>
    protected TextAlign ColumnAlignment(int col) => col < Columns.Count ? Columns[col].Alignment : TextAlign.Left;

    void PlaceEditor()
    {
        if (_editor is not { } editor) return;
        var r = CellInner(CellRect(_editCol, _editRow));
        editor.SetBounds(r.X, r.Y, r.Width, r.Height);
    }

    /// <summary>The in-place editor's key: true if the grid took it.</summary>
    bool EditorKey(KeyEventArgs e)
    {
        var mods = e.Modifiers;
        if (mods == KeyModifiers.None && e.Key == Key.Enter) { EndEdit(commit: true); return true; }
        if (mods == KeyModifiers.None && e.Key == Key.Escape) { EndEdit(commit: false); return true; }
        bool nav = mods == KeyModifiers.None && e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown;
        bool tab = e.Key == Key.Tab && _wantTabs && (mods & ~KeyModifiers.Shift) == 0;
        if (!nav && !tab) return false;
        if (EndEdit(commit: true)) HandleKey(e);
        return true;
    }

    void EditorLostFocus(InplaceEdit editor)
    {
        if (_editor != editor || _endingEdit) return;
        if (!EndEdit(commit: true)) EndEdit(commit: false);   // focus left: a refused value is dropped
    }

    /// <summary>The in-place editor: an <see cref="Edit"/> that hands navigation keys back to the grid.</summary>
    sealed class InplaceEdit(CustomGrid grid) : Edit
    {
        public override bool AcceptsTab => grid._wantTabs;

        protected internal override void OnKeyDown(KeyEventArgs e)
        {
            if (grid.EditorKey(e)) { e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        protected internal override void OnLostFocus()
        {
            base.OnLostFocus();
            grid.EditorLostFocus(this);
        }
    }

    // ---- Subclass hooks ----

    /// <summary>Asks <see cref="SelectCell"/> whether the user may move to a cell (grid coordinates).</summary>
    protected virtual bool CanSelectCell(int col, int row)
    {
        if (SelectCell == null) return true;
        var args = new SelectCellEventArgs(col, row);
        SelectCell(this, args);
        return args.CanSelect;
    }

    /// <summary>
    /// Draws a cell's content after its background: here the header titles from <see cref="Columns"/>, then the
    /// <see cref="DrawCell"/> event. <paramref name="rect"/> is the cell inside its grid lines.
    /// </summary>
    protected virtual void DrawCellContent(Canvas canvas, int col, int row, Rect rect, GridDrawState state)
    {
        if (GetPaintText(col, row) is { Length: > 0 } text)
            DrawCellText(canvas, text, col, row, rect, state);
        DrawCell?.Invoke(this, canvas, col, row, rect, state);
    }

    /// <summary>The text the grid draws in a cell before <see cref="DrawCell"/>: header titles here, every cell's text in a StringGrid.</summary>
    protected virtual string? GetPaintText(int col, int row) => HeaderTitle(col, row);

    /// <summary>Draws text in a cell the way the grid does: padded, aligned per column, shortened with "…", bold in the header row.</summary>
    protected void DrawCellText(Canvas canvas, string text, int col, int row, Rect rect, GridDrawState state)
    {
        if (text.Length == 0) return;
        var t = canvas.Theme;
        int pad = canvas.Px(4);
        int right = col == _sortColumn && row == 0 && _fixedRows > 0 ? canvas.Px(14) : 0;   // room for the sort arrow
        var area = new Rect(rect.X + pad, rect.Y, Math.Max(0, rect.Width - 2 * pad - right), rect.Height);
        var font = row == 0 && FR > 0 ? EffectiveFont with { Bold = true } : EffectiveFont;
        canvas.DrawText(text, font, IsEffectivelyEnabled ? t.Text : t.TextDisabled, area, ColumnAlignment(col), VerticalAlign.Center, ellipsis: true);
    }

    /// <summary>Called when a header click under <see cref="AutoSort"/> changed the sort; a StringGrid sorts its rows here.</summary>
    protected virtual void OnSortRequested(int col, bool descending) { }

    // ---- Internals: structure ----

    int FC => Math.Min(_fixedCols, _colCount);
    int FR => Math.Min(_fixedRows, _rowCount);
    int RowH => Math.Max(1, Px(_defaultRowHeight));
    int FixedWidth { get { EnsureColX(); return _colX[FC]; } }
    int FixedHeight => FR * RowH;
    bool HasCells => _colCount > FC && _rowCount > FR;

    void SetCounts(int cols, int rows, bool keepSort = false)
    {
        if (cols == _colCount && rows == _rowCount) return;
        CloseEditor();
        int oldCols = _colCount, oldRows = _rowCount;
        _colCount = cols;
        _rowCount = rows;
        if (cols < oldCols && _sortColumn >= cols && !keepSort) _sortColumn = -1;
        if (_colWidths.Count > cols) _colWidths.RemoveRange(cols, _colWidths.Count - cols);
        CountsChanged(oldCols, oldRows);
        Structure();
    }

    /// <summary>Called after <see cref="ColCount"/> or <see cref="RowCount"/> changed, before the selection and scroll range follow.</summary>
    protected virtual void CountsChanged(int oldColCount, int oldRowCount) { }

    void Structure()
    {
        WidthsChanged();         // first, so the selection scrolls within the new size
        UpdateSelectionSize();
    }

    /// <summary>Keeps the selection on the same grid cells when the fixed count changed by <paramref name="delta"/> (selection coordinates leave the fixed ones out).</summary>
    void ShiftSelection(bool cols, int delta)
    {
        if (delta > 0) { if (cols) Selection.RemoveColumns(0, delta); else Selection.RemoveRows(0, delta); }
        else if (delta < 0) { if (cols) Selection.InsertColumns(0, -delta); else Selection.InsertRows(0, -delta); }
    }

    void UpdateSelectionSize() => Selection.SetSize(Math.Max(0, _colCount - FC), Math.Max(0, _rowCount - FR));

    void UpdateMode() => Selection.Mode = _multiSelect ? CellSelectionMode.MultiRange : _rangeSelect ? CellSelectionMode.Range : CellSelectionMode.Single;

    internal void ColumnsChanged(bool widths, bool structure = false, bool adopting = false)
    {
        if (structure && Columns.Count > 0 && Columns.Count != _colCount) { SetCounts(Columns.Count, _rowCount, keepSort: adopting); return; }
        if (widths) WidthsChanged();
        else Invalidate();
    }

    internal int GetColWidth(int col)
    {
        if (col >= 0 && col < Columns.Count) return Columns[col].Width > 0 ? Columns[col].Width : _defaultColWidth;
        return col >= 0 && col < _colWidths.Count && _colWidths[col] > 0 ? _colWidths[col] : _defaultColWidth;
    }

    internal void SetColWidth(int col, int width)
    {
        if (col < 0 || col >= _colCount) throw new ArgumentOutOfRangeException(nameof(col));
        width = Math.Max(1, width);
        if (col < Columns.Count) { Columns[col].Width = width; return; }
        while (_colWidths.Count <= col) _colWidths.Add(0);
        _colWidths[col] = width;
        WidthsChanged();
    }

    int MinColWidth(int col) => col < Columns.Count && Columns[col].MinWidth > 0 ? Columns[col].MinWidth : 8;

    void WidthsChanged()
    {
        _colXDirty = true;
        Remeasure();
        PlaceEditor();
    }

    void Remeasure()
    {
        EnsureColX();
        ContentWidth = _colX[_colCount];
        ContentHeight = _rowCount * RowH;
    }

    protected override void MeasureContent()
    {
        EnsureColX();
        ContentWidth = _colX[_colCount];
        ContentHeight = _rowCount * RowH;
    }

    void EnsureColX()
    {
        if (!_colXDirty && _colXScale == UiScale && _colX.Length == _colCount + 1) return;
        var x = new int[_colCount + 1];
        for (int c = 0; c < _colCount; c++) x[c + 1] = x[c] + Px(GetColWidth(c));
        _colX = x;
        _colXDirty = false;
        _colXScale = UiScale;
    }

    int ColW(int col) { EnsureColX(); return _colX[col + 1] - _colX[col]; }

    /// <summary>The column containing content x, or ColCount if it is past the last one (-1 before the first).</summary>
    int ColAtContentX(int x)
    {
        EnsureColX();
        if (x < 0) return -1;
        if (x >= _colX[_colCount]) return _colCount;
        int lo = 0, hi = _colCount - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_colX[mid] <= x) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    Rect CellInner(Rect r)
    {
        int line = _gridLines ? Px(1) : 0;
        return new Rect(r.X, r.Y, Math.Max(0, r.Width - line), Math.Max(0, r.Height - line));
    }

    /// <summary>The scrolling (non-fixed) part of the viewport, in this control's coordinates.</summary>
    Rect BodyRect
    {
        get
        {
            var v = Viewport;
            int fw = Math.Min(FixedWidth, v.Width), fh = Math.Min(FixedHeight, v.Height);
            return new Rect(v.X + fw, v.Y + fh, v.Width - fw, v.Height - fh);
        }
    }

    // The in-place editor is a child clipped to the scrolling cells, so it never paints over the headers.
    protected internal override Rect ChildClipRect => BodyRect;

    protected override int LineStepY => RowH;
    protected override int PageStepY => Math.Max(RowH, Viewport.Height - FixedHeight);

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

    void SelectionChanged()
    {
        var current = Selection.Current;
        if (current != _lastCurrent)
        {
            _lastCurrent = current;
            if (_editor != null && (current.Col + FC != _editCol || current.Row + FR != _editRow) && !EndEdit(commit: true))
                EndEdit(commit: false);
            if (!_noAutoScroll && current.Col >= 0) ScrollCellIntoView(current.Col + FC, current.Row + FR);
        }
        Invalidate();
        Change?.Invoke(this);
    }

    // ---- Input ----

    /// <summary>The column whose right border is under the pointer in a header row (for resizing), or -1.</summary>
    int ResizeBorderAt(int x, int y)
    {
        var view = Viewport;
        if (FR == 0 || y < view.Y || y >= view.Y + Math.Min(FixedHeight, view.Height)) return -1;
        int tol = Math.Max(2, Px(3));
        int best = -1, bestDist = tol + 1;
        void Try(int c)
        {
            var r = CellRect(c, 0);
            int edge = r.X + r.Width, d = Math.Abs(edge - x);
            if (d <= tol && d < bestDist && edge <= view.X + view.Width) { best = c; bestDist = d; }
        }
        for (int c = 0; c < FC; c++) Try(c);
        int fw = FixedWidth;
        for (int c = LeftCol; c < _colCount; c++)
        {
            var r = CellRect(c, 0);
            if (r.X > view.X + view.Width) break;
            if (r.X + r.Width > view.X + fw) Try(c);
        }
        return best;
    }

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);   // the scroll bars take their own presses
        if (IsScrolling || IsOverScrollBar(e.X, e.Y)) return;
        if (e.Button == MouseButton.Left && _colSizing && ResizeBorderAt(e.X, e.Y) is var b and >= 0)
        {
            // Before hit testing: the last column's border is also just past the last cell.
            _resizeCol = b;
            _resizeStartX = e.X;
            _resizeStartWidth = GetColWidth(b);
            return;
        }
        var cell = CellAt(e.X, e.Y);
        if (cell.Col < 0) return;
        if (e.Button != MouseButton.Left)
        {
            // Right-click selects too, before a context menu opens, but keeps a selection it lands in.
            if (cell.Col >= FC && cell.Row >= FR && !IsCellSelected(cell.Col, cell.Row) && CanSelectCell(cell.Col, cell.Row))
                Selection.Click(cell.Col - FC, cell.Row - FR);
            return;
        }
        bool toggle = e.Modifiers.HasFlag(Shortcut.CommandModifier), extend = e.Modifiers.HasFlag(KeyModifiers.Shift);
        if (!HasCells) return;
        int lastCol = _colCount - FC - 1, lastRow = _rowCount - FR - 1;
        if (cell.Row < FR && cell.Col < FC)
        {
            SelectAll();   // the corner
            return;
        }
        if (cell.Row < FR)
        {
            if (_autoSort)
            {
                if (_sortColumn == cell.Col) _sortDescending = !_sortDescending;
                else { _sortColumn = cell.Col; _sortDescending = false; }
                Invalidate();
                OnSortRequested(cell.Col, _sortDescending);
            }
            else if (Selection.Mode != CellSelectionMode.Single)
            {
                int sc = cell.Col - FC, from = extend ? Selection.Anchor.Col : sc;
                SelectQuietly(new GridCoord(from, lastRow), new GridCoord(sc, 0), toggle && !extend);
            }
            ColumnClick?.Invoke(this, cell.Col);
            return;
        }
        if (cell.Col < FC)
        {
            if (Selection.Mode != CellSelectionMode.Single)
            {
                int sr = cell.Row - FR, from = extend ? Selection.Anchor.Row : sr;
                SelectQuietly(new GridCoord(lastCol, from), new GridCoord(0, sr), toggle && !extend);
            }
            return;
        }
        if (!CanSelectCell(cell.Col, cell.Row)) return;
        Selection.Click(cell.Col - FC, cell.Row - FR, toggle, extend);
        _mouseSelecting = true;
        if (e.Clicks == 2)
        {
            DoubleClick?.Invoke(this);
            BeginEdit();
        }
    }

    /// <summary>Selects a whole row or column from a header click without scrolling to its far end.</summary>
    void SelectQuietly(GridCoord anchor, GridCoord current, bool add)
    {
        _noAutoScroll = true;
        try { Selection.SelectRange(anchor, current, add); }
        finally { _noAutoScroll = false; }
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_resizeCol >= 0)
        {
            int w = _resizeStartWidth + (int)Math.Round((e.X - _resizeStartX) / UiScale);
            SetColWidth(_resizeCol, Math.Max(MinColWidth(_resizeCol), w));
            return;
        }
        if (IsScrolling) return;
        if (_mouseSelecting)
        {
            var body = BodyRect;
            if (body.Width <= 0 || body.Height <= 0) return;
            var cell = CellAt(Math.Clamp(e.X, body.X, body.X + body.Width - 1), Math.Clamp(e.Y, body.Y, body.Y + body.Height - 1));
            if (cell.Col < FC || cell.Row < FR) return;
            var target = new GridCoord(cell.Col - FC, cell.Row - FR);
            if (target != Selection.Current && CanSelectCell(cell.Col, cell.Row)) Selection.DragTo(target.Col, target.Row);
            return;
        }
        UpdateSizingCursor(_colSizing && ResizeBorderAt(e.X, e.Y) >= 0);
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

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _mouseSelecting = false;
        _resizeCol = -1;
    }

    protected override bool HandleKey(KeyEventArgs e)
    {
        if (!HasCells) return base.HandleKey(e);
        bool shift = e.Shift, command = e.Command;
        if ((e.Modifiers & ~(KeyModifiers.Shift | Shortcut.CommandModifier)) != 0) return false;
        if (command && !shift && e.Key == Key.C) { CopyToClipboard(); return true; }
        if (command && !shift && e.Key == Key.A) { SelectAll(); return true; }
        if (!command && !shift && e.Key is Key.Enter or Key.F2) return BeginEdit();
        if (e.Key == Key.Tab && _wantTabs && !command) { Tab(forward: !shift); return true; }

        var cur = Selection.Current;
        int lastCol = _colCount - FC - 1, lastRow = _rowCount - FR - 1;
        int page = Math.Max(1, VisibleRowCount);
        (int C, int R)? target = e.Key switch
        {
            Key.Up => (cur.Col, cur.Row - 1),
            Key.Down => (cur.Col, cur.Row + 1),
            Key.Left => (cur.Col - 1, cur.Row),
            Key.Right => (cur.Col + 1, cur.Row),
            Key.Home => command ? (0, 0) : (0, cur.Row),
            Key.End => command ? (lastCol, lastRow) : (lastCol, cur.Row),
            Key.PageUp => (cur.Col, cur.Row - page),
            Key.PageDown => (cur.Col, cur.Row + page),
            _ => null,
        };
        if (target is not { } t) return false;
        MoveCurrent(Math.Clamp(t.C, 0, lastCol), Math.Clamp(t.R, 0, lastRow), shift);
        ScrollCellIntoView(Col, Row);   // also when it didn't move (scrolled away with the wheel)
        return true;
    }

    void MoveCurrent(int sc, int sr, bool extend)
    {
        if (new GridCoord(sc, sr) != Selection.Current && !CanSelectCell(sc + FC, sr + FR)) return;
        Selection.MoveTo(sc, sr, extend);
    }

    void Tab(bool forward)
    {
        var cur = Selection.Current;
        int cols = _colCount - FC, rows = _rowCount - FR;
        int index = cur.Row * cols + cur.Col + (forward ? 1 : -1);
        if (index < 0 || index >= cols * rows) return;   // the first/last cell: stay
        MoveCurrent(index % cols, index / cols, extend: false);
    }

    /// <summary>Typing a character starts editing the current cell with it (with <see cref="Editing"/>).</summary>
    protected internal override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || _editor != null || e.Text.Length == 0 || char.IsControl(e.Text[0])) return;
        if (BeginEdit(e.Text)) e.Handled = true;
    }

    // ---- Painting ----

    protected override Color GetBackgroundColor(Theme theme) =>
        Color ?? (IsEffectivelyEnabled ? theme.InputBackground : theme.FormBackground);

    protected override Color GetBorderColor(Theme theme) => HasFocusWithin ? theme.Accent : theme.InputBorder;

    /// <summary>The grid or its in-place editor has the keyboard focus.</summary>
    bool HasFocusWithin => Focused || _editor is { Focused: true };

    protected override void PaintContent(Canvas canvas, Rect visible)
    {
        // Cells are drawn in this control's coordinates, like CellRect and the rect DrawCell receives.
        var view = Viewport;
        canvas.Translate(visible.X - view.X, visible.Y - view.Y);
        EnsureColX();
        if (_colCount == 0 || _rowCount == 0) return;
        int fc = FC, fr = FR, rh = RowH;
        int fw = Math.Min(FixedWidth, view.Width), fh = Math.Min(FixedHeight, view.Height);

        // The non-fixed rows and columns in view.
        int r0 = Math.Max(fr, (ScrollY + fh) / rh), r1 = Math.Min(_rowCount - 1, (ScrollY + view.Height - 1) / rh);
        int c0 = Math.Max(fc, ColAtContentX(ScrollX + fw)), c1 = Math.Min(_colCount - 1, ColAtContentX(ScrollX + view.Width - 1));

        var body = new Rect(view.X + fw, view.Y + fh, view.Width - fw, view.Height - fh);
        PaintBlock(canvas, body, c0, c1, r0, r1);
        PaintBlock(canvas, new Rect(view.X + fw, view.Y, view.Width - fw, fh), c0, c1, 0, fr - 1);
        PaintBlock(canvas, new Rect(view.X, view.Y + fh, fw, view.Height - fh), 0, fc - 1, r0, r1);
        PaintBlock(canvas, new Rect(view.X, view.Y, fw, fh), 0, fc - 1, 0, fr - 1);

        // The cell cursor: the current cell (or row) outlined while the grid has the focus.
        if (HasFocusWithin && _editor == null && HasCells && Col >= 0 && body.Width > 0 && body.Height > 0)
        {
            var r = CellInner(CellRect(Col, Row));
            if (_rowSelect)
            {
                var first = CellRect(fc, Row);
                var last = CellRect(_colCount - 1, Row);
                r = new Rect(first.X, r.Y, last.X + last.Width - first.X - (_gridLines ? canvas.Px(1) : 0), r.Height);
            }
            canvas.Save();
            canvas.ClipRect(body);
            canvas.DrawBorder(r, canvas.Theme.FocusRing, canvas.Px(2));
            canvas.Restore();
        }
    }

    void PaintBlock(Canvas canvas, Rect clip, int c0, int c1, int r0, int r1)
    {
        if (clip.Width <= 0 || clip.Height <= 0 || c0 > c1 || r0 > r1) return;
        canvas.Save();
        canvas.ClipRect(clip);
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                PaintCell(canvas, c, r);
        canvas.Restore();
    }

    void PaintCell(Canvas canvas, int col, int row)
    {
        var t = canvas.Theme;
        var rect = CellRect(col, row);
        bool isFixed = col < FC || row < FR;
        var state = isFixed ? GridDrawState.Fixed : GridDrawState.None;
        if (!isFixed && IsCellSelected(col, row)) state |= GridDrawState.Selected;
        if (!isFixed && HasFocusWithin && col == Col && row == Row) state |= GridDrawState.Focused;

        var inner = CellInner(rect);
        if (isFixed) canvas.FillRect(rect, t.PanelBackground);
        else if (state.HasFlag(GridDrawState.Selected)) canvas.FillRect(rect, HasFocusWithin ? t.Selection : t.SelectionInactive);
        if (_gridLines)
        {
            int line = canvas.Px(1);
            canvas.FillRect(new Rect(rect.X + rect.Width - line, rect.Y, line, rect.Height), t.PanelBorder);
            canvas.FillRect(new Rect(rect.X, rect.Y + rect.Height - line, rect.Width, line), t.PanelBorder);
        }
        if (row == 0 && _fixedRows > 0 && col == _sortColumn) PaintSortArrow(canvas, inner);
        if (_editor != null && col == _editCol && row == _editRow) return;   // the editor covers it
        DrawCellContent(canvas, col, row, inner, state);
    }

    void PaintSortArrow(Canvas canvas, Rect cell)
    {
        float w = canvas.Px(8), h = canvas.Px(4);
        float x = cell.X + cell.Width - canvas.Px(6) - w, y = cell.Y + (cell.Height - h) / 2f;
        var color = canvas.Theme.TextMuted;
        PointF[] arrow = _sortDescending
            ? [new(x, y), new(x + w, y), new(x + w / 2, y + h)]
            : [new(x, y + h), new(x + w, y + h), new(x + w / 2, y)];
        canvas.FillPolygon(arrow, color);
    }
}

/// <summary>
/// A grid whose cells the application paints (Delphi's TDrawGrid): handle <see cref="CustomGrid.DrawCell"/>. The grid
/// draws the backgrounds, selection, grid lines, header titles from <see cref="CustomGrid.Columns"/>, the sort arrow and
/// the cell cursor; editing and copying get their text from <see cref="CustomGrid.GetEditText"/>.
/// </summary>
public class DrawGrid : CustomGrid
{
}

/// <summary>
/// A grid of text cells (Delphi's TStringGrid): <see cref="Cells"/>[col, row] holds the text in memory, or, in virtual
/// mode, a <see cref="GetCell"/> handler supplies each cell's text as it is painted or copied, so a grid of 100 000
/// rows needs no per-row storage.
/// </summary>
/// <remarks>
/// With <see cref="CustomGrid.AutoSort"/> a header click sorts the non-fixed rows by that column (stable; numbers by
/// value, otherwise text ignoring case, or a <see cref="Compare"/> handler); in virtual mode the grid only updates the
/// sort arrow and the ColumnClick handler sorts the data. A committed edit is stored in <see cref="Cells"/> unless a
/// SetEditText handler refuses it; in virtual mode the handler stores it instead.
/// </remarks>
public class StringGrid : CustomGrid
{
    readonly List<string?[]?> _rows = [];   // only as long as the last row written; null rows are empty

    public StringGrid() => Cells = new StringGridCells(this);

    /// <summary>The text of each cell (Delphi's <c>Cells[ACol, ARow]</c>), fixed cells included.</summary>
    public StringGridCells Cells { get; }

    /// <summary>Virtual mode: supplies the text of a cell when it is drawn or copied (pre-set to its <see cref="Cells"/> text).</summary>
    [Published] public event GridTextEvent? GetCell;

    /// <summary>Compares two rows while sorting; the default compares numbers by value, otherwise text ignoring case.</summary>
    [Published] public event GridCompareEvent? Compare;

    /// <summary>True when a <see cref="GetCell"/> handler supplies the text.</summary>
    public bool IsVirtual => GetCell != null;

    internal string GetStored(int col, int row)
    {
        CheckCell(col, row);
        return row < _rows.Count && _rows[row] is { } cells && col < cells.Length ? cells[col] ?? "" : "";
    }

    internal void SetStored(int col, int row, string? text)
    {
        CheckCell(col, row);
        while (_rows.Count <= row) _rows.Add(null);
        var cells = _rows[row];
        if (cells == null || cells.Length <= col)
        {
            if (string.IsNullOrEmpty(text)) return;   // nothing to clear
            Array.Resize(ref cells, Math.Max(ColCount, col + 1));
            _rows[row] = cells;
        }
        cells[col] = text;
        Invalidate();
    }

    void CheckCell(int col, int row)
    {
        if (col < 0 || col >= ColCount) throw new ArgumentOutOfRangeException(nameof(col));
        if (row < 0 || row >= RowCount) throw new ArgumentOutOfRangeException(nameof(row));
    }

    protected override void CountsChanged(int oldColCount, int oldRowCount)
    {
        if (_rows.Count > RowCount) _rows.RemoveRange(RowCount, _rows.Count - RowCount);
        if (ColCount < oldColCount)
            for (int r = 0; r < _rows.Count; r++)
                if (_rows[r] is { } cells && cells.Length > ColCount) _rows[r] = cells[..ColCount];
    }

    protected override string GetCellText(int col, int row)
    {
        if (HeaderTitle(col, row) is { } title) return title;
        var text = GetStored(col, row);
        if (GetCell == null) return text;
        var args = new GridTextEventArgs(col, row, text);
        GetCell(this, args);
        return args.Text;
    }

    protected override string GetEditSeed(int col, int row) => GetCellText(col, row);

    protected override bool CommitEditText(int col, int row, string text)
    {
        var args = RaiseSetEditText(col, row, text);
        if (!args.Accept) return false;
        if (!IsVirtual && col < ColCount && row < RowCount) SetStored(col, row, args.Text);   // a handler may have shrunk the grid
        return true;
    }

    protected override string? GetPaintText(int col, int row) => GetCellText(col, row);

    protected override void OnSortRequested(int col, bool descending)
    {
        if (!IsVirtual) SortRows(col, descending);
    }

    /// <summary>
    /// Sorts the non-fixed rows of <see cref="Cells"/> by a column, stably (equal rows keep their order). The selection
    /// stays on the same positions. Sets <see cref="CustomGrid.SortColumn"/> and <see cref="CustomGrid.SortDescending"/>.
    /// </summary>
    public void SortRows(int col, bool descending = false)
    {
        if (col < 0 || col >= ColCount) throw new ArgumentOutOfRangeException(nameof(col));
        CloseEditor();   // the rows move under it
        SortColumn = col;
        SortDescending = descending;
        int first = Math.Min(FixedRows, RowCount), count = RowCount - first;
        if (count < 2) return;
        while (_rows.Count < RowCount) _rows.Add(null);
        var keys = new (string Text, int Row)[count];
        for (int i = 0; i < count; i++) keys[i] = (GetStored(col, first + i), first + i);
        int Cmp((string Text, int Row) a, (string Text, int Row) b)
        {
            int result = DefaultCompare(a.Text, b.Text);
            if (Compare != null)
            {
                var args = new GridCompareEventArgs(col, a.Row, b.Row, a.Text, b.Text, result);
                Compare(this, args);
                result = args.Result;
            }
            if (descending) result = result < 0 ? 1 : result > 0 ? -1 : 0;   // not -result: that overflows for int.MinValue
            return result != 0 ? result : a.Row.CompareTo(b.Row);   // stable
        }
        Array.Sort(keys, Cmp);
        var sorted = new string?[]?[count];
        for (int i = 0; i < count; i++) sorted[i] = _rows[keys[i].Row];
        for (int i = 0; i < count; i++) _rows[first + i] = sorted[i];
        Invalidate();
    }

    /// <summary>Numbers (in the current or invariant culture) by value and before text; text by the current culture, ignoring case.</summary>
    static int DefaultCompare(string a, string b)
    {
        bool na = TryNumber(a, out var x), nb = TryNumber(b, out var y);
        if (na && nb) return x.CompareTo(y);
        if (na != nb) return na ? -1 : 1;
        return string.Compare(a, b, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
    }

    // "NaN", "Infinity" (a name, a word) and overflowing digits stay text.
    static bool TryNumber(string s, out double value) =>
        (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        && double.IsFinite(value);
}

/// <summary>A <see cref="StringGrid"/>'s cell texts by column and row (Delphi's <c>Cells[ACol, ARow]</c>).</summary>
public sealed class StringGridCells
{
    readonly StringGrid _grid;
    internal StringGridCells(StringGrid grid) => _grid = grid;

    /// <summary>The stored text ("" for a cell never set); throws for a cell outside the grid.</summary>
    public string this[int col, int row] { get => _grid.GetStored(col, row); set => _grid.SetStored(col, row, value); }
}
