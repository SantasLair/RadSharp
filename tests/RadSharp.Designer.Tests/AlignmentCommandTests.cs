using RadSharp.Design;
using RadSharp.Design.Code;
using RadSharp.Designer;

namespace RadSharp.Tests;

/// <summary>The Format menu's align/make-same-size/space/center commands, driven through the real menu items.</summary>
public class AlignmentCommandTests
{
    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-align-").FullName;

    static DesignerForm Start()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var project = RadProject.Create(TempDir(), "Align");
        var designer = new DesignerForm(project.ProjectFile, Path.Combine(TempDir(), "designer.json"))
        {
            Settings = { Editor = EditorKind.Custom, CustomEditorCommand = "dotnet --version" },
        };
        designer.SetBounds(0, 0, 1220, 780);
        designer.PerformLayout();
        return designer;
    }

    static MenuItem Format(DesignerForm d, params string[] path)
    {
        var items = ((MainMenu)d.FindControl("mnuMain")!).Items.Single(m => m.Caption == "Format").Items;
        for (int i = 0; i < path.Length - 1; i++) items = items.Single(m => m.Caption == path[i]).Items;
        return items.Single(m => m.Caption == path[^1]);
    }

    static Button MakeButton(DesignDocument doc, int x, int y, int w = 80, int h = 28)
    {
        var b = (Button)doc.Create(ComponentRegistry.Find("Button")!, doc.Form, x, y);
        b.SetBounds(x, y, w, h);
        return b;
    }

    [Fact]
    public void Align_lefts_centers_rights_move_to_the_primary_selections_edge()
    {
        var d = Start();
        var doc = d.Document!;
        var a = MakeButton(doc, 10, 10, 80, 28);
        var b = MakeButton(doc, 50, 60, 100, 28);   // last selected below = primary
        doc.SelectMany([a, b]);
        Assert.Same(b, doc.Primary);

        Format(d, "Align", "Lefts").PerformClick();
        Assert.Equal(b.Left, a.Left);

        a.SetBounds(10, 10, 80, 28);   // reset and try Centers
        Format(d, "Align", "Centers").PerformClick();
        Assert.Equal(b.Left + b.Width / 2, a.Left + a.Width / 2);

        a.SetBounds(10, 10, 80, 28);
        Format(d, "Align", "Rights").PerformClick();
        Assert.Equal(b.Left + b.Width, a.Left + a.Width);
    }

    [Fact]
    public void Align_tops_middles_bottoms_move_to_the_primary_selections_edge()
    {
        var d = Start();
        var doc = d.Document!;
        var a = MakeButton(doc, 10, 10, 80, 28);
        var b = MakeButton(doc, 200, 90, 80, 40);
        doc.SelectMany([a, b]);

        Format(d, "Align", "Tops").PerformClick();
        Assert.Equal(b.Top, a.Top);

        a.SetBounds(10, 10, 80, 28);
        Format(d, "Align", "Middles").PerformClick();
        Assert.Equal(b.Top + b.Height / 2, a.Top + a.Height / 2);

        a.SetBounds(10, 10, 80, 28);
        Format(d, "Align", "Bottoms").PerformClick();
        Assert.Equal(b.Top + b.Height, a.Top + a.Height);
    }

    [Fact]
    public void Align_does_nothing_with_fewer_than_two_free_standing_controls_selected()
    {
        var d = Start();
        var doc = d.Document!;
        var a = MakeButton(doc, 10, 10, 80, 28);
        doc.Select(a);
        var before = a.Bounds;

        Format(d, "Align", "Lefts").PerformClick();
        Assert.Equal(before, a.Bounds);
    }

    [Fact]
    public void Make_same_size_matches_the_primary_selections_width_and_or_height()
    {
        var d = Start();
        var doc = d.Document!;
        var a = MakeButton(doc, 10, 10, 60, 20);
        var b = MakeButton(doc, 200, 200, 120, 44);
        doc.SelectMany([a, b]);

        Format(d, "Make Same Size", "Width").PerformClick();
        Assert.Equal((120, 20), (a.Width, a.Height));

        a.SetBounds(10, 10, 60, 20);
        Format(d, "Make Same Size", "Height").PerformClick();
        Assert.Equal((60, 44), (a.Width, a.Height));

        a.SetBounds(10, 10, 60, 20);
        Format(d, "Make Same Size", "Both").PerformClick();
        Assert.Equal((120, 44), (a.Width, a.Height));
    }

    [Fact]
    public void Space_evenly_horizontally_redistributes_the_middle_controls_keeping_the_ends()
    {
        var d = Start();
        var doc = d.Document!;
        var a = MakeButton(doc, 0, 0, 20, 20);
        var b = MakeButton(doc, 30, 0, 20, 20);    // will move
        var c = MakeButton(doc, 200, 0, 20, 20);
        doc.SelectMany([a, b, c]);

        Format(d, "Space Evenly", "Horizontally").PerformClick();

        Assert.Equal(0, a.Left);      // the leftmost control stays put
        Assert.Equal(200, c.Left);    // the rightmost control stays put
        // Gap = (span - sum of widths) / (count - 1) = ((220 - 0) - 60) / 2 = 80; b sits one gap after a.
        Assert.Equal(a.Left + a.Width + 80, b.Left);
    }

    [Fact]
    public void Space_evenly_vertically_redistributes_the_middle_controls_keeping_the_ends()
    {
        var d = Start();
        var doc = d.Document!;
        var a = MakeButton(doc, 0, 0, 20, 20);
        var b = MakeButton(doc, 0, 30, 20, 20);
        var c = MakeButton(doc, 0, 200, 20, 20);
        doc.SelectMany([a, b, c]);

        Format(d, "Space Evenly", "Vertically").PerformClick();

        Assert.Equal(0, a.Top);
        Assert.Equal(200, c.Top);
        // Gap = ((200 + 20 - 0) - 60) / 2 = 80; b sits one gap after a.
        Assert.Equal(a.Top + a.Height + 80, b.Top);
    }

    [Fact]
    public void Space_evenly_does_nothing_with_fewer_than_three_controls()
    {
        var d = Start();
        var doc = d.Document!;
        var a = MakeButton(doc, 0, 0, 20, 20);
        var b = MakeButton(doc, 30, 0, 20, 20);
        doc.SelectMany([a, b]);
        var before = b.Bounds;

        Format(d, "Space Evenly", "Horizontally").PerformClick();
        Assert.Equal(before, b.Bounds);
    }

    [Fact]
    public void Center_in_form_centers_each_selected_control_in_its_own_parent()
    {
        var d = Start();
        var doc = d.Document!;
        var a = MakeButton(doc, 10, 10, 80, 28);
        doc.Select(a);

        Format(d, "Center in Form", "Horizontally").PerformClick();
        Assert.Equal((doc.Form.Width - a.Width) / 2, a.Left);

        Format(d, "Center in Form", "Vertically").PerformClick();
        Assert.Equal((doc.Form.Height - a.Height) / 2, a.Top);
    }

    [Fact]
    public void Align_ignores_docked_controls()
    {
        var d = Start();
        var doc = d.Document!;
        var top = (Panel)doc.Create(ComponentRegistry.Find("Panel")!, doc.Form, 0, 0);
        top.Align = Align.Top;
        doc.NotifyChanged();
        var free = MakeButton(doc, 10, 10, 80, 28);
        doc.SelectMany([top, free]);
        var before = top.Bounds;

        Format(d, "Align", "Lefts").PerformClick();   // only one free-standing control: nothing to align to
        Assert.Equal(before, top.Bounds);
    }

    [Fact]
    public void Context_menu_align_and_make_same_size_submenus_are_disabled_below_two_selected()
    {
        var project = RadProject.Create(TempDir(), "CtxAlign");
        var d = new DesignerForm(project.ProjectFile, Path.Combine(TempDir(), "designer.json"));
        d.Settings.Editor = EditorKind.Custom;
        d.Settings.CustomEditorCommand = "dotnet --version";
        d.SetBounds(0, 0, 1220, 780);
        d.PerformLayout();
        var doc = d.Document!;
        var surface = d.ActiveForm!.Surface;
        MakeButton(doc, 24, 24);
        doc.Select(null);

        var (sx, sy) = surface.DesignToScreen(400, 250);   // empty canvas
        d.Simulate.Click(surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy, MouseButton.Right);
        var popup = Assert.IsType<MenuPopup>(d.Popups[^1]);
        Assert.False(popup.Items.First(m => m.Caption == "Align").Enabled);
        Assert.False(popup.Items.First(m => m.Caption == "Make Same Size").Enabled);
    }
}
