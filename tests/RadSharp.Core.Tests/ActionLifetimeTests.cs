namespace RadSharp.Tests;

/// <summary>Action links made from code or by an unresolved name: unlinking, and clients that leave their form.</summary>
[Collection("PlatformHost")]   // idle updates and closing forms touch process-wide state
public class ActionLifetimeTests
{
    [Fact]
    public void Setting_Action_to_null_cancels_a_name_that_never_resolved()
    {
        var btn = new Button { ActionName = "actSave" };    // no form yet: pending
        Assert.Equal("actSave", btn.ActionName);
        btn.Action = null;
        Assert.Equal("", btn.ActionName);

        var f = new Form();
        f.SetBounds(0, 0, 300, 100);
        var act = new ActionItem { Name = "actSave", Caption = "&Save" };
        f.Add(act);
        f.Add(btn);
        f.PerformLayout();
        Assert.Null(btn.Action);                            // the cancelled name doesn't resolve later
        Assert.DoesNotContain(btn, act.Clients);
    }

    // ---- MenuItem.ActionName from code adopts the action's values; the form file keeps its own ----

    static (Form form, MainMenu menu, MenuItem item, ActionItem act) MenuRig()
    {
        var f = new Form();
        f.SetBounds(0, 0, 400, 200);
        var act = new ActionItem { Name = "actSave", Caption = "&Save", Shortcut = "Mod+S", Enabled = false };
        var item = new MenuItem { Caption = "Mine", Shortcut = "F2" };
        var menu = new MainMenu();
        var top = new MenuItem { Caption = "&File" };
        top.Items.Add(item);
        menu.Items.Add(top);
        f.Add(act);
        f.Add(menu);
        return (f, menu, item, act);
    }

    [Fact]
    public void A_menu_item_whose_ActionName_is_set_from_code_takes_the_actions_values_and_follows_it()
    {
        var (f, _, item, act) = MenuRig();
        item.ActionName = "actSave";
        f.PerformLayout();                                  // the form resolves the name
        Assert.Equal(("&Save", "Mod+S", false), (item.Caption, item.Shortcut, item.Enabled));
        act.Caption = "&Store";
        Assert.Equal("&Store", item.Caption);
        Assert.Same(act, item.Action);
    }

    [Fact]
    public void A_menu_item_loaded_from_a_form_file_keeps_the_values_the_file_sets()
    {
        const string json = """
            { "format": "radform", "version": "1.0", "root": { "class": "Form", "name": "frmA", "width": 400, "height": 300,
              "children": [
                { "class": "MainMenu", "name": "mnu", "items": [ { "caption": "&File", "items": [
                  { "name": "mnuSave", "caption": "Keep me", "action": "actSave" } ] } ] },
                { "class": "Action", "name": "actSave", "caption": "&Save", "shortcut": "Mod+S", "left": 10, "top": 200 }
              ] } }
            """;
        var (form, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);
        form.PerformLayout();
        var item = ((MainMenu)form.FindControl("mnu")!).Items[0].Items[0];
        Assert.Equal("Keep me", item.Caption);              // explicit in the file: the file wins, as in Delphi
        Assert.Equal("Mod+S", item.Shortcut);               // default in the file: from the action
        Assert.Contains("\"Keep me\"", FormWriter.Save(form));   // and the designer saves it back
    }

    // ---- links made from code across forms ----

    static (Form main, ActionItem act, Form dlg, Button btn) TwoForms()
    {
        var main = new Form();
        main.SetBounds(0, 0, 300, 200);
        var act = new ActionItem { Name = "actSave", Caption = "&Save" };
        main.Add(act);
        var dlg = new Form();
        dlg.SetBounds(0, 0, 300, 100);
        var btn = new Button { Name = "dlgBtn" };
        btn.SetBounds(10, 10, 100, 26);
        dlg.Add(btn);
        btn.Action = act;
        return (main, act, dlg, btn);
    }

    [Fact]
    public void A_code_link_to_another_forms_action_is_dropped_when_the_clients_form_closes_and_comes_back_when_it_is_shown_again()
    {
        var (_, act, dlg, btn) = TwoForms();
        dlg.PerformLayout();
        dlg.NotifyShown();
        Assert.Contains(btn, act.Clients);
        Assert.Equal("&Save", btn.Caption);

        dlg.Close();
        Assert.DoesNotContain(btn, act.Clients);            // a closed dialog is no client: no leak, no writes to it
        act.Caption = "&Store";
        Assert.Equal("&Save", btn.Caption);
        Assert.Same(act, btn.Action);                       // the reference is still there for the next show

        dlg.NotifyShown();                                  // reusing the dialog needs no re-linking
        Assert.Contains(btn, act.Clients);
        Assert.Equal("&Store", btn.Caption);
        act.Caption = "&Keep";
        Assert.Equal("&Keep", btn.Caption);
    }

    [Fact]
    public void A_button_removed_from_its_form_is_no_longer_a_client_and_links_again_when_added_back()
    {
        var (_, act, dlg, btn) = TwoForms();
        dlg.PerformLayout();
        Assert.Contains(btn, act.Clients);
        dlg.Remove(btn);
        Assert.DoesNotContain(btn, act.Clients);
        act.Caption = "&Gone";
        Assert.NotEqual("&Gone", btn.Caption);

        var panel = new Panel();
        dlg.Add(panel);
        panel.Add(btn);                                     // moved: back on the form
        Assert.Contains(btn, act.Clients);
        Assert.Equal("&Gone", btn.Caption);
    }

    [Fact]
    public void A_menu_item_of_a_removed_menu_bar_is_no_longer_a_client()
    {
        var (_, act, dlg, _) = TwoForms();
        var menu = new MainMenu();
        var top = new MenuItem { Caption = "&File" };
        var item = new MenuItem();
        top.Items.Add(item);
        menu.Items.Add(top);
        dlg.Add(menu);
        item.Action = act;
        Assert.Contains(item, act.Clients);
        dlg.Remove(menu);
        Assert.DoesNotContain(item, act.Clients);
        dlg.Add(menu);
        Assert.Contains(item, act.Clients);
        dlg.NotifyShown();
        dlg.Close();
        Assert.DoesNotContain(item, act.Clients);
    }

    // ---- the idle list ----

    static (Form form, Func<int> updates) ShownWithUpdate()
    {
        var f = new Form();
        f.SetBounds(0, 0, 200, 100);
        int n = 0;
        var act = new ActionItem { Name = "actX" };
        act.Update += _ => n++;
        f.Add(act);
        f.NotifyShown();
        return (f, () => n);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference ShowAndAbandon()
    {
        var (f, _) = ShownWithUpdate();
        Application.RaiseIdle();
        return new WeakReference(f);                        // never closed, never told the host closed it (a torn-down live-run session)
    }

    [Fact]
    public void A_shown_form_nobody_closed_can_still_be_collected()
    {
        var weak = ShowAndAbandon();
        for (int i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive);                         // the idle list holds forms weakly
    }

    [Fact]
    public void Idle_updates_only_touch_forms_owned_by_the_thread_that_raises_idle()
    {
        Form? other = null;
        Func<int>? otherUpdates = null;
        var t = new Thread(() => (other, otherUpdates) = ShownWithUpdate());
        t.Start();
        t.Join();
        var (mine, mineUpdates) = ShownWithUpdate();
        try
        {
            Application.RaiseIdle();
            Assert.Equal(1, mineUpdates());
            Assert.Equal(0, otherUpdates!());               // another thread's form: not touched from here
        }
        finally { mine.Close(); other!.Close(); }
    }

    // ---- shortcuts and bulk adds ----

    [Fact]
    public void A_shortcut_only_counts_as_handled_when_an_action_actually_ran()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form();
        f.SetBounds(0, 0, 300, 200);
        var first = new ActionItem { Name = "actFirst", Shortcut = "Mod+N" };    // enabled, but nothing to run
        var second = new ActionItem { Name = "actSecond", Shortcut = "Mod+N" };
        int runs = 0;
        second.Executed += _ => runs++;
        f.Add(first);
        f.Add(second);
        f.PerformLayout();
        f.Simulate.Key("Mod+N");
        Assert.Equal(1, runs);                              // the handler-less action didn't swallow the key
    }

    [Fact]
    public void Adding_many_controls_does_not_rebuild_the_action_list_each_time()
    {
        var f = new Form();
        f.SetBounds(0, 0, 300, 200);
        f.Add(new ActionItem { Name = "actA", Shortcut = "F5" });
        f.NotifyShown();
        for (int i = 0; i < 20000; i++) f.Add(new Panel());   // quadratic if every add rescans the tree
        Assert.Single(f.Descendants().OfType<ActionItem>());
        f.Close();
    }

    // ---- resuming a code link keeps what the user set on the client ----

    [Fact]
    public void Showing_a_dialog_again_keeps_a_caption_set_on_its_linked_button_and_still_follows_linked_members()
    {
        var (_, act, dlg, btn) = TwoForms();
        dlg.PerformLayout();
        dlg.NotifyShown();
        btn.Caption = "Save and close";                     // an override: no longer linked for the caption
        dlg.Close();
        act.Enabled = false;                                // changed while the dialog was closed
        dlg.NotifyShown();
        Assert.Equal("Save and close", btn.Caption);
        Assert.False(btn.Enabled);                          // Enabled was linked: it catches up
        act.Enabled = true;
        Assert.True(btn.Enabled);
    }

    [Fact]
    public void Moving_a_linked_button_between_panels_on_an_open_form_keeps_its_own_values()
    {
        var (_, act, dlg, btn) = TwoForms();
        var p1 = new Panel();
        var p2 = new Panel();
        dlg.Add(p1);
        dlg.Add(p2);
        dlg.Remove(btn);
        p1.Add(btn);
        btn.Caption = "Mine";
        p2.Add(btn);
        Assert.Equal("Mine", btn.Caption);
        Assert.Contains(btn, act.Clients);
        act.Caption = "&Other";
        Assert.Equal("Mine", btn.Caption);
    }
}
