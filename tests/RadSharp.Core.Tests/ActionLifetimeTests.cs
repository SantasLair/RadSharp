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
}
