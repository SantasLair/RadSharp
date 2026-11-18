using RadSharp.Design;
using RadAction = RadSharp.Actions.Action;

namespace RadSharp.Tests;

/// <summary>The Action component in the designer: palette, tray, saving, and the surface following inspector edits.</summary>
public class ActionDesignTests
{
    [Fact]
    public void Action_is_on_the_Standard_palette_with_its_own_picture()
    {
        Assert.Contains(ComponentPalette.Classes(), c => c.Name == "Action");
        Assert.True(PaletteIcons.HasArt("Action"));
        Assert.Equal("Standard", PaletteCategories.Of("Action"));
        Assert.Equal(PaletteCategories.Of("PopupMenu"), PaletteCategories.Of("Action"));
    }

    [Fact]
    public void A_dropped_action_lands_on_the_form_and_a_button_linked_in_the_inspector_follows_it()
    {
        var doc = new DesignDocument();
        var btn = (Button)doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 20, 20);
        var act = Assert.IsType<RadAction>(doc.Create(ComponentRegistry.Find("Action")!, doc.Form, 300, 200));
        Assert.Same(doc.Form, act.Parent);                                  // components always go on the form (the tray)
        Assert.Equal("Action1", act.Name);
        Assert.Equal(new Rect(300, 200, 28, 28), DesignSurface.DesignRect(act));

        Assert.True(ComponentRegistry.Of(act).FindProperty("caption")!.TryParseValue(act, "&Open"));
        Assert.True(ComponentRegistry.Of(btn).FindProperty("action")!.TryParseValue(btn, "Action1"));   // what the inspector does
        Assert.Equal("&Open", btn.Caption);
        Assert.True(ComponentRegistry.Of(act).FindProperty("enabled")!.TryParseValue(act, "false"));
        Assert.False(btn.Enabled);                                           // the surface shows the linked state at once

        var json = doc.Save();
        Assert.Contains("\"action\": \"Action1\"", json);
        using (var parsed = System.Text.Json.JsonDocument.Parse(json))
        {
            var node = parsed.RootElement.GetProperty("root").GetProperty("children").EnumerateArray()
                .First(c => c.GetProperty("name").GetString() == "Button1");
            Assert.False(node.TryGetProperty("caption", out _));            // it comes from the action
            Assert.False(node.TryGetProperty("enabled", out _));
        }
        var reloaded = FormReader.Load(json).Form;
        Assert.Equal("&Open", ((Button)reloaded.FindControl("Button1")!).Caption);
        Assert.Equal(json, FormWriter.Save(reloaded));
    }
}
