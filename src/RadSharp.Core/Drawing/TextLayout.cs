using System.Globalization;
using System.Text;
using SkiaSharp;

namespace RadSharp.Drawing;

/// <summary>
/// One line of text laid out in a font: split into runs by font fallback, shaped with HarfBuzz, with
/// a caret position for every grapheme boundary (so the caret never lands inside an emoji or a
/// combining sequence). Left-to-right only for now.
/// </summary>
public sealed class TextLayout
{
    readonly Run[] _runs;
    readonly float[] _caret;      // x (pixels) for every UTF-16 index 0..Length
    readonly int[] _boundaries;   // grapheme boundaries, ascending, including 0 and Length

    sealed record Run(FontFace Face, ShapedText Shaped, float X);

    TextLayout(string text, RenderFont font)
    {
        Text = text;
        Font = font;
        _boundaries = GraphemeBoundaries(text);
        var runs = new List<Run>();
        _caret = new float[text.Length + 1];
        float pen = 0;

        foreach (var (start, end, face) in FallbackRuns(text, font.Face, _boundaries))
        {
            var sub = text.Substring(start, end - start);
            var shaped = face.Shape(sub);
            float s = font.Size / face.UnitsPerEm;
            runs.Add(new Run(face, shaped, pen));
            AssignCaretStops(shaped, start, end, pen, s);
            pen += shaped.Advance * s;
        }
        _caret[text.Length] = pen;
        _runs = [.. runs];
        Width = pen;
    }

    public string Text { get; }
    public RenderFont Font { get; }
    /// <summary>Advance width of the whole line, in pixels.</summary>
    public float Width { get; }
    public IReadOnlyList<int> Boundaries => _boundaries;

    // A small cache: the same captions and edit texts are laid out on every paint.
    static readonly Dictionary<(string, RenderFont), TextLayout> Cache = [];

    public static TextLayout Get(string text, RenderFont font)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((text, font), out var layout)) return layout;
            if (Cache.Count > 2048) Cache.Clear();
            layout = new TextLayout(text, font);
            Cache[(text, font)] = layout;
            return layout;
        }
    }

    /// <summary>The caret x (pixels from the line start) before UTF-16 index <paramref name="index"/>.</summary>
    public float CaretX(int index) => _caret[Math.Clamp(index, 0, Text.Length)];

    /// <summary>The grapheme boundary nearest to <paramref name="x"/> (pixels from the line start).</summary>
    public int HitTest(float x)
    {
        int best = 0;
        float bestDistance = float.MaxValue;
        foreach (int b in _boundaries)
        {
            float d = Math.Abs(_caret[b] - x);
            if (d < bestDistance) { bestDistance = d; best = b; }
        }
        return best;
    }

    /// <summary>Draws the line with its pen origin at (<paramref name="x"/>, <paramref name="baseline"/>).</summary>
    public void Draw(SKCanvas canvas, float x, float baseline, SKPaint paint)
    {
        foreach (var run in _runs)
            run.Face.DrawShaped(canvas, run.Shaped, Font.Size, x + run.X, baseline, paint);
    }

    // ---- Fitting text to a width ----

    /// <summary>The character <see cref="Ellipsize"/> appends.</summary>
    public const string EllipsisText = "\u2026";

    /// <summary>
    /// Shortens a single line to fit <paramref name="maxWidth"/> pixels, cutting at a grapheme boundary and
    /// appending an ellipsis (<c>"Quarterly rep…"</c>). Returns the text unchanged if it already fits, just the
    /// ellipsis if only that fits, and an empty string if not even that does.
    /// </summary>
    public static string Ellipsize(string text, RenderFont font, float maxWidth)
    {
        if (text.Length == 0 || font.MeasureWidth(text) <= maxWidth) return text;
        if (font.MeasureWidth(EllipsisText) > maxWidth) return "";
        var boundaries = GraphemeBoundaries(text);
        // Largest number of leading graphemes whose text plus the ellipsis still fits (0 = just the ellipsis).
        int lo = 0, hi = boundaries.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (font.MeasureWidth(text[..boundaries[mid]].TrimEnd() + EllipsisText) <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return text[..boundaries[lo]].TrimEnd() + EllipsisText;
    }

    /// <summary>
    /// Breaks text into lines no wider than <paramref name="maxWidth"/> pixels: explicit newlines always break,
    /// otherwise at spaces (runs of spaces collapse to one), and a single word wider than the width is split
    /// between graphemes. A <paramref name="maxWidth"/> of 0 or less only splits at newlines.
    /// </summary>
    public static IReadOnlyList<string> WrapLines(string text, RenderFont font, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (maxWidth <= 0 || font.MeasureWidth(paragraph) <= maxWidth) { lines.Add(paragraph); continue; }
            var line = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (font.MeasureWidth(candidate) <= maxWidth) { line = candidate; continue; }
                if (line.Length > 0) lines.Add(line);
                var rest = word;
                while (font.MeasureWidth(rest) > maxWidth)
                {
                    int cut = FitGraphemes(rest, font, maxWidth);
                    lines.Add(rest[..cut]);
                    rest = rest[cut..];
                }
                line = rest;
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>The length of the longest prefix (whole graphemes, at least one) that fits <paramref name="maxWidth"/>.</summary>
    static int FitGraphemes(string text, RenderFont font, float maxWidth)
    {
        var layout = Get(text, font);
        int best = layout.Boundaries[1];
        foreach (int b in layout.Boundaries.Skip(2))
        {
            if (layout.CaretX(b) > maxWidth) break;
            best = b;
        }
        return best;
    }

    // ---- Construction helpers ----

    public static int[] GraphemeBoundaries(string text)
    {
        var list = new List<int> { 0 };
        int i = 0;
        while (i < text.Length)
        {
            i += Math.Max(1, StringInfo.GetNextTextElementLength(text, i));
            list.Add(Math.Min(i, text.Length));
        }
        return [.. list];
    }

    /// <summary>Splits text into runs of graphemes drawn by the same face.</summary>
    static IEnumerable<(int Start, int End, FontFace Face)> FallbackRuns(string text, FontFace primary, int[] boundaries)
    {
        int runStart = 0;
        FontFace? runFace = null;
        for (int g = 0; g + 1 < boundaries.Length; g++)
        {
            int start = boundaries[g], end = boundaries[g + 1];
            var face = FaceFor(text, start, end, primary);
            if (runFace != null && face != runFace)
            {
                yield return (runStart, start, runFace);
                runStart = start;
            }
            runFace = face;
        }
        if (runFace != null) yield return (runStart, text.Length, runFace);
    }

    /// <summary>The face for one grapheme: decided by its first visible code point.</summary>
    static FontFace FaceFor(string text, int start, int end, FontFace primary)
    {
        foreach (var rune in text.AsSpan(start, end - start).EnumerateRunes())
        {
            if (IsInvisibleJoiner(rune.Value)) continue;
            var face = primary.ResolveFace(rune.Value);
            return face;
        }
        return primary;
    }

    static bool IsInvisibleJoiner(int cp) =>
        cp is 0x200D or 0xFE0E or 0xFE0F or 0x200C || Rune.GetUnicodeCategory(new Rune(cp)) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;

    /// <summary>
    /// Sets caret x for every index in [start, end): each cluster starts at its first glyph's pen
    /// position; graphemes inside a multi-character cluster (a ligature) share its advance evenly.
    /// </summary>
    void AssignCaretStops(ShapedText shaped, int start, int end, float runX, float s)
    {
        int n = shaped.Glyphs.Length;
        // Cluster start positions and advances (LTR: clusters ascend with glyph index).
        var clusterX = new SortedDictionary<int, (float X, float Advance)>();
        for (int i = 0; i < n; i++)
        {
            int c = shaped.Clusters[i];
            if (clusterX.TryGetValue(c, out var v)) clusterX[c] = (v.X, v.Advance + shaped.Advances[i]);
            else clusterX[c] = (shaped.Pen[i], shaped.Advances[i]);
        }
        var keys = clusterX.Keys.ToList();
        int length = end - start;
        for (int k = 0; k < keys.Count; k++)
        {
            int cStart = keys[k];
            int cEnd = k + 1 < keys.Count ? keys[k + 1] : length;
            var (x, advance) = clusterX[cStart];
            // Grapheme boundaries inside this cluster.
            var inner = _boundaries.Where(b => b > start + cStart && b < start + cEnd).ToList();
            int parts = inner.Count + 1;
            int prev = start + cStart;
            for (int p = 0; p < parts; p++)
            {
                int boundary = p < inner.Count ? inner[p] : start + cEnd;
                float bx = runX + (x + advance * p / parts) * s;
                for (int i = prev; i < boundary; i++) _caret[i] = bx;
                prev = boundary;
            }
        }
        if (keys.Count == 0) for (int i = start; i < end; i++) _caret[i] = runX;
    }
}
