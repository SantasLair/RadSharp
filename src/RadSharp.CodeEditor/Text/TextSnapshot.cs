namespace RadSharp.CodeEditing;

/// <summary>
/// One edit to apply: replace <see cref="Length"/> characters at <see cref="Start"/> with <see cref="NewText"/>.
/// A set of edits passed together is relative to the same (old) snapshot and must not overlap.
/// </summary>
public readonly record struct TextEdit(int Start, int Length, string NewText)
{
    public int End => Start + Length;
    public static TextEdit Insert(int at, string text) => new(at, 0, text);
    public static TextEdit Delete(int start, int length) => new(start, length, "");
}

/// <summary>
/// One change between two snapshots: <see cref="OldText"/> at <see cref="OldStart"/> in the old snapshot became
/// <see cref="NewText"/> at <see cref="NewStart"/> in the new one. Changes in a list are sorted and don't overlap.
/// </summary>
public readonly record struct TextChange(int OldStart, string OldText, int NewStart, string NewText)
{
    public int OldEnd => OldStart + OldText.Length;
    public int NewEnd => NewStart + NewText.Length;
    public int Delta => NewText.Length - OldText.Length;
}

/// <summary>How a position at an edit boundary moves: with inserted text (<see cref="Positive"/>) or stays before it.</summary>
public enum PointTrackingMode { Negative, Positive }

/// <summary>A line and a column (UTF-16 index within the line), both 0-based.</summary>
public readonly record struct TextPosition(int Line, int Column) : IComparable<TextPosition>
{
    public int CompareTo(TextPosition other) => Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);
    public override string ToString() => $"({Line}, {Column})";
}

/// <summary>
/// An immutable version of a <see cref="TextBuffer"/>'s text. Safe to read from any thread while the buffer keeps
/// changing (a language service reads one on a background worker). Lines end at <c>\n</c>; a <c>\r</c> before it
/// belongs to the line break, so <see cref="GetLineText"/> never includes either. Offsets, lines and columns are
/// 0-based UTF-16 indices.
/// </summary>
public sealed class TextSnapshot
{
    internal readonly Node? Root;
    volatile TextSnapshot? _next;
    TextChange[]? _changesToNext;

    internal TextSnapshot(TextBuffer buffer, Node? root, int version, string newLine)
    {
        Buffer = buffer;
        Root = root;
        Version = version;
        NewLine = newLine;
    }

    public TextBuffer Buffer { get; }
    /// <summary>Increases by one with every change to the buffer.</summary>
    public int Version { get; }
    /// <summary>The line break the buffer inserts (<c>"\r\n"</c> or <c>"\n"</c>), detected from the loaded text.</summary>
    public string NewLine { get; }
    public int Length => Node.Len(Root);
    public int LineCount => Node.LfsOf(Root) + 1;

    /// <summary>The next version and the changes leading to it, once there is one.</summary>
    public TextSnapshot? Next => _next;
    public IReadOnlyList<TextChange>? ChangesToNext => _next != null ? _changesToNext : null;

    internal void SetNext(TextSnapshot next, TextChange[] changes)
    {
        _changesToNext = changes;
        _next = next;
    }

    public char this[int offset]
    {
        get
        {
            if ((uint)offset >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(offset));
            return Node.CharAt(Root!, offset);
        }
    }

    public string GetText() => GetText(0, Length);

    public string GetText(int start, int length)
    {
        if (start < 0 || length < 0 || start + length > Length) throw new ArgumentOutOfRangeException(nameof(start));
        if (length == 0) return "";
        return string.Create(length, (Root, start), static (span, s) => Node.CopyTo(s.Root, s.start, span));
    }

    public void CopyTo(int start, Span<char> destination) => Node.CopyTo(Root, start, destination);

    // ---- Lines ----

    /// <summary>The 0-based line that <paramref name="offset"/> is on.</summary>
    public int GetLineFromOffset(int offset) => Node.LfsBefore(Root, Math.Clamp(offset, 0, Length));

    public int GetLineStart(int line)
    {
        line = Math.Clamp(line, 0, LineCount - 1);
        return line == 0 ? 0 : Node.OffsetAfterLf(Root!, line);
    }

    /// <summary>The offset where the line's content ends: before its <c>\r\n</c> or <c>\n</c>, or the end of the text.</summary>
    public int GetLineEnd(int line)
    {
        line = Math.Clamp(line, 0, LineCount - 1);
        if (line == LineCount - 1) return Length;
        int lf = Node.OffsetAfterLf(Root!, line + 1) - 1;
        return lf > GetLineStart(line) && Node.CharAt(Root!, lf - 1) == '\r' ? lf - 1 : lf;
    }

    /// <summary>The offset where the next line starts (after the line break), or the end of the text.</summary>
    public int GetLineEndIncludingBreak(int line)
    {
        line = Math.Clamp(line, 0, LineCount - 1);
        return line == LineCount - 1 ? Length : Node.OffsetAfterLf(Root!, line + 1);
    }

    public int GetLineLength(int line) => GetLineEnd(line) - GetLineStart(line);

    public string GetLineText(int line)
    {
        int s = GetLineStart(line);
        return GetText(s, GetLineEnd(line) - s);
    }

    public TextPosition GetPosition(int offset)
    {
        offset = Math.Clamp(offset, 0, Length);
        int line = GetLineFromOffset(offset);
        return new TextPosition(line, offset - GetLineStart(line));
    }

    /// <summary>The offset of a line and column, clamped to the text and to the line's content.</summary>
    public int GetOffset(int line, int column)
    {
        line = Math.Clamp(line, 0, LineCount - 1);
        int s = GetLineStart(line);
        return s + Math.Clamp(column, 0, GetLineEnd(line) - s);
    }

    public int GetOffset(TextPosition p) => GetOffset(p.Line, p.Column);

    /// <summary>The lines, without line breaks.</summary>
    public IEnumerable<string> Lines()
    {
        for (int i = 0; i < LineCount; i++) yield return GetLineText(i);
    }

    // ---- Versions ----

    /// <summary>
    /// Maps <paramref name="offset"/> in this snapshot to the same place in <paramref name="target"/>, a later
    /// version of the same buffer (or this one). Text inserted exactly at the offset pushes it right with
    /// <see cref="PointTrackingMode.Positive"/>; an offset inside replaced text moves to the edge of the new text.
    /// </summary>
    public int TranslateOffset(int offset, TextSnapshot target, PointTrackingMode mode = PointTrackingMode.Positive)
    {
        if (target.Buffer != Buffer) throw new ArgumentException("The snapshot belongs to another buffer.", nameof(target));
        if (target.Version < Version) throw new ArgumentException("Can only translate forward to a later version.", nameof(target));
        var s = this;
        while (s.Version < target.Version)
        {
            offset = TranslateThrough(s._changesToNext!, offset, mode);
            s = s._next!;
        }
        return Math.Clamp(offset, 0, target.Length);
    }

    /// <summary>Maps an offset through one list of sorted, non-overlapping changes.</summary>
    public static int TranslateThrough(IReadOnlyList<TextChange> changes, int offset, PointTrackingMode mode)
    {
        int delta = 0;
        foreach (var c in changes)
        {
            if (offset < c.OldStart) break;
            if (offset > c.OldStart && offset >= c.OldEnd) { delta += c.Delta; continue; }
            // At the start of the change, or inside the text it replaced.
            return mode == PointTrackingMode.Positive ? c.NewEnd : c.NewStart;
        }
        return offset + delta;
    }

    public override string ToString() => $"v{Version}, {LineCount} lines, {Length} chars";
}
