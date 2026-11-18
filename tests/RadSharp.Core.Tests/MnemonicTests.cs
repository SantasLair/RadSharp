using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>Mnemonics (&amp; in captions, Alt+letter), spec Appendix C. Everything goes through <c>form.Simulate</c>.</summary>
public class MnemonicTests
{
    static T Put<T>(Control form, T c, int x, int y, int w, int h) where T : Control { c.SetBounds(x, y, w, h); form.Add(c); return c; }

    static Form NewForm(int w = 420, int h = 300)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form { Caption = "Mnemonics" };
        f.SetBounds(0, 0, w, h);
        return f;
    }

    static void Alt(Form f, Key key) => f.Simulate.Key(key, KeyModifiers.Alt);

    [Fact]
    public void Caption_syntax_follows_delphi_with_double_ampersand_as_a_literal()
    {
        Assert.Equal('S', Mnemonics.KeyOf("&Save"));
        Assert.Equal('V', Mnemonics.KeyOf("Sa&ve"));
        Assert.Null(Mnemonics.KeyOf("Save"));
        Assert.Null(Mnemonics.KeyOf("Save && Close"));
        Assert.Equal('C', Mnemonics.KeyOf("Save && &Close"));
        Assert.Equal("Save & Close", Mnemonics.Strip("Save && Close", out int i)); Assert.Equal(-1, i);
        Assert.Equal('1', Mnemonics.KeyOf("&1st"));
    }

    [Fact]
    public void Alt_letter_clicks_a_button_and_focuses_it()
    {
        var f = NewForm();
        var other = Put(f, new Edit(), 10, 10, 100, 24);
        var save = Put(f, new Button { Caption = "&Save" }, 10, 50, 80, 28);
        int clicks = 0;
        save.Click += _ => clicks++;
        other.Focus();

        Alt(f, Key.S);
        Assert.Equal(1, clicks);
        Assert.True(save.Focused);
        Alt(f, Key.S);
        Assert.Equal(2, clicks);
    }

    [Fact]
    public void Only_a_plain_Alt_chord_counts()
    {
        var f = NewForm();
        var save = Put(f, new Button { Caption = "&Save" }, 10, 50, 80, 28);
        int clicks = 0;
        save.Click += _ => clicks++;
        f.Simulate.Key(Key.S);                                                  // no Alt
        f.Simulate.Key(Key.S, KeyModifiers.Alt | KeyModifiers.Shift);
        f.Simulate.Key(Key.S, KeyModifiers.Alt | KeyModifiers.Ctrl);
        Alt(f, Key.F);                                                          // another letter
        Assert.Equal(0, clicks);
    }

    [Fact]
    public void Alt_letter_toggles_a_check_box_and_checks_a_radio_button_and_focuses_them()
    {
        var f = NewForm();
        var edit = Put(f, new Edit(), 10, 10, 100, 24);
        var check = Put(f, new CheckBox { Caption = "&Wrap" }, 10, 50, 100, 20);
        var a = Put(f, new RadioButton { Caption = "&Left", Checked = true }, 10, 80, 100, 20);
        var b = Put(f, new RadioButton { Caption = "&Right" }, 10, 110, 100, 20);
        edit.Focus();

        Alt(f, Key.W);
        Assert.True(check.Checked); Assert.True(check.Focused);
        Alt(f, Key.W);
        Assert.False(check.Checked);

        Alt(f, Key.R);
        Assert.True(b.Checked); Assert.False(a.Checked); Assert.True(b.Focused);
    }

    [Fact]
    public void A_label_moves_focus_to_its_FocusControl_and_does_nothing_without_one()
    {
        var f = NewForm();
        var lonely = Put(f, new Label { Caption = "&Nothing" }, 10, 90, 100, 20);
        var label = Put(f, new Label { Caption = "&Name", FocusControlName = "edName" }, 10, 10, 60, 20);
        var ed = Put(f, new Edit { Name = "edName" }, 80, 10, 120, 24);
        var other = Put(f, new Button { Caption = "Other" }, 10, 50, 80, 28);
        other.Focus();

        Alt(f, Key.N);   // "Nothing" has no target, so only "Name" qualifies
        Assert.True(ed.Focused);

        other.Focus();
        label.FocusControlName = "";
        Alt(f, Key.N);
        Assert.True(other.Focused);

        lonely.FocusControl = ed;                       // set from code instead of by name
        Assert.Equal("edName", lonely.FocusControlName);
        Alt(f, Key.N);
        Assert.True(ed.Focused);
    }

    [Fact]
    public void A_label_whose_target_is_disabled_or_hidden_is_skipped()
    {
        var f = NewForm();
        var ed = Put(f, new Edit { Name = "ed" }, 80, 10, 120, 24);
        Put(f, new Label { Caption = "&Name", FocusControlName = "ed" }, 10, 10, 60, 20);
        var other = Put(f, new Button { Caption = "Other" }, 10, 50, 80, 28);
        other.Focus();
        ed.Enabled = false;
        Alt(f, Key.N);
        Assert.True(other.Focused);
        ed.Enabled = true; ed.Visible = false;
        Alt(f, Key.N);
        Assert.True(other.Focused);
    }

    [Fact]
    public void A_group_box_focuses_its_first_focusable_child()
    {
        var f = NewForm();
        var box = Put(f, new GroupBox { Caption = "&Options" }, 10, 10, 200, 120);
        Put(box, new Label { Caption = "plain" }, 8, 30, 60, 20);
        var first = Put(box, new CheckBox { Caption = "One" }, 8, 50, 100, 20);
        Put(box, new CheckBox { Caption = "Two" }, 8, 72, 100, 20);
        var outside = Put(f, new Button { Caption = "Out" }, 10, 150, 80, 28);
        outside.Focus();

        Alt(f, Key.O);
        Assert.True(first.Focused);
        Assert.False(first.Checked);          // focus only
    }

    [Fact]
    public void An_empty_group_box_is_not_a_target()
    {
        var f = NewForm();
        Put(f, new GroupBox { Caption = "&Options" }, 10, 10, 200, 120);
        var b = Put(f, new Button { Caption = "&Other" }, 10, 150, 80, 28);
        int clicks = 0; b.Click += _ => clicks++;
        Alt(f, Key.O);
        Assert.Equal(1, clicks);             // not shadowed by the empty group box
    }

    static (Form form, PageControl pages, TabSheet general, TabSheet advanced, Edit inGeneral, Edit inAdvanced) PageForm()
    {
        var f = NewForm();
        var pages = Put(f, new PageControl(), 10, 10, 380, 200);
        var general = new TabSheet { Caption = "&General" };
        var advanced = new TabSheet { Caption = "&Advanced" };
        pages.Add(general); pages.Add(advanced);
        var g = Put(general, new Edit { Name = "g" }, 10, 10, 100, 24);
        var a = Put(advanced, new Edit { Name = "a" }, 10, 10, 100, 24);
        f.PerformLayout();
        return (f, pages, general, advanced, g, a);
    }

    [Fact]
    public void A_tab_sheet_mnemonic_selects_its_page_and_focuses_into_it()
    {
        var (f, pages, general, advanced, inGeneral, inAdvanced) = PageForm();
        Assert.Same(general, pages.ActivePage);
        inGeneral.Focus();

        Alt(f, Key.A);
        Assert.Same(advanced, pages.ActivePage);
        Assert.True(inAdvanced.Focused);

        Alt(f, Key.G);
        Assert.Same(general, pages.ActivePage);
        Assert.True(inGeneral.Focused);
    }

    [Fact]
    public void Controls_on_an_inactive_page_do_not_answer()
    {
        var (f, pages, _, advanced, _, _) = PageForm();
        var hidden = Put(advanced, new Button { Caption = "&Hidden" }, 10, 50, 80, 28);
        int clicks = 0; hidden.Click += _ => clicks++;
        Alt(f, Key.H);
        Assert.Equal(0, clicks);
        pages.ActivePageIndex = 1;
        Alt(f, Key.H);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Several_controls_with_one_letter_cycle_focus_without_activating()
    {
        var f = NewForm();
        var edit = Put(f, new Edit(), 10, 10, 100, 24);
        var b1 = Put(f, new Button { Caption = "&Save" }, 10, 50, 80, 28);
        var c1 = Put(f, new CheckBox { Caption = "&Sync" }, 10, 90, 80, 20);
        var b2 = Put(f, new Button { Caption = "Sa&ve all" }, 10, 120, 80, 28);   // 'V': does not count for S
        var b3 = Put(f, new Button { Caption = "&Stop" }, 10, 160, 80, 28);
        int clicks = 0;
        foreach (var b in new Button[] { b1, b2, b3 }) b.Click += _ => clicks++;
        edit.Focus();

        Alt(f, Key.S); Assert.True(b1.Focused);
        Alt(f, Key.S); Assert.True(c1.Focused);
        Alt(f, Key.S); Assert.True(b3.Focused);
        Alt(f, Key.S); Assert.True(b1.Focused);       // wraps
        Assert.Equal(0, clicks);
        Assert.False(c1.Checked);

        Alt(f, Key.V);                                // unique again: activates
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Disabled_and_hidden_controls_are_skipped_when_counting_matches()
    {
        var f = NewForm();
        var off = Put(f, new Button { Caption = "&Save", Enabled = false }, 10, 10, 80, 28);
        var gone = Put(f, new Button { Caption = "&Send", Visible = false }, 10, 50, 80, 28);
        var real = Put(f, new Button { Caption = "&Stash" }, 10, 130, 80, 28);
        int clicks = 0, realClicks = 0;
        off.Click += _ => clicks++; gone.Click += _ => clicks++;
        real.Click += _ => realClicks++;

        Alt(f, Key.S);                                // only one live match: activated, not cycled
        Assert.Equal(1, realClicks); Assert.Equal(0, clicks);
    }

    [Fact]
    public void A_menu_bar_access_key_wins_over_a_control_with_the_same_letter()
    {
        var f = NewForm();
        var bar = new MainMenu();
        bar.Add("&File").Add("&New");
        f.Add(bar);
        var find = Put(f, new Button { Caption = "&Find" }, 10, 100, 80, 28);
        var save = Put(f, new Button { Caption = "&Save" }, 10, 140, 80, 28);
        int finds = 0, saves = 0;
        find.Click += _ => finds++; save.Click += _ => saves++;
        f.PerformLayout();

        Alt(f, Key.F);
        Assert.Equal(0, finds);                       // the menu took Alt+F
        Assert.Single(f.Popups);
        f.CloseAllPopups();

        Alt(f, Key.S);                                // no menu has S: the control answers
        Assert.Equal(1, saves);
    }

    [Fact]
    public void Mnemonics_go_to_the_form_that_has_the_key()
    {
        var f1 = NewForm(); var f2 = NewForm();
        var b1 = Put(f1, new Button { Caption = "&Save" }, 10, 10, 80, 28);
        var b2 = Put(f2, new Button { Caption = "&Save" }, 10, 10, 80, 28);
        int c1 = 0, c2 = 0; b1.Click += _ => c1++; b2.Click += _ => c2++;
        Alt(f2, Key.S);
        Assert.Equal((0, 1), (c1, c2));
    }

    // ---- Drawing ----

    static byte[] Render(Form f)
    {
        f.PerformLayout();
        using var bmp = FormRenderer.RenderToBitmap(f);
        return bmp.GetPixelSpan().ToArray();
    }

    static Form DrawForm(string caption = "&Save")
    {
        var f = NewForm(200, 80);
        Put(f, new Button { Caption = caption }, 10, 10, 90, 28);
        f.PerformLayout();
        return f;
    }

    [Fact]
    public void The_underline_shows_only_while_Alt_is_held()
    {
        var f = DrawForm();
        var rest = Render(f);
        f.Simulate.KeyDown(Key.Alt, KeyModifiers.Alt);
        Assert.True(f.ShowAccelerators);
        var held = Render(f);
        Assert.NotEqual(rest, held);
        f.Simulate.KeyUp(Key.Alt);
        Assert.False(f.ShowAccelerators);
        Assert.Equal(rest, Render(f));
    }

    [Fact]
    public void A_caption_without_ampersand_renders_the_same_with_or_without_Alt()
    {
        var f = DrawForm("Save");
        var rest = Render(f);
        f.Simulate.KeyDown(Key.Alt, KeyModifiers.Alt);
        Assert.Equal(rest, Render(f));
    }

    [Fact]
    public void The_marker_itself_is_never_drawn()
    {
        Assert.Equal(Render(DrawForm("Save")), Render(DrawForm("&Save")));
        Assert.Equal(Render(DrawForm("Save")), Render(DrawForm("Sa&ve")));
    }

    [Fact]
    public void Menu_keyboard_mode_also_underlines_mnemonics_until_it_ends()
    {
        var f = DrawForm();
        var bar = new MainMenu();
        bar.Add("&File").Add("&New");
        f.Add(bar);
        var rest = Render(f);

        f.Simulate.Key(Key.Alt, KeyModifiers.Alt);    // a lone Alt press and release: keyboard menu mode
        Assert.True(bar.InMenuMode);
        Assert.True(f.ShowAccelerators);
        Assert.NotEqual(rest, Render(f));

        f.Simulate.Key(Key.Escape);
        Assert.False(bar.InMenuMode);
        Assert.False(f.ShowAccelerators);
        Assert.Equal(rest, Render(f));
    }

    [Fact]
    public void The_switch_turns_mnemonics_off_but_still_strips_the_marker()
    {
        bool old = Mnemonics.Enabled;
        try
        {
            Mnemonics.Enabled = false;
            var f = DrawForm();
            var b = f.Children.OfType<Button>().First();
            int clicks = 0; b.Click += _ => clicks++;
            var rest = Render(f);
            f.Simulate.KeyDown(Key.Alt, KeyModifiers.Alt);
            Assert.Equal(rest, Render(f));            // no underline
            f.Simulate.KeyUp(Key.Alt);
            Alt(f, Key.S);
            Assert.Equal(0, clicks);                  // no activation
            Assert.Equal(Render(DrawForm("Save")), rest);   // and no stray ampersand
        }
        finally { Mnemonics.Enabled = old; }
    }

    static Form LabelForm(Label label)
    {
        var f = NewForm(200, 80);
        Put(f, new Edit { Name = "ed" }, 10, 40, 80, 24);
        Put(f, label, 10, 10, 120, 20);
        Put(f, new Button { Caption = "x" }, 120, 40, 40, 24);
        f.PerformLayout();
        return f;
    }

    [Fact]
    public void ShowAccelChar_false_draws_the_caption_literally_and_ignores_the_key()
    {
        var f = LabelForm(new Label { Caption = "A && B &C", ShowAccelChar = false, FocusControlName = "ed" });
        var other = f.Children.OfType<Button>().First();
        other.Focus();
        Alt(f, Key.C);
        Assert.True(other.Focused);                   // nothing happened

        var parsed = LabelForm(new Label { Caption = "A && B &C" });
        Assert.NotEqual(Render(f), Render(parsed));   // "A && B &C" literally vs "A & B C"
    }

    [Fact]
    public void A_double_ampersand_draws_one_literal_ampersand()
    {
        var doubled = LabelForm(new Label { Caption = "A && B" });
        var plain = LabelForm(new Label { Caption = "A & B", ShowAccelChar = false });
        Assert.Equal(Render(plain), Render(doubled));
    }

    [Fact]
    public void FocusControl_and_ShowAccelChar_round_trip_through_the_form_file()
    {
        var f = NewForm();
        Put(f, new Label { Name = "lblName", Caption = "&Name", FocusControlName = "edName" }, 10, 10, 60, 20);
        Put(f, new Edit { Name = "edName" }, 80, 10, 100, 24);
        Put(f, new Label { Name = "lblLit", Caption = "R&D", ShowAccelChar = false }, 10, 50, 60, 20);
        var json = FormWriter.Save(f);
        var flat = json.Replace(" ", "");
        Assert.Contains("\"focusControl\":\"edName\"", flat);
        Assert.Contains("\"showAccelChar\":false", flat);
        Assert.DoesNotContain("\"showAccelChar\":true", flat);   // the default is not written

        var (loaded, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);
        var name = (Label)loaded.FindControl("lblName")!;
        Assert.Equal("edName", name.FocusControlName);
        Assert.Same(loaded.FindControl("edName"), name.FocusControl);
        Assert.True(name.ShowAccelChar);
        Assert.False(((Label)loaded.FindControl("lblLit")!).ShowAccelChar);
        Assert.Equal(json, FormWriter.Save(loaded));

        loaded.PerformLayout();
        loaded.Simulate.Key(Key.N, KeyModifiers.Alt);
        Assert.True(loaded.FindControl("edName")!.Focused);
    }

    // ---- Golden images: the underline while Alt is held ----

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Underlines_while_Alt_is_held_match_golden_image(double scale)
    {
        var f = NewForm(400, 260);
        Put(f, new Edit { Name = "edName", Text = "Ada" }, 80, 12, 140, 26);
        Put(f, new Label { Caption = "&Name:", FocusControlName = "edName" }, 12, 16, 60, 20);
        Put(f, new Button { Caption = "&Save" }, 240, 12, 70, 28);
        Put(f, new Button { Caption = "Cance&l", Enabled = false }, 316, 12, 70, 28);
        Put(f, new CheckBox { Caption = "&Wrap text" }, 12, 56, 120, 20);
        Put(f, new RadioButton { Caption = "&Left", Checked = true }, 12, 82, 80, 20);
        Put(f, new RadioButton { Caption = "&Right" }, 100, 82, 80, 20);
        var box = Put(f, new GroupBox { Caption = "&Options" }, 12, 112, 180, 80);
        Put(box, new CheckBox { Caption = "A && B" }, 8, 30, 120, 20);
        var pages = Put(f, new PageControl(), 204, 56, 184, 150);
        pages.Add(new TabSheet { Caption = "&General" });
        pages.Add(new TabSheet { Caption = "&Advanced" });
        f.ApplyScale(scale);
        f.PerformLayout();

        f.Simulate.KeyDown(Key.Alt, KeyModifiers.Alt);
        using var actual = FormRenderer.RenderToBitmap(f);
        GoldenImageTests.CompareOrUpdate(actual, $"mnemonics-alt@{scale * 100:0}%-{f.Width}x{f.Height}.png");
    }
}
