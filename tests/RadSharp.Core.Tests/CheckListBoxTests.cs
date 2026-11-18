namespace RadSharp.Tests;

/// <summary>CheckListBox: a check box per item, through simulated input, the form file and the designer's metadata.</summary>
public class CheckListBoxTests
{
    static (Form Form, CheckListBox List) Setup(bool multiSelect = false, bool allowGrayed = false, double scale = 1)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form { Scale = scale };
        f.SetBounds(0, 0, 500, 400);
        var list = new CheckListBox
        {
            Items = [.. Enumerable.Range(0, 30).Select(i => $"Item {i}")],
            MultiSelect = multiSelect,
            AllowGrayed = allowGrayed,
        };
        list.SetBounds(10, 10, 150, 122);
        f.Add(list);
        f.PerformLayout();
        return (f, list);
    }

    static (int X, int Y) Box(CheckListBox list, int index)
    {
        var box = list.CheckBoxRect(index);
        return (list.AbsoluteLeft + box.X + box.Width / 2, list.AbsoluteTop + box.Y + box.Height / 2);
    }

    static (int X, int Y) Text(CheckListBox list, int index) => (list.AbsoluteLeft + 80, list.AbsoluteTop + list.ItemRect(index).Y + 5);

    [Fact]
    public void Clicking_the_box_toggles_and_selects_but_clicking_the_text_only_selects()
    {
        var (f, list) = Setup();
        int clicks = 0;
        list.ClickCheck += _ => clicks++;

        var (tx, ty) = Text(list, 2);
        f.Simulate.Click(tx, ty);
        Assert.Equal(2, list.ItemIndex);
        Assert.False(list.Checked[2]);
        Assert.Equal(0, clicks);

        var (bx, by) = Box(list, 3);
        f.Simulate.Click(bx, by);
        Assert.True(list.Checked[3]);
        Assert.Equal(CheckState.Checked, list.States[3]);
        Assert.Equal(3, list.ItemIndex);               // like Delphi, the click selects the row too
        Assert.Equal(1, clicks);
        f.Simulate.Click(bx, by);
        Assert.False(list.Checked[3]);
        Assert.Equal(2, clicks);
        Assert.Empty(list.CheckedIndices);
    }

    [Fact]
    public void Space_toggles_every_selected_item_once_per_action()
    {
        var (f, list) = Setup(multiSelect: true);
        list.Focus();
        int clicks = 0;
        list.ClickCheck += _ => clicks++;
        f.Simulate.Click(Text(list, 1).X, Text(list, 1).Y);
        f.Simulate.Click(Text(list, 3).X, Text(list, 3).Y, MouseButton.Left, KeyModifiers.Shift);   // 1..3
        Assert.Equal([1, 2, 3], list.SelectedIndices);
        f.Simulate.Key(Key.Space);
        Assert.Equal([1, 2, 3], list.CheckedIndices);
        Assert.Equal([1, 2, 3], list.SelectedIndices);   // the selection is untouched
        Assert.Equal(1, clicks);
        list.Checked[2] = false;                          // setting from code raises nothing
        f.Simulate.Key(Key.Space);                        // each item flips by itself
        Assert.Equal([2], list.CheckedIndices);
        Assert.Equal(2, clicks);
    }

    [Fact]
    public void Space_in_a_single_select_list_toggles_the_current_item_and_ctrl_space_still_selects_in_multi()
    {
        var (f, list) = Setup();
        list.Focus();
        f.Simulate.Key(Key.Space);                        // nothing is current yet
        Assert.Empty(list.CheckedIndices);
        f.Simulate.Key(Key.Down);
        f.Simulate.Key(Key.Space);
        Assert.Equal([0], list.CheckedIndices);
        f.Simulate.Key(Key.Down);
        f.Simulate.Key(Key.Down);
        f.Simulate.Key(Key.Space);
        Assert.Equal([0, 2], list.CheckedIndices);

        var (f2, multi) = Setup(multiSelect: true);
        multi.Focus();
        multi.ItemIndex = 4;
        f2.Simulate.Key(Key.Down, KeyModifiers.Ctrl);     // focus moves alone
        f2.Simulate.Key(Key.Space, KeyModifiers.Ctrl);    // Ctrl+Space toggles the selection of the focused row...
        Assert.Contains(5, multi.SelectedIndices);
        Assert.Empty(multi.CheckedIndices);               // ...not its check
    }

    [Fact]
    public void AllowGrayed_adds_a_third_state_to_the_cycle()
    {
        var (f, list) = Setup(allowGrayed: true);
        var (bx, by) = Box(list, 0);
        f.Simulate.Click(bx, by);
        Assert.Equal(CheckState.Checked, list.States[0]);
        f.Simulate.Click(bx, by);
        Assert.Equal(CheckState.Grayed, list.States[0]);
        Assert.False(list.Checked[0]);                    // Checked means Checked only
        Assert.Empty(list.CheckedIndices);
        f.Simulate.Click(bx, by);
        Assert.Equal(CheckState.Unchecked, list.States[0]);

        var (f2, plain) = Setup();                        // without it: just two states
        var (px, py) = Box(plain, 1);
        f2.Simulate.Click(px, py);
        f2.Simulate.Click(px, py);
        Assert.Equal(CheckState.Unchecked, plain.States[1]);
        plain.States[1] = CheckState.Grayed;              // code may still set it
        Assert.Equal(CheckState.Grayed, plain.States[1]);
    }

    [Fact]
    public void Inserting_and_deleting_items_moves_the_checks_with_them()
    {
        var (_, list) = Setup();
        list.Items = ["a", "b", "c", "d"];
        list.Checked[1] = true;
        list.Checked[3] = true;
        list.InsertItem(0, "new");
        Assert.Equal([2, 4], list.CheckedIndices);
        list.DeleteItem(1);                               // "a", unchecked
        Assert.Equal([1, 3], list.CheckedIndices);
        list.DeleteItem(1);                               // "b", checked
        Assert.Equal([2], list.CheckedIndices);
        Assert.Equal(1, list.CheckedCount);
        list.ClearChecks();
        Assert.Empty(list.CheckedIndices);
        list.Checked[99] = true;                          // out of range: ignored
        Assert.Empty(list.CheckedIndices);
    }

    [Fact]
    public void Keyboard_scrolling_and_the_wheel_are_the_list_boxs()
    {
        var (f, list) = Setup();
        list.Focus();
        list.ItemIndex = 0;
        f.Simulate.Key(Key.End);
        Assert.Equal(29, list.ItemIndex);
        Assert.True(list.TopOffset > 0);
        f.Simulate.Type("item 1");                        // type-ahead
        Assert.StartsWith("Item 1", list.SelectedItem!);
    }

    [Fact]
    public void A_click_on_the_scroll_bar_never_toggles()
    {
        var (f, list) = Setup();
        Assert.True(list.VerticalBarVisible);
        var view = list.Viewport;
        f.Simulate.Click(list.AbsoluteLeft + view.X + view.Width + 4, list.AbsoluteTop + 20);
        Assert.Empty(list.CheckedIndices);
    }

    [Fact]
    public void Boxes_are_hit_at_150_percent_too()
    {
        var (f, list) = Setup(scale: 1.5);
        var (bx, by) = Box(list, 2);
        Assert.Equal(21, list.CheckBoxRect(2).Width);     // 14 px at 100 %
        f.Simulate.Click(bx, by);
        Assert.Equal([2], list.CheckedIndices);
        var (tx, ty) = Text(list, 3);
        f.Simulate.Click(tx, ty);
        Assert.Equal([2], list.CheckedIndices);
    }

    [Fact]
    public void Variable_row_heights_work_in_a_check_list()
    {
        var (f, list) = Setup();
        list.MeasureItem += (_, e) => e.Height = e.Index == 0 ? 50 : 24;
        list.Style = ListBoxStyle.Variable;
        var (bx, by) = Box(list, 1);
        Assert.Equal(list.ItemRect(1).Y + (24 - 14) / 2 + 7, by - list.AbsoluteTop);
        f.Simulate.Click(bx, by);
        Assert.Equal([1], list.CheckedIndices);
    }

    [Fact]
    public void Published_properties_events_and_the_palette()
    {
        var cls = ComponentRegistry.Find("CheckListBox")!;
        Assert.Equal("ListBox", cls.Base!.Name);
        Assert.NotNull(cls.FindProperty("items"));
        Assert.NotNull(cls.FindProperty("multiSelect"));
        var allow = cls.FindProperty("allowGrayed")!;
        Assert.Equal(PropertyType.Bool, allow.Type);
        Assert.Equal(false, allow.GetValue(cls.Prototype));
        var chk = cls.FindProperty("checked")!;
        Assert.Equal(PropertyType.Ints, chk.Type);
        Assert.Equal("Behavior", chk.Category);
        Assert.NotNull(cls.FindEvent("onClickCheck"));
        Assert.NotNull(cls.FindEvent("onChange"));
    }

    [Fact]
    public void The_inspector_edits_checked_as_a_comma_list()
    {
        var cls = ComponentRegistry.Find("CheckListBox")!;
        var chk = cls.FindProperty("checked")!;
        var list = new CheckListBox { Items = ["a", "b", "c", "d"] };
        Assert.True(chk.TryParseValue(list, "3, 1"));
        Assert.Equal([1, 3], list.CheckedIndices);
        Assert.Equal("1, 3", chk.FormatValue(list));
        Assert.False(chk.TryParseValue(list, "1, x"));
        Assert.Equal([1, 3], list.CheckedIndices);       // a bad value changes nothing
        Assert.True(chk.TryParseValue(list, ""));
        Assert.Empty(list.CheckedIndices);
    }

    [Fact]
    public void Checked_indices_round_trip_in_the_form_file()
    {
        var form = new Form { Name = "frmChecks" };
        form.SetBounds(0, 0, 300, 200);
        var list = new CheckListBox { Name = "clb", Items = ["a", "b", "c", "d"], AllowGrayed = true };
        list.Checked[0] = true;
        list.Checked[3] = true;
        list.States[1] = CheckState.Grayed;               // not saved: only checked indices are
        list.SetBounds(10, 10, 150, 100);
        form.Add(list);
        var empty = new CheckListBox { Name = "clbEmpty", Items = ["x"] };
        empty.SetBounds(180, 10, 100, 100);
        form.Add(empty);

        var json = FormWriter.Save(form);
        Assert.Contains("\"checked\": [0, 3]", json);
        Assert.Equal(1, json.Split("\"checked\"").Length - 1);   // an empty list writes none
        var (loaded, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);
        var back = (CheckListBox)loaded.FindControl("clb")!;
        Assert.Equal([0, 3], back.CheckedIndices);
        Assert.True(back.Checked[3]);
        Assert.Equal(json, FormWriter.Save(loaded));

        // "checked" may follow "items" or precede it in a hand-written file; a bad entry is reported and ignored.
        var (f2, w2) = FormReader.Load("""
            { "format": "radform", "version": "1.0", "root": { "class": "Form", "name": "f", "width": 200, "height": 100,
              "children": [ { "class": "CheckListBox", "name": "c", "items": ["a", "b", "c"], "checked": [2] },
                            { "class": "CheckListBox", "name": "d", "checked": [1], "items": ["a", "b", "c"] },
                            { "class": "CheckListBox", "name": "e", "items": ["a"], "checked": ["x"] } ] } }
            """);
        Assert.Single(w2);
        Assert.Equal([2], ((CheckListBox)f2.FindControl("c")!).CheckedIndices);
        Assert.Equal([1], ((CheckListBox)f2.FindControl("d")!).CheckedIndices);
        Assert.Empty(((CheckListBox)f2.FindControl("e")!).CheckedIndices);
    }

    [Fact]
    public void An_index_past_the_items_is_kept_quietly_and_not_saved()
    {
        var list = new CheckListBox { Items = ["a", "b"] };
        list.CheckedIndices = [1, 5, -2];
        Assert.Equal([1], list.CheckedIndices);
        list.CheckedIndices = [int.MaxValue];             // nothing is allocated per index
        Assert.Empty(list.CheckedIndices);
    }

    [Fact]
    public void Replacing_items_with_a_shorter_list_drops_the_checks_past_its_end()
    {
        var list = new CheckListBox { Items = ["a", "b", "c", "d", "e"] };
        list.Checked[0] = true;
        list.Checked[4] = true;
        list.Items = ["x", "y"];
        Assert.Equal([0], list.CheckedIndices);
        list.Items = ["a", "b", "c", "d", "e"];   // the check on the last row must not come back
        Assert.Equal([0], list.CheckedIndices);
        Assert.False(list.Checked[4]);
    }
}
