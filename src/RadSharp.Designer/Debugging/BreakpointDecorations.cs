using RadSharp.CodeEditing;
using RadSharp.Drawing;
using RadSharp.Ide.Debug;

namespace RadSharp.Designer;

/// <summary>
/// The debugger's marks in one code editor: a gutter glyph per breakpoint of the file (enabled, disabled, or enabled
/// but dimmed when the adapter couldn't bind it), and while paused the current statement (glyph plus line highlight)
/// or the selected caller frame's line. It also keeps the file's breakpoints on their lines while the text is edited:
/// each edit maps every breakpoint's line start through the change (an inserted line above moves it down, joining its
/// line to the one above moves it up) and moves it in the <see cref="BreakpointStore"/>; a breakpoint that lands on a
/// line that already has one is removed.
/// </summary>
public sealed class BreakpointDecorations : IDecorationSource, IDisposable
{
    readonly CodeEditor _editor;
    readonly BreakpointStore _store;
    DecorationSet? _cache;
    bool _dirty = true;
    bool _moving;
    // The marked statement, anchored to the snapshot it was set on (the editor maps it through later edits).
    (TextSnapshot Snapshot, int Start, int End, bool Top)? _statement;

    public BreakpointDecorations(CodeDocument document, CodeEditor editor, BreakpointStore store)
    {
        Document = document;
        _editor = editor;
        _store = store;
        _editor.DecorationSources.Add(this);
        _editor.TextChanged += OnTextChanged;
    }

    public CodeDocument Document { get; }
    public CodeEditor Editor => _editor;
    public string File => Document.Path;

    public event EventHandler<SourceChangedEventArgs>? Changed;

    /// <summary>The 1-based line of the current statement (or selected frame) in the current text, or null.</summary>
    public int? StatementLine
    {
        get
        {
            if (_statement is not { } s) return null;
            var snap = _editor.Snapshot;
            int offset = s.Snapshot == snap ? s.Start : s.Snapshot.TranslateOffset(s.Start, snap, PointTrackingMode.Positive);
            return snap.GetLineFromOffset(offset) + 1;
        }
    }

    /// <summary>True when the marked statement is the top frame's (the current statement), false for a caller frame.</summary>
    public bool StatementIsTopFrame => _statement?.Top ?? false;

    /// <summary>Marks 1-based <paramref name="line"/> as the current statement (<paramref name="top"/>) or a caller frame's line; null clears it.</summary>
    public void SetStatement(int? line, bool top = true)
    {
        if (line is not { } l) { if (_statement == null) return; _statement = null; }
        else
        {
            var snap = _editor.Snapshot;
            int i = Math.Clamp(l - 1, 0, snap.LineCount - 1);
            var text = snap.GetLineText(i);
            int indent = text.Length - text.TrimStart().Length;
            int start = snap.GetLineStart(i);
            _statement = (snap, start + Math.Min(indent, text.Length), start + text.TrimEnd().Length, top);
        }
        Invalidate();
    }

    /// <summary>The store or the session state changed: rebuild on the next paint.</summary>
    public void Invalidate()
    {
        _dirty = true;
        Changed?.Invoke(this, new SourceChangedEventArgs());
        _editor.Invalidate();
    }

    public DecorationSet? GetDecorations(TextSnapshot current)
    {
        if (!_dirty && _cache != null && _cache.Snapshot == current) return _cache;
        var colors = _editor.EffectiveColors;
        var items = new List<Decoration>();
        foreach (var bp in _store.GetForFile(File))
        {
            if (bp.Line > current.LineCount) continue;
            int start = current.GetLineStart(bp.Line - 1);
            var glyph = bp.Enabled ? GutterGlyph.Breakpoint : GutterGlyph.BreakpointDisabled;
            // Unverified (the adapter could not bind it): the enabled glyph, dimmed.
            Color? color = bp.Enabled && bp.Verified == false ? colors.Breakpoint with { A = 96 } : null;
            var message = bp.Verified == false ? "Breakpoint not bound" + (bp.Message is { Length: > 0 } m ? ": " + m : "") : null;
            items.Add(new Decoration(start, 0, DecorationKind.GutterGlyph, message, color, glyph));
        }
        if (_statement is { } s)
        {
            int a = s.Snapshot == current ? s.Start : s.Snapshot.TranslateOffset(s.Start, current, PointTrackingMode.Positive);
            int b = s.Snapshot == current ? s.End : s.Snapshot.TranslateOffset(s.End, current, PointTrackingMode.Negative);
            b = Math.Max(a, b);
            var tint = s.Top ? colors.CurrentStatement with { A = 80 } : colors.CurrentStatement with { A = 40 };
            items.Add(new Decoration(a, b - a, DecorationKind.Highlight, null, tint));
            if (s.Top) items.Add(new Decoration(current.GetLineStart(current.GetLineFromOffset(a)), 0, DecorationKind.GutterGlyph, null, null, GutterGlyph.CurrentStatement));
        }
        items.Sort((x, y) => x.Start.CompareTo(y.Start));
        _cache = new DecorationSet(current, items);
        _dirty = false;
        return _cache;
    }

    /// <summary>Breakpoint lines follow the text (see the class remarks). A reload (SetText) keeps them where they were.</summary>
    void OnTextChanged(Control sender, TextChangedEventArgs e)
    {
        if (_moving) return;
        _dirty = true;
        if (e.Reason == TextChangeReason.SetText) return;
        var before = e.Before;
        var after = e.After;
        var moves = new List<(Guid Id, int Line)>();
        foreach (var bp in _store.GetForFile(File))
        {
            if (bp.Line > before.LineCount) continue;
            int offset = before.TranslateOffset(before.GetLineStart(bp.Line - 1), after, PointTrackingMode.Positive);
            int line = after.GetLineFromOffset(offset) + 1;
            if (line != bp.Line) moves.Add((bp.Id, line));
        }
        if (moves.Count == 0) return;
        _moving = true;
        try
        {
            // A move can wait for another breakpoint to leave its target line; repeat while any succeeds. What is left
            // lands on a line that keeps a breakpoint (its own line was deleted), so it goes.
            var pending = moves;
            bool progress = true;
            while (pending.Count > 0 && progress)
            {
                progress = false;
                for (int i = pending.Count - 1; i >= 0; i--)
                    if (_store.MoveTo(pending[i].Id, pending[i].Line)) { pending.RemoveAt(i); progress = true; }
            }
            foreach (var (id, _) in pending) _store.Remove(id);
        }
        finally { _moving = false; }
    }

    public void Dispose()
    {
        _editor.TextChanged -= OnTextChanged;
        _editor.DecorationSources.Remove(this);
    }
}
