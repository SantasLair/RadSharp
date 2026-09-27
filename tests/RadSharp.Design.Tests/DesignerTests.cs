using RadSharp.Design;

namespace RadSharp.Tests;

/// <summary>The design surface, driven with simulated mouse and keyboard input like a user would.</summary>
public class DesignerTests
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

    static (int X, int Y) At(DesignSurface s, int x, int y)
    {
        var (sx, sy) = s.DesignToScreen(x, y);
        return (s.AbsoluteLeft + sx, s.AbsoluteTop + sy);
    }

    static void Drag(Form host, DesignSurface s, (int X, int Y) from, (int X, int Y) to, KeyModifiers mods = KeyModifiers.None)
    {
        var a = At(s, from.X, from.Y);
        var b = At(s, to.X, to.Y);
        host.Simulate.Press(a.X, a.Y, modifiers: mods);
        for (int i = 1; i <= 4; i++) host.ProcessMouseMove(a.X + (b.X - a.X) * i / 4, a.Y + (b.Y - a.Y) * i / 4, mods);
        host.Simulate.Release(b.X, b.Y, modifiers: mods);
    }

    static void ClickAt(Form host, DesignSurface s, int x, int y, KeyModifiers mods = KeyModifiers.None)
    {
        var p = At(s, x, y);
        host.Simulate.Click(p.X, p.Y, modifiers: mods);
    }

    static void RightClickAt(Form host, DesignSurface s, int x, int y)
    {
        var p = At(s, x, y);
        host.Simulate.Click(p.X, p.Y, MouseButton.Right);
    }

    [Fact]
    public void Palette_click_creates_a_control_at_the_snapped_point_with_its_default_size()
    {
        var (host, s, doc) = Setup();
        s.PendingClass = ComponentRegistry.Find("Button");
        ClickAt(host, s, 43, 38);
        var b = Assert.IsType<Button>(doc.Primary);
        Assert.Equal("Button1", b.Name);
        Assert.Equal(new Rect(40, 40, 80, 28), b.Bounds);
        Assert.Null(s.PendingClass);

        s.PendingClass = ComponentRegistry.Find("Panel");
        Drag(host, s, (200, 100), (330, 205));
        Assert.Equal(new Rect(200, 104, 128, 104), doc.Primary.Bounds);   // rubber-banded and snapped
    }

    [Fact]
    public void Controls_created_over_a_container_go_into_it()
    {
        var (host, s, doc) = Setup();
        s.PendingClass = ComponentRegistry.Find("Panel");
        Drag(host, s, (16, 16), (216, 216));
        var panel = doc.Primary;
        s.PendingClass = ComponentRegistry.Find("CheckBox");
        ClickAt(host, s, 40, 40);
        Assert.Same(panel, doc.Primary.Parent);
        Assert.Equal((24, 24), (doc.Primary.Left, doc.Primary.Top));   // relative to the panel

        s.PendingClass = ComponentRegistry.Find("Timer");
        ClickAt(host, s, 100, 100);                                        // components always go on the form
        Assert.Same(doc.Form, doc.Primary.Parent);
    }

    [Fact]
    public void Dragging_moves_with_grid_snapping_and_handles_resize()
    {
        var (host, s, doc) = Setup();
        s.PendingClass = ComponentRegistry.Find("Button");
        ClickAt(host, s, 40, 40);
        var b = doc.Primary;
        Drag(host, s, (50, 50), (81, 67));
        Assert.Equal((72, 56), (b.Left, b.Top));
        Drag(host, s, (72 + 80, 56 + 28), (72 + 80 + 37, 56 + 28 + 13));   // the bottom-right handle
        Assert.Equal((120, 40), (b.Width, b.Height));
        Drag(host, s, (80, 60), (97, 60), KeyModifiers.Alt);                // Alt: no snapping
        Assert.Equal(89, b.Left);
    }

    [Fact]
    public void Moving_snaps_to_sibling_edges()
    {
        var (host, s, doc) = Setup();
        s.PendingClass = ComponentRegistry.Find("Button");
        ClickAt(host, s, 40, 40);
        var first = doc.Primary;
        first.SetBounds(43, 40, 80, 28);   // off the grid
        s.PendingClass = ComponentRegistry.Find("Button");
        ClickAt(host, s, 40, 120);
        var second = doc.Primary;
        Drag(host, s, (50, 130), (52, 170));
        Assert.Equal(43, second.Left);     // lines up with the first button's left edge, not the grid
    }

    [Fact]
    public void Aligned_controls_resize_only_along_their_free_edge()
    {
        var (host, s, doc) = Setup();
        s.PendingClass = ComponentRegistry.Find("Panel");
        ClickAt(host, s, 16, 16);
        var p = doc.Primary;
        p.Align = Align.Top;
        doc.NotifyChanged();
        Drag(host, s, (100, 10), (100, 80));                  // moving does nothing
        Assert.Equal(0, p.Top);
        Drag(host, s, (doc.Form.Width / 2, p.Height), (doc.Form.Width / 2, 60));   // bottom handle
        Assert.Equal(64, p.Height);
    }

    [Fact]
    public void Rubber_band_selects_and_keys_delete_undo_redo_copy_paste()
    {
        var (host, s, doc) = Setup();
        foreach (var (x, y) in new[] { (40, 40), (160, 40), (40, 200) })
        {
            s.PendingClass = ComponentRegistry.Find("Button");
            ClickAt(host, s, x, y);
        }
        Drag(host, s, (30, 30), (260, 90));
        Assert.Equal(["Button1", "Button2"], doc.Selection.Select(c => c.Name));

        host.Simulate.Key(Key.Delete);
        Assert.Single(doc.Form.Children);
        host.Simulate.Key("Mod+Z");
        Assert.Equal(3, doc.Form.Children.Count);
        Assert.Equal(["Button1", "Button2"], doc.Selection.Select(c => c.Name));   // selection survives undo
        host.Simulate.Key("Mod+Shift+Z");
        Assert.Single(doc.Form.Children);
        host.Simulate.Key("Mod+Z");

        ClickAt(host, s, 400, 300);   // clear the multi-selection first
        ClickAt(host, s, 50, 50);
        host.Simulate.Key("Mod+C");
        host.Simulate.Key("Mod+V");
        var copy = doc.Primary;
        Assert.Equal("Button4", copy.Name);
        Assert.Equal((48, 48), (copy.Left, copy.Top));
        host.Simulate.Key(Key.Right);
        host.Simulate.Key(Key.Down, KeyModifiers.Shift);
        Assert.Equal((49, 29), (copy.Left, copy.Height));
    }

    [Fact]
    public void Form_resize_grip_changes_the_design_size()
    {
        var (host, s, doc) = Setup();
        Drag(host, s, (480, 320), (641, 402));
        Assert.Equal((640, 400), (doc.Form.Width, doc.Form.Height));
        Assert.Contains("\"width\": 640", doc.Save());
    }

    [Fact]
    public void Locked_controls_can_be_selected_but_not_dragged_resized_or_nudged()
    {
        var (host, s, doc) = Setup();
        s.PendingClass = ComponentRegistry.Find("Button");
        ClickAt(host, s, 40, 40);
        var b = doc.Primary;
        var original = b.Bounds;

        s.Locked = true;
        Drag(host, s, (50, 50), (81, 67));             // would normally move it
        Assert.Equal(original, b.Bounds);
        Drag(host, s, (40 + 80, 40 + 28), (200, 200));  // would normally resize it via the bottom-right handle
        Assert.Equal(original, b.Bounds);
        host.Simulate.Key(Key.Right);
        Assert.Equal(original, b.Bounds);

        doc.Select(null);
        ClickAt(host, s, 50, 50);                      // still selectable while locked
        Assert.Same(b, doc.Primary);

        s.Locked = false;
        Drag(host, s, (50, 50), (60, 60));
        Assert.NotEqual(original, b.Bounds);           // dragging works again once unlocked
    }

    [Fact]
    public void Right_click_selects_like_a_left_click_and_raises_the_context_menu_request()
    {
        var (host, s, doc) = Setup();
        s.PendingClass = ComponentRegistry.Find("Button");
        ClickAt(host, s, 40, 40);
        var button = doc.Primary;
        doc.Select(null);

        (int X, int Y)? requestedAt = null;
        s.ContextMenuRequested += (x, y) => requestedAt = (x, y);

        RightClickAt(host, s, 50, 50);   // inside the button
        Assert.Same(button, doc.Primary);
        Assert.NotNull(requestedAt);

        requestedAt = null;
        RightClickAt(host, s, 400, 300);   // empty canvas
        Assert.Empty(doc.Selection);
        Assert.NotNull(requestedAt);
    }

    [Fact]
    public void Right_click_on_an_already_selected_control_keeps_a_multi_selection()
    {
        var (host, s, doc) = Setup();
        foreach (var (x, y) in new[] { (40, 40), (160, 40) })
        {
            s.PendingClass = ComponentRegistry.Find("Button");
            ClickAt(host, s, x, y);
        }
        Drag(host, s, (30, 30), (260, 90));   // rubber-band both
        Assert.Equal(2, doc.Selection.Count);

        RightClickAt(host, s, 50, 50);   // right-click inside the already-selected first button
        Assert.Equal(2, doc.Selection.Count);
    }

    [Fact]
    public void Menu_editor_builds_item_trees_and_tab_order_editor_reorders()
    {
        var doc = new DesignDocument();
        var menu = (MainMenu)doc.Create(ComponentRegistry.Find("MainMenu")!, doc.Form, 0, 0);
        var editor = new MenuEditor(doc, menu);
        var file = editor.AddItem("File");
        editor.AddSubItem("Open");
        var exit = editor.AddItem("Exit");     // after "Open", inside File
        ((Edit)editor.Form.FindControl("edtShortcut")!).Text = "Mod+Q";
        ((Edit)editor.Form.FindControl("edtHandler")!).Text = "mnuExit_Click";
        editor.MoveSelected(-1);
        Assert.Equal(["Exit", "Open"], file.Items.Select(i => i.Caption));
        Assert.Equal(("Mod+Q", "mnuExit_Click"), (exit.Shortcut, exit.ClickHandler));

        var a = doc.Create(ComponentRegistry.Find("Edit")!, doc.Form, 10, 40);
        var b = doc.Create(ComponentRegistry.Find("Edit")!, doc.Form, 10, 80);
        var tabs = new TabOrderEditor(doc.Form);
        Assert.Equal([a, b], tabs.Order);
        tabs.SelectIndex(1);
        tabs.Move(-1);
        tabs.Apply();
        Assert.Equal([b, a], doc.Form.GetTabOrderList());
    }
}
