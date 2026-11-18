using RadSharp.Design;
using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>M14 stage 4: the grids in the designer — palette defaults, inert design surface, the Columns editor, form-file round trip.</summary>
public class GridDesignTests
{
    static (Form Host, DesignSurface Surface, DesignDocument Doc) Setup()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var host = new Form();
        host.SetBounds(0, 0, 800, 600);
        var doc = new DesignDocument();
        var surface = new DesignSurface { Document = doc, Align = Align.Client };
        host.Add(surface);
        host.PerformLayout();
        surface.Focus();
        return (host, surface, doc);
    }

    static (int X, int Y) Screen(DesignSurface s, int x, int y)
    {
        var (sx, sy) = s.DesignToScreen(x, y);
        return (s.AbsoluteLeft + sx, s.AbsoluteTop + sy);
    }

    static T Drop<T>(Form host, DesignSurface s, string cls, int x = 40, int y = 40) where T : CustomGrid
    {
        s.PendingClass = ComponentRegistry.Find(cls);
        var p = Screen(s, x, y);
        host.Simulate.Click(p.X, p.Y);
        return (T)s.Document!.Primary;
    }

    // ---- Palette defaults ----

    [Theory]
    [InlineData("StringGrid")]
    [InlineData("DrawGrid")]
    public void A_grid_dropped_from_the_palette_is_a_5_by_5_grid_with_one_fixed_row_and_column(string cls)
    {
        var (host, s, doc) = Setup();
        var grid = Drop<CustomGrid>(host, s, cls);
        Assert.Equal(cls, ComponentRegistry.Of(grid).Name);
        Assert.Equal((5, 5, 1, 1), (grid.ColCount, grid.RowCount, grid.FixedCols, grid.FixedRows));
        Assert.Empty(grid.Columns);
        Assert.False(grid.Editing);
        Assert.Same(doc.Form, grid.Parent);
        // Nothing but the position and name is written for an untouched grid.
        var json = doc.Save();
        Assert.DoesNotContain("columns", json);
        Assert.DoesNotContain("colCount", json);
    }

    // ---- The design surface keeps the grid inert ----

    [Fact]
    public void Clicks_keys_and_drags_on_a_grid_at_design_time_never_edit_select_or_resize()
    {
        var (host, s, doc) = Setup();
        var grid = Drop<StringGrid>(host, s, "StringGrid");
        grid.Editing = true;
        grid.ColSizing = true;
        grid.Columns.Add("A", 60);
        grid.Columns.Add("B", 60);
        grid.Columns.Add("C", 60);
        host.PerformLayout();
        var current = grid.Selection.Current;
        var cell = grid.CellRect(2, 2);
        var (cx, cy) = Screen(s, grid.AbsoluteLeft + cell.X + 5, grid.AbsoluteTop + cell.Y + 5);

        host.Simulate.Click(cx, cy);
        host.Simulate.DoubleClick(cx, cy);
        host.Simulate.Key(Key.F2);
        host.Simulate.Key(Key.Enter);
        host.Simulate.Type("x");
        Assert.Empty(grid.Children);   // no in-place editor
        Assert.Equal(current, grid.Selection.Current);   // no cell was selected
        Assert.Single(grid.SelectedRanges.Take(2));
        Assert.False(grid.Focused);

        // Dragging a header border moves the grid (a normal control drag) but never resizes the column.
        var header = grid.CellRect(0, 0);
        var (bx, by) = Screen(s, grid.AbsoluteLeft + header.X + header.Width, grid.AbsoluteTop + header.Y + 4);
        host.Simulate.Drag(bx, by, bx + 30, by);
        Assert.Equal([60, 60, 60], grid.Columns.Select(c => c.Width));
        Assert.Same(doc.Form, grid.Parent);
    }

    [Fact]
    public void The_design_surface_draws_the_column_titles()
    {
        var (host, s, _) = Setup();
        var grid = Drop<StringGrid>(host, s, "StringGrid");
        host.PerformLayout();
        using var plain = FormRenderer.RenderToBitmap(host);
        var before = plain.Bytes.ToArray();

        grid.Columns.Add("Name", 80);
        grid.Columns.Add("Cost", 60, TextAlign.Right);
        grid.Columns.Add("Biome", 80);
        host.PerformLayout();
        using var titled = FormRenderer.RenderToBitmap(host);
        Assert.False(before.AsSpan().SequenceEqual(titled.Bytes));
        Assert.Equal(3, grid.ColCount);
    }

    // ---- The Columns editor ----

    [Fact]
    public void The_columns_editor_adds_reorders_edits_and_deletes_columns_and_updates_the_grid_live()
    {
        var (host, s, doc) = Setup();
        var grid = Drop<StringGrid>(host, s, "StringGrid");
        int changes = 0;
        doc.Changed += () => changes++;
        var editor = new GridColumnsEditor(doc, grid);

        // The default 5-column grid has no Columns: the first Add keeps its 5 columns (blank), then adds one.
        var first = editor.AddColumn("Name");
        Assert.Equal(6, grid.ColCount);
        Assert.Equal(6, grid.Columns.Count);
        Assert.Equal(5, editor.SelectedIndex);
        Assert.Same(first, editor.SelectedColumn);

        // Fields edit the selected column and the grid follows.
        ((Edit)editor.Form.FindControl("edtTitle")!).Text = "Title";
        ((SpinEdit)editor.Form.FindControl("spnWidth")!).Value = 90;
        ((SpinEdit)editor.Form.FindControl("spnMinWidth")!).Value = 30;
        ((ComboBox)editor.Form.FindControl("cboAlignment")!).ItemIndex = (int)TextAlign.Right;
        Assert.Equal(("Title", 90, 30, TextAlign.Right), (first.Title, first.Width, first.MinWidth, first.Alignment));
        Assert.Equal(90, grid.ColWidths[5]);
        Assert.True(changes > 0);

        editor.Select(5);
        editor.MoveSelected(-5);
        Assert.Equal(0, editor.SelectedIndex);
        Assert.Same(first, grid.Columns[0]);
        Assert.Equal(6, grid.ColCount);

        editor.DeleteSelected();
        Assert.Equal(5, grid.ColCount);
        Assert.DoesNotContain(first, grid.Columns);
        Assert.Equal(0, editor.SelectedIndex);
        while (editor.SelectedColumn != null) editor.DeleteSelected();
        Assert.Empty(grid.Columns);
    }

    [Fact]
    public void A_columns_editor_session_is_one_undo_step_and_the_columns_round_trip_through_the_form_file()
    {
        var (host, s, doc) = Setup();
        var grid = Drop<StringGrid>(host, s, "StringGrid");
        string before = doc.Save();
        bool dirty = false;
        doc.Changed += () => dirty = true;

        doc.Snapshot();
        var editor = new GridColumnsEditor(doc, grid);
        editor.AddColumn("A");
        editor.AddColumn("B");
        editor.Columns[6].Width = 100;
        doc.NotifyChanged();
        Assert.True(dirty);

        string after = doc.Save();
        Assert.Contains("\"columns\"", after);
        Assert.Contains("\"title\": \"B\"", after);
        var reloaded = (StringGrid)FormReader.Load(after).Form.FindControl(grid.Name)!;
        Assert.Equal(7, reloaded.ColCount);
        Assert.Equal(["", "", "", "", "", "A", "B"], reloaded.Columns.Select(c => c.Title));
        Assert.Equal(100, reloaded.Columns[6].Width);

        Assert.True(doc.Undo());
        Assert.Equal(before, doc.Save());
        Assert.Empty(((StringGrid)doc.Form.FindControl(grid.Name)!).Columns);
        Assert.True(doc.Redo());
        Assert.Equal(after, doc.Save());
    }

    [Fact]
    public void Deleting_the_sorted_column_in_the_editor_clears_the_sort_arrow()
    {
        var (host, s, doc) = Setup();
        var grid = Drop<StringGrid>(host, s, "StringGrid");
        grid.Columns.Add("A");
        grid.Columns.Add("B");
        grid.Columns.Add("C");
        grid.SortColumn = 2;
        var editor = new GridColumnsEditor(doc, grid);
        editor.Select(2);
        editor.DeleteSelected();
        Assert.Equal(-1, grid.SortColumn);
        grid.Columns.Add("D");
        Assert.Equal(-1, grid.SortColumn);
    }
}
