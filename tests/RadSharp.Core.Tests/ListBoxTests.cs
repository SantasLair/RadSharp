namespace RadSharp.Tests;

/// <summary>ListBox v2 on ScrollView: multi-select through real input, keyboard scrolling, items with tags, metadata.</summary>
public class ListBoxTests
{
    const KeyModifiers Ctrl = KeyModifiers.Ctrl, Shift = KeyModifiers.Shift;

    /// <summary>30 rows of 24 px in a 120 px viewport: rows 0–4 visible, a page is 4 rows.</summary>
    static (Form Form, ListBox List) Setup(bool multiSelect = false, bool extendedSelect = true, double scale = 1)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form { Scale = scale };
        f.SetBounds(0, 0, 500, 400);
        var list = new ListBox
        {
            Items = [.. Enumerable.Range(0, 30).Select(i => $"Item {i}")],
            MultiSelect = multiSelect,
            ExtendedSelect = extendedSelect,
        };
        list.SetBounds(10, 10, 150, 122);
        f.Add(list);
        f.PerformLayout();
        return (f, list);
    }

    static (int X, int Y) Row(ListBox list, int index) => (list.AbsoluteLeft + 20, list.AbsoluteTop + list.ItemRect(index).Y + 5);

    static void ClickRow(Form f, ListBox list, int index, KeyModifiers modifiers = KeyModifiers.None, MouseButton button = MouseButton.Left)
    {
        var (x, y) = Row(list, index);
        f.Simulate.Click(x, y, button, modifiers);
    }

    [Fact]
    public void Extended_select_by_click_ctrl_click_shift_click_and_ctrl_a()
    {
        var (f, list) = Setup(multiSelect: true);
        int changes = 0;
        list.Change += _ => changes++;

        ClickRow(f, list, 1);
        Assert.Equal([1], list.SelectedIndices);
        ClickRow(f, list, 3, Ctrl);
        Assert.Equal([1, 3], list.SelectedIndices);
        Assert.True(list.IsSelected(3));
        ClickRow(f, list, 4, Shift);                 // from the anchor (3), replacing the rest
        Assert.Equal([3, 4], list.SelectedIndices);
        ClickRow(f, list, 0, Ctrl | Shift);          // anchor..0 added
        Assert.Equal([0, 1, 2, 3, 4], list.SelectedIndices);
        Assert.Equal(0, list.ItemIndex);
        Assert.Equal(["Item 0", "Item 1", "Item 2", "Item 3", "Item 4"], list.SelectedItems);

        f.Simulate.Key(Key.A, Ctrl);
        Assert.Equal(30, list.SelectedCount);
        Assert.Single(list.Selection.Ranges);
        f.Simulate.Key(Key.Home);
        Assert.Equal([0], list.SelectedIndices);
        Assert.Equal(6, changes);
    }

    [Fact]
    public void Shift_arrows_extend_from_the_anchor_and_keep_the_current_row_in_view()
    {
        var (f, list) = Setup(multiSelect: true);
        ClickRow(f, list, 2);
        for (int i = 0; i < 3; i++) f.Simulate.Key(Key.Down, Shift);
        Assert.Equal([2, 3, 4, 5], list.SelectedIndices);
        Assert.Equal(5, list.ItemIndex);
        Assert.Equal(6 * 24 - 120, list.TopOffset);  // row 5 scrolled fully into view

        f.Simulate.Key(Key.Up, Shift);
        Assert.Equal([2, 3, 4], list.SelectedIndices);
        f.Simulate.Key(Key.Down, Ctrl);              // Ctrl+arrow moves only the focus...
        f.Simulate.Key(Key.Down, Ctrl);
        Assert.Equal(6, list.ItemIndex);
        Assert.Equal([2, 3, 4], list.SelectedIndices);
        f.Simulate.Key(Key.Space, Ctrl);             // ...and Ctrl+Space toggles it
        Assert.Equal([2, 3, 4, 6], list.SelectedIndices);
        f.Simulate.Key(Key.End, Shift);              // the anchor is now 6
        Assert.Equal(24, list.SelectedCount);
        Assert.Equal(30 * 24 - 120, list.TopOffset);
    }

    [Fact]
    public void Toggle_mode_flips_one_row_per_click_or_space()
    {
        var (f, list) = Setup(multiSelect: true, extendedSelect: false);
        ClickRow(f, list, 1);
        ClickRow(f, list, 3);
        Assert.Equal([1, 3], list.SelectedIndices);
        ClickRow(f, list, 1);
        Assert.Equal([3], list.SelectedIndices);

        f.Simulate.Key(Key.Down);                    // moves the focus, not the selection
        Assert.Equal(2, list.ItemIndex);
        Assert.Equal([3], list.SelectedIndices);
        f.Simulate.Key(Key.Space);
        f.Simulate.Type(" ");                        // the platform also sends the space as text: not a type-ahead
        Assert.Equal([2, 3], list.SelectedIndices);
        Assert.Equal(2, list.ItemIndex);
    }

    [Fact]
    public void Dragging_sweeps_a_range_and_right_click_keeps_a_selection_it_lands_in()
    {
        var (f, list) = Setup(multiSelect: true);
        var (x, y1) = Row(list, 1);
        var (_, y4) = Row(list, 4);
        f.Simulate.Press(x, y1);
        f.Simulate.MoveTo(x, y4);
        f.Simulate.Release(x, y4);
        Assert.Equal([1, 2, 3, 4], list.SelectedIndices);

        ClickRow(f, list, 2, button: MouseButton.Right);
        Assert.Equal([1, 2, 3, 4], list.SelectedIndices);
        ClickRow(f, list, 0, button: MouseButton.Right);
        Assert.Equal([0], list.SelectedIndices);
    }

    [Fact]
    public void PageDown_the_wheel_and_a_keyboard_move_after_scrolling_away()
    {
        var (f, list) = Setup();
        ClickRow(f, list, 0);
        f.Simulate.Key(Key.PageDown);
        Assert.Equal(4, list.ItemIndex);
        Assert.Equal(0, list.TopOffset);
        f.Simulate.Key(Key.PageDown);
        Assert.Equal(8, list.ItemIndex);
        Assert.Equal(9 * 24 - 120, list.TopOffset);

        var (x, y) = Row(list, 8);
        f.Simulate.Wheel(x, y, -1);                  // three rows per notch; the selection stays
        Assert.Equal(9 * 24 - 120 + 72, list.TopOffset);
        f.Simulate.Wheel(x, y, -3);
        Assert.Equal(8, list.ItemIndex);
        Assert.True(list.TopOffset > 9 * 24);         // row 8 is out of view now

        f.Simulate.Key(Key.Down);                    // the keyboard brings the new current row back
        Assert.Equal(9, list.ItemIndex);
        Assert.Equal(9 * 24, list.TopOffset);
        Assert.Equal(9, list.TopIndex);

        f.Simulate.Key(Key.PageUp);
        Assert.Equal(5, list.ItemIndex);
        Assert.Equal(5 * 24, list.TopOffset);
    }

    [Fact]
    public void A_single_select_list_behaves_as_before()
    {
        var (f, list) = Setup();
        ClickRow(f, list, 2, Ctrl);
        ClickRow(f, list, 4, Shift);
        Assert.Equal([4], list.SelectedIndices);
        f.Simulate.Key(Key.A, Ctrl);                 // left to shortcuts
        Assert.Equal(1, list.SelectedCount);
        list.SetSelected(7, true);
        Assert.Equal(7, list.ItemIndex);

        list.MultiSelect = true;
        list.SetSelected(9, true);
        list.SetSelected(10, true);
        list.MultiSelect = false;                    // back to single: the current row only
        Assert.Equal([7], list.SelectedIndices);
    }

    [Fact]
    public void Inserting_and_deleting_items_keeps_the_selection_and_tags_on_their_items()
    {
        var (_, list) = Setup(multiSelect: true);
        list.SetSelected(2, true);
        list.ItemIndex = 5;                          // selects 5 alone
        list.SetSelected(2, true);
        var tag = new object();
        list.SetItemTag(5, "five");

        list.InsertItem(0, "New", tag);
        Assert.Equal([3, 6], list.SelectedIndices);
        Assert.Equal(6, list.ItemIndex);
        Assert.Same(tag, list.GetItemTag(0));
        Assert.Equal("five", list.GetItemTag(6));

        list.DeleteItem(3);
        Assert.Equal([5], list.SelectedIndices);
        Assert.Equal(30, list.Items.Length);
        Assert.Equal(31, list.AddItem("Last", 42) + 1);
        Assert.Equal(42, list.GetItemTag(30));

        list.Items = ["a", "b"];                     // replacing the items keeps a valid index, drops tags
        Assert.Equal(1, list.ItemIndex);
        Assert.Null(list.GetItemTag(0));
        Assert.Equal(0, list.TopOffset);
    }

    [Fact]
    public void Rows_follow_the_ui_scale_in_a_combo_box_popup()
    {
        // At 150 % the popup's rows are 36 px: three of them overflow a two-row popup, so it needs a bar even though
        // the list was filled before it knew its scale.
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form { Scale = 1.5 };
        f.SetBounds(0, 0, 500, 400);
        var combo = new ComboBox { Items = ["Small", "Medium", "Large"], DropDownCount = 2, ItemIndex = 2 };
        combo.SetBounds(10, 10, 150, 39);
        f.Add(combo);
        f.PerformLayout();
        combo.DropDown();
        var list = Assert.IsType<ListBox>(f.Popups.Single());
        Assert.True(list.VerticalBarVisible);
        Assert.Equal(3 * 36 - list.Viewport.Height, list.TopOffset);   // the chosen last row in view
    }

    [Fact]
    public void Multi_select_properties_are_published_inspected_and_saved()
    {
        var cls = ComponentRegistry.Find("ListBox")!;
        Assert.Equal("ScrollView", cls.Base!.Name);
        var multi = cls.FindProperty("multiSelect")!;
        var extended = cls.FindProperty("extendedSelect")!;
        Assert.Equal(PropertyType.Bool, multi.Type);
        Assert.Equal(false, multi.GetValue(cls.Prototype));
        Assert.Equal(true, extended.GetValue(cls.Prototype));
        Assert.Equal("Behavior", multi.Category);
        Assert.NotNull(cls.FindProperty("borderStyle"));   // now inherited from ScrollView, same name and default
        Assert.NotNull(cls.FindProperty("color"));

        // The Object Inspector lists and toggles them.
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form();
        f.SetBounds(0, 0, 600, 900);
        var list = new ListBox { Name = "lstTiles", Items = ["a", "b"] };
        list.SetBounds(400, 10, 150, 100);
        f.Add(list);
        var grid = new PropertyGrid();
        grid.SetBounds(10, 10, 300, 860);
        f.Add(grid);
        f.PerformLayout();
        grid.Target = list;
        int row = grid.IndexOf("multiSelect");
        Assert.True(row >= 0);
        Assert.Equal("false", grid.RowValue(row));
        grid.SelectedIndex = row;                    // scrolled into view
        var cell = grid.ValueRect(row);
        f.Simulate.Click(grid.AbsoluteLeft + cell.X + 10, grid.AbsoluteTop + cell.Y + cell.Height / 2);
        Assert.True(list.MultiSelect);
        Assert.Equal(SelectionMode.Extended, list.Selection.Mode);

        // Only non-default values are written, and they load back.
        var form = new Form { Name = "frmList" };
        form.SetBounds(0, 0, 300, 200);
        var saved = new ListBox { Name = "lstTiles", Items = ["a", "b"], MultiSelect = true, ExtendedSelect = false };
        saved.SetBounds(10, 10, 150, 100);
        form.Add(saved);
        var json = FormWriter.Save(form);
        Assert.Contains("\"multiSelect\": true", json);
        Assert.Contains("\"extendedSelect\": false", json);
        var (loaded, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);
        var back = (ListBox)loaded.FindControl("lstTiles")!;
        Assert.True(back.MultiSelect);
        Assert.False(back.ExtendedSelect);
        Assert.Equal(SelectionMode.Multi, back.Selection.Mode);
        Assert.Equal(json, FormWriter.Save(loaded));
    }

    [Fact]
    public void Adding_100000_items_one_by_one_is_not_quadratic_and_Items_reflects_the_edits()
    {
        // Compare scaling, not wall-clock: a busy CI runner can be several times slower than a desk machine, but
        // 4x the items costs about 4x the time when adding is linear and about 16x when it is quadratic.
        // Best of three runs each, so one stall (GC, a noisy neighbour) doesn't decide the result.
        static double Fill(int n, out ListBox list)
        {
            double best = double.MaxValue;
            list = null!;
            for (int run = 0; run < 3; run++)
            {
                var l = new ListBox();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < n; i++) l.AddItem("x" + i);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                list = l;
            }
            return best;
        }
        double small = Fill(25_000, out _);
        double large = Fill(100_000, out var list);
        double ratio = large / Math.Max(small, 1.0);
        Assert.True(ratio < 10, $"4x the items took {ratio:0.0}x the time ({small:0} ms for 25k, {large:0} ms for 100k)");
        Assert.True(large < 30_000, $"100k AddItem took {large:0} ms");
        Assert.Equal(100_000, list.Items.Length);
        list.InsertItem(0, "first");
        list.DeleteItem(1);
        Assert.Equal("first", list.Items[0]);
        Assert.Equal("x1", list.Items[1]);
        Assert.Equal(100_000, list.Items.Length);
    }
}
