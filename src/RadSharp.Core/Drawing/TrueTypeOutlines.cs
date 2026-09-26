using System.Buffers.Binary;
using SkiaSharp;

namespace RadSharp.Drawing;

/// <summary>
/// Reads glyph outlines straight from a TrueType (<c>glyf</c>) font file, so text is shaped by our
/// code rather than an OS font engine and renders to the same pixels on every platform.
/// Paths are in font units with y pointing down (baseline at y = 0).
/// </summary>
internal sealed class TrueTypeOutlines
{
    readonly byte[] _data;
    readonly int _glyf, _loca;
    readonly bool _longLoca;
    readonly int _numGlyphs;

    public int UnitsPerEm { get; }
    public int Ascender { get; }
    public int Descender { get; }
    public int LineGap { get; }

    public TrueTypeOutlines(byte[] data)
    {
        _data = data;
        uint version = U32(0);
        if (version != 0x00010000 && version != 0x74727565 /* 'true' */)
            throw new NotSupportedException("Only TrueType-outline fonts (glyf) are supported.");

        int numTables = U16(4);
        int head = -1, hhea = -1, maxp = -1;
        _glyf = _loca = -1;
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            string tag = System.Text.Encoding.ASCII.GetString(data, rec, 4);
            int offset = (int)U32(rec + 8);
            switch (tag)
            {
                case "head": head = offset; break;
                case "hhea": hhea = offset; break;
                case "maxp": maxp = offset; break;
                case "glyf": _glyf = offset; break;
                case "loca": _loca = offset; break;
            }
        }
        if (head < 0 || hhea < 0 || maxp < 0 || _glyf < 0 || _loca < 0)
            throw new NotSupportedException("The font is missing a required table (head, hhea, maxp, glyf, loca).");

        UnitsPerEm = U16(head + 18);
        _longLoca = S16(head + 50) == 1;
        _numGlyphs = U16(maxp + 4);
        Ascender = S16(hhea + 4);
        Descender = S16(hhea + 6);
        LineGap = S16(hhea + 8);
    }

    public SKPath GetPath(int glyph)
    {
        var path = new SKPathBuilder();
        AppendGlyph(path, glyph, SKMatrix.Identity, depth: 0);
        return path.Detach();
    }

    void AppendGlyph(SKPathBuilder path, int glyph, SKMatrix transform, int depth)
    {
        if (glyph < 0 || glyph >= _numGlyphs || depth > 8) return;
        int start = LocaOffset(glyph), end = LocaOffset(glyph + 1);
        if (end <= start) return;  // empty glyph (e.g. space)
        int g = _glyf + start;
        int contours = S16(g);
        if (contours >= 0) AppendSimple(path, g, contours, transform);
        else AppendComposite(path, g, transform, depth);
    }

    int LocaOffset(int glyph) => _longLoca ? (int)U32(_loca + glyph * 4) : U16(_loca + glyph * 2) * 2;

    void AppendSimple(SKPathBuilder path, int g, int contours, SKMatrix transform)
    {
        if (contours == 0) return;
        var endPts = new int[contours];
        for (int i = 0; i < contours; i++) endPts[i] = U16(g + 10 + i * 2);
        int count = endPts[^1] + 1;
        int p = g + 10 + contours * 2;
        p += 2 + U16(p);  // skip instructions

        var flags = new byte[count];
        for (int i = 0; i < count;)
        {
            byte f = _data[p++];
            flags[i++] = f;
            if ((f & 8) != 0)
            {
                int repeat = _data[p++];
                while (repeat-- > 0 && i < count) flags[i++] = f;
            }
        }

        var xs = new int[count];
        var ys = new int[count];
        int v = 0;
        for (int i = 0; i < count; i++)
        {
            byte f = flags[i];
            if ((f & 2) != 0) { int d = _data[p++]; v += (f & 16) != 0 ? d : -d; }
            else if ((f & 16) == 0) { v += S16(p); p += 2; }
            xs[i] = v;
        }
        v = 0;
        for (int i = 0; i < count; i++)
        {
            byte f = flags[i];
            if ((f & 4) != 0) { int d = _data[p++]; v += (f & 32) != 0 ? d : -d; }
            else if ((f & 32) == 0) { v += S16(p); p += 2; }
            ys[i] = v;
        }

        int first = 0;
        foreach (int last in endPts)
        {
            AppendContour(path, xs, ys, flags, first, last, transform);
            first = last + 1;
        }
    }

    static void AppendContour(SKPathBuilder path, int[] xs, int[] ys, byte[] flags, int first, int last, SKMatrix m)
    {
        int n = last - first + 1;
        if (n < 2) return;

        SKPoint Pt(int i) => m.MapPoint(xs[first + i], -ys[first + i]);  // flip y: font y is up
        bool On(int i) => (flags[first + i] & 1) != 0;
        static SKPoint Mid(SKPoint a, SKPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

        // Start on an on-curve point; if there is none, on the midpoint of the first two off-curve points.
        int startIndex = -1;
        for (int i = 0; i < n; i++) if (On(i)) { startIndex = i; break; }
        SKPoint start = startIndex >= 0 ? Pt(startIndex) : Mid(Pt(0), Pt(1));
        if (startIndex < 0) startIndex = 0;

        path.MoveTo(start);
        SKPoint? control = null;
        for (int k = 1; k <= n; k++)
        {
            int i = (startIndex + k) % n;
            var pt = Pt(i);
            if (On(i))
            {
                if (control is { } c) path.QuadTo(c, pt);
                else path.LineTo(pt);
                control = null;
            }
            else
            {
                if (control is { } c) path.QuadTo(c, Mid(c, pt));
                control = pt;
            }
        }
        if (control is { } cEnd) path.QuadTo(cEnd, start);
        path.Close();
    }

    void AppendComposite(SKPathBuilder path, int g, SKMatrix transform, int depth)
    {
        int p = g + 10;
        const int ArgsAreWords = 0x1, ArgsAreXY = 0x2, HaveScale = 0x8, MoreComponents = 0x20,
            HaveXYScale = 0x40, HaveTwoByTwo = 0x80;
        int flags;
        do
        {
            flags = U16(p);
            int glyph = U16(p + 2);
            p += 4;
            int dx, dy;
            if ((flags & ArgsAreWords) != 0) { dx = S16(p); dy = S16(p + 2); p += 4; }
            else { dx = (sbyte)_data[p]; dy = (sbyte)_data[p + 1]; p += 2; }
            if ((flags & ArgsAreXY) == 0) { dx = 0; dy = 0; }  // point matching: not supported

            float a = 1, b = 0, c = 0, d = 1;
            if ((flags & HaveScale) != 0) { a = d = F2Dot14(p); p += 2; }
            else if ((flags & HaveXYScale) != 0) { a = F2Dot14(p); d = F2Dot14(p + 2); p += 4; }
            else if ((flags & HaveTwoByTwo) != 0) { a = F2Dot14(p); b = F2Dot14(p + 2); c = F2Dot14(p + 4); d = F2Dot14(p + 6); p += 8; }

            // Component transform in font space (y up), expressed in our y-down space.
            var component = new SKMatrix(a, -c, dx, -b, d, -dy, 0, 0, 1);
            AppendGlyph(path, glyph, transform.PreConcat(component), depth + 1);
        } while ((flags & MoreComponents) != 0);
    }

    float F2Dot14(int p) => S16(p) / 16384f;
    int U16(int p) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(p));
    int S16(int p) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(p));
    uint U32(int p) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(p));
}
