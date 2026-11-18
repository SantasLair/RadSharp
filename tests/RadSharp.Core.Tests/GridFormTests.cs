using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>M14 stage 4: grid <c>columns</c> in form files, the SortColumn rule, and the review fixes (cursor, sort overflow).</summary>
public class GridFormTests
{
    static string Wrap(string members, string cls = "StringGrid") =>
        "{ \"format\": \"radform\", \"version\": \"1.0\", \"root\": { \"class\": \"Form\", \"name\": \"f\", \"width\": 400, \"height\": 300, \"children\": [ { \"class\": \""
        + cls + "\", \"name\": \"g\", " + members + " } ] } }";

    static StringGrid Load(string members, out IReadOnlyList<string> warnings)
    {
        var result = FormReader.Load(Wrap(members));
        warnings = result.Warnings;
        return (StringGrid)result.Form.FindControl("g")!;
    }

    // ---- Form files ----

    [Fact]
    public void Columns_are_written_with_only_non_default_values_and_round_trip_byte_for_byte()
    {
        var form = new Form { Name = "f" };
        form.SetBounds(0, 0, 400, 300);
        var g = new StringGrid { Name = "g", FixedCols = 0 };
        g.Columns.Add("#", 28, TextAlign.Right);
        g.Columns.Add("Name").MinWidth = 40;
        g.Columns.Add("");                         // nothing to write
        g.Columns.Add("Say \"hi\"", 60, TextAlign.Center);
        g.SortColumn = 1;
        form.Add(g);

        var json = FormWriter.Save(form);
        string expected = string.Join("\n",
            "        \"columns\": [",
            "          {",
            "            \"alignment\": \"right\",",
            "            \"title\": \"#\",",
            "            \"width\": 28",
            "          },",
            "          {",
            "            \"minWidth\": 40,",
            "            \"title\": \"Name\"",
            "          },",
            "          {},",
            "          {",
            "            \"alignment\": \"center\",",
            "            \"title\": \"Say \\\"hi\\\"\",",
            "            \"width\": 60",
            "          }",
            "        ]");
        Assert.Contains(expected, json.Replace("\r\n", "\n"));
        Assert.Contains("\"colCount\": 4", json);
        var (back, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);
        var loaded = (StringGrid)back.FindControl("g")!;
        Assert.Equal(["#", "Name", "", "Say \"hi\""], loaded.Columns.Select(c => c.Title));
        Assert.Equal([28, 0, 0, 60], loaded.Columns.Select(c => c.Width));
        Assert.Equal(40, loaded.Columns[1].MinWidth);
        Assert.Equal(TextAlign.Center, loaded.Columns[3].Alignment);
        Assert.Equal((4, 1), (loaded.ColCount, loaded.SortColumn));
        Assert.Equal(json, FormWriter.Save(back));
    }

    [Fact]
    public void A_grid_without_columns_writes_no_columns_key()
    {
        var form = new Form { Name = "f" };
        form.Add(new DrawGrid { Name = "g" });
        Assert.DoesNotContain("columns", FormWriter.Save(form));
    }

    const string Abc = "\"columns\": [ {\"title\":\"A\"}, {\"title\":\"B\"}, {\"title\":\"C\"} ]";

    [Theory]
    [InlineData(Abc + ", \"colCount\": 3, \"fixedCols\": 0, \"sortColumn\": 2")]
    [InlineData("\"sortColumn\": 2, \"fixedCols\": 0, \"colCount\": 3, " + Abc)]
    [InlineData("\"fixedCols\": 0, \"sortColumn\": 2, " + Abc + ", \"colCount\": 3")]
    [InlineData("\"colCount\": 3, \"sortColumn\": 2, " + Abc + ", \"fixedCols\": 0")]
    public void Member_order_in_the_file_never_matters(string members)
    {
        var g = Load(members, out var warnings);
        Assert.Empty(warnings);
        Assert.Equal((3, 0, 2), (g.ColCount, g.FixedCols, g.SortColumn));
        Assert.Equal(["A", "B", "C"], g.Columns.Select(c => c.Title));
    }

    [Theory]
    [InlineData("\"colCount\": 6, \"columns\": [ {\"title\":\"A\"}, {\"title\":\"B\"} ]")]
    [InlineData("\"columns\": [ {\"title\":\"A\"}, {\"title\":\"B\"} ], \"colCount\": 6")]
    public void The_columns_array_wins_over_colCount(string members)
    {
        var g = Load(members, out _);
        Assert.Equal(2, g.ColCount);
    }

    [Fact]
    public void An_empty_columns_array_leaves_colCount_alone_and_a_stale_sortColumn_is_dropped_with_a_warning()
    {
        var g = Load("\"columns\": [], \"colCount\": 7", out var warnings);
        Assert.Empty(warnings);
        Assert.Equal((7, 0), (g.ColCount, g.Columns.Count));

        g = Load("\"sortColumn\": 4, \"columns\": [ {\"title\":\"A\"}, {\"title\":\"B\"} ]", out warnings);
        Assert.Equal(-1, g.SortColumn);
        Assert.Contains(warnings, w => w.Contains("sortColumn"));
    }

    [Fact]
    public void Bad_column_entries_warn_and_are_skipped_not_fatal()
    {
        var g = Load("\"columns\": [ {\"title\":\"A\", \"bogus\": 1, \"width\": \"wide\"}, 5 ]", out var warnings);
        Assert.Equal(2, g.Columns.Count);
        Assert.Equal("A", g.Columns[0].Title);
        Assert.Equal(3, warnings.Count);
        g = Load("\"columns\": 7", out warnings);
        Assert.Single(warnings);
    }

    // ---- SortColumn ----

    [Fact]
    public void Shrinking_ColCount_below_the_sort_column_resets_it_and_growing_never_brings_it_back()
    {
        var g = new StringGrid { ColCount = 5, SortColumn = 4 };
        g.ColCount = 3;
        Assert.Equal(-1, g.SortColumn);
        g.ColCount = 5;
        Assert.Equal(-1, g.SortColumn);

        g.SortColumn = 2;
        g.ColCount = 3;                       // 2 still exists
        Assert.Equal(2, g.SortColumn);
        g.ColCount = 2;
        Assert.Equal(-1, g.SortColumn);
    }

    [Fact]
    public void A_sort_column_set_beyond_ColCount_is_kept_until_the_grid_shrinks_further()
    {
        var g = new StringGrid { ColCount = 3, SortColumn = 6 };
        Assert.Equal(6, g.SortColumn);
        g.ColCount = 8;                       // growing keeps it (the columns may be on their way)
        Assert.Equal(6, g.SortColumn);
        g.ColCount = 7;                       // a shrink that no longer holds column 6
        Assert.Equal(6, g.SortColumn);
        g.ColCount = 6;
        Assert.Equal(-1, g.SortColumn);
    }

    [Fact]
    public void Building_Columns_from_empty_keeps_a_sort_column_set_earlier_but_removing_columns_resets_it()
    {
        var g = new StringGrid { SortColumn = 2 };
        g.Columns.Add("A");                   // 5 -> 1 columns while adopting
        g.Columns.Add("B");
        g.Columns.Add("C");
        Assert.Equal((3, 2), (g.ColCount, g.SortColumn));
        g.Columns.RemoveAt(0);
        Assert.Equal((2, -1), (g.ColCount, g.SortColumn));
        g.Columns.Add("X");
        Assert.Equal(-1, g.SortColumn);
    }

    [Fact]
    public void Moving_a_column_keeps_ColCount_SortColumn_and_cells()
    {
        var g = new StringGrid { SortColumn = 2 };
        g.Columns.Add("A");
        g.Columns.Add("B");
        g.Columns.Add("C");
        g.Cells[2, 1] = "kept";
        g.Columns.Move(2, 0);
        Assert.Equal(["C", "A", "B"], g.Columns.Select(c => c.Title));
        Assert.Equal((3, 2, "kept"), (g.ColCount, g.SortColumn, g.Cells[2, 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => g.Columns.Move(0, 3));
    }

    // ---- Review fixes ----

    [Fact]
    public void The_resize_cursor_gives_back_the_cursor_the_app_had_set()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form();
        f.SetBounds(0, 0, 600, 400);
        var g = new StringGrid { ColSizing = true, DefaultColWidth = 60, DefaultRowHeight = 20, Cursor = Cursor.Hand };
        g.SetBounds(10, 10, 262, 162);
        f.Add(g);
        f.PerformLayout();
        int border = g.AbsoluteLeft + 61 + 60, y = g.AbsoluteTop + 10;

        f.Simulate.MoveTo(g.AbsoluteLeft + 100, g.AbsoluteTop + 100);
        Assert.Equal(Cursor.Hand, g.Cursor);
        Assert.Equal(Cursor.Hand, f.CurrentCursor);
        f.Simulate.MoveTo(border, y);
        Assert.Equal(Cursor.SizeWE, g.Cursor);
        f.Simulate.MoveTo(g.AbsoluteLeft + 100, g.AbsoluteTop + 100);
        Assert.Equal(Cursor.Hand, g.Cursor);                      // not reset to Default
        f.Simulate.MoveTo(border, y);
        f.Simulate.MoveTo(500, 300);                              // leaves the grid while over the border
        Assert.Equal(Cursor.Hand, g.Cursor);
    }

    [Fact]
    public void Sorting_descending_survives_a_comparer_returning_int_MinValue()
    {
        var g = new StringGrid { ColCount = 2, RowCount = 4 };
        g.Cells[1, 1] = "a"; g.Cells[1, 2] = "b"; g.Cells[1, 3] = "c";
        g.Compare += (_, e) => e.Result = e.Text1 == e.Text2 ? 0 : string.CompareOrdinal(e.Text1, e.Text2) < 0 ? int.MinValue : int.MaxValue;
        g.SortRows(1, descending: true);
        Assert.Equal(["c", "b", "a"], new[] { g.Cells[1, 1], g.Cells[1, 2], g.Cells[1, 3] });
        g.SortRows(1);
        Assert.Equal(["a", "b", "c"], new[] { g.Cells[1, 1], g.Cells[1, 2], g.Cells[1, 3] });
    }

    // ---- Object Inspector ----

    [Fact]
    public void The_inspector_lists_a_columns_row_for_a_grid_and_asks_the_host_to_edit_it()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form();
        f.SetBounds(0, 0, 600, 700);
        var g = new StringGrid { Name = "grid" };
        g.Columns.Add("A");
        g.Columns.Add("B");
        f.Add(g);
        var inspector = new PropertyGrid();
        inspector.SetBounds(100, 10, 300, 660);
        f.Add(inspector);
        f.PerformLayout();

        inspector.SetTargets([g]);
        int row = inspector.IndexOf("columns");
        Assert.True(row >= 0);
        Assert.True(inspector.IsCollection(row));
        Assert.Equal("(2 columns)", inspector.RowValue(row));
        Assert.False(inspector.IsCollection(inspector.IndexOf("colCount")));
        Assert.False(inspector.Apply(row, "x"));

        var requests = new List<(Control, string)>();
        inspector.CollectionEditRequested += (_, t, n) => requests.Add((t, n));
        inspector.BeginEdit(row);
        Assert.Equal([(g, "columns")], requests);
        Assert.False(inspector.IsEditing);

        // As a mouse click on the selected row's value, and with Enter.
        inspector.SelectedIndex = row;
        var v = inspector.ValueRect(row);
        f.Simulate.Click(inspector.AbsoluteLeft + v.X + 10, inspector.AbsoluteTop + v.Y + 5);
        Assert.Equal(2, requests.Count);
        f.Simulate.Key(Key.Enter);
        Assert.Equal(3, requests.Count);

        // Not for a button, a multi-selection, or the Events tab.
        var b = new Button();
        f.Add(b);
        inspector.SetTargets([b]);
        Assert.Equal(-1, inspector.IndexOf("columns"));
        inspector.SetTargets([g, new StringGrid()]);
        Assert.Equal(-1, inspector.IndexOf("columns"));
        inspector.Mode = PropertyGridMode.Events;
        inspector.SetTargets([g]);
        Assert.Equal(-1, inspector.IndexOf("columns"));
    }
}
