using RadSharp.Design;
using RadSharp.Drawing;

namespace RadSharp.Tests;

public class GridColumnsEditorReviewTests
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

    static StringGrid Drop(Form host, DesignSurface s)
    {
        s.PendingClass = ComponentRegistry.Find("StringGrid");
        var (sx, sy) = s.DesignToScreen(40, 40);
        host.Simulate.Click(s.AbsoluteLeft + sx, s.AbsoluteTop + sy);
        return (StringGrid)s.Document!.Primary;
    }

    [Fact]
    public void Undo_after_a_session_restores_sort_column_widths_and_col_count_exactly_then_redo_reapplies()
    {
        var (host, s, doc) = Setup();
        var grid = Drop(host, s);
        grid.ColCount = 4;
        grid.SortColumn = 3;
        grid.SortDescending = true;
        doc.NotifyChanged();
        string before = doc.Save();
        doc.Snapshot();
        var editor = new GridColumnsEditor(doc, grid);
        editor.AddColumn("X");
        editor.Select(0);
        editor.DeleteSelected();
        editor.DeleteSelected();
        doc.NotifyChanged();
        string after = doc.Save();
        Assert.NotEqual(before, after);
        Assert.True(doc.Undo());
        Assert.Equal(before, doc.Save());
        var g2 = (StringGrid)doc.Form.FindControl(grid.Name)!;
        Assert.Equal((4, 3, true), (g2.ColCount, g2.SortColumn, g2.SortDescending));
        Assert.True(doc.Redo());
        Assert.Equal(after, doc.Save());
    }

    [Fact]
    public void Deleting_columns_below_FixedCols_does_not_throw_on_paint_or_save()
    {
        var (host, s, doc) = Setup();
        var grid = Drop(host, s);
        grid.FixedCols = 3;
        var editor = new GridColumnsEditor(doc, grid);
        editor.AddColumn("a");
        while (grid.Columns.Count > 1) { editor.Select(0); editor.DeleteSelected(); }
        host.PerformLayout();
        host.Simulate.Click(grid.AbsoluteLeft + 5, grid.AbsoluteTop + 5);
        var json = doc.Save();
        Assert.NotNull(FormReader.Load(json));
        Assert.Equal(1, grid.ColCount);
    }

    [Fact]
    public void Delete_all_columns_then_add_again_keeps_working()
    {
        var (host, s, doc) = Setup();
        var grid = Drop(host, s);
        var editor = new GridColumnsEditor(doc, grid);
        editor.AddColumn("a");
        while (editor.SelectedColumn != null) editor.DeleteSelected();
        Assert.Empty(grid.Columns);
        editor.AddColumn("n");
        Assert.Equal(grid.ColCount, grid.Columns.Count);
        string json = doc.Save();
        Assert.Equal(json, FormWriter.Save(FormReader.Load(json).Form));
    }

    [Fact]
    public void Editing_two_selected_grids_shows_no_columns_row()
    {
        var (host, s, doc) = Setup();
        var a = Drop(host, s);
        var pg = new PropertyGrid();
        host.Add(pg);
        pg.SetTargets([a]);
        int n = pg.RowCount;
        Assert.Contains(Enumerable.Range(0, n), i => pg.RowName(i) == "columns" && pg.IsCollection(i));
        var b = new StringGrid { Name = "other" };
        pg.SetTargets([a, b]);
        Assert.DoesNotContain(Enumerable.Range(0, pg.RowCount), i => pg.RowName(i) == "columns");
        pg.SetTargets([a]);
        Assert.Contains(Enumerable.Range(0, pg.RowCount), i => pg.RowName(i) == "columns");
        pg.SetTargets([]);
        Assert.Equal(0, pg.RowCount);
    }
}
