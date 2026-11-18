using RadSharp.Drawing;
using RadSharp.Design;

namespace RadSharp.Tests;

/// <summary>M14 items 3–4 in the designer: the ListView Columns editor (the grid's, generalised) and the TreeView Nodes editor.</summary>
public class ViewDesignTests
{
    static (Form Host, DesignSurface Surface, DesignDocument Doc) Setup()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var host = new Form();
        host.SetBounds(0, 0, 800, 600);
        var doc = new DesignDocument();
        var surface = new DesignSurface { Document = doc, Align = Align.Client };
        host.Add(surface);
        host.PerformLayout();
        surface.Focus();
        return (host, surface, doc);
    }

    static T Drop<T>(Form host, DesignSurface s, string cls) where T : Control
    {
        s.PendingClass = ComponentRegistry.Find(cls);
        var (sx, sy) = s.DesignToScreen(40, 40);
        host.Simulate.Click(s.AbsoluteLeft + sx, s.AbsoluteTop + sy);
        return (T)s.Document!.Primary;
    }

    [Fact]
    public void The_columns_editor_edits_a_list_views_columns_and_the_session_is_one_undo_step()
    {
        var (host, s, doc) = Setup();
        var lv = Drop<ListView>(host, s, "ListView");
        string before = doc.Save();

        doc.Snapshot();
        var editor = new GridColumnsEditor(doc, lv);
        Assert.Equal("Columns: " + lv.Name, editor.Form.Caption);
        Assert.Contains("one untitled column", ((Label)editor.Form.FindControl("lblHint")!).Caption);
        var name = editor.AddColumn("Name");
        Assert.Single(lv.Columns);                    // no blank columns first: a list view has no column count
        editor.AddColumn("Size");
        ((SpinEdit)editor.Form.FindControl("spnWidth")!).Value = 70;
        ((ComboBox)editor.Form.FindControl("cboAlignment")!).ItemIndex = (int)TextAlign.Right;
        Assert.Equal((70, TextAlign.Right), (lv.Columns[1].Width, lv.Columns[1].Alignment));
        editor.Select(1);
        editor.MoveSelected(-1);
        Assert.Equal(["Size", "Name"], lv.Columns.Select(c => c.Title));
        Assert.Same(name, lv.Columns[1]);
        doc.NotifyChanged();

        string after = doc.Save();
        Assert.Contains("\"title\": \"Size\"", after);
        var reloaded = (ListView)FormReader.Load(after).Form.FindControl(lv.Name)!;
        Assert.Equal(["Size", "Name"], reloaded.Columns.Select(c => c.Title));

        Assert.True(doc.Undo());
        Assert.Equal(before, doc.Save());
        Assert.True(doc.Redo());
        Assert.Equal(after, doc.Save());
    }

    [Fact]
    public void The_nodes_editor_adds_children_and_siblings_indents_moves_and_deletes()
    {
        var (host, s, doc) = Setup();
        var tree = Drop<TreeView>(host, s, "TreeView");
        int changes = 0;
        doc.Changed += () => changes++;
        var editor = new TreeNodesEditor(doc, tree);
        Assert.Equal("Nodes: " + tree.Name, editor.Form.Caption);

        var maps = editor.AddSibling("Maps");
        var level1 = editor.AddChild("level1.map");
        var level2 = editor.AddSibling("level2.map");
        Assert.Same(maps, level2.Parent);
        editor.Select(maps);
        var sounds = editor.AddSibling("Sounds");
        Assert.Equal(["Maps", "Sounds"], tree.Nodes.Select(n => n.Text));
        Assert.Equal(["level1.map", "level2.map"], maps.Nodes.Select(n => n.Text));
        Assert.Same(sounds, editor.SelectedNode);
        Assert.True(changes > 0);

        // Fields edit the selected node; the editor's own tree follows.
        ((Edit)editor.Form.FindControl("edtText")!).Text = "Audio";
        ((SpinEdit)editor.Form.FindControl("spnImageIndex")!).Value = 2;
        Assert.Equal(("Audio", 2), (sounds.Text, sounds.ImageIndex));
        var mirror = (TreeView)editor.Form.FindControl("tvNodes")!;
        Assert.Equal("Audio", mirror.Selected?.Text);
        editor.Select(maps);
        ((CheckBox)editor.Form.FindControl("chkExpanded")!).Checked = true;
        Assert.True(maps.Expanded);

        // Indent makes a node the last child of the sibling above; outdent puts it back after its parent.
        editor.Select(sounds);
        editor.Indent();
        Assert.Same(maps, sounds.Parent);
        Assert.Equal(["level1.map", "level2.map", "Audio"], maps.Nodes.Select(n => n.Text));
        editor.Outdent();
        Assert.Null(sounds.Parent);
        Assert.Equal(["Maps", "Audio"], tree.Nodes.Select(n => n.Text));
        editor.MoveSelected(-1);
        Assert.Equal(["Audio", "Maps"], tree.Nodes.Select(n => n.Text));

        editor.Select(level1);
        editor.DeleteSelected();
        Assert.Equal(["level2.map"], maps.Nodes.Select(n => n.Text));
        Assert.Same(level2, editor.SelectedNode);
        editor.DeleteSelected();
        Assert.Same(maps, editor.SelectedNode);       // the last child gone: its parent is selected
        Assert.False(maps.HasChildren);
    }

    [Fact]
    public void A_nodes_editor_session_is_one_undo_step_and_the_nodes_round_trip_through_the_form_file()
    {
        var (host, s, doc) = Setup();
        var tree = Drop<TreeView>(host, s, "TreeView");
        string before = doc.Save();

        doc.Snapshot();
        var editor = new TreeNodesEditor(doc, tree);
        editor.AddSibling("Maps");
        editor.AddChild("level1.map");
        editor.AddSibling("level2.map");
        doc.NotifyChanged();

        string after = doc.Save();
        Assert.Contains("\"nodes\": [", after);
        Assert.Contains("\"text\": \"level2.map\"", after);
        var reloaded = (TreeView)FormReader.Load(after).Form.FindControl(tree.Name)!;
        Assert.Equal(["level1.map", "level2.map"], reloaded.Nodes[0].Nodes.Select(n => n.Text));

        Assert.True(doc.Undo());
        Assert.Equal(before, doc.Save());
        Assert.Empty(((TreeView)doc.Form.FindControl(tree.Name)!).Nodes);
        Assert.True(doc.Redo());
        Assert.Equal(after, doc.Save());
    }

    [Fact]
    public void The_design_surface_does_not_forward_input_to_views()
    {
        var (host, s, doc) = Setup();
        var tree = Drop<TreeView>(host, s, "TreeView");
        var node = tree.Add("Maps");
        node.Add("child");
        host.PerformLayout();
        var (sx, sy) = s.DesignToScreen(tree.Left + 8, tree.Top + 12);
        host.Simulate.Click(s.AbsoluteLeft + sx, s.AbsoluteTop + sy);   // on the arrow
        Assert.False(node.Expanded);
        Assert.Same(tree, doc.Primary);
    }
}
