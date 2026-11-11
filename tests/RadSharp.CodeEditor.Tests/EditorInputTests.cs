using RadSharp.Text;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RadSharp.CodeEditing.Tests;

/// <summary>The editor driven through form.Simulate, the same path real input takes.</summary>
public class EditorInputTests
{
    internal static (Form Form, RadSharp.CodeEditor Editor) Setup(string text = "", int w = 560, int h = 360)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form();
        f.SetBounds(0, 0, w + 20, h + 20);
        var ed = new RadSharp.CodeEditor { Text = text };
        ed.SetBounds(10, 10, w, h);
        f.Add(ed);
        f.PerformLayout();
        ed.Focus();
        return (f, ed);
    }

    static void Type(Form f, string s)
    {
        foreach (var c in s) f.Simulate.Type(c.ToString());
    }

    [Fact]
    public void Typing_backspace_and_undo()
    {
        var (f, ed) = Setup();
        Type(f, "hello");
        Type(f, " world");
        Assert.Equal("hello world", ed.Text);
        f.Simulate.Key(Key.Backspace, KeyModifiers.Ctrl);
        Assert.Equal("hello ", ed.Text);
        f.Simulate.Key("Mod+Z");
        Assert.Equal("hello world", ed.Text);
        f.Simulate.Key("Mod+Z");
        Assert.Equal("hello", ed.Text);
        f.Simulate.Key("Mod+Y");
        Assert.Equal("hello world", ed.Text);
        Assert.Equal(11, ed.CaretOffset);
    }

    [Fact]
    public void Enter_auto_indents_and_splits_braces()
    {
        var (f, ed) = Setup("    if (x)");
        f.Simulate.Key(Key.End);
        f.Simulate.Key(Key.Enter);
        Assert.Equal("    if (x)\n    ", ed.Text);
        Type(f, "{");
        Assert.Equal("    if (x)\n    {}", ed.Text);
        f.Simulate.Key(Key.Enter);
        Assert.Equal("    if (x)\n    {\n        \n    }", ed.Text);
        Assert.Equal(new TextPosition(2, 8), ed.CaretPosition);
    }

    [Fact]
    public void Brackets_auto_close_step_over_and_delete_in_pairs()
    {
        var (f, ed) = Setup();
        Type(f, "f(");
        Assert.Equal("f()", ed.Text);
        Type(f, "a");
        Type(f, ")");
        Assert.Equal("f(a)", ed.Text);
        Assert.Equal(4, ed.CaretOffset);
        Type(f, "[");
        f.Simulate.Key(Key.Backspace);
        Assert.Equal("f(a)", ed.Text);
        Type(f, "\"");
        Assert.Equal("f(a)\"\"", ed.Text);
        Type(f, "s\"");
        Assert.Equal("f(a)\"s\"", ed.Text);
        // Surround a selection.
        ed.Select(2, 1);
        Type(f, "[");
        Assert.Equal("f([a])\"s\"", ed.Text);
    }

    [Fact]
    public void Smart_home_and_word_movement()
    {
        var (f, ed) = Setup("    var total = price * count;");
        f.Simulate.Key(Key.End);
        f.Simulate.Key(Key.Home);
        Assert.Equal(4, ed.CaretOffset);
        f.Simulate.Key(Key.Home);
        Assert.Equal(0, ed.CaretOffset);
        f.Simulate.Key(Key.Home);
        Assert.Equal(4, ed.CaretOffset);
        f.Simulate.Key(Key.Right, KeyModifiers.Ctrl);
        Assert.Equal(8, ed.CaretOffset);
        f.Simulate.Key(Key.Right, KeyModifiers.Ctrl | KeyModifiers.Shift);
        Assert.Equal("total ", ed.SelectedText);
        f.Simulate.Key(Key.Left, KeyModifiers.Ctrl);
        Assert.Equal(8, ed.CaretOffset);
    }

    [Fact]
    public void Tab_and_shift_tab_indent_blocks()
    {
        var (f, ed) = Setup("a\nb\nc");
        ed.Select(0, 3);                       // "a\nb"
        f.Simulate.Key(Key.Tab);
        Assert.Equal("    a\n    b\nc", ed.Text);
        f.Simulate.Key(Key.Tab, KeyModifiers.Shift);
        Assert.Equal("a\nb\nc", ed.Text);
        f.Simulate.Key("Mod+Z");
        Assert.Equal("    a\n    b\nc", ed.Text);
        ed.Select(ed.Text.Length, 0);
        f.Simulate.Key(Key.Tab);
        Assert.Equal("    a\n    b\nc   ", ed.Text);  // to the next tab stop
    }

    [Fact]
    public void Ctrl_slash_toggles_line_comments_with_the_prefix_property()
    {
        var (f, ed) = Setup("    x = 1;\n    y = 2;\nz");
        ed.Select(2, 14);
        f.Simulate.Key(Key.Slash, KeyModifiers.Ctrl);
        Assert.Equal("    // x = 1;\n    // y = 2;\nz", ed.Text);
        f.Simulate.Key(Key.Slash, KeyModifiers.Ctrl);
        Assert.Equal("    x = 1;\n    y = 2;\nz", ed.Text);
        ed.LineCommentPrefix = "#";
        ed.Select(ed.Text.Length, 0);
        f.Simulate.Key(Key.Slash, KeyModifiers.Ctrl);
        Assert.Equal("    x = 1;\n    y = 2;\n# z", ed.Text);
    }

    [Fact]
    public void Duplicate_and_move_lines()
    {
        var (f, ed) = Setup("one\ntwo\nthree");
        ed.GoTo(1, 1);
        f.Simulate.Key(Key.D, KeyModifiers.Ctrl | KeyModifiers.Shift);
        Assert.Equal("one\ntwo\ntwo\nthree", ed.Text);
        Assert.Equal(new TextPosition(2, 1), ed.CaretPosition);
        f.Simulate.Key(Key.Down, KeyModifiers.Alt);
        Assert.Equal("one\ntwo\nthree\ntwo", ed.Text);
        Assert.Equal(new TextPosition(3, 1), ed.CaretPosition);
        f.Simulate.Key(Key.Up, KeyModifiers.Alt);
        f.Simulate.Key(Key.Up, KeyModifiers.Alt);
        f.Simulate.Key(Key.Up, KeyModifiers.Alt);
        Assert.Equal("two\none\ntwo\nthree", ed.Text);
        Assert.Equal(new TextPosition(0, 1), ed.CaretPosition);
        f.Simulate.Key(Key.Up, KeyModifiers.Alt);     // already at the top: nothing happens
        Assert.Equal("two\none\ntwo\nthree", ed.Text);
    }

    [Fact]
    public void Alt_click_adds_carets_and_typing_goes_to_all()
    {
        var (f, ed) = Setup("alpha\nbeta\ngamma");
        var (x0, y0) = ed.PointOfOffset(ed.Snapshot.GetOffset(0, 5));
        var (x1, y1) = ed.PointOfOffset(ed.Snapshot.GetOffset(1, 4));
        int ox = ed.AbsoluteLeft, oy = ed.AbsoluteTop, lh = ed.LineHeight;
        f.Simulate.Click(ox + x0 + 1, oy + y0 + lh / 2);
        f.Simulate.Click(ox + x1 + 1, oy + y1 + lh / 2, modifiers: KeyModifiers.Alt);
        Assert.Equal(2, ed.Selections.Count);
        Type(f, "!");
        Assert.Equal("alpha!\nbeta!\ngamma", ed.Text);
        f.Simulate.Key(Key.Backspace);
        Assert.Equal("alpha\nbeta\ngamma", ed.Text);
        f.Simulate.Key(Key.Escape);
        Assert.Single(ed.Selections);
    }

    [Fact]
    public void Ctrl_d_selects_next_occurrences()
    {
        var (f, ed) = Setup("var a = foo + foo * foo;");
        ed.GoTo(0, 9);
        f.Simulate.Key(Key.D, KeyModifiers.Ctrl);
        Assert.Equal("foo", ed.SelectedText);
        f.Simulate.Key(Key.D, KeyModifiers.Ctrl);
        f.Simulate.Key(Key.D, KeyModifiers.Ctrl);
        Assert.Equal(3, ed.Selections.Count);
        Type(f, "bar");
        Assert.Equal("var a = bar + bar * bar;", ed.Text);
        f.Simulate.Key("Mod+Z");
        Assert.Equal("var a = foo + foo * foo;", ed.Text);
    }

    [Fact]
    public void Column_selection_with_shift_alt_arrows_and_alt_drag()
    {
        var (f, ed) = Setup("abcdef\nabcdef\nabcdef\nab");
        ed.GoTo(0, 1);
        f.Simulate.Key(Key.Down, KeyModifiers.Shift | KeyModifiers.Alt);
        f.Simulate.Key(Key.Down, KeyModifiers.Shift | KeyModifiers.Alt);
        f.Simulate.Key(Key.Right, KeyModifiers.Shift | KeyModifiers.Alt);
        f.Simulate.Key(Key.Right, KeyModifiers.Shift | KeyModifiers.Alt);
        Assert.Equal(3, ed.Selections.Count);
        Assert.All(ed.Selections, s => Assert.Equal(2, s.Length));
        Type(f, "X");
        Assert.Equal("aXdef\naXdef\naXdef\nab", ed.Text);

        // Alt+drag from line 0 col 2 to line 3 col 4: the short last line clamps.
        var (sx, sy) = ed.PointOfOffset(ed.Snapshot.GetOffset(0, 2));
        var (ex, ey) = ed.PointOfOffset(ed.Snapshot.GetOffset(2, 4));
        int ox = ed.AbsoluteLeft, oy = ed.AbsoluteTop, lh = ed.LineHeight;
        f.Simulate.Press(ox + sx + 1, oy + sy + lh / 2, modifiers: KeyModifiers.Alt);
        f.Simulate.MoveTo(ox + ex + 1, oy + ey + lh + lh / 2);
        f.Simulate.Release(ox + ex + 1, oy + ey + lh + lh / 2);
        Assert.Equal(4, ed.Selections.Count);
        Assert.Equal("de", ed.Snapshot.GetText(ed.Selections[0].Start, ed.Selections[0].Length));
        Assert.True(ed.Selections[3].IsEmpty);
    }

    [Fact]
    public void Clipboard_cut_copy_paste_including_whole_lines_and_per_caret_paste()
    {
        var (f, ed) = Setup("first\nsecond");
        ed.GoTo(0, 2);
        f.Simulate.Key("Mod+X");                    // nothing selected: cuts the line
        Assert.Equal("second", ed.Text);
        Assert.Equal("first\n", Clipboard.GetText());
        f.Simulate.Key("Mod+V");
        Assert.Equal("first\nsecond", ed.Text);
        ed.Select(0, 5);
        f.Simulate.Key("Mod+C");
        Assert.Equal("first", Clipboard.GetText());

        var (f2, ed2) = Setup("a\nb");
        Clipboard.SetText("1\n2");
        ed2.SetSelections([TextSelection.At(1), TextSelection.At(3)]);
        f2.Simulate.Key("Mod+V");
        Assert.Equal("a1\nb2", ed2.Text);
    }

    [Fact]
    public void Ime_composition_shows_inline_and_commits_through_text_input()
    {
        var (f, ed) = Setup("x = ;");
        ed.GoTo(0, 4);
        f.Simulate.Compose("にほ", 2);
        Assert.True(ed.IsComposing);
        Assert.Equal("x = ;", ed.Text);
        f.Simulate.Key(Key.Left);                         // owned by the input method while composing
        Assert.Equal(4, ed.CaretOffset);
        Assert.NotNull(f.TextInputArea);
        using (FormRendering.Render(f)) { }               // paints with the composition spliced in
        f.Simulate.Compose("日本", 2);
        f.Simulate.Compose("", 0);
        f.Simulate.Type("日本");
        Assert.False(ed.IsComposing);
        Assert.Equal("x = 日本;", ed.Text);
        Assert.Equal(6, ed.CaretOffset);
    }

    [Fact]
    public void Mouse_selects_words_lines_and_drag_ranges()
    {
        var (f, ed) = Setup("hello brave world\nnext");
        var (x, y) = ed.PointOfOffset(8);
        int ox = ed.AbsoluteLeft, oy = ed.AbsoluteTop, lh = ed.LineHeight;
        f.Simulate.DoubleClick(ox + x, oy + y + lh / 2);
        Assert.Equal("brave", ed.SelectedText);
        var (x2, _) = ed.PointOfOffset(2);
        var (x3, _) = ed.PointOfOffset(9);
        f.Simulate.Drag(ox + x2, oy + y + lh / 2, ox + x3, oy + y + lh / 2);
        Assert.Equal("llo bra", ed.SelectedText);
        // A click on a line number selects that line.
        f.Simulate.Click(ox + 22, oy + lh + lh / 2);    // the glyph lane is 18 px; then the numbers
        Assert.Equal("next", ed.SelectedText);
    }

    [Fact]
    public void Wheel_and_keyboard_scroll()
    {
        var (f, ed) = Setup(string.Join("\n", Enumerable.Range(1, 500).Select(i => $"line {i}")), 300, 200);
        f.Simulate.Wheel(ed.AbsoluteLeft + 100, ed.AbsoluteTop + 50, -2);
        Assert.Equal(6 * ed.LineHeight, ed.ScrollY);
        Assert.Equal(6, ed.FirstVisibleLine);
        f.Simulate.Key(Key.End, KeyModifiers.Ctrl);
        Assert.Equal(499, ed.CaretPosition.Line);
        Assert.True(ed.FirstVisibleLine > 480);
        f.Simulate.Key(Key.PageUp);
        Assert.True(ed.CaretPosition.Line < 499);
    }

    [Fact]
    public void Find_and_replace_bar()
    {
        var (f, ed) = Setup("Cat cat CAT concat");
        f.Simulate.Key("Mod+F");
        Assert.True(ed.FindBarVisible);
        f.Simulate.Type("cat");
        Assert.Equal(4, ed.FindMatches.Count);
        f.Simulate.Key(Key.C, KeyModifiers.Alt);            // match case
        Assert.Equal(2, ed.FindMatches.Count);
        f.Simulate.Key(Key.W, KeyModifiers.Alt);            // whole word
        Assert.Single(ed.FindMatches);
        f.Simulate.Key(Key.Enter);
        Assert.Equal(4, ed.Selection.Start);
        f.Simulate.Key(Key.W, KeyModifiers.Alt);
        f.Simulate.Key(Key.C, KeyModifiers.Alt);
        ed.FindUseRegex = true;
        ed.FindText = "(c)at";
        Assert.Equal(4, ed.FindMatches.Count);
        Assert.Equal(4, ed.ReplaceAll("$1og"));
        Assert.Equal("Cog cog Cog concog", ed.Text);
        f.Simulate.Key(Key.Escape);
        Assert.False(ed.FindBarVisible);
        Assert.True(ed.Focused);
        f.Simulate.Key("Mod+Z");
        Assert.Equal("Cat cat CAT concat", ed.Text);

        f.Simulate.Key("Mod+H");
        Assert.True(ed.FindBar!.ShowReplace);
        ed.FindUseRegex = false;
        ed.FindText = "cat";
        ed.FindBar.ReplaceBox.Text = "dog";
        ed.Select(0, 0);
        ed.FindBar.ReplaceBox.Focus();
        f.Simulate.Key(Key.Enter);                          // first press finds, then replaces
        f.Simulate.Key(Key.Enter);
        Assert.Equal("dog cat CAT concat", ed.Text);
    }

    [Fact]
    public void Folding_hides_lines_and_gutter_click_reports_lines()
    {
        var (f, ed) = Setup("class A\n{\n    void F()\n    {\n    }\n}\nend");
        Assert.Contains(ed.Folds, x => x.Range.StartLine == 0 && x.Range.EndLine == 5);
        Assert.True(ed.ToggleFold(0));
        int clicked = -1;
        GutterArea area = default;
        ed.GutterClick += (_, e) => { clicked = e.Line; area = e.Area; };
        f.Simulate.Click(ed.AbsoluteLeft + 4, ed.AbsoluteTop + ed.LineHeight + ed.LineHeight / 2);
        Assert.Equal(6, clicked);
        Assert.Equal(GutterArea.Glyphs, area);
        // Moving down from the folded header skips the hidden lines.
        ed.GoTo(0, 0);
        f.Simulate.Key(Key.Down);
        Assert.Equal(6, ed.CaretPosition.Line);
        // Revealing a hidden line expands its fold.
        ed.GoTo(3, 0);
        Assert.False(ed.Folds.Single(x => x.Range.StartLine == 0).Collapsed);
    }

    [Fact]
    public void Text_changed_and_caret_moved_events()
    {
        var (f, ed) = Setup("abc");
        var versions = new List<int>();
        int moves = 0;
        ed.TextChanged += (_, e) => versions.Add(e.Version);
        ed.CaretMoved += _ => moves++;
        f.Simulate.Key(Key.End);
        Type(f, "d");
        Assert.Single(versions);
        Assert.Equal(ed.Snapshot.Version, versions[0]);
        Assert.True(moves >= 2);
        ed.ApplyEdits([TextEdit.Insert(0, ">")]);
        Assert.Equal(">abcd", ed.Text);
        Assert.Equal(5, ed.CaretOffset);                    // carried along by the programmatic edit
    }

    [Fact]
    public void Read_only_blocks_editing_but_not_navigation()
    {
        var (f, ed) = Setup("abc");
        ed.ReadOnly = true;
        Type(f, "x");
        f.Simulate.Key(Key.Backspace);
        f.Simulate.Key(Key.End);
        Assert.Equal("abc", ed.Text);
        Assert.Equal(3, ed.CaretOffset);
    }

    [Fact]
    public void Crlf_text_keeps_crlf_when_editing()
    {
        var (f, ed) = Setup("a\r\nb");
        f.Simulate.Key(Key.End, KeyModifiers.Ctrl);
        f.Simulate.Key(Key.Enter);
        Type(f, "c");
        Assert.Equal("a\r\nb\r\nc", ed.Text);
        ed.GoTo(0, 1);
        f.Simulate.Key(Key.Right);
        Assert.Equal(new TextPosition(1, 0), ed.CaretPosition);
        f.Simulate.Key(Key.Backspace);
        Assert.Equal("ab\r\nc", ed.Text);
    }
}

static class FormRendering
{
    public static SkiaSharp.SKBitmap Render(Form f) => RadSharp.Drawing.FormRenderer.RenderToBitmap(f);
}
