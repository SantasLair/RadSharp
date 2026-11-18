using RadSharp.Drawing;
using SkiaSharp;

namespace RadSharp.Tests;

/// <summary>M14 items 3–4: golden images of ListView v2 and TreeView v2 at 100 % and 150 % (see <see cref="GoldenImageTests"/>).</summary>
public class ViewGoldenImageTests
{
    static readonly string[] Icons =
    [
        ViewImages.Square(new SKColor(220, 60, 50)),
        ViewImages.Square(new SKColor(40, 110, 220), round: true),
    ];

    /// <summary>
    /// The focused list view has check boxes, images, a right-aligned column with the sort arrow, two selected ranges and
    /// its focus cue; the other is scrolled both ways (its header follows sideways only) with an inactive selection.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void List_views_match_golden_image(double scale)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var form = new Form { Caption = "List views" };
        form.SetBounds(0, 0, 360, 200);
        T Put<T>(T c, int x, int y, int w, int h) where T : Control { c.SetBounds(x, y, w, h); form.Add(c); return c; }
        Put(new ImageList { Name = "icons", Images = Icons }, 0, 0, 0, 0);

        string[] tiles = ["Grass", "Water", "Sand", "Stone", "Lava", "Snow", "Ice", "Mud", "Clay", "Moss"];
        var tilesView = Put(new ListView { Checkboxes = true, ImagesName = "icons", MultiSelect = true, SortColumn = 1 }, 12, 12, 220, 176);
        tilesView.Columns.Add("Tile", 112);
        tilesView.Columns.Add("Cost", 58, TextAlign.Right);
        for (int i = 0; i < tiles.Length; i++)
            tilesView.Items.Add(new ListViewItem(tiles[i], (i * 7 % 40 + 1).ToString()) { ImageIndex = i % 3 == 2 ? -1 : i % 2, Checked = i is 1 or 4 });

        var files = Put(new ListView { Columns = ["Name:90", "Size:70", "Kind:80"] }, 240, 12, 108, 176);
        for (int i = 0; i < 12; i++) files.Items.Add(new ListViewItem($"map{i}.json", $"{i + 1} KB", "Map"));
        form.ApplyScale(scale);
        form.PerformLayout();

        form.Simulate.Key(Key.Tab);                  // keyboard focus, with focus cues, on the first list
        Assert.True(tilesView.Focused);
        tilesView.Selection.SelectRange(0, 1);
        tilesView.SetSelected(3, true);
        tilesView.Selection.Current = 5;
        files.ItemIndex = 4;
        files.ScrollX = files.Px(40);
        files.ScrollY = files.Px(30);

        using var actual = FormRenderer.RenderToBitmap(form);
        GoldenImageTests.CompareOrUpdate(actual, $"listviews@{scale * 100:0}%-{form.Width}x{form.Height}.png");
    }

    /// <summary>
    /// The focused tree has expanders (expanded, collapsed, lazy), check boxes, images and a selected child; the other is
    /// multi-select with an inactive selection and both scroll bars.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Tree_views_match_golden_image(double scale)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var form = new Form { Caption = "Tree views" };
        form.SetBounds(0, 0, 360, 200);
        T Put<T>(T c, int x, int y, int w, int h) where T : Control { c.SetBounds(x, y, w, h); form.Add(c); return c; }
        Put(new ImageList { Name = "icons", Images = Icons }, 0, 0, 0, 0);

        var assets = Put(new TreeView { Checkboxes = true, ImagesName = "icons" }, 12, 12, 168, 176);
        var maps = assets.Add("Maps");
        maps.ImageIndex = 1;
        maps.Add("level1.map").Checked = true;
        maps.Add("level2.map").ImageIndex = 0;
        var sprites = assets.Add("Sprites");
        sprites.ImageIndex = 1;
        sprites.Add("hero.png");
        assets.Add("Remote").HasChildren = true;
        assets.Add("Sounds").Checked = true;
        maps.Expanded = true;

        var outline = Put(new TreeView { MultiSelect = true }, 188, 12, 160, 176);
        for (int g = 0; g < 4; g++)
        {
            var group = outline.Add($"Group {g}");
            for (int c = 0; c < 3; c++) group.Add(c == 1 && g == 1 ? "A rather long item name" : $"Item {g}.{c}");
            group.Expanded = g < 3;
        }
        form.ApplyScale(scale);
        form.PerformLayout();

        form.Simulate.Key(Key.Tab);                  // keyboard focus, with focus cues, on the first tree
        Assert.True(assets.Focused);
        assets.Selected = maps.Nodes[1];
        outline.Selection.SelectRange(1, 2);
        outline.Selection.SetSelected(5, true);
        outline.ScrollY = outline.Px(36);
        outline.ScrollX = outline.Px(20);

        using var actual = FormRenderer.RenderToBitmap(form);
        GoldenImageTests.CompareOrUpdate(actual, $"treeviews@{scale * 100:0}%-{form.Width}x{form.Height}.png");
    }
}
