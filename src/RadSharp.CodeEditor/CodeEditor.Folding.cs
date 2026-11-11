using RadSharp.CodeEditing;

namespace RadSharp;

public partial class CodeEditor
{
    /// <summary>A fold, tracked by the offsets of its first and last lines' starts so it moves with edits.</summary>
    sealed class Fold
    {
        public int Start;       // offset of the start line's first character
        public int End;         // offset of the end line's first character
        public bool Collapsed;
        public string? CollapsedText;
    }

    readonly List<Fold> _folds = [];
    int _foldsVersion = -1;
    bool _foldsStale = true;

    // Hidden line runs (first, last), sorted and merged, with the count of hidden lines before each run.
    readonly List<(int First, int Last)> _hidden = [];
    int _hiddenTotal;

    /// <summary>Number of display rows: lines minus the ones inside collapsed folds.</summary>
    int RowCount => Math.Max(1, LineCount - _hiddenTotal);

    int RowOfLine(int line)
    {
        int hiddenBefore = 0;
        foreach (var (first, last) in _hidden)
        {
            if (line < first) break;
            if (line <= last) return first - 1 - hiddenBefore;    // inside a fold: the fold's header row
            hiddenBefore += last - first + 1;
        }
        return line - hiddenBefore;
    }

    int LineOfRow(int row)
    {
        int line = row;
        foreach (var (first, last) in _hidden)
        {
            if (line < first) break;
            line += last - first + 1;
        }
        return Math.Clamp(line, 0, LineCount - 1);
    }

    bool IsLineHidden(int line)
    {
        foreach (var (first, last) in _hidden)
        {
            if (line < first) return false;
            if (line <= last) return true;
        }
        return false;
    }

    /// <summary>The next visible line after <paramref name="line"/> (skipping folds), or LineCount.</summary>
    int NextVisibleLine(int line)
    {
        int next = line + 1;
        foreach (var (first, last) in _hidden)
            if (next >= first && next <= last) next = last + 1;
        return next;
    }

    int PreviousVisibleLine(int line)
    {
        int prev = line - 1;
        for (int i = _hidden.Count - 1; i >= 0; i--)
            if (prev >= _hidden[i].First && prev <= _hidden[i].Last) prev = _hidden[i].First - 1;
        return prev;
    }

    void RebuildHidden()
    {
        _hidden.Clear();
        _hiddenTotal = 0;
        var snap = Snapshot;
        var runs = new List<(int, int)>();
        foreach (var f in _folds)
        {
            if (!f.Collapsed) continue;
            int s = snap.GetLineFromOffset(f.Start), e = snap.GetLineFromOffset(f.End);
            if (e > s) runs.Add((s + 1, e));
        }
        runs.Sort();
        foreach (var (first, last) in runs)
        {
            if (_hidden.Count > 0 && first <= _hidden[^1].Last + 1)
            {
                var prev = _hidden[^1];
                _hidden[^1] = (prev.First, Math.Max(prev.Last, last));
            }
            else _hidden.Add((first, last));
        }
        foreach (var (first, last) in _hidden) _hiddenTotal += last - first + 1;
        ClampScroll();
        Invalidate();
    }

    void TranslateFolds(TextChangedEventArgs e)
    {
        if (_folds.Count == 0) return;
        var snap = e.After;
        for (int i = _folds.Count - 1; i >= 0; i--)
        {
            var f = _folds[i];
            f.Start = snap.GetLineStart(snap.GetLineFromOffset(TextSnapshot.TranslateThrough(e.Changes, f.Start, PointTrackingMode.Negative)));
            f.End = snap.GetLineStart(snap.GetLineFromOffset(TextSnapshot.TranslateThrough(e.Changes, f.End, PointTrackingMode.Negative)));
            if (f.End <= f.Start) _folds.RemoveAt(i);
        }
        if (_hidden.Count > 0 || _folds.Any(f => f.Collapsed)) RebuildHidden();
    }

    /// <summary>Asks the folding source again (after typing pauses), keeping collapsed folds collapsed.</summary>
    void RefreshFolds()
    {
        _foldsStale = false;
        if (_foldingSource == null || !_showFolding) return;
        var snap = Snapshot;
        IReadOnlyList<FoldRange>? ranges;
        try { ranges = _foldingSource.GetFoldRanges(snap); }
        catch (Exception) { ranges = null; }
        if (ranges == null) return;
        var collapsed = new HashSet<int>(_folds.Where(f => f.Collapsed).Select(f => f.Start));
        _folds.Clear();
        foreach (var r in ranges)
        {
            if (r.EndLine <= r.StartLine || r.StartLine < 0 || r.EndLine >= snap.LineCount) continue;
            int start = snap.GetLineStart(r.StartLine);
            _folds.Add(new Fold { Start = start, End = snap.GetLineStart(r.EndLine), Collapsed = collapsed.Contains(start), CollapsedText = r.CollapsedText });
        }
        _foldsVersion = snap.Version;
        RebuildHidden();
    }

    /// <summary>Makes sure the fold list is current (ranges are fetched lazily, the first time they're needed).</summary>
    void EnsureFolds()
    {
        if (_foldsVersion < 0 && _foldingSource != null && _showFolding) RefreshFolds();
    }

    Fold? FoldStartingAt(int line)
    {
        var snap = Snapshot;
        int start = snap.GetLineStart(line);
        Fold? best = null;
        foreach (var f in _folds)
            if (f.Start == start && (best == null || f.End > best.End)) best = f;
        return best;
    }

    /// <summary>The fold ranges the gutter shows, and whether each is collapsed.</summary>
    public IReadOnlyList<(FoldRange Range, bool Collapsed)> Folds
    {
        get
        {
            EnsureFolds();
            var snap = Snapshot;
            return [.. _folds.Select(f => (new FoldRange(snap.GetLineFromOffset(f.Start), snap.GetLineFromOffset(f.End), f.CollapsedText), f.Collapsed))];
        }
    }

    /// <summary>Collapses or expands the fold starting at <paramref name="line"/>. Returns false if none starts there.</summary>
    public bool ToggleFold(int line)
    {
        EnsureFolds();
        if (FoldStartingAt(line) is not { } f) return false;
        SetFoldCollapsed(f, !f.Collapsed);
        return true;
    }

    public bool CollapseFold(int line)
    {
        EnsureFolds();
        if (FoldStartingAt(line) is not { } f) return false;
        SetFoldCollapsed(f, true);
        return true;
    }

    public bool ExpandFold(int line)
    {
        EnsureFolds();
        if (FoldStartingAt(line) is not { } f) return false;
        SetFoldCollapsed(f, false);
        return true;
    }

    void SetFoldCollapsed(Fold f, bool collapsed)
    {
        if (f.Collapsed == collapsed) return;
        f.Collapsed = collapsed;
        RebuildHidden();
        if (collapsed)
        {
            // Carets inside the fold move to its header line's end.
            var snap = Snapshot;
            int header = snap.GetLineFromOffset(f.Start);
            int headerEnd = snap.GetLineEnd(header);
            var moved = _sels.Select(s => IsLineHidden(snap.GetLineFromOffset(s.Caret)) ? new Sel(headerEnd, headerEnd) : s).ToList();
            SetSelectionsCore(moved, _primary);
        }
    }

    public void ExpandAll()
    {
        if (_folds.All(f => !f.Collapsed)) return;
        foreach (var f in _folds) f.Collapsed = false;
        RebuildHidden();
    }

    public void CollapseAll()
    {
        EnsureFolds();
        foreach (var f in _folds) f.Collapsed = true;
        RebuildHidden();
    }

    /// <summary>Expands any collapsed fold that hides <paramref name="offset"/>'s line.</summary>
    void RevealOffset(int offset)
    {
        if (_hidden.Count == 0) return;
        var snap = Snapshot;
        int line = snap.GetLineFromOffset(offset);
        bool changed = false;
        foreach (var f in _folds)
        {
            if (!f.Collapsed) continue;
            int s = snap.GetLineFromOffset(f.Start), e = snap.GetLineFromOffset(f.End);
            if (line > s && line <= e) { f.Collapsed = false; changed = true; }
        }
        if (changed) RebuildHidden();
    }
}
