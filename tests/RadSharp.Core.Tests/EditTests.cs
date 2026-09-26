using RadSharp.Text;

namespace RadSharp.Tests;

/// <summary>Edit and Memo driven through simulated input (clipboard tests share the global clipboard, so they live here).</summary>
public class EditTests
{
    static (Form Form, T Control) Setup<T>(T control, int w = 200, int h = 28) where T : Control
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form();
        f.SetBounds(0, 0, 400, 300);
        control.SetBounds(10, 10, w, h);
        f.Add(control);
        f.PerformLayout();
        control.Focus();
        return (f, control);
    }

    [Fact]
    public void Typing_editing_and_change_events()
    {
        var (f, edit) = Setup(new Edit());
        int changes = 0;
        edit.Change += _ => changes++;
        f.Simulate.Type("Hello");
        f.Simulate.Type(" world");
        f.Simulate.Key(Key.Backspace, KeyModifiers.Ctrl);
        Assert.Equal("Hello ", edit.Text);
        f.Simulate.Key(Key.Home);
        f.Simulate.Key(Key.Delete);
        Assert.Equal("ello ", edit.Text);
        Assert.Equal(4, changes);
    }

    [Fact]
    public void Shift_arrows_select_and_clipboard_round_trips()
    {
        var (f, edit) = Setup(new Edit { Text = "copy me" });
        f.Simulate.Key(Key.Home);
        for (int i = 0; i < 4; i++) f.Simulate.Key(Key.Right, KeyModifiers.Shift);
        Assert.Equal("copy", edit.SelectedText);
        f.Simulate.Key("Mod+X");
        Assert.Equal(" me", edit.Text);
        Assert.Equal("copy", Clipboard.GetText());
        f.Simulate.Key(Key.End);
        f.Simulate.Key("Mod+V");
        Assert.Equal(" mecopy", edit.Text);
        f.Simulate.Key("Mod+Z");
        Assert.Equal(" me", edit.Text);
        f.Simulate.Key("Mod+A");
        Assert.Equal(" me", edit.SelectedText);
    }

    [Fact]
    public void Password_mode_hides_text_and_blocks_copy()
    {
        var (f, edit) = Setup(new Edit { PasswordChar = "•" });
        Clipboard.SetText("unchanged");
        f.Simulate.Type("secret");
        f.Simulate.Key("Mod+A");
        f.Simulate.Key("Mod+C");
        Assert.Equal("unchanged", Clipboard.GetText());
        Assert.Equal("secret", edit.Text);
    }

    [Fact]
    public void Read_only_ignores_typing_but_allows_selection()
    {
        var (f, edit) = Setup(new Edit { Text = "fixed", ReadOnly = true });
        f.Simulate.Type("x");
        f.Simulate.Key(Key.Backspace);
        f.Simulate.Key(Key.Home);
        f.Simulate.Key(Key.End, KeyModifiers.Shift);
        Assert.Equal("fixed", edit.Text);
        Assert.Equal("fixed", edit.SelectedText);
    }

    [Fact]
    public void Enter_in_an_edit_reaches_the_default_button()
    {
        var (f, edit) = Setup(new Edit());
        var ok = new Button { Default = true };
        ok.SetBounds(10, 50, 80, 28);
        f.Add(ok);
        int clicks = 0;
        ok.Click += _ => clicks++;
        edit.Focus();
        f.Simulate.Type("abc");
        f.Simulate.Key(Key.Enter);
        Assert.Equal(1, clicks);
        Assert.Equal("abc", edit.Text);
    }

    [Fact]
    public void Clicking_places_the_caret_and_double_click_selects_a_word()
    {
        var (f, edit) = Setup(new Edit { Text = "alpha beta gamma" }, w: 300);
        f.PerformLayout();
        // Click right at the start of "beta": find its x from the edit's own hit testing.
        int x = 0;
        while (edit.IndexAtPoint(x, 14) < 6) x++;
        f.Simulate.Click(edit.AbsoluteLeft + x, edit.AbsoluteTop + 14);
        Assert.Equal(6, edit.CaretPosition);
        f.Simulate.DoubleClick(edit.AbsoluteLeft + x + 3, edit.AbsoluteTop + 14);
        Assert.Equal("beta", edit.SelectedText);
    }

    [Fact]
    public void Tab_into_an_edit_selects_all()
    {
        var (f, edit) = Setup(new Edit { Text = "pre-filled" });
        f.SetFocus(null);
        f.Simulate.Key(Key.Tab);
        Assert.True(edit.Focused);
        Assert.Equal("pre-filled", edit.SelectedText);
    }

    [Fact]
    public void Composition_is_shown_inline_and_committed_as_text()
    {
        var (f, edit) = Setup(new Edit { Text = "ab" });
        f.Simulate.Key(Key.Left);          // caret between a and b
        f.Simulate.Compose("にほ", 2);
        Assert.True(edit.IsComposing);
        Assert.Equal("ab", edit.Text);     // nothing committed yet
        f.Simulate.Key(Key.Backspace);     // owned by the IME while composing
        Assert.Equal("ab", edit.Text);
        f.Simulate.Compose("", 0);
        f.Simulate.Type("日本");
        Assert.False(edit.IsComposing);
        Assert.Equal("a日本b", edit.Text);
        Assert.NotNull(f.TextInputArea);
    }

    [Fact]
    public void Memo_breaks_lines_moves_vertically_and_wraps_words()
    {
        var (f, memo) = Setup(new Memo(), w: 120, h: 120);
        f.Simulate.Type("first");
        f.Simulate.Key(Key.Enter);
        f.Simulate.Type("second line");
        Assert.Equal(["first", "second line"], memo.TextLines);
        f.Simulate.Key(Key.Up);
        Assert.Equal(5, memo.CaretPosition);   // end of "first" (x clamps to its end)
        f.Simulate.Key(Key.Home);
        f.Simulate.Key(Key.Down, KeyModifiers.Shift);
        Assert.Equal("first\n", memo.SelectedText);

        memo.Text = "a fairly long sentence that must wrap onto several visual lines";
        f.PerformLayout();
        memo.Select(0, 0);
        f.Simulate.Key(Key.End);
        Assert.True(memo.CaretPosition < memo.Text.Length);   // End stops at the visual line's end
        f.Simulate.Key(Key.Down);
        Assert.True(memo.CaretPosition > 0);
    }

    [Fact]
    public void Memo_tabs_move_focus_unless_want_tabs()
    {
        var (f, memo) = Setup(new Memo(), w: 120, h: 80);
        var other = new Edit();
        other.SetBounds(10, 150, 100, 24);
        f.Add(other);
        memo.Focus();
        f.Simulate.Key(Key.Tab);
        Assert.True(other.Focused);

        memo.WantTabs = true;
        memo.Focus();
        f.Simulate.Key(Key.Tab);
        Assert.True(memo.Focused);
        Assert.Equal("\t", memo.Text);
    }

    [Fact]
    public void Memo_scrolls_to_keep_the_caret_visible()
    {
        var (f, memo) = Setup(new Memo(), w: 150, h: 60);
        for (int i = 0; i < 20; i++) { f.Simulate.Type($"line {i}"); f.Simulate.Key(Key.Enter); }
        var caret = memo.CaretRect;
        Assert.True(caret.Y >= 0 && caret.Y + caret.Height <= memo.Height);
        f.Simulate.Key(Key.Home, KeyModifiers.Ctrl);
        Assert.Equal(0, memo.CaretPosition);
        Assert.True(memo.CaretRect.Y < 20);
    }
}
