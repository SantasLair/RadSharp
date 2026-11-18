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

    [Fact]
    public void A_click_on_a_trees_open_rename_editor_selects_the_tree()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var host = new Form();
        host.SetBounds(0, 0, 800, 600);
        var doc = new DesignDocument();
        var surface = new DesignSurface { Document = doc, Align = Align.Client };
        host.Add(surface);
        host.PerformLayout();

        var tree = (TreeView)doc.Create(ComponentRegistry.Find("TreeView")!, doc.Form, 20, 20, 200, 150);
        tree.ReadOnly = false;
        tree.Add("node");
        host.PerformLayout();
        tree.Selected = tree.Nodes[0];
        tree.BeginEdit(tree.Nodes[0]);
        host.PerformLayout();
        var editor = Assert.Single(tree.Children, c => c.IsTransient);
        Assert.Same(tree, surface.ControlAt(editor.Left + tree.Left + 10, editor.Top + tree.Top + 5));
    }

    [Fact]
    public void Every_M14_list_control_is_placeable_with_a_picture_and_the_additional_category()
    {
        foreach (var name in new[] { "ListBox", "ComboBox", "CheckListBox", "ListView", "TreeView", "StringGrid", "DrawGrid" })
        {
            Assert.Contains(ComponentPalette.Classes(), c => c.Name == name);
            Assert.True(PaletteIcons.HasArt(name), name);
            Assert.Equal("Additional", PaletteCategories.Of(name));
        }
    }
}
