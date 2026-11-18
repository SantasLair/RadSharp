using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>
/// M14's done-when for ListView/TreeView: one selection-and-scrolling suite that runs unchanged against the grid (with
/// row select), the list view, the tree (rows = visible nodes) and the list box: the wheel, keyboard paging, scroll-bar
/// dragging, scroll-into-view on a keyboard move, click/Ctrl/Shift selection, and a 100 000-row virtual view that paints
/// only the rows in view. Each control is driven through simulated input; an adapter only maps rows to the control.
/// </summary>
public class RowViewSuiteTests
{
    const KeyModifiers Ctrl = KeyModifiers.Ctrl, Shift = KeyModifiers.Shift;

    public RowViewSuiteTests() => Shortcut.CommandModifier = KeyModifiers.Ctrl;

    public static TheoryData<string> Kinds => ["grid", "listview", "treeview", "listbox"];

    /// <summary>One control under test: how its rows map to selection, geometry and keys.</summary>
    abstract class Rows
    {
        public Form Form = null!;
        public ScrollView View = null!;
        /// <summary>Rows the last paints asked for (virtual views only).</summary>
        public readonly List<int> Touched = [];

        public abstract void Build(int rows, bool multi, bool @virtual = false);
        public abstract int Current { get; }
        public abstract bool IsSelected(int row);
        /// <summary>A row's rectangle in the control's coordinates.</summary>
        public abstract Rect RowRect(int row);
        public abstract int TopRow { get; }
        public virtual int RowHeight => 24;
        /// <summary>The part of the viewport rows scroll in (below any header), in the control's coordinates.</summary>
        public virtual Rect Body => View.Viewport;
        public virtual (Key Key, KeyModifiers Mods) FirstRow => (Key.Home, KeyModifiers.None);
        public virtual (Key Key, KeyModifiers Mods) LastRow => (Key.End, KeyModifiers.None);

        protected T Put<T>(T control) where T : ScrollView
        {
            Form = new Form();
            Form.SetBounds(0, 0, 400, 300);
            control.SetBounds(10, 10, 240, 200);
            Form.Add(control);
            View = control;
            return control;
        }

        public (int X, int Y) Center(int row)
        {
            var r = RowRect(row);
            return (View.AbsoluteLeft + r.X + 60, View.AbsoluteTop + r.Y + r.Height / 2);
        }

        public bool FullyVisible(int row)
        {
            var r = RowRect(row);
            var b = Body;
            return r.Y >= b.Y && r.Y + r.Height <= b.Y + b.Height;
        }

        public int VisibleRows => Body.Height / RowHeight;

        public int[] Selected(int count) => [.. Enumerable.Range(0, count).Where(IsSelected)];
    }

    sealed class GridRows : Rows
    {
        StringGrid G => (StringGrid)View;
        public override void Build(int rows, bool multi, bool @virtual = false)
        {
            var g = Put(new StringGrid { ColCount = 2, RowCount = rows + 1, FixedCols = 0, FixedRows = 1, RowSelect = true, RangeSelect = multi, MultiSelect = multi, DefaultColWidth = 100 });
            if (@virtual) g.GetCell += (_, e) => { if (e.Row > 0) Touched.Add(e.Row - 1); e.Text = $"R{e.Row}"; };
            else for (int r = 1; r <= rows; r++) g.Cells[0, r] = $"Row {r - 1}";
            Form.PerformLayout();
        }
        public override int Current => G.Row - 1;
        public override bool IsSelected(int row) => G.IsCellSelected(0, row + 1);
        public override Rect RowRect(int row) => G.CellRect(0, row + 1);
        public override int TopRow => G.TopRow - 1;
        public override Rect Body { get { var v = G.Viewport; return new Rect(v.X, v.Y + 24, v.Width, v.Height - 24); } }
        public override (Key, KeyModifiers) FirstRow => (Key.Home, Ctrl);
        public override (Key, KeyModifiers) LastRow => (Key.End, Ctrl);
    }

    sealed class ListViewRows : Rows
    {
        ListView L => (ListView)View;
        public override void Build(int rows, bool multi, bool @virtual = false)
        {
            var lv = Put(new ListView { Columns = ["Name:100", "Value:100"], MultiSelect = multi });
            if (@virtual)
            {
                lv.GetItem += (_, e) => { Touched.Add(e.Index); e.Item.Caption = $"Item {e.Index}"; e.Item.SubItems.Add("v"); };
                lv.ItemCount = rows;
            }
            else for (int i = 0; i < rows; i++) lv.Items.Add(new ListViewItem($"Item {i}", "v"));
            Form.PerformLayout();
        }
        public override int Current => L.ItemIndex;
        public override bool IsSelected(int row) => L.IsSelected(row);
        public override Rect RowRect(int row) => L.ItemRect(row);
        public override int TopRow => L.TopIndex;
        public override Rect Body { get { var v = L.Viewport; return new Rect(v.X, v.Y + 26, v.Width, v.Height - 26); } }
    }

    sealed class TreeRows : Rows
    {
        TreeView T => (TreeView)View;
        public override void Build(int rows, bool multi, bool @virtual = false)
        {
            var tv = Put(new TreeView { MultiSelect = multi });
            // Rows are the visible nodes: a few expanded parents with children, then top-level nodes. In the virtual
            // build every node's icon painter records its row, so the paints show which rows were drawn.
            int made = 0;
            void Track(TreeNode node)
            {
                int row = made++;
                if (@virtual) node.Icon = (_, _) => Touched.Add(row);
            }
            for (int p = 0; p < 3 && made < rows; p++)
            {
                var parent = tv.Add($"Group {p}");
                Track(parent);
                for (int c = 0; c < 4 && made < rows; c++) Track(parent.Add($"Child {p}.{c}"));
                parent.Expanded = true;
            }
            while (made < rows) Track(tv.Add($"Node {made}"));
            Form.PerformLayout();
            Assert.Equal(rows, tv.VisibleCount);
        }
        public override int Current => T.Selection.Current;
        public override bool IsSelected(int row) => T.Selection.IsSelected(row);
        public override Rect RowRect(int row) => T.RowRect(row);
        public override int TopRow => T.TopIndex;
    }

    sealed class ListBoxRows : Rows
    {
        ListBox B => (ListBox)View;
        public override void Build(int rows, bool multi, bool @virtual = false)
        {
            var lb = Put(new ListBox { Items = [.. Enumerable.Range(0, rows).Select(i => $"Item {i}")], MultiSelect = multi });
            if (@virtual) lb.DrawItem += (_, _, index, _, _) => Touched.Add(index);
            Form.PerformLayout();
        }
        public override int Current => B.ItemIndex;
        public override bool IsSelected(int row) => B.IsSelected(row);
        public override Rect RowRect(int row) => B.ItemRect(row);
        public override int TopRow => B.TopIndex;
    }

    static Rows Make(string kind) => kind switch
    {
        "grid" => new GridRows(),
        "listview" => new ListViewRows(),
        "treeview" => new TreeRows(),
        "listbox" => new ListBoxRows(),
        _ => throw new ArgumentException(kind),
    };

    static void Press(Rows r, (Key Key, KeyModifiers Mods) k) => r.Form.Simulate.Key(k.Key, k.Mods);

    [Theory]
    [MemberData(nameof(Kinds))]
    public void The_wheel_scrolls_three_rows_a_notch_and_back(string kind)
    {
        var r = Make(kind);
        r.Build(50, multi: false);
        var (x, y) = r.Center(1);
        r.Form.Simulate.Wheel(x, y, -1);
        Assert.Equal(3, r.TopRow);
        Assert.Equal(3 * r.RowHeight, r.View.ScrollY);
        r.Form.Simulate.Wheel(x, y, -2);
        Assert.Equal(9, r.TopRow);
        r.Form.Simulate.Wheel(x, y, 10);
        Assert.Equal(0, r.View.ScrollY);
        Assert.True(r.View.VerticalBarVisible);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Keys_move_the_current_row_page_by_page_and_keep_it_in_view(string kind)
    {
        var r = Make(kind);
        r.Build(100, multi: false);
        r.View.Focus();
        var (x, y) = r.Center(0);
        r.Form.Simulate.Click(x, y);
        Assert.Equal(0, r.Current);

        for (int i = 0; i < r.VisibleRows + 3; i++) r.Form.Simulate.Key(Key.Down);
        Assert.Equal(r.VisibleRows + 3, r.Current);
        Assert.True(r.FullyVisible(r.Current));
        Assert.True(r.TopRow > 0);
        Assert.True(r.IsSelected(r.Current));

        Press(r, r.LastRow);
        Assert.Equal(99, r.Current);
        Assert.Equal(r.View.ContentHeight - r.View.Viewport.Height, r.View.ScrollY);
        Assert.True(r.FullyVisible(99));

        r.Form.Simulate.Key(Key.PageUp);
        int paged = r.Current;
        Assert.InRange(paged, 99 - r.VisibleRows, 98);
        Assert.True(r.FullyVisible(paged));
        r.Form.Simulate.Key(Key.PageDown);
        Assert.True(r.Current > paged);

        Press(r, r.FirstRow);
        Assert.Equal(0, r.Current);
        Assert.Equal(0, r.View.ScrollY);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_keyboard_move_scrolls_the_current_row_back_into_view_after_the_wheel_took_it_away(string kind)
    {
        var r = Make(kind);
        r.Build(200, multi: false);
        r.View.Focus();
        var (x, y) = r.Center(1);
        r.Form.Simulate.Click(x, y);
        r.Form.Simulate.Wheel(x, y, -10);
        Assert.False(r.FullyVisible(1));
        r.Form.Simulate.Key(Key.Down);
        Assert.Equal(2, r.Current);
        Assert.True(r.FullyVisible(2));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Dragging_the_scroll_bar_thumb_scrolls_through_the_rows(string kind)
    {
        var r = Make(kind);
        r.Build(1000, multi: false);
        var view = r.View.Viewport;
        int barX = r.View.AbsoluteLeft + view.X + view.Width + 6, trackTop = r.View.AbsoluteTop + view.Y;
        r.Form.Simulate.Press(barX, trackTop + 4);
        Assert.True(r.View.IsScrolling);
        r.Form.Simulate.MoveTo(barX, trackTop + 4 + (view.Height - 16) / 2);
        r.Form.Simulate.Release(barX, trackTop + 4 + (view.Height - 16) / 2);
        Assert.False(r.View.IsScrolling);
        Assert.InRange(r.TopRow, 400, 600);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Click_shift_click_and_ctrl_click_select_rows(string kind)
    {
        var r = Make(kind);
        r.Build(30, multi: true);
        r.View.Focus();
        void Click(int row, KeyModifiers mods = KeyModifiers.None)
        {
            var (x, y) = r.Center(row);
            r.Form.Simulate.Click(x, y, MouseButton.Left, mods);
        }

        Click(1);
        Assert.Equal([1], r.Selected(30));
        Click(3, Shift);
        Assert.Equal([1, 2, 3], r.Selected(30));
        Assert.Equal(3, r.Current);
        Click(5, Ctrl);
        Assert.Equal([1, 2, 3, 5], r.Selected(30));
        Assert.Equal(5, r.Current);
        Click(0);
        Assert.Equal([0], r.Selected(30));

        // Keyboard: Shift+Down extends, Ctrl/⌘+A selects everything.
        r.Form.Simulate.Key(Key.Down, Shift);
        r.Form.Simulate.Key(Key.Down, Shift);
        Assert.Equal([0, 1, 2], r.Selected(30));
        r.Form.Simulate.Key(Key.A, Ctrl);
        Assert.Equal(30, r.Selected(30).Length);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Without_multi_select_a_click_selects_one_row(string kind)
    {
        var r = Make(kind);
        r.Build(30, multi: false);
        var (x, y) = r.Center(2);
        r.Form.Simulate.Click(x, y);
        (x, y) = r.Center(6);
        r.Form.Simulate.Click(x, y, MouseButton.Left, Shift);
        Assert.Equal([6], r.Selected(30));
        r.Form.Simulate.Click(x, y - r.RowHeight, MouseButton.Left, Ctrl);
        Assert.Equal([5], r.Selected(30));
        Assert.Equal(5, r.Current);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_100000_row_view_paints_only_the_rows_in_view_wherever_it_is_scrolled(string kind)
    {
        const int Count = 100_000;
        var r = Make(kind);
        r.Build(Count, multi: true, @virtual: true);
        r.View.Focus();
        int bound = r.VisibleRows + 2;

        void PaintAndCheck()
        {
            r.Touched.Clear();
            FormRenderer.RenderToBitmap(r.Form).Dispose();
            var rows = r.Touched.Distinct().ToList();
            Assert.InRange(rows.Count, 1, bound);
            Assert.All(rows, row => Assert.InRange(row, r.TopRow - 1, r.TopRow + bound));
        }

        PaintAndCheck();
        var (x, y) = r.Center(1);
        r.Form.Simulate.Wheel(x, y, -5);
        Assert.Equal(15, r.TopRow);
        PaintAndCheck();

        Press(r, r.LastRow);
        Assert.Equal(Count - 1, r.Current);
        PaintAndCheck();
        Assert.Contains(Count - 1, r.Touched);

        Press(r, r.FirstRow);
        var view = r.View.Viewport;
        int barX = r.View.AbsoluteLeft + view.X + view.Width + 6, trackTop = r.View.AbsoluteTop + view.Y;
        r.Form.Simulate.Press(barX, trackTop + 4);
        r.Form.Simulate.MoveTo(barX, trackTop + 4 + (view.Height - 16) / 2);
        r.Form.Simulate.Release(barX, trackTop + 4 + (view.Height - 16) / 2);
        Assert.InRange(r.TopRow, 40_000, 60_000);
        PaintAndCheck();

        // Selecting everything is one range, whatever the count.
        r.Form.Simulate.Key(Key.A, Ctrl);
        Assert.True(r.IsSelected(0) && r.IsSelected(Count / 2) && r.IsSelected(Count - 1));
    }
}
