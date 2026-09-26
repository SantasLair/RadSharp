using RadSharp.Drawing;
using HB = HarfBuzzSharp;

namespace RadSharp.Tests;

/// <summary>
/// Shaping through FontFace must match HarfBuzz reading the font file itself (native memory only),
/// glyph for glyph and position for position.
/// </summary>
public class FontReferenceCheck
{
    [Theory]
    [InlineData("Centred: Wavy AVAWAY fi ffl")]
    [InlineData("Right aligned 0123456789")]
    [InlineData("Café, naïve, Ångström, Øre, Ĳssel")]
    [InlineData("Ready — 3 items")]
    public void Shaping_matches_harfbuzz_reading_the_file_natively(string text)
    {
        var path = FontFile();
        using var blob = HB.Blob.FromFile(path);
        using var face = new HB.Face(blob, 0);
        using var font = new HB.Font(face);
        font.SetFunctionsOpenType();
        font.SetScale(face.UnitsPerEm, face.UnitsPerEm);
        using var buffer = new HB.Buffer();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        font.Shape(buffer);
        var infos = buffer.GetGlyphInfoSpan().ToArray();
        var positions = buffer.GetGlyphPositionSpan().ToArray();

        var shaped = FontFace.Regular.Shape(text);
        Assert.Equal(infos.Select(i => (ushort)i.Codepoint), shaped.Glyphs);
        Assert.Equal(positions.Select(p => (float)p.XAdvance), shaped.Advances);
    }

    static string FontFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "src", "RadSharp.Core", "Fonts", "Inter-Regular.ttf");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("Inter-Regular.ttf");
    }
}
