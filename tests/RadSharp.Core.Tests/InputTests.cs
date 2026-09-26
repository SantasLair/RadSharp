namespace RadSharp.Tests;

/// <summary>Input, focus and popup rules (spec, Appendix C), driven through the simulator.</summary>
public class InputTests
{
    /// <summary>A control that records what happens to it.</summary>
    sealed class Probe : Control
    {
        public readonly List<string> Log = [];
        public Func<KeyEventArgs, bool>? Shortcut;

        protected override void OnClick() { Log.Add("click"); base.OnClick(); }
        protected override void OnMouseDown(MouseEventArgs e) { Log.Add($"down {e.X},{e.Y} {e.Button}"); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Log.Add($"up {e.X},{e.Y}"); base.OnMouseUp(e); }
        protected override void OnMouseMove(MouseEventArgs e) { Log.Add($"move {e.X},{e.Y}"); base.OnMouseMove(e); }
        protected override void OnMouseEnter() { Log.Add("enter"); base.OnMouseEnter(); }
        protected override void OnMouseLeave() { Log.Add("leave"); base.OnMouseLeave(); }
        protected override void OnKeyDown(KeyEventArgs e) { Log.Add($"key {e.Key}"); base.OnKeyDown(e); }
        protected override void OnTextInput(TextInputEventArgs e) { Log.Add($"text {e.Text}"); base.OnTextInput(e); }
        protected override void OnMouseWheel(MouseWheelEventArgs e) { Log.Add($"wheel {e.DeltaY}"); base.OnMouseWheel(e); }
        protected override bool ProcessShortcut(KeyEventArgs e) => Shortcut?.Invoke(e) ?? false;
        protected override void OnPopupClosed() { Log.Add("closed"); base.OnPopupClosed(); }
    }

    static Form NewForm(int w = 400, int h = 300)
    {
        var f = new Form();
        f.SetBounds(0, 0, w, h);
        return f;
    }

    static T Put<T>(Control parent, T c, int x, int y, int w, int h) where T : Control
    {
        c.SetBounds(x, y, w, h);
        parent.Add(c);
        return c;
    }

    // ---- Hit testing and capture ----

    [Fact]
    public void Hit_test_finds_the_deepest_topmost_visible_control_clipped_to_parents()
    {
        var f = NewForm();
        var panel = Put(f, new Panel { Name = "panel" }, 10, 10, 100, 100);
        var inner = Put(panel, new Probe { Name = "inner" }, 50, 50, 100, 100);  // sticks out of panel
        var over = Put(f, new Probe { Name = "over" }, 90, 90, 20, 20);          // later sibling, on top
        f.PerformLayout();

        Assert.Same(inner, f.HitTestForm(70, 70));
        Assert.Same(over, f.HitTestForm(95, 95));
        Assert.Same(f, f.HitTestForm(130, 130));  // inside inner's rect, but outside its parent
        over.Visible = false;
        Assert.Same(inner, f.HitTestForm(95, 95));
    }

    [Fact]
    public void Captured_control_gets_moves_and_release_and_click_needs_release_over_it()
    {
        var f = NewForm();
        var a = Put(f, new Probe(), 10, 10, 50, 50);
        f.Simulate.Press(20, 20);
        f.Simulate.MoveTo(200, 200);
        f.Simulate.Release(200, 200);
        Assert.Equal(["enter", "move 10,10", "down 10,10 Left", "leave", "move 190,190", "up 190,190"], a.Log);

        a.Log.Clear();
        f.Simulate.Click(30, 30);
        Assert.Contains("click", a.Log);
    }

    [Fact]
    public void Right_button_does_not_click()
    {
        var f = NewForm();
        var a = Put(f, new Probe(), 10, 10, 50, 50);
        f.Simulate.Click(20, 20, MouseButton.Right);
        Assert.DoesNotContain("click", a.Log);
    }

    [Fact]
    public void Disabled_controls_get_no_mouse_input()
    {
        var f = NewForm();
        var a = Put(f, new Probe { Enabled = false }, 10, 10, 50, 50);
        f.Simulate.Click(20, 20);
        Assert.Empty(a.Log);
    }

    // ---- Focus ----

    [Fact]
    public void Press_focuses_tab_stops_only()
    {
        var f = NewForm();
        var button = Put(f, new Button(), 10, 10, 80, 28);
        var label = Put(f, new Label(), 10, 50, 80, 20);
        f.Simulate.Click(button);
        Assert.Same(button, f.FocusedControl);
        f.Simulate.Click(label);
        Assert.Same(button, f.FocusedControl);
        Assert.False(f.ShowFocusCues);
    }

    [Fact]
    public void Tab_order_is_depth_first_with_explicit_orders_first()
    {
        var f = NewForm();
        var b1 = Put(f, new Button { Name = "b1" }, 0, 0, 10, 10);
        var panel = Put(f, new Panel { Name = "panel" }, 0, 20, 100, 100);
        var p1 = Put(panel, new Button { Name = "p1" }, 0, 0, 10, 10);
        var p2 = Put(panel, new Button { Name = "p2", TabOrder = 0 }, 0, 20, 10, 10);
        var b2 = Put(f, new Button { Name = "b2", TabOrder = 0 }, 0, 130, 10, 10);
        var hidden = Put(f, new Button { Name = "hidden", Visible = false }, 0, 150, 10, 10);
        var disabledPanel = Put(f, new Panel { Enabled = false }, 0, 170, 50, 50);
        Put(disabledPanel, new Button { Name = "inDisabled" }, 0, 0, 10, 10);

        Assert.Equal(["b2", "b1", "p2", "p1"], f.GetTabOrderList().Select(c => c.Name));

        f.Simulate.Key(Key.Tab);
        Assert.Same(b2, f.FocusedControl);
        Assert.True(f.ShowFocusCues);
        f.Simulate.Key(Key.Tab);
        f.Simulate.Key(Key.Tab);
        f.Simulate.Key(Key.Tab);
        Assert.Same(p1, f.FocusedControl);
        f.Simulate.Key(Key.Tab);
        Assert.Same(b2, f.FocusedControl);  // wraps
        f.Simulate.Key(Key.Tab, KeyModifiers.Shift);
        Assert.Same(p1, f.FocusedControl);
        _ = hidden;
    }

    [Fact]
    public void Hiding_disabling_or_removing_clears_hover_capture_and_focus_inside()
    {
        var f = NewForm();
        var panel = Put(f, new Panel(), 0, 0, 200, 200);
        var b = Put(panel, new Button(), 10, 10, 80, 28);

        f.Simulate.Press(20, 20);
        Assert.Same(b, f.FocusedControl);
        Assert.True(b.IsHovered);

        panel.Visible = false;
        Assert.Null(f.FocusedControl);
        Assert.False(b.IsHovered);
        f.Simulate.Release(20, 20);  // capture was cleared: no click, no crash

        panel.Visible = true;
        b.Focus();
        panel.Enabled = false;
        Assert.Null(f.FocusedControl);

        panel.Enabled = true;
        b.Focus();
        panel.Remove(b);
        Assert.Null(f.FocusedControl);
    }

    // ---- Keyboard ----

    [Fact]
    public void Keys_go_to_the_focused_control_and_text_arrives_separately()
    {
        var f = NewForm();
        var p = Put(f, new Probe { TabStop = true }, 0, 0, 50, 50);
        p.Focus();
        f.Simulate.Key(Key.A);
        f.Simulate.Type("a");
        Assert.Equal(["key A", "text a"], p.Log);
    }

    [Fact]
    public void Shortcuts_are_offered_to_the_tree_after_the_focused_control()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = NewForm();
        var focused = Put(f, new Probe { TabStop = true }, 0, 0, 50, 50);
        var handler = Put(f, new Probe(), 60, 0, 50, 50);
        var seen = new List<string>();
        handler.Shortcut = e => { seen.Add(e.Key.ToString()); return Shortcut.Parse("Mod+S").Matches(e) || e.Key == Key.F5; };
        focused.Focus();

        f.Simulate.Key("Mod+S");
        f.Simulate.Key(Key.F5);
        f.Simulate.Key(Key.S);  // no modifier: not a shortcut
        Assert.Equal(["S", "F5"], seen);
        Assert.Equal(["key S", "key F5", "key S"], focused.Log);
    }

    [Fact]
    public void Enter_and_Escape_click_default_and_cancel_buttons()
    {
        var f = NewForm();
        var ok = Put(f, new Button { Default = true }, 0, 0, 80, 28);
        var cancel = Put(f, new Button { Cancel = true }, 90, 0, 80, 28);
        var box = Put(f, new CheckBox(), 0, 40, 100, 20);
        int okClicks = 0, cancelClicks = 0;
        ok.Click += _ => okClicks++;
        cancel.Click += _ => cancelClicks++;
        box.Focus();

        f.Simulate.Key(Key.Enter);
        f.Simulate.Key(Key.Escape);
        Assert.Equal((1, 1), (okClicks, cancelClicks));
    }

    [Fact]
    public void Button_clicks_on_space_release_and_on_enter()
    {
        var f = NewForm();
        var b = Put(f, new Button(), 0, 0, 80, 28);
        int clicks = 0;
        b.Click += _ => clicks++;
        b.Focus();
        f.ProcessKeyDown(Key.Space);
        Assert.True(b.IsPressed);
        Assert.Equal(0, clicks);
        f.ProcessKeyUp(Key.Space);
        Assert.Equal(1, clicks);
        f.Simulate.Key(Key.Enter);
        Assert.Equal(2, clicks);
        b.Enabled = false;
        b.PerformClick();
        Assert.Equal(2, clicks);
    }

    // ---- Wheel ----

    [Fact]
    public void Wheel_bubbles_up_until_handled()
    {
        var f = NewForm();
        var outer = Put(f, new Probe(), 0, 0, 200, 200);
        var inner = Put(outer, new Probe(), 10, 10, 50, 50);
        f.Simulate.Wheel(20, 20, 1);
        Assert.Equal(["wheel 1"], inner.Log);
        Assert.Equal(["wheel 1"], outer.Log);
    }

    // ---- Popups ----

    [Fact]
    public void Press_outside_all_popups_closes_them_and_is_consumed()
    {
        var f = NewForm();
        var b = Put(f, new Button(), 10, 10, 80, 28);
        int clicks = 0;
        b.Click += _ => clicks++;
        var popup = new Probe();
        popup.SetBounds(0, 0, 100, 100);
        f.OpenPopup(popup, 200, 50);

        Assert.Same(popup, f.HitTestForm(250, 100));  // popups are hit-tested first
        f.Simulate.Click(b);
        Assert.Empty(f.Popups);
        Assert.Equal(0, clicks);
        Assert.Equal(["closed"], popup.Log);
    }

    [Fact]
    public void Popups_take_keys_discard_text_and_close_with_escape_and_tab()
    {
        var f = NewForm();
        var focused = Put(f, new Probe { TabStop = true }, 0, 0, 50, 50);
        var other = Put(f, new Button(), 0, 60, 80, 28);
        focused.Focus();
        var p1 = new Probe();
        p1.SetBounds(0, 0, 50, 50);
        var p2 = new Probe();
        p2.SetBounds(0, 0, 50, 50);
        f.OpenPopup(p1, 100, 100);
        f.OpenPopup(p2, 160, 100);

        f.Simulate.Key(Key.Down);
        f.Simulate.Type("x");
        Assert.Equal(["key Down"], p2.Log);
        Assert.Empty(focused.Log);

        f.Simulate.Key(Key.Escape);  // closes only the topmost
        Assert.Equal([p1], f.Popups);

        f.Simulate.Key(Key.Tab);  // closes all, then moves focus
        Assert.Empty(f.Popups);
        Assert.Same(other, f.FocusedControl);
    }

    [Fact]
    public void Closing_a_popup_closes_those_above_and_hiding_the_owner_closes_it()
    {
        var f = NewForm();
        var owner = Put(f, new Button(), 0, 0, 80, 28);
        var p1 = new Panel();
        p1.SetBounds(0, 0, 50, 50);
        var p2 = new Panel();
        p2.SetBounds(0, 0, 50, 50);
        f.OpenPopup(p1, 100, 100, owner);
        f.OpenPopup(p2, 160, 100);
        f.ClosePopup(p1);
        Assert.Empty(f.Popups);

        f.OpenPopup(p1, 100, 100, owner);
        owner.Visible = false;
        Assert.Empty(f.Popups);
    }

    [Fact]
    public void Popups_are_kept_inside_the_form()
    {
        var f = NewForm(400, 300);
        var p = new Panel();
        p.SetBounds(0, 0, 100, 80);
        f.OpenPopup(p, 380, 290);
        Assert.Equal(new Rect(300, 220, 100, 80), p.Bounds);
    }

    // ---- Shortcuts ----

    [Theory]
    [InlineData("Mod+Shift+Z", Key.Z, KeyModifiers.Ctrl | KeyModifiers.Shift)]
    [InlineData("Ctrl+F5", Key.F5, KeyModifiers.Ctrl)]
    [InlineData("Alt+Enter", Key.Enter, KeyModifiers.Alt)]
    [InlineData("Delete", Key.Delete, KeyModifiers.None)]
    public void Shortcuts_parse(string text, Key key, KeyModifiers mods)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        Assert.Equal(new Shortcut(key, mods), Shortcut.Parse(text));
    }

    [Fact]
    public void Mod_means_meta_on_macOS()
    {
        var saved = Shortcut.CommandModifier;
        try
        {
            Shortcut.CommandModifier = KeyModifiers.Meta;
            Assert.Equal(new Shortcut(Key.S, KeyModifiers.Meta), Shortcut.Parse("Mod+S"));
        }
        finally { Shortcut.CommandModifier = saved; }
    }

    [Fact]
    public void Invalid_shortcuts_are_rejected()
    {
        Assert.False(Shortcut.TryParse("Hyper+S", out _));
        Assert.False(Shortcut.TryParse("Ctrl+", out _));
        Assert.False(Shortcut.TryParse("", out _));
    }
}
