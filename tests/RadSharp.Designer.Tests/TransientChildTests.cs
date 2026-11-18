using RadSharp.Design;
using RadSharp.Design.Code;
using RadSharp.Designer;

namespace RadSharp.Tests;

/// <summary>Transient children (an editable combo box's edit, a tree's rename editor) stay out of the designer's structure tree and hit testing.</summary>
public class TransientChildTests
{
    static DesignerForm Start()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var dir = Directory.CreateTempSubdirectory("radsharp-transient-").FullName;
        var project = RadProject.Create(dir, "Transient");
        var designer = new DesignerForm(project.ProjectFile, Path.Combine(dir, "designer.json"))
        {
            Settings = { Editor = EditorKind.Custom, CustomEditorCommand = "dotnet --version" },
        };
        designer.SetBounds(0, 0, 1220, 780);
        designer.PerformLayout();
        return designer;
    }

    static IEnumerable<TreeNode> All(IEnumerable<TreeNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(All(n.Nodes)));

    [Fact]
    public void The_structure_tree_lists_neither_a_combo_boxs_edit_nor_a_trees_rename_editor()
    {
        var d = Start();
        var doc = d.Document!;
        var combo = (ComboBox)doc.Create(ComponentRegistry.Find("ComboBox")!, doc.Form, 20, 20, 160, 26);
        combo.Style = ComboBoxStyle.DropDown;
        var tree = (TreeView)doc.Create(ComponentRegistry.Find("TreeView")!, doc.Form, 20, 80, 200, 150);
        tree.ReadOnly = false;
        tree.Add("node");
        d.PerformLayout();
        tree.Selected = tree.Nodes[0];
        tree.BeginEdit(tree.Nodes[0]);
        d.PerformLayout();
        Assert.NotNull(combo.EditPart);
        Assert.Contains(tree.Children, c => c.IsTransient);   // the rename editor is open

        doc.SelectMany([combo]);
        doc.SelectMany([tree]);
        var rows = All(((TreeView)d.FindControl("tvStructure")!).Nodes).Select(n => n.Tag).OfType<Control>().ToList();
        Assert.Contains(combo, rows);
        Assert.Contains(tree, rows);
        Assert.DoesNotContain(rows, c => c.IsTransient);
    }
}
