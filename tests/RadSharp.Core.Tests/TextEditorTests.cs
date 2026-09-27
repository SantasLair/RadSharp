using RadSharp.Drawing;
using RadSharp.Text;

namespace RadSharp.Tests;

public class TextEditorTests
{
    const string Family = "\U0001F468‍\U0001F469‍\U0001F467";  // 👨‍👩‍👧: one grapheme, 8 UTF-16 units
    const string EAcute = "é";                                       // e + combining acute: one grapheme

    static TextEditor Ed(string text, bool multiline = false)
    {
        var e = new TextEditor { Multiline = multiline };
        e.SetText(text);
        return e;
    }

    [Fact]
    public void Caret_moves_by_grapheme_over_emoji_and_combining_marks()
    {
        var e = Ed("a" + Family + EAcute + "b");
        e.MoveTo(0);
        e.MoveTo(e.NextBoundary(e.Caret));
        Assert.Equal(1, e.Caret);
        e.MoveTo(e.NextBoundary(e.Caret));
        Assert.Equal(1 + Family.Length, e.Caret);
        e.MoveTo(e.NextBoundary(e.Caret));
        Assert.Equal(1 + Family.Length + 2, e.Caret);
        Assert.Equal(4, e.GraphemeCount);

        e.MoveTo(5);  // inside the emoji: snaps back to its start
        Assert.Equal(1, e.Caret);
    }

    [Fact]
    public void Backspace_deletes_a_whole_grapheme()
    {
        var e = Ed("x" + Family);
        Assert.True(e.DeleteBackward());
        Assert.Equal("x", e.Text);
        e.SetText("caf" + EAcute);
        e.DeleteBackward();
        Assert.Equal("caf", e.Text);
    }

    [Fact]
    public void Word_movement_and_deletion()
    {
        var e = Ed("hello, big world");
        e.MoveTo(0);
        Assert.Equal(5, e.NextWordStart(0));       // end of "hello" run, then no space
        Assert.Equal(7, e.NextWordStart(5));       // skip ", " → "big"
        Assert.Equal(11, e.NextWordStart(7));      // "big " → "world"
        Assert.Equal(7, e.PreviousWordStart(11));
        Assert.Equal(0, e.PreviousWordStart(5));
        e.MoveTo(16);
        e.DeleteBackward(word: true);
        Assert.Equal("hello, big ", e.Text);
        Assert.Equal((7, 10), e.WordAt(8));
    }

    [Fact]
    public void Typing_coalesces_into_one_undo_step_per_word()
    {
        var e = Ed("");
        foreach (var ch in "hi there") e.Insert(ch.ToString());
        Assert.Equal("hi there", e.Text);
        Assert.True(e.Undo());
        Assert.Equal("hi", e.Text);   // " there" was one step (the space starts a new one)
        Assert.True(e.Undo());
        Assert.Equal("", e.Text);
        Assert.False(e.Undo());
        Assert.True(e.Redo());
        Assert.True(e.Redo());
        Assert.Equal("hi there", e.Text);
    }

    [Fact]
    public void Max_length_counts_graphemes_and_cuts_pastes()
    {
        var e = Ed("");
        e.MaxLength = 3;
        e.Insert("ab" + Family + "cd");
        Assert.Equal("ab" + Family, e.Text);
        Assert.False(e.Insert("z"));
        e.SelectAll();
        e.Insert("xyz!");
        Assert.Equal("xyz", e.Text);
    }

    [Fact]
    public void Single_line_turns_line_breaks_into_spaces_and_drops_control_characters()
    {
        var e = Ed("");
        e.Insert("one\r\ntwo\tthree\u0007");
        Assert.Equal("one two three", e.Text);
        var m = Ed("", multiline: true);
        m.Insert("one\r\ntwo\rthree");
        Assert.Equal("one\ntwo\nthree", m.Text);
        Assert.Equal(4, m.LineStart(6));
        Assert.Equal(7, m.LineEnd(5));
    }

    [Fact]
    public void Selection_is_replaced_by_typing()
    {
        var e = Ed("hello world");
        e.Select(6, 5);
        Assert.Equal("world", e.SelectedText);
        e.Insert("there");
        Assert.Equal("hello there", e.Text);
        Assert.False(e.HasSelection);
    }

    [Fact]
    public void Layout_caret_stops_are_monotonic_and_only_at_grapheme_boundaries()
    {
        var layout = TextLayout.Get("Wa" + EAcute + "ffi" + Family + "!", new RenderFont(FontFace.Regular, 20));
        float last = -1;
        foreach (var b in layout.Boundaries)
        {
            Assert.True(layout.CaretX(b) >= last);
            last = layout.CaretX(b);
        }
        Assert.Equal(layout.Width, layout.CaretX(layout.Text.Length));
        Assert.DoesNotContain(3, layout.Boundaries);  // inside "e + combining accent"
        Assert.Equal(layout.CaretX(2), layout.CaretX(3));
        Assert.Equal(4, layout.HitTest(layout.CaretX(4) + 0.1f));
    }
}
