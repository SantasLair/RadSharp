using System.Collections.Concurrent;
using SkiaSharp;
using HB = HarfBuzzSharp;

namespace RadSharp.Drawing;

/// <summary>A shaped run of text in font units: glyph ids, positions and source clusters.</summary>
public sealed class ShapedText
{
    internal ShapedText(ushort[] glyphs, float[] x, float[] y, float[] pen, float[] advances, int[] clusters, float advance)
    {
        Glyphs = glyphs; X = x; Y = y; Pen = pen; Advances = advances; Clusters = clusters; Advance = advance;
    }

    public ushort[] Glyphs { get; }
    /// <summary>Where each glyph is drawn (pen + offset), in font units.</summary>
    public float[] X { get; }
    /// <summary>Vertical offset of each glyph (y down), in font units.</summary>
    public float[] Y { get; }
    /// <summary>Pen position before each glyph (without offsets), in font units.</summary>
    public float[] Pen { get; }
    public float[] Advances { get; }
    /// <summary>UTF-16 index (within the shaped string) of the character each glyph came from.</summary>
    public int[] Clusters { get; }
    /// <summary>Total advance width, in font units.</summary>
    public float Advance { get; }
}

/// <summary>
/// One font: HarfBuzz shaping plus glyph drawing. <b>Bundled</b> faces (TrueType files shipped with the
/// app) are drawn from outlines read by our own code, so they render identically on every OS.
/// <b>System</b> faces (used only as fallback for characters the bundled faces lack, such as CJK or
/// emoji) are drawn by Skia through the OS font engine, including colour emoji.
/// </summary>
public sealed class FontFace
{
    readonly TrueTypeOutlines? _outlines;
    readonly SKTypeface? _typeface;
    readonly HB.Font _hbFont;
    readonly object _hbLock = new();
    readonly ConcurrentDictionary<ushort, SKPath> _paths = new();
    readonly ConcurrentDictionary<string, ShapedText> _shaped = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<int, bool> _coverage = new();

    public string Name { get; }
    public int UnitsPerEm { get; }
    public int Ascender { get; }
    public int Descender { get; }
    public int LineGap { get; }

    /// <summary>True for faces loaded from font files we ship (pixel-identical on every OS).</summary>
    public bool IsBundled => _outlines != null;

    /// <summary>Loads a bundled TrueType (glyf) font file.</summary>
    public FontFace(string name, byte[] fontFile)
    {
        Name = name;
        _outlines = new TrueTypeOutlines(fontFile);
        _hbFont = CreateHarfBuzzFont(fontFile, 0, _outlines.UnitsPerEm);
        UnitsPerEm = _outlines.UnitsPerEm;
        Ascender = _outlines.Ascender;
        Descender = _outlines.Descender;
        LineGap = _outlines.LineGap;
    }

    FontFace(SKTypeface typeface, byte[] fontFile, int index)
    {
        Name = typeface.FamilyName;
        _typeface = typeface;
        UnitsPerEm = typeface.UnitsPerEm > 0 ? typeface.UnitsPerEm : 1000;
        _hbFont = CreateHarfBuzzFont(fontFile, index, UnitsPerEm);
        if (_hbFont.TryGetHorizontalFontExtents(out var extents))
        {
            Ascender = extents.Ascender;
            Descender = extents.Descender;
            LineGap = extents.LineGap;
        }
    }

    static unsafe HB.Font CreateHarfBuzzFont(byte[] fontFile, int index, int unitsPerEm)
    {
        // Not HB.Blob.FromStream: it hands HarfBuzz a pointer into a managed array that is only pinned for
        // the duration of that call, so a later compacting GC moves or frees the font data under HarfBuzz
        // (an intermittent access violation in hb_shape_full, e.g. after the designer's Roslyn compile).
        // The data goes to native memory instead, freed by HarfBuzz when the last font using it is destroyed.
        var length = fontFile.Length;
        var data = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)length);
        fontFile.AsSpan().CopyTo(new Span<byte>(data, length));
        using var blob = new HB.Blob((nint)data, length, HB.MemoryMode.ReadOnly, () => System.Runtime.InteropServices.NativeMemory.Free(data));
        using var face = new HB.Face(blob, index);
        var font = new HB.Font(face);
        font.SetFunctionsOpenType();
        font.SetScale(unitsPerEm, unitsPerEm);
        return font;
    }

    public static FontFace Regular { get; } = LoadBundled("Inter-Regular");
    public static FontFace Bold { get; } = LoadBundled("Inter-Bold");
    public static FontFace Italic { get; } = LoadBundled("Inter-Italic");
    public static FontFace BoldItalic { get; } = LoadBundled("Inter-BoldItalic");

    static FontFace LoadBundled(string name)
    {
        using var s = typeof(FontFace).Assembly.GetManifestResourceStream($"RadSharp.Fonts.{name}.ttf")
            ?? throw new InvalidOperationException($"Bundled font {name} is missing.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return new FontFace(name, ms.ToArray());
    }

    // ---- Family registry (published Font.Family -> the four style faces) ----

    sealed record FontFamily(FontFace Regular, FontFace? Bold, FontFace? Italic, FontFace? BoldItalic);

    static readonly Dictionary<string, FontFamily> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Inter"] = new FontFamily(Regular, Bold, Italic, BoldItalic),
    };

    /// <summary>
    /// Registers a font family so controls can select it by name in their published <see cref="Font"/>.
    /// Only <paramref name="regular"/> is required; missing styles fall back within the family, then to
    /// <see cref="Regular"/> (Delphi-style: a style a face doesn't have is simply not applied).
    /// </summary>
    public static void Register(string family, byte[] regular, byte[]? bold = null, byte[]? italic = null, byte[]? boldItalic = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(family);
        ArgumentNullException.ThrowIfNull(regular);
        lock (Families)
            Families[family] = new FontFamily(
                new FontFace(family, regular),
                bold == null ? null : new FontFace(family + " Bold", bold),
                italic == null ? null : new FontFace(family + " Italic", italic),
                boldItalic == null ? null : new FontFace(family + " Bold Italic", boldItalic));
    }

    /// <summary>The face for a published <see cref="Font"/>, falling back within the family and then to <see cref="Regular"/>.</summary>
    public static FontFace Resolve(Font font) => Resolve(font.Family, font.Bold, font.Italic);

    /// <summary>The face for a published <see cref="Font"/>'s family and style, falling back within the family and then to <see cref="Regular"/>.</summary>
    public static FontFace Resolve(string family, bool bold, bool italic)
    {
        FontFamily? f;
        lock (Families)
            if (!Families.TryGetValue(family, out f)) return Regular;
        if (bold && italic) return f.BoldItalic ?? f.Bold ?? f.Italic ?? f.Regular;
        if (bold) return f.Bold ?? f.Regular;
        if (italic) return f.Italic ?? f.Regular;
        return f.Regular;
    }

    // ---- Fallback ----

    static readonly List<FontFace> BundledFallbacks = [];
    static readonly ConcurrentDictionary<int, FontFace?> SystemFallbackByCodepoint = new();
    static readonly ConcurrentDictionary<string, FontFace> SystemFaces = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds a bundled font tried (in order) for characters the primary face lacks, before system
    /// fonts. Ship e.g. a CJK font this way to keep those characters pixel-identical everywhere.
    /// </summary>
    public static void AddFallback(FontFace face)
    {
        lock (BundledFallbacks) BundledFallbacks.Add(face);
    }

    /// <summary>The face to draw <paramref name="codepoint"/> with: this one, a bundled fallback, or a system font.</summary>
    public FontFace ResolveFace(int codepoint)
    {
        if (HasGlyph(codepoint)) return this;
        lock (BundledFallbacks)
            foreach (var f in BundledFallbacks)
                if (f.HasGlyph(codepoint)) return f;
        return SystemFallback(codepoint) ?? this;
    }

    static FontFace? SystemFallback(int codepoint) => SystemFallbackByCodepoint.GetOrAdd(codepoint, cp =>
    {
        try
        {
            var tf = SKFontManager.Default.MatchCharacter(cp);
            if (tf == null) return null;
            var key = tf.FamilyName + "|" + tf.FontStyle.Weight + "|" + tf.FontStyle.Slant;
            return SystemFaces.GetOrAdd(key, _ =>
            {
                using var stream = tf.OpenStream(out int index);
                var bytes = new byte[stream.Length];
                stream.Read(bytes, bytes.Length);
                return new FontFace(tf, bytes, index);
            });
        }
        catch (Exception)
        {
            return null;  // no usable system font; the primary face draws .notdef
        }
    });

    public bool HasGlyph(int codepoint) => _coverage.GetOrAdd(codepoint, cp =>
    {
        lock (_hbLock) return _hbFont.TryGetGlyph((uint)cp, out var g) && g != 0;
    });

    // ---- Shaping and drawing ----

    /// <summary>The glyph outline in font units (y down, baseline at 0). Bundled faces only. Cached.</summary>
    public SKPath GetGlyphPath(ushort glyph) =>
        _outlines is { } o ? _paths.GetOrAdd(glyph, g => o.GetPath(g)) : throw new InvalidOperationException("System faces are drawn by Skia.");

    /// <summary>Shapes a run of text (kerning, ligatures) with HarfBuzz. Cached.</summary>
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

            int n = infos.Length;
            var glyphs = new ushort[n];
            var xs = new float[n];
            var ys = new float[n];
            var pens = new float[n];
            var advances = new float[n];
            var clusters = new int[n];
            float pen = 0;
            for (int i = 0; i < n; i++)
            {
                glyphs[i] = (ushort)infos[i].Codepoint;
                clusters[i] = (int)infos[i].Cluster;
                pens[i] = pen;
                xs[i] = pen + positions[i].XOffset;
                ys[i] = -positions[i].YOffset;
                advances[i] = positions[i].XAdvance;
                pen += positions[i].XAdvance;
            }
            result = new ShapedText(glyphs, xs, ys, pens, advances, clusters, pen);
        }
        _shaped[text] = result;
        return result;
    }

    /// <summary>Draws shaped glyphs with the pen at (<paramref name="x"/>, <paramref name="baseline"/>).</summary>
    internal void DrawShaped(SKCanvas canvas, ShapedText shaped, float size, float x, float baseline, SKPaint paint)
    {
        float s = size / UnitsPerEm;
        if (_outlines != null)
        {
            for (int i = 0; i < shaped.Glyphs.Length; i++)
            {
                var path = GetGlyphPath(shaped.Glyphs[i]);
                if (path.IsEmpty) continue;
                canvas.Save();
                canvas.Translate(x + shaped.X[i] * s, baseline + shaped.Y[i] * s);
                canvas.Scale(s);
                canvas.DrawPath(path, paint);
                canvas.Restore();
            }
            return;
        }

        using var font = new SKFont(_typeface, size) { Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.None, Subpixel = true };
        var points = new SKPoint[shaped.Glyphs.Length];
        for (int i = 0; i < points.Length; i++) points[i] = new SKPoint(x + shaped.X[i] * s, baseline + shaped.Y[i] * s);
        using var builder = new SKTextBlobBuilder();
        builder.AddPositionedRun(shaped.Glyphs, font, points);
        using var blob = builder.Build();
        if (blob != null) canvas.DrawText(blob, 0, 0, paint);
    }
}

/// <summary>
/// A face at a pixel size, ready to shape and draw. Used internally by <see cref="Canvas"/> and
/// <see cref="TextLayout"/>; the published, control-facing font descriptor is <see cref="Font"/>,
/// resolved to one of these via <see cref="Canvas.Resolve(Font)"/>.
/// </summary>
public sealed record RenderFont(FontFace Face, float Size)
{
    float Scale => Size / Face.UnitsPerEm;

    /// <summary>Distance from the top of a line to the baseline, in pixels.</summary>
    public float Ascent => Face.Ascender * Scale;
    /// <summary>Distance from the baseline to the bottom of a line (positive), in pixels.</summary>
    public float Descent => -Face.Descender * Scale;
    public float LineHeight => (Face.Ascender - Face.Descender + Face.LineGap) * Scale;

    public float MeasureWidth(string text) => text.Length == 0 ? 0 : TextLayout.Get(text, this).Width;

    public RenderFont WithSize(float size) => this with { Size = size };
}

/// <summary>
/// A control's published font (Delphi's <c>TFont</c>): family, size (at 100 % scale), and style. A
/// control's <see cref="Control.Font"/> is <c>Font?</c> — <c>null</c> means <i>ParentFont</i>: inherit
/// the parent's <see cref="Control.EffectiveFont"/>, or the theme's <see cref="Theme.DefaultFont"/> at
/// the root. Resolved to a drawable <see cref="RenderFont"/> via <see cref="Canvas.Resolve(Font)"/>.
/// </summary>
public readonly record struct Font(string Family, float Size, bool Bold = false, bool Italic = false, bool Underline = false);
