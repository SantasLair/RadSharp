namespace RadSharp.Tests;

/// <summary>Control.Tag, Form.KeyPreview and Application.OpenUrl's URL rules.</summary>
public class SmallApiTests
{
    [Fact]
    public void Tag_holds_any_object_and_is_not_saved_to_the_form_file()
    {
        var f = new Form();
        var b = new Button { Name = "btn", Tag = new[] { 1, 2, 3 } };
        f.Add(b);
        Assert.Equal([1, 2, 3], Assert.IsType<int[]>(b.Tag));
        Assert.Null(new Button().Tag);

        var (loaded, warnings) = FormReader.Load(FormWriter.Save(f));
        Assert.Empty(warnings);
        Assert.Null(loaded.FindControl("btn")!.Tag);
    }

    static (Form Form, Edit Edit, List<string> Log) PreviewSetup(bool preview)
    {
        var f = new Form { KeyPreview = preview };
        f.SetBounds(0, 0, 300, 100);
        var edit = new Edit { TabStop = true };
        edit.SetBounds(10, 10, 150, 24);
        f.Add(edit);
        f.PerformLayout();
        edit.Focus();
        var log = new List<string>();
        f.KeyDown += (_, e) => log.Add($"form down {e.Key}");
        f.KeyUp += (_, e) => log.Add($"form up {e.Key}");
        return (f, edit, log);
    }

    [Fact]
    public void KeyPreview_off_leaves_keys_to_the_focused_control()
    {
        var (f, _, log) = PreviewSetup(false);
        f.Simulate.Key(Key.F2);
        Assert.Empty(log);
    }

    [Fact]
    public void KeyPreview_shows_the_form_keys_first_and_lets_it_stop_them()
    {
        var (f, edit, log) = PreviewSetup(true);
        f.Simulate.Key(Key.Left);
        Assert.Equal(["form down Left", "form up Left"], log);

        // The form handles Home: the control's own handling (caret movement) never sees it.
        f.Simulate.Type("abc");
        Assert.Equal("abc", edit.Text);
        log.Clear();
        f.KeyDown += (_, e) => { if (e.Key == Key.Home) e.Handled = true; };
        f.Simulate.Key(Key.Home);
        f.Simulate.Type("X");
        Assert.Equal("abcX", edit.Text);   // Home never reached the Edit, so the caret stayed at the end
    }

    [Fact]
    public void KeyPreview_round_trips_through_the_form_file()
    {
        var (loaded, warnings) = FormReader.Load(FormWriter.Save(new Form { KeyPreview = true }));
        Assert.Empty(warnings);
        Assert.True(loaded.KeyPreview);
    }

    [Fact]
    public void KeyPreview_does_not_fire_twice_when_the_form_itself_has_focus()
    {
        var f = new Form { KeyPreview = true };
        f.SetBounds(0, 0, 100, 100);
        int downs = 0;
        f.KeyDown += (_, _) => downs++;
        f.Simulate.Key(Key.A);
        Assert.Equal(1, downs);
    }

    [Fact]
    public void KeyPreview_is_skipped_while_a_popup_is_open()
    {
        var (f, edit, log) = PreviewSetup(true);
        var menu = new Panel();
        menu.SetBounds(0, 0, 50, 50);
        f.OpenPopup(menu, 100, 50, edit);
        f.Simulate.Key(Key.Left);
        Assert.Empty(log);
    }
}
