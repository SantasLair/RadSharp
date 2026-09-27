using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>Control.Font / EffectiveFont (Delphi's ParentFont) and the FontFace family registry.</summary>
public class FontInheritanceTests
{
    [Fact]
    public void A_control_with_no_font_and_no_parent_uses_the_theme_default()
    {
        var c = new Panel();
        Assert.Equal(Theme.Default.DefaultFont, c.EffectiveFont);
    }

    [Fact]
    public void A_child_with_no_font_inherits_its_parents_font()
    {
        var parent = new Panel { Font = new Font("Georgia", 18, Bold: true) };
        var child = new Label();
        parent.Add(child);

        Assert.Equal(parent.Font, child.EffectiveFont);
    }

    [Fact]
    public void A_childs_own_font_overrides_its_parents()
    {
        var parent = new Panel { Font = new Font("Georgia", 18) };
        var child = new Label { Font = new Font("Consolas", 12, Italic: true) };
        parent.Add(child);

        Assert.Equal(child.Font, child.EffectiveFont);
    }

    [Fact]
    public void Inheritance_walks_through_several_levels()
    {
        var grandparent = new Panel { Font = new Font("Georgia", 18) };
        var parent = new Panel();
        var child = new Label();
        grandparent.Add(parent);
        parent.Add(child);

        Assert.Equal(grandparent.Font, child.EffectiveFont);
    }

    [Fact]
    public void Setting_font_to_null_reverts_to_inheriting()
    {
        var c = new Label { Font = new Font("Georgia", 18) };
        Assert.Equal(new Font("Georgia", 18), c.EffectiveFont);

        c.Font = null;
        Assert.Equal(Theme.Default.DefaultFont, c.EffectiveFont);
    }

    [Fact]
    public void FontFace_resolve_falls_back_within_a_family_then_to_regular()
    {
        var interLike = File.ReadAllBytes(FindBundledFont("Inter-Regular.ttf"));
        FontFace.Register("RegularOnlyFamily", interLike);

        Assert.Same(FontFace.Resolve("RegularOnlyFamily", bold: false, italic: false),
                    FontFace.Resolve("RegularOnlyFamily", bold: true, italic: true));  // no bold/italic -> falls back to regular

        // An unregistered family falls back to the theme's default face rather than throwing.
        Assert.Same(FontFace.Regular, FontFace.Resolve("NoSuchFamily", bold: false, italic: false));
    }

    [Fact]
    public void FontFace_resolve_prefers_the_closest_available_style()
    {
        var regular = File.ReadAllBytes(FindBundledFont("Inter-Regular.ttf"));
        var bold = File.ReadAllBytes(FindBundledFont("Inter-Bold.ttf"));
        FontFace.Register("BoldOnlyFamily", regular, bold: bold);

        // Bold+Italic requested but only Bold is registered: falls back to Bold, not all the way to Regular.
        var resolved = FontFace.Resolve("BoldOnlyFamily", bold: true, italic: true);
        Assert.Equal("BoldOnlyFamily Bold", resolved.Name);
    }

    [Fact]
    public void Canvas_resolve_scales_size_by_the_current_ui_scale()
    {
        using var bitmap = new SkiaSharp.SKBitmap(10, 10);
        using var sk = new SkiaSharp.SKCanvas(bitmap);
        var canvas = new Canvas(sk, Theme.Default, 1.5);

        var render = canvas.Resolve(new Font("Inter", 20));
        Assert.Equal(30, render.Size);  // 20 * 1.5
        Assert.Same(FontFace.Regular, render.Face);
    }

    [Fact]
    public void Label_font_size_and_bold_render_identically_to_an_equivalent_explicit_font()
    {
        // FontSize/FontBold are sugar: for a Label with no Font of its own (the theme's "Inter" family,
        // size 13), setting them should paint exactly like setting the equivalent Font outright.
        var legacy = new Label { Caption = "Sugar", FontSize = 22, FontBold = true };
        Assert.Null(legacy.Font);  // the legacy properties don't materialize Font on their own

        var equivalent = new Label { Caption = "Sugar", Font = new Font("Inter", 22, Bold: true) };

        using var a = RenderLabel(legacy);
        using var b = RenderLabel(equivalent);
        Assert.Equal(a.Bytes, b.Bytes);
    }

    static SkiaSharp.SKBitmap RenderLabel(Label label)
    {
        label.SetBounds(0, 0, 200, 40);
        var form = new Form();
        form.SetBounds(0, 0, 200, 40);
        form.Add(label);
        return FormRenderer.RenderToBitmap(form);
    }

    [Fact]
    public void An_explicit_font_on_label_wins_over_the_legacy_sugar_properties()
    {
        // Old files never set both; this documents the precedence when a form (or code) does.
        var label = new Label { FontSize = 22, FontBold = true, Font = new Font("Consolas", 10) };
        Assert.Equal(new Font("Consolas", 10), label.Font);
    }

    static string FindBundledFont(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "RadSharp.Core", "Fonts", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(name);
    }
}
