namespace RadSharp.Tests;

public class MenuTests
{
    static (Form Form, MainMenu Bar, List<string> Log) Setup()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "serialization", "menus.radform.json"));
        var (form, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);
        var bar = (MainMenu)form.FindControl("mnuMain")!;
        var log = new List<string>();
        void Hook(IEnumerable<MenuItem> items)
        {
            foreach (var i in items) { i.Click += s => log.Add(s.Caption); Hook(i.Items); }
        }
        Hook(bar.Items);
        Hook(((PopupMenu)form.FindControl("pmnList")!).Items);
        return (form, bar, log);
    }

    static MenuPopup Top(Form f) => Assert.IsType<MenuPopup>(f.Popups[^1]);

    static void ClickItem(Form f, MenuPopup popup, int index)
    {
        var r = popup.ItemRect(index);
        f.Simulate.Click(popup.Left + r.X + 10, popup.Top + r.Y + r.Height / 2);
    }

    [Fact]
    public void Clicking_the_bar_opens_a_menu_and_clicking_an_item_runs_it()
    {
        var (f, bar, log) = Setup();
        var file = bar.ItemRects()[0].Rect;
        f.Simulate.Click(bar.AbsoluteLeft + file.X + 5, bar.AbsoluteTop + 5);
        var popup = Top(f);
        Assert.Equal(["New", "-", "Recent", "Exit"], popup.Items.Select(i => i.Caption));
        ClickItem(f, popup, 0);
        Assert.Equal(["New"], log);
        Assert.Empty(f.Popups);
    }

    [Fact]
    public void Hovering_another_bar_item_switches_menus()
    {
        var (f, bar, _) = Setup();
        var rects = bar.ItemRects();
        f.Simulate.Click(bar.AbsoluteLeft + rects[0].Rect.X + 5, bar.AbsoluteTop + 5);
        f.Simulate.MoveTo(bar.AbsoluteLeft + rects[1].Rect.X + 5, bar.AbsoluteTop + 5);
        Assert.Single(f.Popups);
        Assert.Equal("Redo", Top(f).Items[0].Caption);
    }

    [Fact]
    public void Keyboard_navigates_submenus_and_skips_separators()
    {
        var (f, bar, log) = Setup();
        bar.OpenMenu(0, selectFirst: true);
        var popup = Top(f);
        Assert.Equal(0, popup.HotIndex);
        f.Simulate.Key(Key.Down);          // skips the separator
        Assert.Equal(2, popup.HotIndex);
        f.Simulate.Key(Key.Right);         // opens "Recent"
        Assert.Equal(2, f.Popups.Count);
        f.Simulate.Key(Key.Enter);
        Assert.Equal(["a.map"], log);
        Assert.Empty(f.Popups);

        bar.OpenMenu(0, selectFirst: true);
        f.Simulate.Key(Key.Right);         // "New" has no submenu: moves to the Edit menu
        Assert.Equal("Redo", Top(f).Items[0].Caption);
        f.Simulate.Key(Key.Escape);
        Assert.Empty(f.Popups);
    }

    [Fact]
    public void Shortcuts_run_enabled_commands_without_opening_menus()
    {
        var (f, _, log) = Setup();
        f.Simulate.Key("Ctrl+N");
        f.Simulate.Key("Ctrl+Y");          // Redo is disabled
        f.Simulate.Key("Ctrl+Shift+Z");
        Assert.Equal(["New"], log);
        Assert.Empty(f.Popups);
    }

    [Fact]
    public void Right_click_opens_the_controls_context_menu()
    {
        var (f, _, log) = Setup();
        var panel = f.FindControl("pnlList")!;
        f.Simulate.Click(panel.AbsoluteLeft + 50, panel.AbsoluteTop + 50, MouseButton.Right);
        var popup = Top(f);
        Assert.Equal(panel.AbsoluteLeft + 50, popup.Left);
        ClickItem(f, popup, 0);
        Assert.Equal(["Delete"], log);
    }

    [Fact]
    public void Timers_fire_on_the_form_clock()
    {
        var (f, _, _) = Setup();
        var timer = (Timer)f.FindControl("tmrTick")!;
        int ticks = 0;
        timer.Elapsed += _ => ticks++;
        for (int ms = 0; ms <= 1000; ms += 50) f.ProcessTick(ms);
        Assert.Equal(4, ticks);
        timer.Enabled = false;
        f.ProcessTick(2000);
        Assert.Equal(4, ticks);
        Assert.False(timer.Visible);
        Assert.NotSame(timer, f.HitTestForm(timer.Left + 1, timer.Top + 1));  // components are never hit
    }
}
