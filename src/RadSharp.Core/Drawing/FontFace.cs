using System.Collections.Concurrent;
using SkiaSharp;
using HB = HarfBuzzSharp;

namespace RadSharp.Drawing;

/// <summary>A shaped line of text in font units: glyph ids and pen positions.</summary>
public sealed class ShapedText
{
    internal ShapedText(ushort[] glyphs, float[] x, float[] y, int[] clusters, float advance)
    {
        Glyphs = glyphs; X = x; Y = y; Clusters = clusters; Advance = advance;
    }

    public ushort[] Glyphs { get; }
    /// <summary>Pen x of each glyph, in font units.</summary>
    public float[] X { get; }
    /// <summary>Vertical offset of each glyph (y down), in font units.</summary>
    public float[] Y { get; }
    /// <summary>UTF-16 index of the character each glyph came from.</summary>
    public int[] Clusters { get; }
    /// <summary>Total advance width, in font units.</summary>
    public float Advance { get; }
}

/// <summary>
/// One font file: HarfBuzz shaping plus our own outline reader. Bundled faces render identically on
/// every OS because no platform font engine is involved.
/// </summary>
public sealed class FontFace
{
    readonly TrueTypeOutlines _outlines;
    readonly HB.Font _hbFont;
    readonly object _hbLock = new();
    readonly ConcurrentDictionary<ushort, SKPath> _paths = new();
    readonly ConcurrentDictionary<string, ShapedText> _shaped = new(StringComparer.Ordinal);

    public string Name { get; }
    public int UnitsPerEm => _outlines.UnitsPerEm;
    public int Ascender => _outlines.Ascender;
    public int Descender => _outlines.Descender;
    public int LineGap => _outlines.LineGap;

    public FontFace(string name, byte[] fontFile)
    {
        Name = name;
        _outlines = new TrueTypeOutlines(fontFile);
        using var blob = HB.Blob.FromStream(new MemoryStream(fontFile));
        using var face = new HB.Face(blob, 0);
        _hbFont = new HB.Font(face);
        _hbFont.SetFunctionsOpenType();
        _hbFont.SetScale(UnitsPerEm, UnitsPerEm);
    }

    public static FontFace Regular { get; } = LoadBundled("Inter-Regular");
    public static FontFace Bold { get; } = LoadBundled("Inter-Bold");

    static FontFace LoadBundled(string name)
    {
        using var s = typeof(FontFace).Assembly.GetManifestResourceStream($"RadSharp.Fonts.{name}.ttf")
            ?? throw new InvalidOperationException($"Bundled font {name} is missing.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return new FontFace(name, ms.ToArray());
    }

    /// <summary>The glyph outline in font units (y down, baseline at 0). Cached.</summary>
    public SKPath GetGlyphPath(ushort glyph) => _paths.GetOrAdd(glyph, g => _outlines.GetPath(g));

    /// <summary>Shapes a single line of text (kerning, ligatures) with HarfBuzz. Cached.</summary>
    public ShapedText Shape(string text)
    {
        if (_shaped.TryGetValue(text, out var cached)) return cached;
        if (_shaped.Count > 4096) _shaped.Clear();

        ShapedText result;
        lock (_hbLock)
        {
            using var buffer = new HB.Buffer();
            buffer.AddUtf16(text);
            buffer.GuessSegmentProperties();
            _hbFont.Shape(buffer);
            var infos = buffer.GetGlyphInfoSpan();
            var positions = buffer.GetGlyphPositionSpan();

            var glyphs = new ushort[infos.Length];
            var xs = new float[infos.Length];
            var ys = new float[infos.Length];
            var clusters = new int[infos.Length];
            float pen = 0;
            for (int i = 0; i < infos.Length; i++)
            {
                glyphs[i] = (ushort)infos[i].Codepoint;
                clusters[i] = (int)infos[i].Cluster;
                xs[i] = pen + positions[i].XOffset;
                ys[i] = -positions[i].YOffset;
                pen += positions[i].XAdvance;
            }
            result = new ShapedText(glyphs, xs, ys, clusters, pen);
        }
        _shaped[text] = result;
        return result;
    }
}

/// <summary>A face at a pixel size.</summary>
public sealed record Font(FontFace Face, float Size)
{
    float Scale => Size / Face.UnitsPerEm;

    /// <summary>Distance from the top of a line to the baseline, in pixels.</summary>
    public float Ascent => Face.Ascender * Scale;
    /// <summary>Distance from the baseline to the bottom of a line (positive), in pixels.</summary>
    public float Descent => -Face.Descender * Scale;
    public float LineHeight => (Face.Ascender - Face.Descender + Face.LineGap) * Scale;

    public float MeasureWidth(string text) => text.Length == 0 ? 0 : Face.Shape(text).Advance * Scale;

    public Font WithSize(float size) => this with { Size = size };
}
