namespace RadSharp.CodeEditing;

/// <summary>Which edits may merge into the previous undo step (typing a word is undone as one step).</summary>
public enum UndoCoalescing
{
    /// <summary>Always a step of its own.</summary>
    None,
    /// <summary>Typed characters that continue the previous typed characters.</summary>
    Typing,
    /// <summary>Backspaces that continue the previous backspaces.</summary>
    DeleteBackward,
    /// <summary>Deletes that continue the previous deletes.</summary>
    DeleteForward,
}

/// <summary>What happened to a <see cref="TextBuffer"/>: the snapshots before and after, and the changes between them.</summary>
public sealed class TextChangedEventArgs(TextSnapshot before, TextSnapshot after, IReadOnlyList<TextChange> changes, TextChangeReason reason) : EventArgs
{
    public TextSnapshot Before { get; } = before;
    public TextSnapshot After { get; } = after;
    /// <summary>Sorted, non-overlapping; <c>OldStart</c> is in <see cref="Before"/>, <c>NewStart</c> in <see cref="After"/>.</summary>
    public IReadOnlyList<TextChange> Changes { get; } = changes;
    public TextChangeReason Reason { get; } = reason;
    /// <summary>The new version number (<see cref="After"/>'s).</summary>
    public int Version => After.Version;
}

public enum TextChangeReason { Edit, Undo, Redo, SetText }

/// <summary>
/// The text of one document: a piece table (an immutable balanced tree of pieces over append-only blocks, which is
/// also the line index) with versioned snapshots and delta-based undo.
/// <para>
/// Every change makes a new <see cref="TextSnapshot"/>; old ones stay valid and can be read on any thread. Undo
/// stores only what changed (the old and new text of each edit) grouped into steps; consecutive typing or deleting
/// merges into one step. Line breaks are kept exactly as loaded (CRLF, LF or mixed); <see cref="NewLine"/> is
/// what new line breaks use, detected from the first line break in the text.
/// </para>
/// Not thread-safe for writing: edit it from one thread (the UI thread), read snapshots from anywhere.
/// </summary>
public sealed class TextBuffer
{
    const int ChunkSize = 64 * 1024;

    Chunk _add = new(ChunkSize);
    TextSnapshot _current;
    int _version;

    sealed class Batch(TextChange[] changes)
    {
        public TextChange[] Changes = changes;
    }

    sealed class UndoStep
    {
        public readonly List<Batch> Batches = [];
        public UndoCoalescing Kind;
        public object? StateBefore, StateAfter;
        public int VersionAfter;
    }

    readonly List<UndoStep> _undo = [];
    readonly List<UndoStep> _redo = [];
    UndoStep? _open;          // the compound step BeginTransaction opened
    int _transactionDepth;
    bool _coalesceBroken;
    UndoStep? _cleanStep;     // the undo-stack top when the text was last clean (null: the empty stack)
    bool _cleanLost;          // the clean state can no longer be reached by undo or redo

    public TextBuffer(string text = "")
    {
        _current = CreateInitial(text ?? "", 0);
        if ((text ?? "").Contains('\n')) NewLine = _current.NewLine;
    }

    TextSnapshot CreateInitial(string text, int version)
    {
        Node? root = null;
        if (text.Length > 0)
        {
            var chunk = new Chunk(text);
            root = Node.Join(null, new Piece(chunk, 0, text.Length, 0, chunk.LfCount), null);
        }
        return new TextSnapshot(this, root, version, DetectNewLine(text));
    }

    static string DetectNewLine(string text)
    {
        int lf = text.IndexOf('\n');
        if (lf < 0) return "\n";
        return lf > 0 && text[lf - 1] == '\r' ? "\r\n" : "\n";
    }

    /// <summary>The latest snapshot.</summary>
    public TextSnapshot Current => _current;
    public int Version => _current.Version;
    public int Length => _current.Length;
    public int LineCount => _current.LineCount;
    public string GetText() => _current.GetText();

    /// <summary>The line break inserted for Enter and used when pasting (detected on load; default <c>\n</c>).</summary>
    public string NewLine { get; set; } = "\n";

    /// <summary>The most undo steps kept.</summary>
    public int UndoLimit { get; set; } = 10_000;

    /// <summary>Raised after every change, on the thread that made it.</summary>
    public event EventHandler<TextChangedEventArgs>? Changed;

    // ---- Whole text ----

    /// <summary>Replaces all text, detects the line break style and clears undo (loading a file).</summary>
    public void SetText(string text)
    {
        text ??= "";
        var before = _current;
        var detected = DetectNewLine(text);
        NewLine = text.Contains('\n') ? detected : NewLine;
        var after = CreateInitial(text, ++_version);
        var changes = new[] { new TextChange(0, before.GetText(), 0, text) };
        before.SetNext(after, changes);
        _current = after;
        _undo.Clear();
        _redo.Clear();
        _open = null;
        _transactionDepth = 0;
        _cleanStep = null;
        _cleanLost = false;
        Changed?.Invoke(this, new TextChangedEventArgs(before, after, changes, TextChangeReason.SetText));
    }

    // ---- Dirty tracking ----

    /// <summary>True when the text differs from when it was loaded or <see cref="MarkClean"/> was last called (by undo history).</summary>
    public bool IsModified => _cleanLost || (_undo.Count > 0 ? _undo[^1] : null) != _cleanStep;

    /// <summary>Remembers the current state as saved.</summary>
    public void MarkClean()
    {
        _cleanStep = _undo.Count > 0 ? _undo[^1] : null;
        _cleanLost = false;
    }

    void ClearRedo()
    {
        if (_redo.Count == 0) return;
        if (_cleanStep != null && _redo.Contains(_cleanStep)) _cleanLost = true;
        _redo.Clear();
    }

    // ---- Editing ----

    /// <summary>
    /// Applies <paramref name="edits"/> (relative to <see cref="Current"/>, non-overlapping, any order) as one change.
    /// Returns null if nothing changed. <paramref name="coalescing"/> lets typing merge into the previous undo step;
    /// <paramref name="stateBefore"/> is stored with the undo step (the editor keeps its carets there) and handed
    /// back by <see cref="Undo"/>.
    /// </summary>
    public TextChangedEventArgs? Apply(IEnumerable<TextEdit> edits, UndoCoalescing coalescing = UndoCoalescing.None, object? stateBefore = null)
    {
        var list = Normalize(edits, _current.Length);
        if (list.Count == 0) return null;
        var before = _current;
        var changes = ApplyCore(list);
        ClearRedo();
        RecordUndo(changes, coalescing, stateBefore);
        var args = new TextChangedEventArgs(before, _current, changes, TextChangeReason.Edit);
        Changed?.Invoke(this, args);
        return args;
    }

    public TextChangedEventArgs? Apply(TextEdit edit, UndoCoalescing coalescing = UndoCoalescing.None, object? stateBefore = null) =>
        Apply([edit], coalescing, stateBefore);

    public TextChangedEventArgs? Insert(int offset, string text) => Apply(TextEdit.Insert(offset, text));
    public TextChangedEventArgs? Delete(int start, int length) => Apply(TextEdit.Delete(start, length));
    public TextChangedEventArgs? Replace(int start, int length, string text) => Apply(new TextEdit(start, length, text));

    static List<TextEdit> Normalize(IEnumerable<TextEdit> edits, int length)
    {
        var list = new List<TextEdit>();
        foreach (var e in edits)
        {
            if (e.Start < 0 || e.Length < 0 || e.End > length) throw new ArgumentOutOfRangeException(nameof(edits), $"Edit {e} is outside the text (length {length}).");
            if (e.Length == 0 && string.IsNullOrEmpty(e.NewText)) continue;
            list.Add(e with { NewText = e.NewText ?? "" });
        }
        if (list.Count > 1)
        {
            list = [.. list.OrderBy(e => e.Start).ThenBy(e => e.Length)];   // stable: same-offset inserts keep their order
            for (int i = 1; i < list.Count; i++)
                if (list[i].Start < list[i - 1].End)
                    throw new ArgumentException($"Edits {list[i - 1]} and {list[i]} overlap.", nameof(edits));
        }
        return list;
    }

    /// <summary>Applies sorted edits back to front, builds the new snapshot and links the old one to it.</summary>
    TextChange[] ApplyCore(List<TextEdit> edits)
    {
        var before = _current;
        var root = before.Root;
        var changes = new TextChange[edits.Count];
        int delta = 0;
        for (int i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            changes[i] = new TextChange(e.Start, e.Length == 0 ? "" : before.GetText(e.Start, e.Length), e.Start + delta, e.NewText);
            delta += e.NewText.Length - e.Length;
        }
        for (int i = edits.Count - 1; i >= 0; i--)
        {
            var e = edits[i];
            var (left, rest) = Node.Split(root, e.Start);
            var (_, right) = Node.Split(rest, e.Length);
            if (e.NewText.Length > 0) left = AppendText(left, e.NewText);
            root = Node.Concat(left, right);
        }
        var after = new TextSnapshot(this, root, ++_version, NewLine);
        before.SetNext(after, changes);
        _current = after;
        return changes;
    }

    /// <summary>Adds text at the end of <paramref name="left"/>, extending its last piece when that piece ends where the add block does (typing).</summary>
    Node AppendText(Node? left, string text)
    {
        ReadOnlySpan<char> span = text;
        while (span.Length > 0)
        {
            if (_add.Free == 0) _add = new Chunk(Math.Max(ChunkSize, span.Length));
            int take = Math.Min(span.Length, _add.Free);
            var piece = _add.Append(span[..take]);
            span = span[take..];
            if (left != null)
            {
                var (rest, last) = Node.SplitLast(left);
                if (last.Chunk == piece.Chunk && last.End == piece.Start)
                {
                    left = Node.Join(rest, new Piece(last.Chunk, last.Start, last.Length + piece.Length, last.FirstLf, last.LfCount + piece.LfCount), null);
                    continue;
                }
            }
            left = Node.Join(left, piece, null);
        }
        return left!;
    }

    // ---- Undo ----

    public bool CanUndo => _undo.Count > 0 && _transactionDepth == 0;
    public bool CanRedo => _redo.Count > 0 && _transactionDepth == 0;

    /// <summary>Stops the next edit from merging into the current undo step (the caret moved, focus left...).</summary>
    public void BreakUndoCoalescing() => _coalesceBroken = true;

    /// <summary>
    /// Groups every edit until the returned scope is disposed into one undo step (nesting is allowed). Use it for
    /// commands made of several <see cref="Apply"/> calls.
    /// </summary>
    public IDisposable BeginTransaction(object? stateBefore = null)
    {
        if (_transactionDepth++ == 0)
        {
            _open = new UndoStep { Kind = UndoCoalescing.None, StateBefore = stateBefore };
        }
        return new TransactionScope(this);
    }

    sealed class TransactionScope(TextBuffer buffer) : IDisposable
    {
        bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            buffer.EndTransaction();
        }
    }

    void EndTransaction()
    {
        if (_transactionDepth == 0 || --_transactionDepth > 0) return;
        var step = _open!;
        _open = null;
        if (step.Batches.Count > 0) PushUndo(step);
        _coalesceBroken = true;
    }

    /// <summary>Sets the state stored as "after" on the latest undo step (the editor's carets after an edit); <see cref="Redo"/> hands it back.</summary>
    public void SetUndoStateAfter(object? state)
    {
        if (_open != null) _open.StateAfter = state;
        else if (_undo.Count > 0) _undo[^1].StateAfter = state;
    }

    void RecordUndo(TextChange[] changes, UndoCoalescing kind, object? stateBefore)
    {
        if (_open != null)
        {
            _open.Batches.Add(new Batch(changes));
            _open.StateBefore ??= stateBefore;
            return;
        }
        if (kind != UndoCoalescing.None && !_coalesceBroken && _undo.Count > 0)
        {
            var last = _undo[^1];
            if (last.Kind == kind && last.VersionAfter == _version - 1 && last.Batches.Count == 1 && last != _cleanStep
                && TryMerge(last.Batches[0].Changes, changes) is { } merged)
            {
                last.Batches[0].Changes = merged;
                last.VersionAfter = _version;
                return;
            }
        }
        _coalesceBroken = false;
        var step = new UndoStep { Kind = kind, StateBefore = stateBefore, VersionAfter = _version };
        step.Batches.Add(new Batch(changes));
        PushUndo(step);
    }

    void PushUndo(UndoStep step)
    {
        step.VersionAfter = _version;
        _undo.Add(step);
        if (_undo.Count > UndoLimit)
        {
            if (_undo[0] == _cleanStep) _cleanStep = null;          // the new bottom of history is the clean state
            else if (_cleanStep == null) _cleanLost = true;          // the clean state was before the dropped step
            _undo.RemoveAt(0);
        }
    }

    /// <summary>
    /// Composes two consecutive change lists into one (old → newer) when they pair up one to one and each later
    /// change touches its earlier partner's new text and no other: the shape of typing or deleting at every caret.
    /// </summary>
    static TextChange[]? TryMerge(TextChange[] first, TextChange[] second)
    {
        if (first.Length != second.Length) return null;
        var result = new TextChange[first.Length];
        int delta = 0;
        for (int i = 0; i < first.Length; i++)
        {
            var p = first[i];
            var n = second[i];
            // n is in the coordinates after `first`; it must touch p's new text...
            if (n.OldEnd < p.NewStart || n.OldStart > p.NewEnd) return null;
            // ...and stay clear of the neighbours.
            if (i > 0 && n.OldStart < first[i - 1].NewEnd) return null;
            if (i + 1 < first.Length && n.OldEnd > first[i + 1].NewStart) return null;

            int regionStart = Math.Min(n.OldStart, p.NewStart);
            string leftExtra = n.OldStart < p.NewStart ? n.OldText[..(p.NewStart - n.OldStart)] : "";
            string rightExtra = n.OldEnd > p.NewEnd ? n.OldText[(p.NewEnd - n.OldStart)..] : "";
            string region = leftExtra + p.NewText + rightExtra;          // the region's text after `first`
            int a = n.OldStart - regionStart;
            string newRegion = string.Concat(region.AsSpan(0, a), n.NewText, region.AsSpan(a + n.OldText.Length));
            int oldStart = p.OldStart - leftExtra.Length;
            string oldText = leftExtra + p.OldText + rightExtra;
            result[i] = new TextChange(oldStart, oldText, oldStart + delta, newRegion);
            delta += newRegion.Length - oldText.Length;
        }
        return result;
    }

    /// <summary>Undoes the latest step. Returns false if there is none; <paramref name="state"/> is the state stored before it.</summary>
    public bool Undo(out object? state)
    {
        state = null;
        if (!CanUndo) return false;
        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        for (int b = step.Batches.Count - 1; b >= 0; b--)
            ApplyInverse(step.Batches[b].Changes, TextChangeReason.Undo);
        _redo.Add(step);
        _coalesceBroken = true;
        state = step.StateBefore;
        return true;
    }

    public bool Undo() => Undo(out _);

    /// <summary>Redoes the latest undone step. <paramref name="state"/> is the state stored after it.</summary>
    public bool Redo(out object? state)
    {
        state = null;
        if (!CanRedo) return false;
        var step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        foreach (var batch in step.Batches)
            ApplyForward(batch.Changes, TextChangeReason.Redo);
        step.VersionAfter = _version;
        _undo.Add(step);
        _coalesceBroken = true;
        state = step.StateAfter;
        return true;
    }

    public bool Redo() => Redo(out _);

    void ApplyInverse(TextChange[] changes, TextChangeReason reason)
    {
        var edits = new List<TextEdit>(changes.Length);
        foreach (var c in changes) edits.Add(new TextEdit(c.NewStart, c.NewText.Length, c.OldText));
        Emit(edits, reason);
    }

    void ApplyForward(TextChange[] changes, TextChangeReason reason)
    {
        var edits = new List<TextEdit>(changes.Length);
        foreach (var c in changes) edits.Add(new TextEdit(c.OldStart, c.OldText.Length, c.NewText));
        Emit(edits, reason);
    }

    void Emit(List<TextEdit> edits, TextChangeReason reason)
    {
        var before = _current;
        var changes = ApplyCore(edits);
        Changed?.Invoke(this, new TextChangedEventArgs(before, _current, changes, reason));
    }

    /// <summary>Forgets all undo and redo steps.</summary>
    public void ClearUndo()
    {
        _cleanLost = IsModified;
        _undo.Clear();
        _redo.Clear();
        _cleanStep = null;
    }

    public override string ToString() => _current.ToString();
}
