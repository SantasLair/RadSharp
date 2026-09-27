using RadSharp.Drawing;

namespace RadSharp.Tests;

public class RenderingTests
{
    static Form LoadVector(string file, double scale = 1)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", file));
        var formJson = System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("form").GetRawText();
        return FormReader.Load(formJson, new FormLoadOptions { Scale = scale }).Form;
    }

    [Fact]
    public void Scale_multiplies_stored_lengths_before_layout()
    {
        var form = LoadVector("d1-align.json", 1.5);
        Assert.Equal(new Rect(0, 0, 1536, 60), form.FindControl("pnlToolbar")!.Bounds);
        Assert.Equal(new Rect(9, 36, 312, 843), form.FindControl("pnlInspectorBody")!.Bounds);
        Assert.Equal((1024, 700), (form.DesignWidth, form.DesignHeight));
    }

    [Fact]
    public void Changing_a_caption_dirties_only_that_control()
    {
        var form = LoadVector("d1-align.json");
        var label = Assert.IsType<Label>(form.FindControl("lblInspector"));
        form.TryTakeDirtyRect(out _);
        Assert.False(form.TryTakeDirtyRect(out _));

        label.Caption = "Properties";
        Assert.True(form.TryTakeDirtyRect(out var dirty));
        Assert.Equal(new Rect(804, 40, 220, 24), dirty);
    }

    [Fact]
    public void Layout_changes_dirty_the_whole_form()
    {
        var form = LoadVector("d1-align.json");
        form.TryTakeDirtyRect(out _);
        form.Resize(1100, 700);
        Assert.True(form.TryTakeDirtyRect(out var dirty));
        Assert.Equal(new Rect(0, 0, 1100, 700), dirty);
    }

    [Fact]
    public void Shaping_applies_kerning()
    {
        var font = new RenderFont(FontFace.Regular, 40);
        Assert.True(font.MeasureWidth("AV") < font.MeasureWidth("A") + font.MeasureWidth("V"));
        Assert.Equal(0, font.MeasureWidth(""));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("é")]
    [InlineData("Å")]
    public void Glyph_outlines_are_read_for_simple_and_composite_glyphs(string ch)
    {
        var face = FontFace.Regular;
        var glyph = face.Shape(ch).Glyphs[0];
        Assert.NotEqual(0, glyph);  // not .notdef
        var bounds = face.GetGlyphPath(glyph).Bounds;
        Assert.True(bounds.Width > 0 && bounds.Height > 0);
        Assert.True(bounds.Top < 0, "outlines are y-down with the baseline at 0");
    }

    [Theory]
    [InlineData("#FF8000", 255, 128, 0, 255)]
    [InlineData("#11223344", 0x11, 0x22, 0x33, 0x44)]
    public void Colours_parse_and_format(string hex, int r, int g, int b, int a)
    {
        var c = Color.FromHex(hex);
        Assert.Equal(new Color((byte)r, (byte)g, (byte)b, (byte)a), c);
        Assert.Equal(hex, c.ToHex());
    }
}
