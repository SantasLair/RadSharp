using RadSharp.Text;

namespace RadSharp.Tests;

/// <summary>ComboBox.Style = DropDown (editable text part), autocomplete, and that the drop-down-list style is unchanged.</summary>
[Collection("Clipboard")]   // paste goes through the process-wide clipboard
public class ComboBoxEditTests
{
    static readonly string[] Fruit = ["Apple", "Apricot", "Banana", "Blueberry", "Cherry"];

    static (Form Form, ComboBox Combo) Setup(bool editable = true, double scale = 1)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form { Scale = scale };
        f.SetBounds(0, 0, 500, 400);
        var combo = new ComboBox { Items = Fruit, Style = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList };
        combo.SetBounds(10, 10, 160, 26);
        f.Add(combo);
        var other = new Edit { TabStop = true };   // a tab stop on either side
        other.SetBounds(10, 60, 100, 26);
        f.Add(other);
        f.PerformLayout();
        return (f, combo);
    }

    static void Click(Form f, Control c, int dx, int dy) => f.Simulate.Click(c.AbsoluteLeft + dx, c.AbsoluteTop + dy);

    [Fact]
    public void The_default_style_is_a_drop_down_list_with_no_edit_part_and_unchanged_behaviour()
    {
        var (f, combo) = Setup(editable: false);
        Assert.Equal(ComboBoxStyle.DropDownList, combo.Style);
        Assert.Null(combo.EditPart);
        Assert.Empty(combo.Children);
        combo.ItemIndex = 1;
        Assert.Equal("Apricot", combo.Text);
        combo.Focus();
        f.Simulate.Key(Key.End);
        Assert.Equal("Cherry", combo.Text);
        f.Simulate.Key(Key.Home);
        Assert.Equal("Apple", combo.Text);
        f.Simulate.Type("b");                  // type-ahead: the next item starting with b
        Assert.Equal("Banana", combo.Text);
        f.Simulate.Type("b");
        Assert.Equal("Blueberry", combo.Text);
        f.Simulate.Type("b");
        Assert.Equal("Banana", combo.Text);    // wraps
        combo.Text = "Cherry";                 // a list-style Text setter selects the item
        Assert.Equal(4, combo.ItemIndex);
        combo.Text = "Mango";                  // not an item: ignored
        Assert.Equal(4, combo.ItemIndex);
    }

    [Fact]
    public void Editable_style_hosts_a_transient_edit_and_text_is_the_edits()
    {
        var (f, combo) = Setup();
        var edit = Assert.IsAssignableFrom<Edit>(combo.EditPart);
        Assert.True(edit.IsTransient);
        Assert.DoesNotContain(combo, f.GetTabOrderList());   // the edit is the tab stop, once
        Assert.Contains(edit, f.GetTabOrderList());
        Assert.Equal(BorderStyle.None, edit.BorderStyle);
        // Left of the arrow button, inside the border.
        Assert.Equal(1, edit.Left);
        Assert.Equal(160 - 2 - 22, edit.Width);
        Assert.Equal(26 - 2, edit.Height);

        int changes = 0;
        combo.Change += _ => changes++;
        combo.Text = "hello";
        Assert.Equal("hello", edit.Text);
        Assert.Equal(-1, combo.ItemIndex);
        Assert.Equal(1, changes);
        combo.Text = "hello";                 // no change, no event
        Assert.Equal(1, changes);
        combo.Text = "Banana";                // an item's text selects it
        Assert.Equal(2, combo.ItemIndex);
        Assert.Equal(2, changes);
        combo.ItemIndex = 4;                  // choosing an item fills the edit and selects all of it
        Assert.Equal("Cherry", combo.Text);
        Assert.Equal(6, combo.SelectionLength);
        Assert.Equal(3, changes);
        Assert.DoesNotContain(f.Children, c => c.IsTransient);   // the edit is the combo box's child, not the form's
    }

    [Fact]
    public void Clicking_the_text_part_focuses_the_edit_and_typing_edits_it()
    {
        var (f, combo) = Setup();
        Click(f, combo, 20, 13);
        Assert.True(combo.EditPart!.Focused);
        Assert.False(combo.IsDroppedDown);
        combo.AutoComplete = false;
        f.Simulate.Type("zebra");
        Assert.Equal("zebra", combo.Text);
        f.Simulate.Key(Key.Backspace);
        f.Simulate.Key(Key.Left, KeyModifiers.Ctrl);
        f.Simulate.Type("big ");
        Assert.Equal("big zebr", combo.Text);
        f.Simulate.Key(Key.Z, KeyModifiers.Ctrl);   // the edit's undo
        Assert.NotEqual("big zebr", combo.Text);
        f.Simulate.Key(Key.A, KeyModifiers.Ctrl);
        Assert.Equal(combo.Text.Length, combo.SelectionLength);
    }

    [Fact]
    public void Clipboard_and_the_context_menu_belong_to_the_edit()
    {
        var (f, combo) = Setup();
        combo.AutoComplete = false;
        combo.Text = "copy me";
        combo.EditPart!.Focus();
        f.Simulate.Key(Key.A, KeyModifiers.Ctrl);
        f.Simulate.Key(Key.C, KeyModifiers.Ctrl);
        Assert.Equal("copy me", Clipboard.GetText());
        f.Simulate.Key(Key.End);
        f.Simulate.Key(Key.V, KeyModifiers.Ctrl);
        Assert.Equal("copy mecopy me", combo.Text);
        // Right-click on the text opens the edit's built-in menu.
        f.Simulate.Click(combo.AbsoluteLeft + 20, combo.AbsoluteTop + 13, MouseButton.Right);
        Assert.NotEmpty(f.Popups);
        f.Simulate.Key(Key.Escape);
    }

    [Fact]
    public void The_arrow_button_alt_down_and_f4_open_the_list_and_choosing_fills_the_edit()
    {
        var (f, combo) = Setup();
        Click(f, combo, 160 - 10, 13);                    // the arrow button
        Assert.True(combo.IsDroppedDown);
        Assert.True(combo.EditPart!.Focused);             // focus went to the text part
        var list = Assert.IsType<ListBox>(f.Popups.Single());
        Click(f, list, 20, list.ItemRect(3).Y + 5);
        Assert.Equal("Blueberry", combo.Text);
        Assert.False(combo.IsDroppedDown);
        Assert.Equal(3, combo.ItemIndex);

        f.Simulate.Key(Key.Down, KeyModifiers.Alt);
        Assert.True(combo.IsDroppedDown);
        f.Simulate.Key(Key.F4);                           // toggles it closed again while the edit has focus
        Assert.False(combo.IsDroppedDown);
        f.Simulate.Key(Key.F4);
        Assert.True(combo.IsDroppedDown);
        f.Simulate.Key(Key.Escape);
        Assert.False(combo.IsDroppedDown);
        Assert.Equal("Blueberry", combo.Text);
    }

    [Fact]
    public void Up_and_down_cycle_through_the_items_and_home_end_stay_with_the_caret()
    {
        var (f, combo) = Setup();
        combo.EditPart!.Focus();
        f.Simulate.Key(Key.Down);
        Assert.Equal("Apple", combo.Text);                // from no item to the first
        f.Simulate.Key(Key.Down);
        Assert.Equal("Apricot", combo.Text);
        f.Simulate.Key(Key.Up);
        f.Simulate.Key(Key.Up);
        Assert.Equal("Apple", combo.Text);
        combo.AutoComplete = false;
        f.Simulate.Key(Key.End);
        Assert.Equal("Apple", combo.Text);                // End moved the caret, not the item
        Assert.Equal(5, combo.EditPart.CaretPosition);
    }

    [Fact]
    public void Typing_completes_to_the_first_match_and_selects_the_added_part()
    {
        var (f, combo) = Setup();
        combo.EditPart!.Focus();
        f.Simulate.Type("a");
        Assert.Equal("Apple", combo.Text);
        Assert.Equal(1, combo.SelectionStart);
        Assert.Equal("pple", combo.SelectedText);
        Assert.Equal(0, combo.ItemIndex);                 // the completed item is the current one

        f.Simulate.Type("p");                             // overwrites the selected suffix: "Ap" -> "Apple"
        Assert.Equal("Apple", combo.Text);
        Assert.Equal("ple", combo.SelectedText);
        f.Simulate.Type("r");                             // "Apr" -> "Apricot"
        Assert.Equal("Apricot", combo.Text);
        Assert.Equal("icot", combo.SelectedText);
        Assert.Equal(1, combo.ItemIndex);

        f.Simulate.Key(Key.Backspace);                    // deletes the selection and does not complete again
        Assert.Equal("Apr", combo.Text);
        Assert.Equal(-1, combo.ItemIndex);
    }

    [Fact]
    public void Autocomplete_takes_the_items_letter_case_and_stops_at_no_match()
    {
        var (f, combo) = Setup();
        combo.EditPart!.Focus();
        f.Simulate.Type("B");
        Assert.Equal("Banana", combo.Text);
        f.Simulate.Type("L");                             // case-insensitive match, the item's own case
        Assert.Equal("Blueberry", combo.Text);
        Assert.Equal("ueberry", combo.SelectedText);
        f.Simulate.Type("x");                             // nothing starts with "Blx": plain text
        Assert.Equal("Blx", combo.Text);
        Assert.Equal(0, combo.SelectionLength);
        Assert.Equal(-1, combo.ItemIndex);
        f.Simulate.Key(Key.Delete);                       // Delete never completes
        f.Simulate.Key(Key.Backspace);
        Assert.Equal("Bl", combo.Text);
        Assert.Equal(-1, combo.ItemIndex);
    }

    [Fact]
    public void Autocomplete_does_not_run_for_paste_code_or_when_switched_off()
    {
        var (f, combo) = Setup();
        combo.EditPart!.Focus();
        Clipboard.SetText("Ch");
        f.Simulate.Key(Key.V, KeyModifiers.Ctrl);
        Assert.Equal("Ch", combo.Text);                   // pasted, not completed
        combo.Text = "Ba";
        Assert.Equal("Ba", combo.Text);
        Assert.Equal(0, combo.SelectionLength);

        combo.AutoComplete = false;
        combo.Text = "";
        f.Simulate.Type("a");
        Assert.Equal("a", combo.Text);
    }

    [Fact]
    public void Typing_raises_one_change_even_when_a_completion_follows()
    {
        var (f, combo) = Setup();
        combo.EditPart!.Focus();
        int changes = 0;
        string? last = null;
        combo.Change += c => { changes++; last = ((ComboBox)c).Text; };
        f.Simulate.Type("a");
        Assert.Equal(1, changes);
        Assert.Equal("Apple", last);
        f.Simulate.Type("n");                             // "An": no match
        Assert.Equal(2, changes);
    }

    [Fact]
    public void The_open_list_tracks_the_match_and_typing_still_reaches_the_edit()
    {
        var (f, combo) = Setup();
        combo.EditPart!.Focus();
        combo.DropDown();
        var list = Assert.IsType<ListBox>(f.Popups.Single());
        Assert.Equal(-1, list.ItemIndex);
        f.Simulate.Type("b");
        Assert.Equal("Banana", combo.Text);
        Assert.Equal(2, list.ItemIndex);
        f.Simulate.Type("l");
        Assert.Equal(3, list.ItemIndex);
        Assert.True(combo.IsDroppedDown);

        f.Simulate.Key(Key.Left);                         // caret keys reach the edit, not the list
        Assert.Equal(2, combo.SelectionStart);            // the selected completion collapsed to its start
        Assert.Equal(3, list.ItemIndex);
        f.Simulate.Key(Key.Down);                         // Up/Down move through the list
        Assert.Equal(4, list.ItemIndex);
        f.Simulate.Key(Key.Enter);                        // Enter chooses
        Assert.Equal("Cherry", combo.Text);
        Assert.False(combo.IsDroppedDown);
    }

    [Fact]
    public void Enter_with_nothing_highlighted_just_closes_the_list_and_keeps_the_text()
    {
        var (f, combo) = Setup();
        combo.AutoComplete = false;
        combo.EditPart!.Focus();
        f.Simulate.Type("zzz");
        combo.DropDown();
        Assert.True(combo.IsDroppedDown);
        f.Simulate.Key(Key.Enter);
        Assert.False(combo.IsDroppedDown);
        Assert.Equal("zzz", combo.Text);
    }

    [Fact]
    public void Tab_moves_through_the_combo_box_once_in_each_direction()
    {
        var (f, combo) = Setup();
        var after = f.Children.OfType<Edit>().Single();
        combo.Focus();                                    // lands in the edit
        Assert.True(combo.EditPart!.Focused);
        f.Simulate.Key(Key.Tab);
        Assert.True(after.Focused);
        f.Simulate.Key(Key.Tab, KeyModifiers.Shift);
        Assert.True(combo.EditPart.Focused);
        f.Simulate.Key(Key.Tab, KeyModifiers.Shift);      // wraps to the last stop, then back
        Assert.True(after.Focused);
    }

    [Fact]
    public void Style_round_trips_through_the_form_file_in_any_property_order()
    {
        var form = new Form { Name = "frmCombo" };
        form.SetBounds(0, 0, 300, 200);
        var combo = new ComboBox { Name = "cboFree", Items = Fruit, Style = ComboBoxStyle.DropDown, Text = "Custom", AutoComplete = false };
        combo.SetBounds(10, 10, 150, 26);
        form.Add(combo);
        var list = new ComboBox { Name = "cboList", Items = Fruit };
        list.SetBounds(10, 50, 150, 26);
        form.Add(list);
        var json = FormWriter.Save(form);
        Assert.Contains("\"style\": \"dropDown\"", json);
        Assert.Contains("\"text\": \"Custom\"", json);
        Assert.Contains("\"autoComplete\": false", json);
        Assert.DoesNotContain("\"Edit\"", json);                        // the transient edit is never written
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"style\""));   // the list-style combo writes no style

        var (loaded, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);
        var back = (ComboBox)loaded.FindControl("cboFree")!;
        Assert.Equal(ComboBoxStyle.DropDown, back.Style);
        Assert.Equal("Custom", back.Text);
        Assert.False(back.AutoComplete);
        Assert.Equal(json, FormWriter.Save(loaded));

        // "text" before "style" (hand-edited file) loads the same.
        const string scrambled = """
            { "format": "radform", "version": "1.0", "root": { "class": "Form", "name": "f", "width": 300, "height": 100,
              "children": [ { "class": "ComboBox", "name": "c", "text": "Hi", "style": "dropDown", "items": ["Hi", "Ho"] } ] } }
            """;
        var (f2, w2) = FormReader.Load(scrambled);
        Assert.Empty(w2);
        var c2 = (ComboBox)f2.FindControl("c")!;
        Assert.Equal("Hi", c2.Text);
        Assert.Equal(0, c2.ItemIndex);
    }

    [Fact]
    public void An_old_drop_down_list_form_loads_and_saves_unchanged()
    {
        const string old = """
            {
              "format": "radform",
              "version": "1.0",
              "generator": "RADSharp",
              "root": {
                "class": "Form",
                "name": "frmOld",
                "height": 100,
                "width": 300,
                "children": [
                  {
                    "class": "ComboBox",
                    "name": "cboSize",
                    "dropDownCount": 3,
                    "items": [
                      "Small",
                      "Medium"
                    ],
                    "left": 10,
                    "top": 10,
                    "width": 150
                  }
                ]
              }
            }
            """;
        var (form, warnings) = FormReader.Load(old.ReplaceLineEndings("\n") + "\n");
        Assert.Empty(warnings);
        var combo = (ComboBox)form.FindControl("cboSize")!;
        Assert.Equal(ComboBoxStyle.DropDownList, combo.Style);
        Assert.True(combo.AutoComplete);
        Assert.Equal(old.ReplaceLineEndings("\n") + "\n", FormWriter.Save(form));
    }

    [Fact]
    public void Properties_are_published_and_the_inspector_shows_them()
    {
        var cls = ComponentRegistry.Find("ComboBox")!;
        Assert.Equal(["dropDown", "dropDownList"], cls.FindProperty("style")!.Values!.Order().ToArray());
        Assert.Equal(ComboBoxStyle.DropDownList, cls.FindProperty("style")!.GetValue(cls.Prototype));
        Assert.Equal("", cls.FindProperty("text")!.GetValue(cls.Prototype));
        Assert.Equal(true, cls.FindProperty("autoComplete")!.GetValue(cls.Prototype));
        Assert.Equal(PropertyType.String, cls.FindProperty("text")!.Type);

        var f = new Form();
        f.SetBounds(0, 0, 600, 900);
        var combo = new ComboBox { Name = "cbo", Items = Fruit };
        combo.SetBounds(400, 10, 150, 26);
        f.Add(combo);
        var grid = new PropertyGrid();
        grid.SetBounds(10, 10, 300, 860);
        f.Add(grid);
        f.PerformLayout();
        grid.Target = combo;
        Assert.True(grid.IndexOf("style") >= 0);
        Assert.True(grid.IndexOf("autoComplete") >= 0);
        Assert.True(grid.IndexOf("text") >= 0);
        Assert.Equal("dropDownList", grid.RowValue(grid.IndexOf("style")));
        var style = cls.FindProperty("style")!;
        Assert.True(style.TryParseValue(combo, "dropDown"));
        Assert.Equal(ComboBoxStyle.DropDown, combo.Style);
        Assert.NotNull(combo.EditPart);
    }

    [Fact]
    public void A_color_box_stays_a_drop_down_list()
    {
        var box = new ColorBox();
        box.Style = ComboBoxStyle.DropDown;
        Assert.Equal(ComboBoxStyle.DropDownList, box.Style);
        Assert.Null(box.EditPart);
    }

    [Fact]
    public void Editing_works_at_150_percent_and_the_edit_follows_resizes()
    {
        var (f, combo) = Setup(scale: 1.5);
        var edit = combo.EditPart!;
        int w0 = edit.Width;
        Assert.Equal(combo.Width - 2 * 2 - 33 + 1, edit.Width + 1);     // border 1.5 -> 2 px, button 33 px (rounded)
        combo.SetBounds(10, 10, 300, 39);
        Assert.True(edit.Width > w0);
        combo.EditPart!.Focus();
        f.Simulate.Type("c");
        Assert.Equal("Cherry", combo.Text);
        Assert.Equal("herry", combo.SelectedText);
        Click(f, combo, combo.Width - 15, 20);
        Assert.True(combo.IsDroppedDown);
        var list = Assert.IsType<ListBox>(f.Popups.Single());
        Assert.Equal(4, list.ItemIndex);
        Assert.Equal(36, list.ItemRect(1).Height);
    }

    [Fact]
    public void Switching_the_style_back_removes_the_edit()
    {
        var (_, combo) = Setup();
        combo.Text = "Banana";
        combo.Style = ComboBoxStyle.DropDownList;
        Assert.Null(combo.EditPart);
        Assert.Empty(combo.Children);
        Assert.Equal("Banana", combo.Text);                // the selected item
        combo.Style = ComboBoxStyle.DropDown;
        Assert.Equal("Banana", combo.Text);                // seeded from the selected item
    }

    [Fact]
    public void Autocomplete_does_not_run_past_an_item_that_equals_the_typed_text()
    {
        var (f, combo) = Setup();
        combo.Items = ["app", "apple"];
        combo.EditPart!.Focus();
        f.Simulate.Type("a");
        f.Simulate.Type("p");
        f.Simulate.Type("p");
        Assert.Equal("app", combo.Text);
        Assert.Equal(0, combo.ItemIndex);
        f.Simulate.Type("l");
        Assert.Equal("apple", combo.Text);
    }
}
