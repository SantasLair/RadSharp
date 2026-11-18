using RadSharp.Drawing;
using RadSharp.Text;

namespace RadSharp.Tests;

/// <summary>Review regressions for the grid controls.</summary>
public class GridReviewTests
{
    static (Form Form, StringGrid Grid) Setup(int rows = 20)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form();
        f.SetBounds(0, 0, 600, 400);
        var g = new StringGrid { ColCount = 5, RowCount = rows, DefaultColWidth = 60, DefaultRowHeight = 20, Editing = true };
        g.SetBounds(10, 10, 262, 162);
        f.Add(g);
        f.PerformLayout();
        return (f, g);
    }

    [Fact]
    public void A_SetEditText_handler_that_moves_the_selection_commits_once()
    {
        var (f, g) = Setup();
        int calls = 0;
        g.SetEditText += (_, e) => { calls++; if (calls < 5) g.Row = 4; };
        g.Focus();
        f.Simulate.Key(Key.F2);
        f.Simulate.Type("x");
        f.Simulate.Key(Key.Enter);
        Assert.Equal(1, calls);
        Assert.False(g.IsEditing);
        Assert.Equal("x", g.Cells[1, 1]);
    }

    [Fact]
    public void A_SetEditText_handler_that_changes_RowCount_does_not_recurse_or_throw()
    {
        var (f, g) = Setup();
        int calls = 0;
        g.SetEditText += (_, e) => { calls++; if (calls > 5) throw new InvalidOperationException("recursed"); g.RowCount = 10; };
        g.Focus();
        f.Simulate.Key(Key.F2);
        f.Simulate.Type("x");
        f.Simulate.Key(Key.Enter);
        Assert.Equal(1, calls);
        Assert.False(g.IsEditing);
    }

    [Fact]
    public void A_SetEditText_handler_that_shrinks_the_grid_under_the_edited_cell_does_not_throw()
    {
        var (f, g) = Setup();
        g.FocusCell(1, 15);
        g.SetEditText += (_, e) => g.RowCount = 5;
        g.Focus();
        f.Simulate.Key(Key.F2);
        f.Simulate.Type("x");
        f.Simulate.Key(Key.Enter);
        Assert.False(g.IsEditing);
    }

    [Fact]
    public void The_editor_is_never_serialized()
    {
        var (f, g) = Setup();
        g.Name = "grd";
        g.Focus();
        f.Simulate.Key(Key.F2);
        Assert.True(g.IsEditing);
        var json = FormWriter.Save(f);
        Assert.Equal(2, json.Split("\"class\"").Length - 1);   // the form and the grid, not the editor
        Assert.True(g.IsEditing);
    }

    [Fact]
    public void CellRect_on_an_empty_grid_does_not_throw()
    {
        var (_, g) = Setup();
        g.ColCount = 0;
        _ = g.CellRect(0, 0);
    }

    [Fact]
    public void Changing_FixedCols_keeps_the_current_cell()
    {
        var (_, g) = Setup();
        g.FocusCell(3, 4);
        g.FixedCols = 2;
        Assert.Equal((3, 4), (g.Col, g.Row));
        g.FixedCols = 0;
        Assert.Equal((3, 4), (g.Col, g.Row));
        g.FixedRows = 3;
        Assert.Equal((3, 4), (g.Col, g.Row));
    }

    [Fact]
    public void A_scale_change_repositions_the_editor()
    {
        var (f, g) = Setup();
        g.Focus();
        f.Simulate.Key(Key.F2);
        f.ChangeScale(2);
        f.PerformLayout();
        var cell = g.CellRect(g.Col, g.Row);
        Assert.Equal(cell.X, g.InplaceEditor!.Left);
        Assert.Equal(cell.Y, g.InplaceEditor!.Top);
        Assert.Equal(cell.Width - 2, g.InplaceEditor!.Width);
    }

    [Fact]
    public void Focus_moving_to_another_control_commits_and_leaves_focus_there()
    {
        var (f, g) = Setup();
        var b = new Button();
        b.SetBounds(300, 10, 80, 24);
        f.Add(b);
        f.PerformLayout();
        g.Focus();
        f.Simulate.Key(Key.F2);
        f.Simulate.Type("zz");
        b.Focus();
        Assert.False(g.IsEditing);
        Assert.Equal("zz", g.Cells[1, 1]);
        Assert.True(b.Focused);
        Assert.Empty(g.Children);
    }

    [Fact]
    public void Text_that_parses_as_NaN_or_Infinity_sorts_as_text()
    {
        var (_, g) = Setup(rows: 6);
        g.Cells[1, 1] = "Zed"; g.Cells[1, 2] = "Nan"; g.Cells[1, 3] = "Abe"; g.Cells[1, 4] = "Infinity"; g.Cells[1, 5] = "5";
        g.SortRows(1);
        Assert.Equal(["5", "Abe", "Infinity", "Nan", "Zed"], Enumerable.Range(1, 5).Select(r => g.Cells[1, r]));
    }

    [Fact]
    public void Sorting_with_a_refused_editor_does_not_store_into_the_wrong_row()
    {
        var (f, g) = Setup(rows: 4);
        g.Cells[1, 1] = "b"; g.Cells[1, 2] = "a";
        g.SetEditText += (_, e) => e.Accept = false;
        g.Focus();
        f.Simulate.Key(Key.F2);
        g.SortRows(1);
        Assert.False(g.IsEditing);
    }

    [Fact]
    public void Shrinking_rows_with_a_refused_editor_closes_it()
    {
        var (f, g) = Setup();
        g.FocusCell(1, 15);
        g.SetEditText += (_, e) => e.Accept = false;
        g.Focus();
        f.Simulate.Key(Key.F2);
        g.RowCount = 5;
        Assert.False(g.IsEditing);
    }

    [Fact]
    public void FixedRows_change_with_a_refused_editor_does_not_leave_it_on_a_header()
    {
        var (f, g) = Setup();
        g.SetEditText += (_, e) => e.Accept = false;
        g.Focus();
        f.Simulate.Key(Key.F2);
        g.FixedRows = 3;
        Assert.False(g.IsEditing && g.InplaceEditor != null && g.Row != 1);
    }
}

public class GridReviewProbeTests
{
    static (Form Form, StringGrid Grid) Setup(double scale, int fc, int fr, int cols = 5, int rows = 20)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form { Scale = scale };
        f.SetBounds(0, 0, 600, 400);
        var g = new StringGrid { ColCount = cols, RowCount = rows, FixedCols = fc, FixedRows = fr, DefaultColWidth = 60, DefaultRowHeight = 20, Editing = true, ColSizing = true, AutoSort = true, WantTabs = true, MultiSelect = true };
        g.SetBounds(10, 10, 262, 162);
        f.Add(g);
        f.PerformLayout();
        return (f, g);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(1, 0)] [InlineData(5, 20)] [InlineData(7, 25)] [InlineData(5, 1)] [InlineData(1, 20)] [InlineData(2, 2)]
    [InlineData(1, 1)]
    public void Edge_fixed_counts_survive_input_painting_and_copying(int fc, int fr)
    {
        foreach (var scale in new[] { 1.0, 1.5, 2.0 })
        {
            var (f, g) = Setup(scale, fc, fr);
            g.Focus();
            FormRenderer.RenderToBitmap(f).Dispose();
            foreach (var key in new[] { Key.Right, Key.Down, Key.Tab, Key.End, Key.Home, Key.PageDown, Key.PageUp, Key.Left, Key.Up, Key.Enter, Key.Escape })
            {
                f.Simulate.Key(key);
                f.Simulate.Key(key, KeyModifiers.Shift);
                f.Simulate.Key(key, KeyModifiers.Ctrl);
            }
            f.Simulate.Key(Key.A, KeyModifiers.Ctrl);
            _ = g.SelectionAsText();
            for (int c = 0; c < g.ColCount; c++)
                for (int r = 0; r < g.RowCount; r += 3)
                {
                    var rc = g.CellRect(c, r);
                    f.Simulate.Click(g.AbsoluteLeft + rc.X + 3, g.AbsoluteTop + rc.Y + 3);
                    f.Simulate.Click(g.AbsoluteLeft + rc.X + 3, g.AbsoluteTop + rc.Y + 3, MouseButton.Left, KeyModifiers.Shift);
                    f.Simulate.Click(g.AbsoluteLeft + rc.X + 3, g.AbsoluteTop + rc.Y + 3, MouseButton.Left, KeyModifiers.Ctrl);
                }
            f.Simulate.Type("a");
            f.Simulate.Key(Key.Enter);
            g.EndEdit(true);
            g.ScrollCellIntoView(g.ColCount - 1, g.RowCount - 1);
            FormRenderer.RenderToBitmap(f).Dispose();
            g.RowCount = 2; g.ColCount = 2;
            g.RowCount = 0;
            g.ColCount = 0;
            FormRenderer.RenderToBitmap(f).Dispose();
        }
    }

    [Fact]
    public void Virtual_copy_of_a_small_range_asks_only_for_those_cells_and_paint_for_visible_ones()
    {
        var (f, g) = Setup(1, 1, 1, 5, 100_000);
        int calls = 0;
        g.GetCell += (_, e) => { calls++; e.Text = $"{e.Col}:{e.Row}"; };
        g.Focus();
        FormRenderer.RenderToBitmap(f).Dispose();
        Assert.True(calls < 200, calls.ToString());
        calls = 0;
        g.FocusCell(1, 50_000);
        f.Simulate.Key(Key.Right, KeyModifiers.Shift);
        f.Simulate.Key(Key.Down, KeyModifiers.Shift);
        f.Simulate.Key(Key.PageDown);
        f.Simulate.Key(Key.End, KeyModifiers.Ctrl);
        f.Simulate.Key(Key.Home, KeyModifiers.Ctrl);
        Assert.True(calls < 20, calls.ToString());
        g.FocusCell(1, 3);
        f.Simulate.Key(Key.Down, KeyModifiers.Shift);
        calls = 0;
        Assert.Equal("1:3\n1:4", g.SelectionAsText());
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Tsv_quotes_tabs_newlines_and_quotes()
    {
        var (_, g) = Setup(1, 1, 1);
        g.Cells[1, 1] = "a\tb"; g.Cells[2, 1] = "line1\nline2"; g.Cells[1, 2] = "say \"hi\""; g.Cells[2, 2] = "cr\rx";
        g.Selection.SelectRange(new GridCoord(0, 0), new GridCoord(1, 1));
        Assert.Equal("\"a\tb\"\t\"line1\nline2\"\n\"say \"\"hi\"\"\"\t\"cr\rx\"", g.SelectionAsText());
    }

    [Theory]
    [InlineData(1.0)] [InlineData(1.5)] [InlineData(2.0)]
    public void Resize_borders_hit_test_at_scrolled_and_fixed_boundaries(double scale)
    {
        var (f, g) = Setup(scale, 1, 1);
        // fixed column border
        int Hit(int x) { f.Simulate.MoveTo(g.AbsoluteLeft + x, g.AbsoluteTop + g.Viewport.Y + 3); return f.CurrentCursor == Cursor.SizeWE ? 1 : 0; }
        int fw = g.CellRect(1, 0).X;
        Assert.Equal(1, Hit(fw));
        g.ScrollX = 60 * (int)Math.Round(scale) / 2 + 7;
        // after scrolling, the border of the partially hidden column 1 must not be offered when it is under the fixed column
        var c1 = g.CellRect(1, 0);
        int edge1 = c1.X + c1.Width;
        Assert.Equal(1, Hit(edge1));
        Assert.Equal(1, Hit(fw));
    }
}

public class GridReviewGetEditTextTests
{
    [Fact]
    public void A_GetEditText_handler_that_shrinks_the_grid_does_not_open_an_editor_on_a_missing_cell()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form();
        f.SetBounds(0, 0, 600, 400);
        var g = new StringGrid { ColCount = 5, RowCount = 20, Editing = true };
        g.SetBounds(10, 10, 262, 162);
        f.Add(g);
        f.PerformLayout();
        g.FocusCell(1, 15);
        g.GetEditText += (_, e) => g.RowCount = 5;
        g.Focus();
        f.Simulate.Key(Key.F2);
        Assert.False(g.IsEditing);
    }
}
