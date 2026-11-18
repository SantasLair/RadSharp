using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>TreeView v2 (M14 item 4): rows over expanded nodes, lazy expand, check boxes, rename, keys, form files, the v1 API.</summary>
public class TreeViewTests
{
    const KeyModifiers Ctrl = KeyModifiers.Ctrl, Shift = KeyModifiers.Shift;

    public TreeViewTests() => Shortcut.CommandModifier = KeyModifiers.Ctrl;

    // A 300 × 300 tree at the form's origin: row i spans y 1 + 24·i; a level-0 node's arrow is x 1–18, its text from 19.
    static (Form, TreeView) Make(Action<TreeView>? setup = null)
    {
        var f = new Form();
        f.SetBounds(0, 0, 300, 300);
        var tree = new TreeView();
        tree.SetBounds(0, 0, 300, 300);
        f.Add(tree);
        var maps = tree.Add("Maps");
        maps.Add("level1.map");
        maps.Add("level2.map");
        var sprites = tree.Add("Sprites");
        sprites.Add("hero.png");
        tree.Add("Sounds");
        setup?.Invoke(tree);
        f.PerformLayout();
        return (f, tree);
    }

    static int RowY(int i) => 13 + 24 * i;

    static string[] Rows(TreeView t) => [.. t.VisibleNodes().Select(n => n.Text)];

    [Fact]
    public void The_v1_api_still_works()
    {
        var (f, tree) = Make();
        var maps = tree.Nodes[0];
        int changes = 0;
        tree.Change += _ => changes++;
        Assert.Equal(["Maps", "Sprites", "Sounds"], Rows(tree));
        Assert.Same(maps, maps.Nodes[0].Parent);
        Assert.Equal(1, maps.Nodes[0].Level);

        tree.Selected = maps.Nodes[1];                 // expands its parent
        Assert.True(maps.Expanded);
        Assert.Equal("level2.map", tree.Selected?.Text);
        Assert.Equal(1, changes);
        tree.Toggle(maps);                             // collapsing moves the selection up to it
        Assert.Same(maps, tree.Selected);
        Assert.Equal(2, changes);
        tree.Selected = null;
        Assert.Null(tree.Selected);

        // Nodes added through the list get their parent (and level) too, and a node built off-tree keeps its flags.
        var extra = new TreeNode("Extra") { Expanded = true, Tag = 7 };
        extra.Nodes.Add(new TreeNode("child"));
        tree.Nodes.Add(extra);
        Assert.Same(extra, extra.Nodes[0].Parent);
        Assert.Equal(["Maps", "Sprites", "Sounds", "Extra", "child"], Rows(tree));
        Assert.Same(tree, extra.Nodes[0].TreeView);

        int doubles = 0;
        tree.DoubleClick += _ => doubles++;
        f.Simulate.DoubleClick(40, RowY(0));           // a double click toggles, as before
        Assert.True(maps.Expanded);
        Assert.Equal(1, doubles);
    }

    [Fact]
    public void Expanding_inserts_rows_into_the_selection_and_collapsing_removes_them()
    {
        var (f, tree) = Make(t => t.MultiSelect = true);
        tree.Focus();
        f.Simulate.Click(40, RowY(1));                 // Sprites
        f.Simulate.Click(40, RowY(2), MouseButton.Left, Ctrl);   // + Sounds
        Assert.Equal(["Sprites", "Sounds"], tree.SelectedNodes.Select(n => n.Text));

        f.Simulate.Click(8, RowY(0));                  // expand Maps above them: rows shift down, the selection follows
        Assert.Equal(["Maps", "level1.map", "level2.map", "Sprites"], Rows(tree)[..4]);
        Assert.Equal(["Sprites", "Sounds"], tree.SelectedNodes.Select(n => n.Text));
        Assert.Equal("Sounds", tree.Selected?.Text);

        f.Simulate.Click(40, RowY(1), MouseButton.Left, Shift);  // the anchor (Sounds) moved with its row
        Assert.Equal(["level1.map", "level2.map", "Sprites", "Sounds"], tree.SelectedNodes.Select(n => n.Text));

        f.Simulate.Click(8, RowY(0));                  // collapse Maps: its selected children go, the rest stay,
        Assert.Equal(["Maps", "Sprites", "Sounds"], tree.SelectedNodes.Select(n => n.Text));
        Assert.Equal("Maps", tree.Selected?.Text);     // and the hidden current node gives way to Maps
        Assert.Equal(3, tree.VisibleCount);
    }

    [Fact]
    public void Lazy_expand_adds_children_on_demand_and_events_can_refuse()
    {
        var (f, tree) = Make();
        var lazy = tree.Add("Lazy");
        lazy.HasChildren = true;
        var empty = tree.Add("Empty");
        empty.HasChildren = true;
        var log = new List<string>();
        tree.Expanding += (_, e) =>
        {
            log.Add("+" + e.Node.Text);
            if (e.Node == lazy && lazy.Nodes.Count == 0) { lazy.Add("loaded 1"); lazy.Add("loaded 2"); }
            if (e.Node.Text == "Sprites") e.Cancel = true;
        };
        tree.Collapsing += (_, e) => { log.Add("-" + e.Node.Text); e.Cancel = e.Node == lazy; };
        f.PerformLayout();

        f.Simulate.Click(8, RowY(3));                  // Lazy
        Assert.True(lazy.Expanded);
        Assert.Equal(["Maps", "Sprites", "Sounds", "Lazy", "loaded 1", "loaded 2", "Empty"], Rows(tree));
        f.Simulate.Click(8, RowY(3));                  // collapsing refused
        Assert.True(lazy.Expanded);
        f.Simulate.Click(8, RowY(1));                  // Sprites: expanding refused
        Assert.False(tree.Nodes[1].Expanded);
        f.Simulate.Click(8, RowY(6));                  // Empty: nothing came, so its arrow goes
        Assert.False(empty.Expanded);
        Assert.False(empty.HasChildren);
        Assert.Equal(["+Lazy", "-Lazy", "+Sprites", "+Empty"], log);

        tree.Nodes[0].Expanded = true;                 // from code too
        Assert.Equal("+Maps", log[^1]);
    }

    [Fact]
    public void Keys_walk_the_tree_like_windows()
    {
        var (f, tree) = Make();
        tree.Focus();
        f.Simulate.Key(Key.Down);
        Assert.Equal("Maps", tree.Selected?.Text);
        f.Simulate.Key(Key.Right);                     // expand
        f.Simulate.Key(Key.Right);                     // first child
        Assert.Equal("level1.map", tree.Selected?.Text);
        f.Simulate.Key(Key.Left);                      // parent
        f.Simulate.Key(Key.Left);                      // collapse
        Assert.False(tree.Nodes[0].Expanded);
        f.Simulate.Type("+");
        Assert.True(tree.Nodes[0].Expanded);
        f.Simulate.Type("-");
        Assert.False(tree.Nodes[0].Expanded);
        f.Simulate.Key(Key.End);
        Assert.Equal("Sounds", tree.Selected?.Text);
        f.Simulate.Key(Key.Home);
        f.Simulate.Type("*");                          // everything below
        Assert.Equal(["Maps", "level1.map", "level2.map", "Sprites", "Sounds"], Rows(tree));
        tree.FullCollapse();
        Assert.Equal(3, tree.VisibleCount);
        tree.FullExpand();
        Assert.Equal(6, tree.VisibleCount);

        tree.Selected = tree.Nodes[0];
        f.Simulate.Type("s");                          // type-ahead over the visible rows
        Assert.Equal("Sprites", tree.Selected?.Text);
        f.Simulate.Type("o");                          // "so"
        Assert.Equal("Sounds", tree.Selected?.Text);
        f.Simulate.Key(Key.PageUp);
        Assert.Equal("Maps", tree.Selected?.Text);
        f.Simulate.Key(Key.PageDown);
        Assert.Equal("Sounds", tree.Selected?.Text);
    }

    [Fact]
    public void Check_boxes_toggle_from_a_click_or_space_and_raise_NodeChecked()
    {
        var (f, tree) = Make(t => t.Checkboxes = true);
        var log = new List<string>();
        tree.NodeChecked += (_, n) => log.Add($"{n.Text}={n.Checked}");
        tree.Selected = tree.Nodes[2];
        f.Simulate.Click(26, RowY(1));                 // Sprites' box (x 19–36)
        Assert.True(tree.Nodes[1].Checked);
        Assert.Same(tree.Nodes[2], tree.Selected);     // the selection didn't move
        tree.Focus();
        f.Simulate.Key(Key.Space);
        Assert.Equal(["Sprites=True", "Sounds=True"], log);
    }

    [Fact]
    public void Rename_with_F2_or_a_slow_second_click_and_events_can_veto()
    {
        var (f, tree) = Make();
        var sounds = tree.Nodes[2];
        tree.Selected = sounds;
        tree.Focus();
        f.Simulate.Key(Key.F2);
        Assert.False(tree.IsEditing);                  // read-only by default

        tree.ReadOnly = false;
        var log = new List<string>();
        tree.Editing += (_, e) => { log.Add("editing " + e.Node.Text); e.Accept = e.Node.Text != "Maps"; };
        tree.Edited += (_, e) => { log.Add("edited " + e.Text); e.Accept = e.Text.Length > 0; e.Text = e.Text.Trim(); };
        f.Simulate.Key(Key.F2);
        Assert.True(tree.IsEditing);
        Assert.Same(sounds, tree.EditingNode);
        Assert.Equal("Sounds", tree.InplaceEditor!.Text);
        Assert.True(tree.InplaceEditor.Focused);
        tree.InplaceEditor.Text = "";
        f.Simulate.Key(Key.Enter);                     // refused: stays open
        Assert.True(tree.IsEditing);
        tree.InplaceEditor.Text = " Audio ";
        f.Simulate.Key(Key.Enter);
        Assert.False(tree.IsEditing);
        Assert.Equal("Audio", sounds.Text);
        Assert.True(tree.Focused);
        Assert.DoesNotContain(tree.Children, c => c is Edit);

        f.Simulate.Key(Key.F2);
        tree.InplaceEditor!.Text = "Nope";
        f.Simulate.Key(Key.Escape);                    // cancel
        Assert.Equal("Audio", sounds.Text);

        tree.Selected = tree.Nodes[0];
        Assert.False(tree.BeginEdit());                // Editing refused Maps

        // A slow second click on the selected node renames once the double-click time has passed...
        tree.Selected = sounds;
        f.Simulate.Click(60, RowY(2));
        Assert.False(tree.IsEditing);
        f.ProcessTick(f.Now + TreeView.RenameDelayMs + 1);
        Assert.True(tree.IsEditing);
        Assert.Equal(FormWriter.Save(f), FormWriter.Save(f));   // the editor is transient: never saved
        Assert.DoesNotContain("\"class\": \"Edit\"", FormWriter.Save(f));
        tree.EndEdit(commit: false);

        // ...but a double click doesn't.
        f.Simulate.DoubleClick(60, RowY(2));
        f.ProcessTick(f.Now + 2 * TreeView.RenameDelayMs);
        Assert.False(tree.IsEditing);
        Assert.Equal(["editing Sounds", "edited ", "edited  Audio ", "editing Audio", "editing Maps", "editing Audio"], log);
    }

    [Fact]
    public void Code_changes_keep_the_selection_on_its_node()
    {
        var (f, tree) = Make();
        var sprites = tree.Nodes[1];
        tree.Selected = sprites;
        tree.Nodes.Insert(0, new TreeNode("First"));
        Assert.Same(sprites, tree.Selected);
        Assert.Equal(2, tree.Selection.Current);

        tree.Selected = sprites.Nodes[0];
        sprites.Nodes.Clear();                         // the selected node goes: its parent takes over
        Assert.Same(sprites, tree.Selected);
        Assert.False(sprites.HasChildren);

        tree.Nodes.Remove(sprites);
        Assert.Null(sprites.TreeView);
        Assert.Null(tree.Selected);
        tree.Nodes.Clear();
        Assert.Equal(0, tree.VisibleCount);

        var a = tree.Add("a");
        var b = a.Add("b");
        Assert.Throws<InvalidOperationException>(() => b.Nodes.Add(a));   // not under itself
        tree.Nodes.Add(b);                             // moves it to the top level
        Assert.Empty(a.Nodes);
        Assert.Null(b.Parent);
    }

    [Fact]
    public void Long_rows_get_a_horizontal_scroll_bar_and_many_rows_a_vertical_one()
    {
        var (f, tree) = Make();
        Assert.False(tree.HorizontalBarVisible || tree.VerticalBarVisible);
        tree.Add(new string('W', 80));
        for (int i = 0; i < 20; i++) tree.Add($"Node {i}");
        f.PerformLayout();
        Assert.True(tree.HorizontalBarVisible && tree.VerticalBarVisible);
        Assert.Equal(24 * 24, tree.ContentHeight);
        Assert.True(tree.ContentWidth > tree.Viewport.Width);
    }

    [Fact]
    public void Nodes_round_trip_through_a_form_file()
    {
        const string json = """
            {
              "format": "radform", "version": "1.0",
              "root": { "class": "Form", "name": "frm", "width": 300, "height": 300, "children": [
                { "class": "TreeView", "name": "tvFiles", "checkboxes": true, "readOnly": false, "multiSelect": true, "images": "icons",
                  "nodes": [
                    { "imageIndex": 0, "expanded": true, "text": "Maps", "nodes": [ { "text": "level1.map", "checked": true }, { "text": "level2.map", "colour": "red" } ] },
                    { "text": "Remote", "hasChildren": true },
                    "oops",
                    {}
                  ],
                  "onExpanding": "tvFiles_Expanding", "onEdited": "tvFiles_Edited", "left": 0, "top": 0 }
              ] }
            }
            """;
        var (form, warnings) = FormReader.Load(json);
        Assert.Equal([
            "tvFiles.nodes[0].nodes[1]: tree nodes have no property \"colour\"; ignored.",
            "tvFiles.nodes[2]: a tree node must be an object; skipped.",
        ], warnings);
        var tree = (TreeView)form.FindControl("tvFiles")!;
        Assert.Equal((true, false, true), (tree.Checkboxes, tree.ReadOnly, tree.MultiSelect));
        Assert.Equal(["Maps", "level1.map", "level2.map", "Remote", ""], Rows(tree));
        Assert.True(tree.Nodes[0].Nodes[0].Checked);
        Assert.True(tree.Nodes[1].HasChildren);
        Assert.Equal("tvFiles_Expanding", tree.EventHandlers["onExpanding"]);

        var saved = FormWriter.Save(form);
        Assert.Contains("\"nodes\": [", saved);
        Assert.True(saved.IndexOf("\"onExpanding\"") < saved.IndexOf("\"nodes\""));   // after the other members
        Assert.Equal(saved, FormWriter.Save(FormReader.Load(saved).Form));

        var inspector = new PropertyGrid();
        inspector.SetTargets([tree]);
        int row = inspector.IndexOf("nodes");
        Assert.True(inspector.IsCollection(row));
        Assert.Equal("(5 nodes)", inspector.RowValue(row));
    }

    [Fact]
    public void Images_from_the_image_list_take_the_icon_slot_of_every_node()
    {
        var (f, tree) = Make();
        f.Add(new ImageList { Name = "icons", Images = [ViewImages.Square(new SkiaSharp.SKColor(30, 90, 220))] });
        tree.ImagesName = "icons";
        tree.Nodes[1].ImageIndex = 0;
        bool iconCalled = false;
        tree.Nodes[2].Icon = (_, _) => iconCalled = true;   // no image: the painter still draws in the slot
        using var bmp = FormRenderer.RenderToBitmap(f);
        var pixel = bmp.GetPixel(19 + 10, RowY(1));
        Assert.True(pixel.Blue > 180 && pixel.Red < 80, $"expected the blue image, got {pixel}");
        Assert.True(iconCalled);
        Assert.True(tree.NodeAt(19 + 20 + 4, RowY(0)) == tree.Nodes[0]);
    }

    [Fact]
    public void Review_fixes_rename_survives_code_changes_and_handlers_can_re_enter()
    {
        var (f, tree) = Make(t => t.ReadOnly = false);
        var sounds = tree.Nodes[2];
        tree.Selected = sounds;
        tree.Focus();
        int edited = 0;
        tree.Edited += (_, e) => { edited++; tree.Nodes.Sort((a, b) => string.CompareOrdinal(b.Text, a.Text)); };
        Assert.True(tree.BeginEdit());
        tree.Add("Added while renaming");
        f.PerformLayout();
        Assert.True(tree.IsEditing);                   // a rebuild of the rows doesn't commit the edit
        tree.InplaceEditor!.Text = "Audio";
        f.Simulate.Key(Key.Enter);
        Assert.Equal(1, edited);                       // once, though the handler re-sorted the rows
        Assert.Equal("Audio", sounds.Text);
        Assert.Same(sounds, tree.Selected);

        // An Expanding handler may expand the node itself, or select one of its children.
        var maps = tree.Nodes.Single(n => n.Text == "Maps");
        tree.Expanding += (_, e) => { e.Node.Expanded = true; tree.Selected = e.Node.Nodes[0]; };
        tree.Toggle(maps);
        Assert.True(maps.Expanded);
        Assert.Same(maps.Nodes[0], tree.Selected);

        // Replacing a node with its own ancestor is refused before anything changes.
        Assert.Throws<InvalidOperationException>(() => maps.Nodes[0] = maps);
        Assert.Equal(2, maps.Nodes.Count);

        // Removing the selected leaf, then its parent: the grandparent takes over.
        var deep = maps.Nodes[1].Add("deep");
        tree.Selected = deep;
        var level2 = maps.Nodes[1];
        level2.Nodes.Remove(deep);
        maps.Nodes.Remove(level2);
        Assert.Same(maps, tree.Selected);
    }

    [Fact]
    public void Review_fixes_keys_after_a_type_ahead_pause_and_collapsing_everything_in_multi_select()
    {
        var (f, tree) = Make(t => t.MultiSelect = true);
        tree.Focus();
        tree.Selected = tree.Nodes[0];
        f.Simulate.Type("x");                          // no match, but a search is under way
        f.ProcessTick(f.Now + 2000);
        f.Simulate.Type("+");                          // after the pause it expands again
        Assert.True(tree.Nodes[0].Expanded);

        tree.Selected = tree.Nodes[0].Nodes[1];
        tree.FullCollapse();
        Assert.Same(tree.Nodes[0], tree.Selected);
        Assert.Equal([tree.Nodes[0]], tree.SelectedNodes);   // selected, as a click on its arrow does
    }

    sealed class ProbeTree : TreeView
    {
        public bool Busy;
        protected override void PaintContent(Canvas canvas, Rect visible)
        {
            Busy = true;
            try { base.PaintContent(canvas, visible); }
            finally { Busy = false; }
        }
        protected override void MeasureContent()
        {
            Busy = true;
            try { base.MeasureContent(); }
            finally { Busy = false; }
        }
    }

    [Fact]
    public void Change_for_a_code_made_tree_change_never_runs_inside_layout_or_paint_and_fires_once()
    {
        var f = new Form();
        f.SetBounds(0, 0, 300, 300);
        var tree = new ProbeTree();
        tree.SetBounds(0, 0, 300, 300);
        f.Add(tree);
        var maps = tree.Add("Maps");
        maps.Add("a.map");
        tree.Add("Other");
        f.PerformLayout();
        tree.Selected = maps.Nodes[0];
        int changes = 0, insideBusy = 0;
        tree.Change += s =>
        {
            changes++;
            if (tree.Busy) insideBusy++;
            _ = tree.VisibleCount;                 // a handler that inspects the tree ...
            if (changes == 1) tree.Add("Added in handler");   // ... and changes it
        };
        tree.Nodes.Remove(maps);                   // the current node goes: a code-made change, rows rebuild lazily
        f.PerformLayout();                         // layout and paint are not safe points
        using (var bmp = FormRenderer.RenderToBitmap(f)) { }
        Assert.Equal(0, insideBusy);
        _ = tree.VisibleCount;                     // the next call from outside is
        Assert.Equal(1, changes);
        Assert.Equal(0, insideBusy);
        f.PerformLayout();
        using (var bmp = FormRenderer.RenderToBitmap(f)) { }
        _ = tree.VisibleCount;
        Assert.Equal(1, changes);                  // no repeats without a new change
    }
}
