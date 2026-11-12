using RadSharp.Design;
using RadSharp.Drawing;
using SkiaSharp;

namespace RadSharp.Tests;

/// <summary>The toolbox pictures: every placeable class has one, and each draws on light and dark themes at any size.</summary>
public class PaletteIconTests
{
    public static IEnumerable<object[]> Classes() => ComponentPalette.Classes().Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(Classes))]
    public void Every_palette_class_has_its_own_picture(string name) => Assert.True(PaletteIcons.HasArt(name), name);

    [Theory]
    [MemberData(nameof(Classes))]
    public void Each_picture_draws_inside_its_box_on_both_themes(string name)
    {
        foreach (var theme in new[] { Theme.Default, Theme.Dark })
            foreach (var size in new[] { 16, 24, 32 })
            {
                var (inside, outside) = Draw(name, theme, size);
                Assert.True(inside > size * size / 10, $"{name} at {size}px drew only {inside} pixels");
                Assert.Equal(0, outside);
            }
    }

    [Fact]
    public void A_class_without_a_picture_gets_an_initials_badge()
    {
        Assert.False(PaletteIcons.HasArt("FancyGauge"));
        Assert.Equal("FG", PaletteIcons.Initials("FancyGauge"));
        var (inside, outside) = Draw("FancyGauge", Theme.Default, 16);
        Assert.True(inside > 0);
        Assert.Equal(0, outside);
    }

    /// <summary>Paints the icon into the middle of a transparent bitmap; counts painted pixels inside and outside the box.</summary>
    static (int Inside, int Outside) Draw(string name, Theme theme, int size)
    {
        int margin = 8, total = size + 2 * margin;
        using var bmp = new SKBitmap(total, total);
        using (var sk = new SKCanvas(bmp))
        {
            sk.Clear(SKColors.Transparent);
            PaletteIcons.Paint(new Canvas(sk, theme, 1), name, new Rect(margin, margin, size, size));
        }
        int inside = 0, outside = 0;
        for (int y = 0; y < total; y++)
            for (int x = 0; x < total; x++)
            {
                if (bmp.GetPixel(x, y).Alpha == 0) continue;
                bool inBox = x >= margin && x < margin + size && y >= margin && y < margin + size;
                if (inBox) inside++; else outside++;
            }
        return (inside, outside);
    }
}
