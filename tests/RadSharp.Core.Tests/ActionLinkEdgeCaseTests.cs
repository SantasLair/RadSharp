using System.Text.Json;

namespace RadSharp.Tests;

/// <summary>Regression tests for <see cref="ActionItem"/> links and the form writer: file order, removed actions, links to another form.</summary>
public class ActionLinkEdgeCaseTests
{
    const string Json = """
        { "format": "radform", "version": "1.0", "root": { "class": "Form", "name": "frmActions", "width": 400, "height": 300,
          "children": [
            { "class": "MainMenu", "name": "mnuMain", "items": [
              { "caption": "&File", "items": [ { "name": "mnuSave", "action": "actSave" } ] } ] },
            { "class": "Button", "name": "btnSave", "action": "actSave", "left": 10, "top": 80 },
            { "class": "Action", "name": "actSave", "caption": "&Save", "hint": "Save it", "shortcut": "Mod+S", "left": 10, "top": 200 }
          ] } }
        """;

    static Form Load()
    {
        var (form, warnings) = FormReader.Load(Json);
        Assert.Empty(warnings);
        form.PerformLayout();   // links are resolved
        return form;
    }

    [Fact]
    public void Saving_right_after_the_action_is_removed_keeps_the_values_its_clients_show()
    {
        var f = Load();
        var save = Assert.IsType<ActionItem>(f.FindControl("actSave"));
        f.Remove(save);                                     // e.g. deleted in the designer, then saved at once
        using var doc = JsonDocument.Parse(FormWriter.Save(f));
        var children = doc.RootElement.GetProperty("root").GetProperty("children");
        var btn = children.EnumerateArray().First(c => c.GetProperty("name").GetString() == "btnSave");
        Assert.Equal("&Save", btn.GetProperty("caption").GetString());
        Assert.Equal("Save it", btn.GetProperty("hint").GetString());
        var item = children.EnumerateArray().First(c => c.GetProperty("name").GetString() == "mnuMain")
            .GetProperty("items")[0].GetProperty("items")[0];
        Assert.Equal("&Save", item.GetProperty("caption").GetString());
        Assert.Equal("Mod+S", item.GetProperty("shortcut").GetString());
    }

    [Fact]
    public void An_action_before_its_clients_in_the_file_still_lets_the_file_values_win()
    {
        const string json = """
            { "format": "radform", "version": "1.0", "root": { "class": "Form", "name": "frmA", "width": 400, "height": 300,
              "children": [
                { "class": "Action", "name": "actSave", "caption": "&Save", "hint": "Save it", "left": 10, "top": 200 },
                { "class": "Panel", "name": "pnl", "children": [
                  { "class": "Button", "name": "btnKeep", "caption": "Keep", "hint": "Mine", "action": "actSave", "left": 100, "top": 10 } ] },
                { "class": "Button", "name": "btnOwn", "caption": "Own", "enabled": false, "action": "actSave", "left": 100, "top": 80 },
                { "class": "MainMenu", "name": "mnu", "items": [ { "caption": "&File", "items": [
                  { "name": "mnuSaveAs", "caption": "Save &as", "action": "actSave" } ] } ] }
              ] } }
            """;
        var (f, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);
        var keep = Assert.IsType<Button>(f.FindControl("btnKeep"));
        var own = Assert.IsType<Button>(f.FindControl("btnOwn"));
        Assert.Equal(("Keep", "Mine"), (keep.Caption, keep.Hint));
        Assert.Equal(("Own", false, "Save it"), (own.Caption, own.Enabled, own.Hint));
        Assert.Equal("Save &as", ((MainMenu)f.FindControl("mnu")!).Items[0].Items[0].Caption);
        var saved = FormWriter.Save(f);
        Assert.Equal(saved, FormWriter.Save(FormReader.Load(saved).Form));
    }

    [Fact]
    public void A_control_linked_from_code_to_another_forms_action_saves_its_values()
    {
        var f = Load();
        var other = new Form();
        var shared = new ActionItem { Name = "actShared", Caption = "Shared", Hint = "From elsewhere" };
        other.Add(shared);
        var btn = new Button { Name = "btnShared" };
        f.Add(btn);
        btn.Action = shared;
        using var doc = JsonDocument.Parse(FormWriter.Save(f));
        var node = doc.RootElement.GetProperty("root").GetProperty("children").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "btnShared");
        // "actShared" isn't on this form, so loading the file can't copy the values back: they must be written.
        Assert.Equal("Shared", node.GetProperty("caption").GetString());
        Assert.Equal("From elsewhere", node.GetProperty("hint").GetString());
    }
}
