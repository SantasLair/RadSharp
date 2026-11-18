using RadSharp.Design;

namespace RadSharp.Tests;

/// <summary>M14 list controls in the designer: the toolbox entry, and an editable combo box's inner edit staying out of the way.</summary>
public class ListControlsDesignTests
{
    [Fact]
    public void CheckListBox_is_in_the_palette_with_the_same_category_and_its_own_picture()
    {
        Assert.Contains(ComponentPalette.Classes(), c => c.Name == "CheckListBox");
        Assert.True(PaletteIcons.HasArt("CheckListBox"));
        Assert.Equal(PaletteCategories.Of("ListBox"), PaletteCategories.Of("CheckListBox"));
    }

    [Fact]
    public void A_click_on_an_editable_combo_box_selects_the_combo_box_not_its_inner_edit()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var host = new Form();
        host.SetBounds(0, 0, 800, 600);
        var doc = new DesignDocument();
        var surface = new DesignSurface { Document = doc, Align = Align.Client };
        host.Add(surface);
        host.PerformLayout();

        var combo = (ComboBox)doc.Create(ComponentRegistry.Find("ComboBox")!, doc.Form, 20, 20, 160, 26);
        combo.Style = ComboBoxStyle.DropDown;
        host.PerformLayout();
        Assert.NotNull(combo.EditPart);
        Assert.Same(combo, surface.ControlAt(30, 30));     // over the text part
        Assert.Same(combo, surface.ControlAt(170, 30));    // over the arrow

        var json = doc.Save();
        Assert.Contains("\"style\": \"dropDown\"", json);
        Assert.DoesNotContain("\"class\": \"Edit\"", json);
    }
}
