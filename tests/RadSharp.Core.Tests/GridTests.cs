using RadSharp.Drawing;
using RadSharp.Text;

namespace RadSharp.Tests;

/// <summary>DrawGrid and StringGrid (M14 item 6) through real input: selection, keys, in-place editing, sizing, sorting, virtual mode.</summary>
public class GridTests
{
    const KeyModifiers Ctrl = KeyModifiers.Ctrl, Shift = KeyModifiers.Shift;

    static GridRect G(int l, int t, int r, int b) => new(l, t, r, b);

    /// <summary>
    /// 5 columns of 60 px (one fixed) × 20 rows of 20 px (one fixed) in a 260 × 160 inner area: both bars show, so the
    /// viewport is 248 × 148, the fixed column is 60 wide and the header 20 high, and 6 body rows fit in full.
    /// </summary>
    static (Form Form, T Grid) Setup<T>(double scale = 1, int rows = 20) where T : CustomGrid, new()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form { Scale = scale };
        f.SetBounds(0, 0, 600, 400);
        var g = new T { ColCount = 5, RowCount = rows, DefaultColWidth = 60, DefaultRowHeight = 20 };
        g.SetBounds(10, 10, 262, 162);
        f.Add(g);
        f.PerformLayout();
        return (f, g);
    }

    static (Form Form, StringGrid Grid) Setup(double scale = 1, int rows = 20) => Setup<StringGrid>(scale, rows);

    /// <summary>A point inside a cell, in form coordinates.</summary>
    static (int X, int Y) At(CustomGrid g, int col, int row)
    {
        var r = g.CellRect(col, row);
        return (g.AbsoluteLeft + r.X + 5, g.AbsoluteTop + r.Y + 5);
    }

    /// <summary>Clicks a cell, first scrolling a non-fixed one into view (a click lands only on what is visible).</summary>
    static void ClickCell(Form f, CustomGrid g, int col, int row, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (col >= g.FixedCols && row >= g.FixedRows) g.ScrollCellIntoView(col, row);
        var (x, y) = At(g, col, row);
        f.Simulate.Click(x, y, MouseButton.Left, modifiers);
    }

    sealed class TextProbe : StringGrid
    {
        public string Text(int col, int row) => GetCellText(col, row);
    }

    // ---- Geometry ----

    [Fact]
    public void Fixed_rows_and_columns_stay_pinned_while_the_cells_scroll()
    {
        var (f, g) = Setup();
        var view = g.Viewport;
        Assert.Equal(new Rect(1, 1, 248, 148), view);
        Assert.Equal(6, g.VisibleRowCount);
        Assert.Equal(new Rect(61, 21, 60, 20), g.CellRect(1, 1));

        g.ScrollY = 100;
        g.ScrollX = 40;
        Assert.Equal(new Rect(1, 1, 60, 20), g.CellRect(0, 0));          // the corner never moves
        Assert.Equal(1, g.CellRect(0, 9).X);                             // the row header only moves vertically
        Assert.Equal(1 + 9 * 20 - 100, g.CellRect(0, 9).Y);
        Assert.Equal(1, g.CellRect(3, 0).Y);                             // the column header only horizontally
        Assert.Equal(1 + 180 - 40, g.CellRect(3, 0).X);
        Assert.Equal(6, g.TopRow);                                       // content y 120 is just below the header
        Assert.Equal(1, g.LeftCol);

        // Hit testing follows: the header row is row 0 wherever the cells scrolled to.
        Assert.Equal(new GridCoord(3, 0), g.CellAt(g.CellRect(3, 0).X + 2, 5));
        Assert.Equal(new GridCoord(0, 9), g.CellAt(5, g.CellRect(0, 9).Y + 2));
        Assert.Equal(new GridCoord(-1, -1), g.CellAt(view.X + view.Width + 2, 30));   // on the scroll bar

        // Paging leaves the header out: one page is the body's height.
        g.ScrollY = 0;
        g.Focus();
        g.FocusCell(1, 1);
        f.Simulate.Key(Key.PageDown);
        Assert.Equal(7, g.Row);
    }

    [Fact]
    public void Painting_draws_each_visible_cell_once_with_its_state()
    {
        var (f, g) = Setup<DrawGrid>();
        var log = new List<(int Col, int Row, Rect Rect, GridDrawState State)>();
        g.DrawCell += (_, _, col, row, rect, state) => log.Add((col, row, rect, state));
        g.Focus();
        g.Selection.SelectRange(new GridCoord(0, 0), new GridCoord(1, 1));   // grid (1,1)–(2,2), current (2,2)

        FormRenderer.RenderToBitmap(f).Dispose();
        // Rows 0–7 (the header and the body rows in 148 px) × columns 0–4 (content x < 248).
        Assert.Equal(40, log.Count);
        Assert.Equal(40, log.Select(e => (e.Col, e.Row)).Distinct().Count());
        Assert.Equal(GridDrawState.Fixed, log.Single(e => e.Col == 0 && e.Row == 0).State);
        Assert.Equal(GridDrawState.Selected, log.Single(e => e.Col == 1 && e.Row == 1).State);
        Assert.Equal(GridDrawState.Selected | GridDrawState.Focused, log.Single(e => e.Col == 2 && e.Row == 2).State);
        Assert.Equal(GridDrawState.None, log.Single(e => e.Col == 3 && e.Row == 3).State);
        var cell = g.CellRect(2, 3);
        Assert.Equal(new Rect(cell.X, cell.Y, cell.Width - 1, cell.Height - 1), log.Single(e => e.Col == 2 && e.Row == 3).Rect);   // inside the grid lines

        g.GridLines = false;
        log.Clear();
        FormRenderer.RenderToBitmap(f).Dispose();
        Assert.Equal(cell, log.Single(e => e.Col == 2 && e.Row == 3).Rect);
    }

    [Fact]
    public void Sizes_scale_with_the_form_while_widths_stay_at_100_percent()
    {
        var (f, g) = Setup(scale: 1.5);
        Assert.Equal(new Rect(g.Viewport.X + 90, g.Viewport.Y + 30, 90, 30), g.CellRect(1, 1));
        Assert.Equal(60, g.ColWidths[1]);
        Assert.Equal(5 * 90, g.ContentWidth);
        Assert.Equal(20 * 30, g.ContentHeight);
    }

    // ---- Selection ----

    [Fact]
    public void Clicks_drags_and_modifiers_select_cells_and_ranges()
    {
        var (f, g) = Setup();
        int changes = 0;
        g.Change += _ => changes++;

        ClickCell(f, g, 2, 3);
        Assert.Equal((2, 3), (g.Col, g.Row));
        Assert.Equal(new GridCoord(1, 2), g.Selection.Current);          // the model leaves the fixed cells out
        Assert.Equal([G(2, 3, 2, 3)], g.SelectedRanges);
        ClickCell(f, g, 3, 5, Shift);
        Assert.Equal([G(2, 3, 3, 5)], g.SelectedRanges);
        Assert.Equal((3, 5), (g.Col, g.Row));
        Assert.True(g.IsCellSelected(2, 4));
        Assert.False(g.IsCellSelected(0, 4));                           // fixed cells never are

        var (x1, y1) = At(g, 1, 1);
        var (x2, y2) = At(g, 3, 2);
        f.Simulate.Drag(x1, y1, x2, y2);
        Assert.Equal([G(1, 1, 3, 2)], g.SelectedRanges);
        Assert.Equal((3, 2), (g.Col, g.Row));

        ClickCell(f, g, 4, 4, Ctrl);                                    // without MultiSelect: a plain click
        Assert.Equal([G(4, 4, 4, 4)], g.SelectedRanges);
        g.MultiSelect = true;
        ClickCell(f, g, 1, 1);
        ClickCell(f, g, 4, 4, Ctrl);
        ClickCell(f, g, 4, 6, Shift);                                   // Shift changes only the last range
        Assert.Equal([G(1, 1, 1, 1), G(4, 4, 4, 6)], g.SelectedRanges);

        g.MultiSelect = false;
        g.RangeSelect = false;
        ClickCell(f, g, 2, 2);
        ClickCell(f, g, 3, 3, Shift);                                   // single-cell selection
        Assert.Equal([G(3, 3, 3, 3)], g.SelectedRanges);
        Assert.True(changes >= 6);
    }

    [Fact]
    public void Header_clicks_select_a_column_a_row_or_everything_without_scrolling()
    {
        var (f, g) = Setup();
        var clicked = new List<int>();
        g.ColumnClick += (_, c) => clicked.Add(c);
        g.ScrollY = 100;

        ClickCell(f, g, 3, 0);
        Assert.Equal([G(3, 1, 3, 19)], g.SelectedRanges);
        Assert.Equal((3, 1), (g.Col, g.Row));
        Assert.Equal(100, g.ScrollY);                                   // no jump to the column's end
        Assert.Equal([3], clicked);
        ClickCell(f, g, 4, 0, Shift);                                   // spans columns from the anchor
        Assert.Equal([G(3, 1, 4, 19)], g.SelectedRanges);

        ClickCell(f, g, 0, 8);                                          // a row header
        Assert.Equal([G(1, 8, 4, 8)], g.SelectedRanges);
        ClickCell(f, g, 0, 10, Shift);
        Assert.Equal([G(1, 8, 4, 10)], g.SelectedRanges);
        ClickCell(f, g, 0, 0);                                          // the corner
        Assert.Equal([G(1, 1, 4, 19)], g.SelectedRanges);
        Assert.Equal(100, g.ScrollY);

        g.MultiSelect = true;
        ClickCell(f, g, 1, 0);
        ClickCell(f, g, 2, 0, Ctrl);
        Assert.Equal([G(1, 1, 1, 19), G(2, 1, 2, 19)], g.SelectedRanges);

        g.RangeSelect = g.MultiSelect = false;
        ClickCell(f, g, 2, 8);
        ClickCell(f, g, 4, 0);                                          // single mode: the click is only reported
        Assert.Equal([G(2, 8, 2, 8)], g.SelectedRanges);
        Assert.Equal([3, 4, 1, 2, 4], clicked);
    }

    [Fact]
    public void Row_select_covers_whole_rows_and_turns_editing_off()
    {
        var (f, g) = Setup();
        g.RowSelect = true;
        g.Editing = true;
        ClickCell(f, g, 2, 3);
        Assert.Equal([G(1, 3, 4, 3)], g.SelectedRanges);
        Assert.True(g.IsCellSelected(4, 3));
        f.Simulate.Key(Key.Down, Shift);
        Assert.Equal([G(1, 3, 4, 4)], g.SelectedRanges);
        f.Simulate.Key(Key.Enter);
        Assert.False(g.IsEditing);
        Assert.False(g.BeginEdit());
    }

    [Fact]
    public void SelectCell_can_refuse_a_cell_for_clicks_drags_and_keys()
    {
        var (f, g) = Setup();
        g.SelectCell += (_, e) => e.CanSelect = e.Col != 3;
        ClickCell(f, g, 2, 2);
        ClickCell(f, g, 3, 2);
        Assert.Equal((2, 2), (g.Col, g.Row));
        f.Simulate.Key(Key.Right);
        Assert.Equal((2, 2), (g.Col, g.Row));
        f.Simulate.Key(Key.Down);
        Assert.Equal((2, 3), (g.Col, g.Row));
    }

    // ---- Keyboard ----

    [Fact]
    public void Keys_move_the_current_cell_and_keep_it_in_view_beside_the_headers()
    {
        var (f, g) = Setup();
        g.Focus();
        Assert.Equal((1, 1), (g.Col, g.Row));
        f.Simulate.Key(Key.Right);
        f.Simulate.Key(Key.Right);
        f.Simulate.Key(Key.Down);
        Assert.Equal((3, 2), (g.Col, g.Row));
        f.Simulate.Key(Key.Down, Shift);
        f.Simulate.Key(Key.Down, Shift);
        Assert.Equal([G(3, 2, 3, 4)], g.SelectedRanges);

        f.Simulate.Key(Key.End);                                        // the row's last column, scrolled in at the right
        Assert.Equal((4, 4), (g.Col, g.Row));
        Assert.Equal(52, g.ScrollX);
        Assert.Equal(g.Viewport.X + g.Viewport.Width, g.CellRect(4, 4).X + g.CellRect(4, 4).Width);
        f.Simulate.Key(Key.Home);                                       // the first column, just right of the fixed one
        Assert.Equal(1, g.Col);
        Assert.Equal(0, g.ScrollX);

        f.Simulate.Key(Key.End, Ctrl);
        Assert.Equal((4, 19), (g.Col, g.Row));
        Assert.Equal(400 - 148, g.ScrollY);
        f.Simulate.Key(Key.Home, Ctrl);
        Assert.Equal((1, 1), (g.Col, g.Row));
        Assert.Equal(0, g.ScrollY);

        f.Simulate.Key(Key.PageDown);
        Assert.Equal(7, g.Row);
        Assert.Equal(160 - 148, g.ScrollY);
        f.Simulate.Key(Key.PageUp);
        Assert.Equal(1, g.Row);
        f.Simulate.Key(Key.Up);
        Assert.Equal(1, g.Row);                                         // stays on the first row

        g.Row = 15;                                                     // from code: scrolled in at the bottom
        Assert.Equal(320 - 148, g.ScrollY);
        for (int i = 0; i < 6; i++) f.Simulate.Key(Key.Up);             // to row 9, the first row above the view
        Assert.Equal(9, g.Row);
        Assert.Equal(g.Viewport.Y + 20, g.CellRect(1, 9).Y);            // just below the header, not under it

        g.ScrollY = 0;                                                  // scrolled away with the wheel...
        f.Simulate.Key(Key.Up, Shift);                                  // ...a key brings the current cell back
        Assert.Equal(g.Viewport.Y + g.Viewport.Height, g.CellRect(1, 8).Y + 20);

        f.Simulate.Key(Key.A, Ctrl);
        Assert.Equal([G(1, 1, 4, 19)], g.SelectedRanges);
    }

    [Fact]
    public void Tab_moves_between_cells_with_WantTabs_and_otherwise_to_the_next_control()
    {
        var (f, g) = Setup();
        var button = new Button();
        button.SetBounds(400, 10, 80, 28);
        f.Add(button);
        f.PerformLayout();

        g.Focus();
        f.Simulate.Key(Key.Tab);
        Assert.True(button.Focused);

        g.WantTabs = true;
        g.Focus();
        g.FocusCell(4, 3);
        f.Simulate.Key(Key.Tab);                                        // wraps to the next row
        Assert.Equal((1, 4), (g.Col, g.Row));
        f.Simulate.Key(Key.Tab, Shift);
        Assert.Equal((4, 3), (g.Col, g.Row));
        g.FocusCell(4, 19);
        f.Simulate.Key(Key.Tab);                                        // the last cell: stays
        Assert.Equal((4, 19), (g.Col, g.Row));
        Assert.True(g.Focused);
    }

    // ---- Editing ----

    [Fact]
    public void Enter_edits_in_place_and_enter_commits_or_escape_cancels()
    {
        var (f, g) = Setup();
        g.Editing = true;
        g.Cells[1, 1] = "old";
        g.Focus();

        f.Simulate.Key(Key.Enter);
        Assert.True(g.IsEditing);
        var editor = g.InplaceEditor!;
        Assert.True(editor.Focused);
        Assert.Equal("old", editor.Text);
        Assert.Equal("old", editor.SelectedText);                       // all selected: typing replaces it
        var cell = g.CellRect(1, 1);
        Assert.Equal(new Rect(cell.X, cell.Y, cell.Width - 1, cell.Height - 1), editor.Bounds);
        f.Simulate.Type("new");
        f.Simulate.Key(Key.Enter);
        Assert.False(g.IsEditing);
        Assert.Equal("new", g.Cells[1, 1]);
        Assert.True(g.Focused);

        f.Simulate.Key(Key.F2);
        f.Simulate.Type("x");
        f.Simulate.Key(Key.Escape);
        Assert.False(g.IsEditing);
        Assert.Equal("new", g.Cells[1, 1]);
        Assert.True(g.Focused);
        Assert.Empty(g.Children);                                       // the editor is gone, not hidden

        f.Simulate.Type("Q");                                           // typing starts editing with the key
        Assert.Equal("Q", g.InplaceEditor!.Text);
        f.Simulate.Type("z");
        f.Simulate.Key(Key.Down);                                       // commits and moves
        Assert.Equal("Qz", g.Cells[1, 1]);
        Assert.Equal(2, g.Row);
        Assert.False(g.IsEditing);

        g.Editing = false;
        f.Simulate.Key(Key.Enter);
        f.Simulate.Type("a");
        Assert.False(g.IsEditing);
    }

    [Fact]
    public void The_editor_follows_scrolling_and_a_click_elsewhere_commits()
    {
        var (f, g) = Setup();
        g.Editing = true;
        var (x, y) = At(g, 1, 5);
        f.Simulate.DoubleClick(x, y);
        Assert.True(g.IsEditing);
        var editor = g.InplaceEditor!;
        int top = editor.Top;
        f.Simulate.Type("abc");

        var (hx, hy) = At(g, 0, 3);
        f.Simulate.Wheel(hx, hy, -1);                                   // three rows down
        Assert.Equal(60, g.ScrollY);
        Assert.Equal(top - 60, editor.Top);
        Assert.True(g.IsEditing);

        ClickCell(f, g, 2, 6);
        Assert.False(g.IsEditing);
        Assert.Equal("abc", g.Cells[1, 5]);
        Assert.Equal((2, 6), (g.Col, g.Row));

        // Moving the current cell from code commits too.
        f.Simulate.Key(Key.F2);
        f.Simulate.Type("def");
        g.FocusCell(3, 3);
        Assert.False(g.IsEditing);
        Assert.Equal("def", g.Cells[2, 6]);
    }

    [Fact]
    public void Edit_events_supply_refuse_and_validate_the_text()
    {
        var (f, g) = Setup();
        g.Editing = true;
        g.Cells[1, 1] = "1.50";
        g.GetEditText += (_, e) =>
        {
            if (e.Col == 2) e.Accept = false;                           // a read-only column
            else e.Text = e.Text.TrimEnd('0');                          // the editor shows another form of the value
        };
        var committed = new List<string>();
        g.SetEditText += (_, e) =>
        {
            e.Accept = e.Text != "bad";
            if (e.Accept) { e.Text = e.Text.ToUpperInvariant(); committed.Add($"{e.Col},{e.Row}={e.Text}"); }
        };
        g.Focus();

        f.Simulate.Key(Key.Enter);
        Assert.Equal("1.5", g.InplaceEditor!.Text);
        f.Simulate.Type("bad");
        f.Simulate.Key(Key.Enter);                                      // refused: the editor stays open
        Assert.True(g.IsEditing);
        f.Simulate.Key(Key.Escape);
        Assert.Equal("1.50", g.Cells[1, 1]);

        f.Simulate.Key(Key.F2);
        f.Simulate.Type("ok");
        f.Simulate.Key(Key.Enter);
        Assert.Equal("OK", g.Cells[1, 1]);                              // the handler may change what is stored
        Assert.Equal(["1,1=OK"], committed);

        f.Simulate.Key(Key.Right);
        f.Simulate.Key(Key.Enter);
        Assert.False(g.IsEditing);
        f.Simulate.Type("x");
        Assert.False(g.IsEditing);
    }

    // ---- Column sizing ----

    [Fact]
    public void Dragging_a_header_border_resizes_the_column_down_to_its_minimum()
    {
        var (f, g) = Setup();
        int border = g.AbsoluteLeft + 61 + 60, y = g.AbsoluteTop + 10;   // the right edge of column 1
        f.Simulate.MoveTo(border, y);
        Assert.Equal(Cursor.Default, f.CurrentCursor);                  // off without ColSizing

        g.ColSizing = true;
        var clicked = new List<int>();
        g.ColumnClick += (_, c) => clicked.Add(c);
        f.Simulate.MoveTo(border, y);
        Assert.Equal(Cursor.SizeWE, f.CurrentCursor);
        f.Simulate.Drag(border, y, border + 30, y);
        Assert.Equal(90, g.ColWidths[1]);
        Assert.Equal(5 * 60 + 30, g.ContentWidth);
        Assert.Empty(clicked);                                          // a resize is not a header click

        f.Simulate.Drag(border + 30, y, border - 200, y);
        Assert.Equal(8, g.ColWidths[1]);
        g.Columns.Add("#");
        g.Columns.Add("Name").MinWidth = 40;
        int nameBorder = g.AbsoluteLeft + g.CellRect(1, 0).X + g.CellRect(1, 0).Width;
        f.Simulate.Drag(nameBorder, y, nameBorder - 200, y);
        Assert.Equal(40, g.ColWidths[1]);
        Assert.Equal(40, g.Columns[1].Width);

        var (cx, cy) = At(g, 1, 3);
        f.Simulate.MoveTo(cx, cy);
        Assert.Equal(Cursor.Default, f.CurrentCursor);
    }

    [Fact]
    public void Column_resizing_at_150_percent_stores_widths_at_100_percent()
    {
        var (f, g) = Setup(scale: 1.5);
        g.ColSizing = true;
        var r = g.CellRect(1, 0);
        int border = g.AbsoluteLeft + r.X + r.Width, y = g.AbsoluteTop + 10;
        f.Simulate.Drag(border, y, border + 30, y);
        Assert.Equal(80, g.ColWidths[1]);
        Assert.Equal(120, g.CellRect(1, 0).Width);
    }

    // ---- Columns ----

    [Fact]
    public void Columns_give_the_header_titles_widths_and_alignment_and_set_the_column_count()
    {
        var f = new Form();
        f.SetBounds(0, 0, 500, 300);
        var g = new TextProbe { RowCount = 3 };
        g.SetBounds(0, 0, 400, 200);
        f.Add(g);
        g.Columns.Add("Id", 40);
        g.Columns.Add("Name");
        var amount = g.Columns.Add("Amount", 80, TextAlign.Right);
        f.PerformLayout();

        Assert.Equal(3, g.ColCount);
        Assert.Equal("Id", g.Text(0, 0));
        Assert.Equal("Amount", g.Text(2, 0));
        Assert.Equal("", g.Text(2, 1));
        Assert.Equal([40, 64, 80], Enumerable.Range(0, 3).Select(c => g.ColWidths[c]));
        Assert.Equal(g.Viewport.X + 104, g.CellRect(2, 1).X);
        g.ColWidths[1] = 100;
        Assert.Equal(100, g.Columns[1].Width);
        g.Columns[1].Width = 0;                                         // back to DefaultColWidth
        Assert.Equal(64, g.ColWidths[1]);
        g.DefaultColWidth = 50;
        Assert.Equal(50, g.ColWidths[1]);
        Assert.Equal(40 + 50 + 80, g.ContentWidth);

        g.Cells[0, 0] = "stored";
        g.Columns.RemoveAt(0);
        Assert.Equal(2, g.ColCount);
        Assert.Equal("Name", g.Text(0, 0));
        g.Columns.Clear();                                              // no columns: the count stays, Cells show
        Assert.Equal(2, g.ColCount);
        Assert.Equal("stored", g.Text(0, 0));
        var owned = new StringGrid().Columns.Add("x");
        Assert.Throws<InvalidOperationException>(() => g.Columns.Add(owned));   // a column belongs to one grid
        Assert.Equal(TextAlign.Right, amount.Alignment);
    }

    // ---- Sorting ----

    static StringGrid Fruit(Form f)
    {
        var g = new StringGrid { RowCount = 6, AutoSort = true };
        g.SetBounds(0, 0, 400, 200);
        f.Add(g);
        g.Columns.Add("#", 30);
        g.Columns.Add("Name");
        g.Columns.Add("Score", 60, TextAlign.Right);
        string[] names = ["pear", "Apple", "fig", "apple", "Banana"], scores = ["10", "9", "100", "9", "-1"];
        for (int r = 1; r <= 5; r++)
        {
            g.Cells[0, r] = r.ToString();
            g.Cells[1, r] = names[r - 1];
            g.Cells[2, r] = scores[r - 1];
        }
        f.PerformLayout();
        return g;
    }

    static string[] Column(StringGrid g, int col) => [.. Enumerable.Range(1, g.RowCount - 1).Select(r => g.Cells[col, r])];

    [Fact]
    public void A_header_click_sorts_the_rows_stably_and_a_second_click_reverses()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form();
        f.SetBounds(0, 0, 500, 300);
        var g = Fruit(f);
        var clicked = new List<(int, int, bool)>();
        g.ColumnClick += (s, c) => clicked.Add((c, g.SortColumn, g.SortDescending));
        g.FocusCell(1, 2);
        var before = g.SelectedRanges.ToList();

        ClickCell(f, g, 2, 0);
        Assert.Equal(["Banana", "Apple", "apple", "pear", "fig"], Column(g, 1));   // numbers by value; ties keep their order
        Assert.Equal(["5", "2", "4", "1", "3"], Column(g, 0));                       // whole rows move
        Assert.Equal((2, false), (g.SortColumn, g.SortDescending));
        Assert.Equal(before, g.SelectedRanges);                                       // a sorting click doesn't select

        ClickCell(f, g, 2, 0);
        Assert.Equal(["fig", "pear", "Apple", "apple", "Banana"], Column(g, 1));
        Assert.True(g.SortDescending);

        ClickCell(f, g, 1, 0);
        Assert.Equal(["Apple", "apple", "Banana", "fig", "pear"], Column(g, 1));     // text ignoring case, stable
        Assert.Equal([(2, 2, false), (2, 2, true), (1, 1, false)], clicked);

        g.Compare += (_, e) => e.Result = e.Text1.Length.CompareTo(e.Text2.Length);
        g.SortRows(1);
        Assert.Equal(["fig", "pear", "Apple", "apple", "Banana"], Column(g, 1));
        g.SortRows(1, descending: true);
        Assert.Equal(["Banana", "Apple", "apple", "pear", "fig"], Column(g, 1));
        Assert.True(g.SortDescending);
    }

    // ---- Copy ----

    [Fact]
    public void The_selection_copies_as_tab_separated_text()
    {
        var (_, g) = Setup();
        g.Cells[1, 1] = "a"; g.Cells[2, 1] = "b"; g.Cells[1, 2] = "c"; g.Cells[2, 2] = "d";
        g.Cells[3, 3] = "x\ty"; g.Cells[4, 3] = "say \"hi\"";
        g.Selection.SelectRange(new GridCoord(0, 0), new GridCoord(1, 1));
        Assert.Equal("a\tb\nc\td", g.SelectionAsText());

        g.MultiSelect = true;
        g.Selection.SelectRange(new GridCoord(2, 2), new GridCoord(3, 2), add: true);   // the bounding box, gaps empty
        Assert.Equal("a\tb\t\t\nc\td\t\t\n\t\t\"x\ty\"\t\"say \"\"hi\"\"\"", g.SelectionAsText());

        g.MultiSelect = false;
        g.RowSelect = true;
        g.FocusCell(2, 1);
        Assert.Equal("a\tb\t\t", g.SelectionAsText());                  // whole rows
    }

    [Fact]
    public void A_DrawGrid_copies_the_text_GetEditText_supplies()
    {
        var (_, g) = Setup<DrawGrid>();
        g.GetEditText += (_, e) => e.Text = $"{e.Col},{e.Row}";
        g.Selection.SelectRange(new GridCoord(0, 0), new GridCoord(1, 1));
        Assert.Equal("1,1\t2,1\n1,2\t2,2", g.SelectionAsText());
    }

    // ---- Virtual mode: the M14 done-when ----

    [Fact]
    public void A_100000_row_virtual_grid_scrolls_sorts_edits_and_selects_ranges_touching_only_visible_cells()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        const int Rows = 100_000;
        var f = new Form();
        f.SetBounds(0, 0, 600, 400);
        var g = new StringGrid { ColCount = 6, RowCount = Rows + 1, DefaultColWidth = 60, DefaultRowHeight = 20, AutoSort = true, Editing = true };
        g.SetBounds(10, 10, 262, 162);
        f.Add(g);
        f.PerformLayout();

        // The app's data: row order (for sorting) and edits, nothing per row in the grid.
        var order = Enumerable.Range(0, Rows).ToArray();
        var edits = new Dictionary<(int, int), string>();
        var touched = new List<(int Col, int Row)>();
        g.GetCell += (_, e) =>
        {
            touched.Add((e.Col, e.Row));
            if (e.Row == 0) { e.Text = $"Col {e.Col}"; return; }
            int item = order[e.Row - 1];
            e.Text = e.Col == 0 ? e.Row.ToString() : edits.TryGetValue((e.Col, item), out var t) ? t : $"R{item}C{e.Col}";
        };
        g.ColumnClick += (_, col) => Array.Reverse(order);              // the app sorts (here: item order flips)
        g.SetEditText += (_, e) => edits[(e.Col, order[e.Row - 1])] = e.Text;

        // A paint asks only for the cells in view: 8 rows (header included) × 5 columns.
        FormRenderer.RenderToBitmap(f).Dispose();
        Assert.Equal(40, touched.Count);
        Assert.All(touched, c => Assert.True(c.Row <= 7 && c.Col <= 4));

        // Any scroll position: at most the rows and columns in view, each possibly cut at both ends.
        int bound = (g.Viewport.Height / 20 + 2) * (g.Viewport.Width / 60 + 2);

        // The wheel.
        var (wx, wy) = At(g, 2, 3);
        f.Simulate.Wheel(wx, wy, -1);
        Assert.Equal(60, g.ScrollY);
        Assert.Equal(4, g.TopRow);

        // The keyboard: to the last row and back up a page.
        g.Focus();
        f.Simulate.Key(Key.End, Ctrl);
        Assert.Equal((5, Rows), (g.Col, g.Row));
        Assert.Equal(g.ContentHeight - g.Viewport.Height, g.ScrollY);
        f.Simulate.Key(Key.PageUp);
        Assert.Equal(Rows - g.VisibleRowCount, g.Row);
        touched.Clear();
        FormRenderer.RenderToBitmap(f).Dispose();
        Assert.InRange(touched.Count, 1, bound);
        Assert.All(touched, c => Assert.True(c.Row == 0 || c.Row >= g.TopRow - 1));

        // The scroll bar: drag the thumb from the top to the middle of the track.
        f.Simulate.Key(Key.Home, Ctrl);
        Assert.Equal(0, g.ScrollY);
        var view = g.Viewport;
        int barX = g.AbsoluteLeft + view.X + view.Width + 6, trackTop = g.AbsoluteTop + view.Y;
        f.Simulate.Press(barX, trackTop + 4);
        Assert.True(g.IsScrolling);
        f.Simulate.MoveTo(barX, trackTop + 4 + (view.Height - 16) / 2);
        f.Simulate.Release(barX, trackTop + 4 + (view.Height - 16) / 2);
        Assert.InRange(g.TopRow, 45_000, 55_000);
        touched.Clear();
        FormRenderer.RenderToBitmap(f).Dispose();
        Assert.InRange(touched.Count, 1, bound);

        // Selecting ranges: Shift+Ctrl+End spans to the last cell; a header click selects a whole column.
        g.FocusCell(2, 10);
        f.Simulate.Key(Key.End, Ctrl | Shift);
        Assert.Equal([G(2, 10, 5, Rows)], g.SelectedRanges);
        f.Simulate.Key(Key.A, Ctrl);
        Assert.Single(g.Selection.Ranges);

        // Editing a cell in place goes to the app; the grid stores nothing.
        g.FocusCell(1, 3);
        f.Simulate.Key(Key.F2);
        Assert.Equal("R2C1", g.InplaceEditor!.Text);
        f.Simulate.Type("edited");
        f.Simulate.Key(Key.Enter);
        Assert.Equal("", g.Cells[1, 3]);
        g.Selection.SelectRange(new GridCoord(0, 2), new GridCoord(0, 2));
        Assert.Equal("edited", g.SelectionAsText());

        // Sorting on a header click: the grid shows the arrow, the app reorders, the cell text follows.
        ClickCell(f, g, 1, 0);
        Assert.Equal((1, false), (g.SortColumn, g.SortDescending));
        Assert.Equal($"R{Rows - 3}C1", g.SelectionAsText());
        Assert.Equal("", g.Cells[1, 3]);
    }

    // ---- Metadata and form files ----

    [Fact]
    public void Grid_properties_are_published_and_round_trip_through_a_form_file()
    {
        var custom = ComponentRegistry.Find("CustomGrid")!;
        Assert.False(custom.CanCreate);
        var cls = ComponentRegistry.Find("StringGrid")!;
        Assert.Equal("CustomGrid", cls.Base!.Name);
        Assert.Equal("CustomGrid", ComponentRegistry.Find("DrawGrid")!.Base!.Name);
        foreach (var (name, type, value) in new (string, PropertyType, object)[]
        {
            ("colCount", PropertyType.Int, 5), ("rowCount", PropertyType.Int, 5), ("fixedCols", PropertyType.Int, 1),
            ("fixedRows", PropertyType.Int, 1), ("defaultColWidth", PropertyType.Int, 64), ("defaultRowHeight", PropertyType.Int, 24),
            ("gridLines", PropertyType.Bool, true), ("editing", PropertyType.Bool, false), ("rangeSelect", PropertyType.Bool, true),
            ("multiSelect", PropertyType.Bool, false), ("rowSelect", PropertyType.Bool, false), ("colSizing", PropertyType.Bool, false),
            ("wantTabs", PropertyType.Bool, false), ("autoSort", PropertyType.Bool, false), ("sortColumn", PropertyType.Int, -1),
            ("sortDescending", PropertyType.Bool, false),
        })
        {
            var p = cls.FindProperty(name);
            Assert.True(p != null, name);
            Assert.Equal(type, p!.Type);
            Assert.Equal(value, p.GetValue(cls.Prototype));
        }
        foreach (var ev in new[] { "onDrawCell", "onSelectCell", "onGetEditText", "onSetEditText", "onColumnClick", "onChange", "onDblClick", "onGetCell", "onCompare" })
            Assert.True(cls.FindEvent(ev) != null, ev);
        Assert.Null(ComponentRegistry.Find("DrawGrid")!.FindEvent("onGetCell"));

        var form = new Form { Name = "frmGrid" };
        form.SetBounds(0, 0, 400, 300);
        var saved = new StringGrid
        {
            Name = "grdData", ColCount = 4, RowCount = 1000, FixedCols = 0, FixedRows = 2, DefaultColWidth = 80, DefaultRowHeight = 22,
            GridLines = false, Editing = true, RangeSelect = false, MultiSelect = true, RowSelect = true, ColSizing = true,
            WantTabs = true, AutoSort = true, SortColumn = 2, SortDescending = true,
        };
        saved.SetBounds(10, 10, 300, 200);
        saved.EventHandlers["onGetCell"] = "grdData_GetCell";
        form.Add(saved);
        var draw = new DrawGrid { Name = "grdDraw", ColCount = 3 };
        draw.SetBounds(10, 220, 100, 60);
        form.Add(draw);

        var json = FormWriter.Save(form);
        Assert.Contains("\"rowCount\": 1000", json);
        Assert.Contains("\"onGetCell\": \"grdData_GetCell\"", json);
        Assert.DoesNotContain("fixedRows\": 1", json);
        var (loaded, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);
        var back = (StringGrid)loaded.FindControl("grdData")!;
        Assert.Equal((4, 1000, 0, 2, 80, 22), (back.ColCount, back.RowCount, back.FixedCols, back.FixedRows, back.DefaultColWidth, back.DefaultRowHeight));
        Assert.Equal((false, true, false, true, true, true, true, true, 2, true),
            (back.GridLines, back.Editing, back.RangeSelect, back.MultiSelect, back.RowSelect, back.ColSizing, back.WantTabs, back.AutoSort, back.SortColumn, back.SortDescending));
        Assert.Equal(CellSelectionMode.MultiRange, back.Selection.Mode);
        Assert.Equal(3, ((DrawGrid)loaded.FindControl("grdDraw")!).ColCount);
        Assert.Equal(json, FormWriter.Save(loaded));
    }

    [Fact]
    public void Shrinking_the_counts_trims_the_cells_and_the_selection()
    {
        var (_, g) = Setup();
        g.Cells[4, 19] = "far";
        g.Cells[2, 2] = "kept";
        g.FocusCell(4, 19);
        g.RowCount = 10;
        Assert.Equal((4, 9), (g.Col, g.Row));
        g.ColCount = 3;
        Assert.Equal((2, 9), (g.Col, g.Row));
        Assert.Equal("kept", g.Cells[2, 2]);
        g.ColCount = 5;
        g.RowCount = 20;
        Assert.Equal("", g.Cells[4, 19]);                               // gone, not resurrected
        Assert.Throws<ArgumentOutOfRangeException>(() => g.Cells[5, 0]);
        g.FixedRows = 25;                                               // more fixed rows than rows: no selectable cells
        Assert.Equal((-1, -1), (g.Col, g.Row));
        Assert.Empty(g.SelectedRanges);
    }
}

/// <summary>Ctrl+C in a grid; shares the process-wide clipboard with the edit tests, so not in parallel with them.</summary>
[Collection("Clipboard")]
public class GridClipboardTests
{
    [Fact]
    public void Ctrl_c_copies_the_selected_range()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form();
        f.SetBounds(0, 0, 400, 300);
        var g = new StringGrid();
        g.SetBounds(0, 0, 300, 200);
        f.Add(g);
        f.PerformLayout();
        g.Cells[1, 1] = "1"; g.Cells[2, 1] = "2"; g.Cells[1, 2] = "3"; g.Cells[2, 2] = "4";
        g.Focus();
        f.Simulate.Key(Key.Right, KeyModifiers.Shift);
        f.Simulate.Key(Key.Down, KeyModifiers.Shift);
        Clipboard.SetText("before");
        f.Simulate.Key(Key.C, KeyModifiers.Ctrl);
        Assert.Equal("1\t2\n3\t4", Clipboard.GetText());
    }
}
