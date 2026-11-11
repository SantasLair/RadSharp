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

    // ---- Access keys, menu mode, Alt / F10 ----

    sealed class Rig
    {
        public Form Form = new();
        public MainMenu Bar = new();
        public Edit Box = new() { TabStop = true };
        public List<string> Log = [];
        public MenuItem File, EditMenu;

        public Rig()
        {
            Shortcut.CommandModifier = KeyModifiers.Ctrl;
            Form.SetBounds(0, 0, 500, 400);
            File = Bar.Add("&File");
            File.Add("&New", s => Log.Add("New"));
            File.Add("&Open", s => Log.Add("Open"));
            File.Add("E&xit", s => Log.Add("Exit"));
            EditMenu = Bar.Add("&Edit");
            EditMenu.Add("&Undo", s => Log.Add("Undo"));
            var more = EditMenu.Add("&More");
            more.Add("&Deep", s => Log.Add("Deep"));
            Box.SetBounds(20, 100, 200, 28);
            Form.Add(Bar);
            Form.Add(Box);
            Form.PerformLayout();
            Form.SetFocus(Box);
        }

        public MenuPopup Top => Assert.IsType<MenuPopup>(Form.Popups[^1]);
    }

    [Fact]
    public void Access_keys_are_parsed_and_stripped()
    {
        Assert.Equal("File", MenuItem.StripAccessKey("&File", out int i)); Assert.Equal(0, i);
        Assert.Equal("Save As", MenuItem.StripAccessKey("Save &As", out i)); Assert.Equal(5, i);
        Assert.Equal("&Go", MenuItem.StripAccessKey("&&Go", out i)); Assert.Equal(-1, i);
        Assert.Equal("A&B C", MenuItem.StripAccessKey("A&&B &C", out i)); Assert.Equal(4, i);
        Assert.Equal("x", MenuItem.StripAccessKey("x&", out i)); Assert.Equal(-1, i);
        Assert.Equal("Plain", MenuItem.StripAccessKey("Plain", out i)); Assert.Equal(-1, i);
        Assert.Equal('F', new MenuItem("&file").AccessKey);
        Assert.Null(new MenuItem("&&file").AccessKey);
        Assert.Null(new MenuItem("file").AccessKey);
        Assert.Equal("Edit", new MenuItem("&Edit").DisplayText);
    }

    [Fact]
    public void Popup_width_ignores_the_ampersand()
    {
        var a = new Rig();
        a.Bar.OpenMenu(0);
        int with = a.Top.Width;
        var b = new Rig();
        foreach (var item in b.File.Items) item.Caption = item.Caption.Replace("&", "");
        b.Bar.OpenMenu(0);
        Assert.Equal(with, b.Top.Width);
    }

    [Fact]
    public void Alt_letter_opens_that_menu_with_the_first_item_hot()
    {
        var r = new Rig();
        r.Form.Simulate.Key(Key.E, KeyModifiers.Alt);
        Assert.Equal("Undo", r.Top.Items[0].DisplayText);
        Assert.Equal(0, r.Top.HotIndex);
        r.Form.Simulate.Key(Key.Escape);
        r.Form.Simulate.Key(Key.F, KeyModifiers.Alt);
        Assert.Equal(["&New", "&Open", "E&xit"], r.Top.Items.Select(i => i.Caption));
        Assert.Equal(0, r.Top.HotIndex);
    }

    [Fact]
    public void Alt_letter_without_a_matching_menu_is_ignored()
    {
        var r = new Rig();
        r.Form.Simulate.Key(Key.Q, KeyModifiers.Alt);
        r.Form.Simulate.Key(Key.F, KeyModifiers.Alt | KeyModifiers.Shift);
        Assert.Empty(r.Form.Popups);
    }

    [Fact]
    public void Explicit_shortcuts_beat_access_keys()
    {
        var r = new Rig();
        r.EditMenu.Add("Special", s => r.Log.Add("Special"), "Alt+F");
        r.Form.Simulate.Key(Key.F, KeyModifiers.Alt);
        Assert.Equal(["Special"], r.Log);
        Assert.Empty(r.Form.Popups);
    }

    [Fact]
    public void A_letter_in_an_open_popup_runs_the_item_with_that_access_key()
    {
        var r = new Rig();
        r.Form.Simulate.Key(Key.F, KeyModifiers.Alt);
        r.Form.Simulate.Key(Key.X);
        Assert.Equal(["Exit"], r.Log);
        Assert.Empty(r.Form.Popups);

        r.Form.Simulate.Key(Key.F, KeyModifiers.Alt);
        r.Form.Simulate.Key(Key.O, KeyModifiers.Alt);
        Assert.Equal(["Exit", "Open"], r.Log);

        r.Form.Simulate.Key(Key.E, KeyModifiers.Alt);
        r.Form.Simulate.Key(Key.M);          // opens the submenu
        Assert.Equal(2, r.Form.Popups.Count);
        Assert.Equal(0, r.Top.HotIndex);
        r.Form.Simulate.Key(Key.D);
        Assert.Equal(["Exit", "Open", "Deep"], r.Log);
        Assert.Empty(r.Form.Popups);

        r.Form.Simulate.Key(Key.F, KeyModifiers.Alt);
        r.Form.Simulate.Key(Key.Z);          // no such access key
        Assert.Single(r.Form.Popups);
    }

    [Fact]
    public void F10_enters_menu_mode_and_keys_move_open_and_exit()
    {
        var r = new Rig();
        var f = r.Form;
        f.Simulate.Key(Key.F10);
        Assert.True(r.Bar.InMenuMode);
        Assert.Same(r.Bar, f.FocusedControl);
        Assert.Equal(0, r.Bar.HotIndex);
        Assert.Empty(f.Popups);

        f.Simulate.Key(Key.Right);
        Assert.Equal(1, r.Bar.HotIndex);
        f.Simulate.Key(Key.Right);                       // wraps
        Assert.Equal(0, r.Bar.HotIndex);
        f.Simulate.Key(Key.Left);                        // wraps back
        Assert.Equal(1, r.Bar.HotIndex);

        f.Simulate.Key(Key.Down);
        Assert.Equal("Undo", r.Top.Items[0].DisplayText);
        Assert.Equal(0, r.Top.HotIndex);
        f.Simulate.Key(Key.Escape);
        Assert.Empty(f.Popups);
        Assert.False(r.Bar.InMenuMode);
        Assert.Same(r.Box, f.FocusedControl);
    }

    [Fact]
    public void Menu_mode_letter_opens_and_escape_exits_without_a_dropdown()
    {
        var r = new Rig();
        r.Form.Simulate.Key(Key.F10);
        r.Form.Simulate.Key(Key.E);
        Assert.Equal("Undo", r.Top.Items[0].DisplayText);
        r.Form.Simulate.Key(Key.Escape);
        Assert.Same(r.Box, r.Form.FocusedControl);

        r.Form.Simulate.Key(Key.F10);
        r.Form.Simulate.Key(Key.Enter);
        Assert.Equal("New", r.Top.Items[0].DisplayText);
        r.Form.Simulate.Key(Key.Escape);

        r.Form.Simulate.Key(Key.F10);
        r.Form.Simulate.Key(Key.Escape);
        Assert.False(r.Bar.InMenuMode);
        Assert.Same(r.Box, r.Form.FocusedControl);
    }

    [Fact]
    public void Shift_F10_is_not_taken_by_the_menu_bar()
    {
        var r = new Rig();
        r.Form.Simulate.Key(Key.F10, KeyModifiers.Shift);
        Assert.False(r.Bar.InMenuMode);
        Assert.Same(r.Box, r.Form.FocusedControl);
    }

    [Fact]
    public void Alt_pressed_and_released_alone_toggles_menu_mode()
    {
        var r = new Rig();
        r.Form.Simulate.Key(Key.Alt, KeyModifiers.Alt);
        Assert.True(r.Bar.InMenuMode);
        Assert.Same(r.Bar, r.Form.FocusedControl);
        r.Form.Simulate.Key(Key.Alt, KeyModifiers.Alt);
        Assert.False(r.Bar.InMenuMode);
        Assert.Same(r.Box, r.Form.FocusedControl);
    }

    [Fact]
    public void Alt_alone_closes_an_open_dropdown_and_exits()
    {
        var r = new Rig();
        r.Bar.OpenMenu(0);
        Assert.Single(r.Form.Popups);
        r.Form.Simulate.Key(Key.Alt, KeyModifiers.Alt);
        Assert.Empty(r.Form.Popups);
        Assert.False(r.Bar.InMenuMode);
        Assert.Same(r.Box, r.Form.FocusedControl);
    }

    [Fact]
    public void Alt_with_another_key_or_a_click_in_between_is_not_alt_alone()
    {
        var r = new Rig();
        var f = r.Form;
        f.ProcessKeyDown(Key.Alt, KeyModifiers.Alt);
        f.Simulate.Key(Key.F, KeyModifiers.Alt);
        f.ProcessKeyUp(Key.Alt);
        Assert.Single(f.Popups);                // Alt+F's dropdown is still open
        Assert.False(r.Bar.InMenuMode);

        f.CloseAllPopups();
        f.ProcessKeyDown(Key.Alt, KeyModifiers.Alt);
        f.Simulate.Click(r.Box.AbsoluteLeft + 5, r.Box.AbsoluteTop + 5);
        f.ProcessKeyUp(Key.Alt);
        Assert.False(r.Bar.InMenuMode);
    }

    [Fact]
    public void Clicking_elsewhere_leaves_menu_mode()
    {
        var r = new Rig();
        r.Form.SetFocus(null);
        r.Form.Simulate.Key(Key.F10);
        Assert.True(r.Bar.InMenuMode);
        r.Form.Simulate.Click(r.Box.AbsoluteLeft + 5, r.Box.AbsoluteTop + 5);
        Assert.False(r.Bar.InMenuMode);
        Assert.Same(r.Box, r.Form.FocusedControl);
    }

    [Fact]
    public void Focus_returns_to_the_previous_control_before_the_click_handler_runs()
    {
        var r = new Rig();
        Control? seen = null;
        r.File.Items[0].Click += s => seen = r.Form.FocusedControl;

        r.Form.Simulate.Key(Key.F10);
        r.Form.Simulate.Key(Key.Down);
        r.Form.Simulate.Key(Key.Enter);
        Assert.Same(r.Box, seen);

        seen = null;
        var rect = r.Bar.ItemRects()[0].Rect;
        r.Form.Simulate.Click(r.Bar.AbsoluteLeft + rect.X + 5, r.Bar.AbsoluteTop + 5);
        Assert.Same(r.Box, r.Form.FocusedControl);
        ClickItem(r.Form, r.Top, 0);
        Assert.Same(r.Box, seen);
        Assert.Equal(["New", "New"], r.Log);
    }

    [Fact]
    public void Focus_returns_after_escape_or_an_outside_click_on_a_dropdown()
    {
        var r = new Rig();
        r.Form.Simulate.Key(Key.F, KeyModifiers.Alt);
        r.Form.Simulate.Key(Key.Escape);
        Assert.Same(r.Box, r.Form.FocusedControl);

        r.Form.Simulate.Key(Key.F10);
        r.Form.Simulate.Key(Key.Down);
        r.Form.Simulate.Click(450, 350);
        Assert.Empty(r.Form.Popups);
        Assert.False(r.Bar.InMenuMode);
        Assert.Same(r.Box, r.Form.FocusedControl);
    }

    [Fact]
    public void Left_and_right_in_a_dropdown_still_switch_menus_and_keep_the_session()
    {
        var r = new Rig();
        r.Form.Simulate.Key(Key.F10);
        r.Form.Simulate.Key(Key.Down);
        r.Form.Simulate.Key(Key.Right);
        Assert.Equal("Undo", r.Top.Items[0].DisplayText);
        Assert.Single(r.Form.Popups);
        r.Form.Simulate.Key(Key.Escape);
        Assert.Same(r.Box, r.Form.FocusedControl);
    }

    [Fact]
    public void Opening_is_raised_before_the_dropdown_is_shown_and_its_changes_are_shown()
    {
        var r = new Rig();
        int popupsAtEvent = -1;
        r.File.Opening += m =>
        {
            popupsAtEvent = r.Form.Popups.Count;
            m.Items[1].Enabled = false;
            m.Items[0].Checked = true;
            m.Items[2].Caption = "Quit the &application now";
        };
        r.Bar.OpenMenu(0);
        Assert.Equal(0, popupsAtEvent);
        var p = r.Top;
        Assert.False(p.Items[1].Enabled);
        Assert.True(p.Items[0].Checked);
        Assert.Equal("Quit the &application now", p.Items[2].Caption);

        var narrow = new Rig();
        narrow.Bar.OpenMenu(0);
        Assert.True(p.Width > narrow.Top.Width);

        int nested = 0;
        r.EditMenu.Items[1].Opening += _ => nested++;
        r.Form.CloseAllPopups();
        r.Bar.OpenMenu(1);
        Assert.Equal(0, nested);
        r.Form.Simulate.Key(Key.Down);
        r.Form.Simulate.Key(Key.Down);
        r.Form.Simulate.Key(Key.Right);
        Assert.Equal(1, nested);
    }
}
