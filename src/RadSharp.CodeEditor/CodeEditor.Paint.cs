using RadSharp.CodeEditing;
using RadSharp.Drawing;

namespace RadSharp;

public partial class CodeEditor
{
    // Decorations mapped to the current snapshot: (start, end, decoration), sorted by start.
    List<(int Start, int End, Decoration D)>? _decoCache;
    int _decoVersion = -1;

    /// <summary>Every decoration source's items, mapped forward to the current snapshot.</summary>
    internal List<(int Start, int End, Decoration D)> CurrentDecorations()
    {
        var snap = Snapshot;
        if (_decoCache != null && _decoVersion == snap.Version) return _decoCache;
        var list = new List<(int, int, Decoration)>();
        foreach (var source in _decorationSources)
        {
            DecorationSet? set;
            try { set = source.GetDecorations(snap); }
            catch (Exception) { set = null; }
            if (set == null || set.Snapshot.Buffer != snap.Buffer || set.Snapshot.Version > snap.Version) continue;
            int len = set.Snapshot.Length;
            foreach (var d in set.Items)
            {
                int s = Math.Clamp(d.Start, 0, len), e = Math.Clamp(d.End, s, len);
                if (set.Snapshot != snap)
                {
                    s = set.Snapshot.TranslateOffset(s, snap, PointTrackingMode.Positive);
                    int e2 = set.Snapshot.TranslateOffset(e, snap, PointTrackingMode.Negative);
                    if (e2 < s) { if (d.Kind != DecorationKind.GutterGlyph) continue; e2 = s; }
                    e = e2;
                }
                list.Add((s, e, d));
            }
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        _decoCache = list;
        _decoVersion = snap.Version;
        return list;
    }

    /// <summary>The decorations overlapping [from, to].</summary>
    IEnumerable<(int Start, int End, Decoration D)> DecorationsIn(int from, int to)
    {
        foreach (var d in CurrentDecorations())
        {
            if (d.Start > to) yield break;
            if (d.End >= from) yield return d;
        }
    }

    protected override void Paint(Canvas canvas)
    {
        EnsureFolds();
        ClampScroll();
        var colors = EffectiveColors;
        var font = TextFont;
        int lh = LineHeight;
        var snap = Snapshot;
        var full = new Rect(0, 0, Width, Height);
        canvas.FillRect(full, colors.Background);

        var area = TextArea;
        int x0 = area.X + TextPadding - _scrollX;
        int firstRow = _scrollY / Math.Max(1, lh);
        int lastRow = Math.Min(RowCount - 1, (_scrollY + area.Height) / Math.Max(1, lh));
        int primaryLine = snap.GetLineFromOffset(CaretOffset);
        bool noSelection = _sels.All(s => s.IsEmpty);
        bool focused = Focused;

        // Visible lines with their tops.
        var rows = new List<(int Line, int Y)>();
        for (int row = firstRow, line = LineOfRow(firstRow); row <= lastRow && line < LineCount; row++, line = NextVisibleLine(line))
            rows.Add((line, area.Y + row * lh - _scrollY));

        int visibleFrom = rows.Count > 0 ? snap.GetLineStart(rows[0].Line) : 0;
        int visibleTo = rows.Count > 0 ? snap.GetLineEndIncludingBreak(rows[^1].Line) : 0;
        var decorations = DecorationsIn(visibleFrom, visibleTo).ToList();
        var matches = FindMatchesIn(visibleFrom, visibleTo);
        var bracket = focused || !_sels[_primary].IsEmpty ? MatchingBracketPair() : null;

        canvas.Save();
        canvas.ClipRect(area);
        foreach (var (line, y) in rows)
        {
            int lineStart = snap.GetLineStart(line), lineEnd = snap.GetLineEnd(line);
            bool composingHere = IsComposing && line == primaryLine;
            int compStart = 0;
            var view = composingHere ? CompositionView(line, out compStart) : GetLineView(line);

            if (HighlightCurrentLine && line == primaryLine && noSelection)
                canvas.FillRect(new Rect(area.X, y, area.Width, lh), colors.CurrentLine);

            float XAt(int offset) => view.XOf(Math.Clamp(offset - lineStart, 0, view.Text.Length));
            void FillRange(int s, int e, Color c, bool pastEnd = false)
            {
                if (composingHere) return;
                int a = Math.Max(s, lineStart), b = Math.Min(e, lineEnd);
                if (b < a || (a == b && !pastEnd)) return;
                int xa = x0 + (int)MathF.Round(XAt(a)), xb = x0 + (int)MathF.Round(XAt(b));
                if (pastEnd) xb += Math.Max(Px(6), (int)(CharWidth * 0.6));
                canvas.FillRect(new Rect(xa, y, Math.Max(0, xb - xa), lh), c);
            }

            foreach (var (s, e, d) in decorations)
                if (d.Kind == DecorationKind.Highlight) FillRange(s, e, d.Color ?? colors.Highlight);
            foreach (var (s, len, current) in matches)
                FillRange(s, s + len, current ? colors.FindMatchCurrent : colors.FindMatch);
            if (bracket is { } br)
            {
                FillRange(br.A, br.A + 1, colors.BracketMatch);
                FillRange(br.B, br.B + 1, colors.BracketMatch);
            }
            foreach (var sel in _sels)
            {
                if (sel.IsEmpty || sel.End < lineStart || sel.Start > lineEnd) continue;
                bool pastEnd = sel.End > lineEnd && line < LineCount - 1;
                FillRange(sel.Start, sel.End, focused ? colors.Selection : colors.SelectionInactive, pastEnd);
            }

            // Text.
            float baseline = y + font.Ascent;
            if (composingHere)
                canvas.DrawText(view.Layout, colors.Text, x0, baseline);
            else
            {
                EnsureSegments(view, line, colors);
                foreach (var (sx, layout, cls) in view.Segments!)
                {
                    if (x0 + sx > area.X + area.Width) break;
                    if (x0 + sx + layout.Width < area.X) continue;
                    canvas.DrawText(layout, (cls != null ? colors.ColorOf(cls) : null) ?? colors.Text, x0 + sx, baseline);
                }
            }

            // A collapsed fold shows a marker after the header line's text.
            if (FoldStartingAt(line) is { Collapsed: true } fold)
            {
                string label = " … ";
                int bx = x0 + (int)MathF.Ceiling(view.Layout.Width) + Px(6);
                int bw = (int)Math.Ceiling(font.MeasureWidth(label));
                var box = new Rect(bx, y + Px(2), bw, lh - Px(4));
                canvas.FillRoundRect(box, Px(3), colors.Highlight);
                canvas.DrawText(label, font, colors.CollapsedText, bx, baseline);
                _ = fold;
            }

            // Squiggles.
            foreach (var (s, e, d) in decorations)
            {
                if (d.Kind is not (DecorationKind.ErrorSquiggle or DecorationKind.WarningSquiggle or DecorationKind.InfoSquiggle)) continue;
                int a = Math.Max(s, lineStart), b = Math.Min(e, lineEnd);
                if (b < a || composingHere) continue;
                int xa = x0 + (int)MathF.Round(XAt(a));
                int xb = x0 + (int)MathF.Round(XAt(b));
                if (xb - xa < Px(4)) { xb = xa + Px(4); }   // an empty range still shows a short squiggle
                var c = d.Color ?? d.Kind switch
                {
                    DecorationKind.ErrorSquiggle => colors.ErrorSquiggle,
                    DecorationKind.WarningSquiggle => colors.WarningSquiggle,
                    _ => colors.InfoSquiggle,
                };
                DrawSquiggle(canvas, xa, xb, y + lh - Px(3), c);
            }

            // The IME composition is underlined.
            if (composingHere && _composition.Length > 0)
            {
                float xa = view.Layout.CaretX(compStart), xb = view.Layout.CaretX(compStart + _composition.Length);
                canvas.FillRect(new Rect(x0 + (int)MathF.Round(xa), y + lh - Math.Max(1, Px(1)) - Px(1), (int)MathF.Round(xb - xa), Math.Max(1, Px(1))), colors.Text);
            }
        }

        // Carets.
        if (focused && _caretVisible)
        {
            for (int i = 0; i < _sels.Count; i++)
            {
                if (IsComposing && i == _primary) { canvas.FillRect(CaretRect, colors.Caret); continue; }
                var p = snap.GetPosition(_sels[i].Caret);
                if (IsLineHidden(p.Line)) continue;
                int y = area.Y + RowOfLine(p.Line) * lh - _scrollY;
                if (y + lh < area.Y || y > area.Y + area.Height) continue;
                int x = x0 + (int)MathF.Round(GetLineView(p.Line).XOf(p.Column));
                canvas.FillRect(new Rect(x, y, Math.Max(1, Px(2)), lh), i == _primary ? colors.Caret : colors.SecondaryCaret);
            }
        }
        canvas.Restore();

        PaintGutter(canvas, colors, rows, decorations, primaryLine);
        PaintScrollBars(canvas, colors);
        if (BorderStyle == BorderStyle.Single)
            canvas.DrawBorder(full, focused ? canvas.Theme.Accent : canvas.Theme.InputBorder, Border);
        PaintCompletion(canvas, colors);
        PaintHint(canvas, colors);
    }

    void DrawSquiggle(Canvas canvas, int x1, int x2, int y, Color color)
    {
        int step = Math.Max(2, Px(2));
        var pts = new List<PointF>();
        bool up = true;
        for (int x = x1; x <= x2; x += step, up = !up) pts.Add(new PointF(x, up ? y : y + step));
        if (pts.Count < 2) pts.Add(new PointF(x2, y + step));
        canvas.DrawPolyline(pts, color, Math.Max(1, Px(1)));
    }

    void PaintGutter(Canvas canvas, CodeEditorColors colors, List<(int Line, int Y)> rows,
        List<(int Start, int End, Decoration D)> decorations, int primaryLine)
    {
        int gw = GutterWidth;
        if (gw == 0) return;
        var g = GutterRect;
        var font = TextFont;
        int lh = LineHeight;
        canvas.Save();
        canvas.ClipRect(g);
        canvas.FillRect(g, colors.GutterBackground);
        canvas.FillRect(new Rect(g.X + gw - Math.Max(1, Px(1)), g.Y, Math.Max(1, Px(1)), g.Height), colors.GutterBorder);
        var snap = Snapshot;

        // Glyphs per line from decorations.
        var glyphs = new Dictionary<int, (GutterGlyph Glyph, Color? Color)>();
        foreach (var (s, _, d) in decorations)
        {
            if (d.Kind != DecorationKind.GutterGlyph || d.Glyph == GutterGlyph.None) continue;
            int line = snap.GetLineFromOffset(s);
            // Breakpoints and the current statement win over diagnostics on the same line.
            if (!glyphs.TryGetValue(line, out var existing) || Priority(d.Glyph) > Priority(existing.Glyph))
                glyphs[line] = (d.Glyph, d.Color);
        }

        int glyphX = g.X, numbersX = g.X + GlyphLaneWidth, foldX = numbersX + NumbersWidth;
        foreach (var (line, y) in rows)
        {
            if (_showLineNumbers)
            {
                var num = (line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                float w = font.MeasureWidth(num);
                canvas.DrawText(num, font, line == primaryLine ? colors.CurrentLineNumber : colors.LineNumber,
                    numbersX + NumbersWidth - Px(6) - w, y + font.Ascent);
            }
            if (_showGlyphMargin && glyphs.TryGetValue(line, out var gl))
                PaintGlyph(canvas, colors, gl.Glyph, gl.Color, new Rect(glyphX, y, GlyphLaneWidth, lh));
            if (FoldLaneWidth > 0 && FoldStartingAt(line) is { } fold)
                PaintFoldMarker(canvas, colors, fold.Collapsed, new Rect(foldX, y, FoldLaneWidth, lh));
        }
        canvas.Restore();
    }

    static int Priority(GutterGlyph g) => g switch
    {
        GutterGlyph.CurrentStatement => 6,
        GutterGlyph.Breakpoint => 5,
        GutterGlyph.BreakpointDisabled => 4,
        GutterGlyph.Error => 3,
        GutterGlyph.Warning => 2,
        GutterGlyph.Bookmark => 1,
        _ => 0,
    };

    void PaintGlyph(Canvas canvas, CodeEditorColors colors, GutterGlyph glyph, Color? color, Rect cell)
    {
        int d = Math.Min(cell.Width, cell.Height) - Px(6);
        var r = new Rect(cell.X + (cell.Width - d) / 2, cell.Y + (cell.Height - d) / 2, d, d);
        switch (glyph)
        {
            case GutterGlyph.Breakpoint:
                canvas.FillEllipse(r, color ?? colors.Breakpoint);
                break;
            case GutterGlyph.BreakpointDisabled:
                canvas.DrawEllipseBorder(r, color ?? colors.Breakpoint, Math.Max(1, Px(1)));
                break;
            case GutterGlyph.CurrentStatement:
            {
                var c = color ?? colors.CurrentStatement;
                canvas.FillPolygon([new PointF(r.X, r.Y + d * 0.25f), new PointF(r.X + d * 0.5f, r.Y + d * 0.25f), new PointF(r.X + d * 0.5f, r.Y),
                    new PointF(r.X + d, r.Y + d * 0.5f), new PointF(r.X + d * 0.5f, r.Y + d), new PointF(r.X + d * 0.5f, r.Y + d * 0.75f),
                    new PointF(r.X, r.Y + d * 0.75f)], c);
                break;
            }
            case GutterGlyph.Error:
            {
                canvas.FillEllipse(r, color ?? colors.ErrorSquiggle);
                float m = d * 0.3f, t = Math.Max(1, Px(1)) * 1.5f;
                canvas.DrawLine(new PointF(r.X + m, r.Y + m), new PointF(r.X + d - m, r.Y + d - m), colors.Background, t);
                canvas.DrawLine(new PointF(r.X + d - m, r.Y + m), new PointF(r.X + m, r.Y + d - m), colors.Background, t);
                break;
            }
            case GutterGlyph.Warning:
                canvas.FillPolygon([new PointF(r.X + d / 2f, r.Y), new PointF(r.X + d, r.Y + d), new PointF(r.X, r.Y + d)], color ?? colors.WarningSquiggle);
                break;
            case GutterGlyph.Info:
                canvas.FillEllipse(r, color ?? colors.InfoSquiggle);
                break;
            case GutterGlyph.Bookmark:
                canvas.FillRect(new Rect(r.X + d / 4, r.Y, d / 2, d), color ?? colors.Bookmark);
                break;
        }
    }

    void PaintFoldMarker(Canvas canvas, CodeEditorColors colors, bool collapsed, Rect cell)
    {
        float s = Math.Min(cell.Width, cell.Height) * 0.36f;
        float cx = cell.X + cell.Width / 2f, cy = cell.Y + cell.Height / 2f;
        if (collapsed)
            canvas.FillPolygon([new PointF(cx - s / 2, cy - s), new PointF(cx + s / 2, cy), new PointF(cx - s / 2, cy + s)], colors.FoldMarker);
        else
            canvas.FillPolygon([new PointF(cx - s, cy - s / 2), new PointF(cx + s, cy - s / 2), new PointF(cx, cy + s / 2)], colors.FoldMarker);
    }

    Rect VBarRect
    {
        get
        {
            var a = TextArea;
            return new Rect(Width - Border - BarSize, Border, BarSize, a.Height);
        }
    }

    Rect HBarRect
    {
        get
        {
            var a = TextArea;
            return new Rect(a.X, Height - Border - BarSize, a.Width, BarSize);
        }
    }

    void SyncScrollAxes()
    {
        var a = TextArea;
        _vAxis.Max = Math.Max(ContentHeight, a.Height);
        _vAxis.PageSize = a.Height;
        _vAxis.SetPosition(_scrollY);
        _hAxis.Max = Math.Max(ContentWidth, a.Width);
        _hAxis.PageSize = a.Width;
        _hAxis.SetPosition(_scrollX);
    }

    void PaintScrollBars(Canvas canvas, CodeEditorColors colors)
    {
        bool v = ShowVBar, h = ShowHBar;
        if (!v && !h) return;
        SyncScrollAxes();
        if (v) ScrollBarDriver.Paint(canvas, VBarRect, vertical: true, _vAxis, _barDrag == BarDrag.Vertical, Px(16));
        if (h) ScrollBarDriver.Paint(canvas, HBarRect, vertical: false, _hAxis, _barDrag == BarDrag.Horizontal, Px(16));
        if (v && h) canvas.FillRect(new Rect(Width - Border - BarSize, Height - Border - BarSize, BarSize, BarSize), canvas.Theme.ScrollTrack);
        // The gutter's strip under the horizontal bar.
        if (h && GutterWidth > 0) canvas.FillRect(new Rect(Border, Height - Border - BarSize, GutterWidth, BarSize), colors.GutterBackground);
    }
}
