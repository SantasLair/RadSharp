using System.Globalization;
using System.Text;
using RadSharp.CodeEditing;
using RadSharp.Drawing;
using RadSharp.Text;

namespace RadSharp;

public partial class CodeEditor
{
    // =====================================================================
    // Grapheme and word boundaries (the same rules as RadSharp.Text.TextEditor, per line)
    // =====================================================================

    enum CharClass { Space, Word, Punctuation }

    static CharClass Classify(char c)
    {
        if (char.IsWhiteSpace(c)) return CharClass.Space;
        if (char.IsLetterOrDigit(c) || c == '_' || char.IsSurrogate(c)
            || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            return CharClass.Word;
        return CharClass.Punctuation;
    }

    static bool IsWordChar(char c) => Classify(c) == CharClass.Word;

    static int PrevBoundary(string text, int col)
    {
        if (col <= 0) return 0;
        var b = TextLayout.GraphemeBoundaries(text);
        int i = Array.BinarySearch(b, Math.Min(col, text.Length));
        if (i < 0) i = ~i;
        return i > 0 ? b[i - 1] : 0;
    }

    static int NextBoundary(string text, int col)
    {
        if (col >= text.Length) return text.Length;
        var b = TextLayout.GraphemeBoundaries(text);
        int i = Array.BinarySearch(b, col);
        i = i < 0 ? ~i : i + 1;
        return i < b.Length ? b[i] : text.Length;
    }

    static int PrevWordStart(string s, int i)
    {
        while (i > 0 && Classify(s[i - 1]) == CharClass.Space) i--;
        if (i == 0) return 0;
        var cls = Classify(s[i - 1]);
        while (i > 0 && Classify(s[i - 1]) == cls) i--;
        return i;
    }

    static int NextWordStart(string s, int i)
    {
        int n = s.Length;
        if (i < n)
        {
            var cls = Classify(s[i]);
            if (cls != CharClass.Space) while (i < n && Classify(s[i]) == cls) i++;
        }
        while (i < n && Classify(s[i]) == CharClass.Space) i++;
        return i;
    }

    static int NextWordEnd(string s, int i)
    {
        int n = s.Length;
        while (i < n && Classify(s[i]) == CharClass.Space) i++;
        if (i == n) return n;
        var cls = Classify(s[i]);
        while (i < n && Classify(s[i]) == cls) i++;
        return i;
    }

    static (int Start, int End) WordAt(string s, int i)
    {
        if (s.Length == 0) return (0, 0);
        i = Math.Clamp(i, 0, s.Length);
        // Prefer the word to the left when the caret sits just after it.
        int probe = i < s.Length && IsWordChar(s[i]) ? i : i > 0 ? i - 1 : 0;
        var cls = Classify(s[probe]);
        int a = probe, b = probe + 1;
        while (a > 0 && Classify(s[a - 1]) == cls) a--;
        while (b < s.Length && Classify(s[b]) == cls) b++;
        return (a, b);
    }

    static int LeadingWhitespace(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return i;
    }

    // ---- Offset-level movement ----

    int PrevPosition(int offset, bool word)
    {
        var snap = Snapshot;
        var p = snap.GetPosition(offset);
        if (p.Column == 0)
        {
            int prev = PreviousVisibleLine(p.Line);
            return prev < 0 ? 0 : snap.GetLineEnd(prev);
        }
        var text = snap.GetLineText(p.Line);
        int col = word ? PrevWordStart(text, p.Column) : PrevBoundary(text, p.Column);
        return snap.GetLineStart(p.Line) + col;
    }

    int NextPosition(int offset, bool word)
    {
        var snap = Snapshot;
        var p = snap.GetPosition(offset);
        var text = snap.GetLineText(p.Line);
        if (p.Column >= text.Length)
        {
            int next = NextVisibleLine(p.Line);
            return next >= snap.LineCount ? snap.Length : snap.GetLineStart(next);
        }
        int col = word ? NextWordStart(text, p.Column) : NextBoundary(text, p.Column);
        return snap.GetLineStart(p.Line) + col;
    }

    int SmartHome(int offset)
    {
        var snap = Snapshot;
        var p = snap.GetPosition(offset);
        int indent = LeadingWhitespace(snap.GetLineText(p.Line));
        int start = snap.GetLineStart(p.Line);
        return p.Column == indent ? start : start + indent;
    }

    // =====================================================================
    // Caret movement
    // =====================================================================

    void MoveCarets(Func<Sel, int> target, bool extend)
    {
        _buffer.BreakUndoCoalescing();
        var list = _sels.Select(s =>
        {
            int t = target(s);
            return new Sel(extend ? s.Anchor : t, t);
        }).ToList();
        SetSelectionsCore(list, _primary);
        ScrollToCaret();
    }

    void MoveHorizontal(bool left, bool word, bool extend)
    {
        if (!extend && !word && _sels.Any(s => !s.IsEmpty))
        {
            MoveCarets(s => s.IsEmpty ? (left ? PrevPosition(s.Caret, false) : NextPosition(s.Caret, false)) : left ? s.Start : s.End, false);
            return;
        }
        MoveCarets(s => left ? PrevPosition(s.Caret, word) : NextPosition(s.Caret, word), extend);
    }

    void MoveVertical(int rows, bool extend)
    {
        _buffer.BreakUndoCoalescing();
        var snap = Snapshot;
        var list = new List<Sel>(_sels.Count);
        foreach (var s in _sels)
        {
            var p = snap.GetPosition(s.Caret);
            float x = s.DesiredX >= 0 ? s.DesiredX : GetLineView(p.Line).XOf(p.Column);
            int row = RowOfLine(p.Line) + rows;
            int t;
            if (row < 0) t = 0;
            else if (row >= RowCount) t = snap.Length;
            else
            {
                int line = LineOfRow(row);
                t = snap.GetLineStart(line) + GetLineView(line).ColumnAt(x);
            }
            list.Add(new Sel(extend ? s.Anchor : t, t, x));
        }
        SetSelectionsCore(list, _primary);
        ScrollToCaret();
    }

    // =====================================================================
    // Column (box) selection
    // =====================================================================

    (int AnchorLine, float AnchorX, int ActiveLine, float ActiveX)? _box;

    void ApplyBox()
    {
        if (_box is not { } box) return;
        var snap = Snapshot;
        int step = box.ActiveLine >= box.AnchorLine ? 1 : -1;
        var list = new List<Sel>();
        for (int line = box.AnchorLine; ; line = step > 0 ? NextVisibleLine(line) : PreviousVisibleLine(line))
        {
            if (line < 0 || line >= snap.LineCount) break;
            var view = GetLineView(line);
            int start = snap.GetLineStart(line);
            list.Add(new Sel(start + view.ColumnAt(box.AnchorX), start + view.ColumnAt(box.ActiveX), box.ActiveX));
            if (line == box.ActiveLine || (step > 0 ? line > box.ActiveLine : line < box.ActiveLine)) break;
        }
        _buffer.BreakUndoCoalescing();
        // The active line's selection is primary.
        SetSelectionsCore(list, list.Count - 1);
        ScrollToCaret();
    }

    void ExtendBox(int dLines, int dCols)
    {
        var snap = Snapshot;
        if (_box == null)
        {
            var p = snap.GetPosition(CaretOffset);
            float x = GetLineView(p.Line).XOf(p.Column);
            var a = snap.GetPosition(_sels[_primary].Anchor);
            _box = (a.Line, GetLineView(a.Line).XOf(a.Column), p.Line, x);
        }
        var b = _box.Value;
        int line = b.ActiveLine;
        if (dLines < 0) { int prev = PreviousVisibleLine(line); if (prev >= 0) line = prev; }
        if (dLines > 0) { int next = NextVisibleLine(line); if (next < snap.LineCount) line = next; }
        float activeX = Math.Max(0, b.ActiveX + dCols * CharWidth);
        _box = (b.AnchorLine, b.AnchorX, line, activeX);
        ApplyBox();
    }

    // =====================================================================
    // Typing
    // =====================================================================

    static readonly Dictionary<char, char> Pairs = new() { ['('] = ')', ['['] = ']', ['{'] = '}', ['"'] = '"', ['\''] = '\'' };
    static bool IsCloser(char c) => c is ')' or ']' or '}' or '"' or '\'';

    string IndentUnit => InsertSpaces ? new string(' ', _tabSize) : "\t";

    /// <summary>Types <paramref name="text"/> at every caret, as if from the keyboard (brackets auto-close).</summary>
    public void TypeText(string text)
    {
        if (ReadOnly || string.IsNullOrEmpty(text)) return;
        text = NormalizeNewLines(text);
        var snap = Snapshot;
        bool single = text.Length == 1 || (text.Length == 2 && char.IsSurrogatePair(text[0], text[1]));
        char ch = text[0];
        var edits = new List<(TextEdit, int, int)>();
        foreach (var s in _sels)
        {
            if (single && AutoCloseBrackets)
            {
                char next = s.End < snap.Length ? snap[s.End] : '\0';
                // Step over a closer that's already there.
                if (s.IsEmpty && IsCloser(ch) && next == ch && (ch is not ('"' or '\'') || IsAutoClosedQuote(snap, s.Caret)))
                {
                    edits.Add((new TextEdit(s.Caret, 0, ""), 1, 1));
                    continue;
                }
                if (Pairs.TryGetValue(ch, out char close))
                {
                    if (!s.IsEmpty)
                    {
                        // Surround the selection.
                        var inner = snap.GetText(s.Start, s.End - s.Start);
                        edits.Add((new TextEdit(s.Start, s.End - s.Start, ch + inner + close), 1, 1 + inner.Length));
                        continue;
                    }
                    char prev = s.Caret > 0 ? snap[s.Caret - 1] : '\0';
                    bool quote = ch is '"' or '\'';
                    bool nextOk = next == '\0' || char.IsWhiteSpace(next) || next is ')' or ']' or '}' or ';' or ',';
                    bool prevOk = !quote || !(IsWordChar(prev) || prev == ch);
                    if (nextOk && prevOk)
                    {
                        edits.Add((new TextEdit(s.Caret, 0, $"{ch}{close}"), 1, 1));
                        continue;
                    }
                }
            }
            edits.Add((new TextEdit(s.Start, s.End - s.Start, text), text.Length, text.Length));
        }
        bool whitespace = char.IsWhiteSpace(ch);
        if (!single || whitespace && !_lastTypedWhitespace) _buffer.BreakUndoCoalescing();
        if (_sels.Any(s => !s.IsEmpty)) _buffer.BreakUndoCoalescing();
        _lastTypedWhitespace = whitespace;
        ApplyOwn(edits, single ? UndoCoalescing.Typing : UndoCoalescing.None);
        if (single) OnCharTyped(ch);
    }

    bool _lastTypedWhitespace;

    /// <summary>A quote at <paramref name="offset"/> that closes a string on this line (an even count of quotes before it would open one).</summary>
    static bool IsAutoClosedQuote(TextSnapshot snap, int offset)
    {
        var p = snap.GetPosition(offset);
        var line = snap.GetLineText(p.Line);
        char q = line[p.Column];
        int count = 0;
        for (int i = 0; i < p.Column; i++) if (line[i] == q && (i == 0 || line[i - 1] != '\\')) count++;
        return count % 2 == 1;
    }

    string NormalizeNewLines(string text)
    {
        if (!text.Contains('\n') && !text.Contains('\r')) return text;
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", _buffer.NewLine);
    }

    /// <summary>Enter: a line break at every caret, keeping the indentation (and indenting after an opening brace).</summary>
    public void InsertNewLine()
    {
        if (ReadOnly) return;
        _buffer.BreakUndoCoalescing();
        var snap = Snapshot;
        var nl = _buffer.NewLine;
        var edits = new List<(TextEdit, int, int)>();
        foreach (var s in _sels)
        {
            var p = snap.GetPosition(s.Start);
            var lineText = snap.GetLineText(p.Line);
            string indent = AutoIndent ? lineText[..Math.Min(LeadingWhitespace(lineText), p.Column)] : "";
            var before = lineText[..p.Column].TrimEnd();
            var endPos = snap.GetPosition(s.End);
            var after = endPos.Line == p.Line ? lineText[endPos.Column..].TrimStart() : snap.GetLineText(endPos.Line)[endPos.Column..].TrimStart();
            // Trailing spaces left behind on the line being split are removed.
            int removeFrom = s.Start;
            if (AutoIndent) while (removeFrom > snap.GetLineStart(p.Line) + indent.Length && snap[removeFrom - 1] is ' ' or '\t') removeFrom--;
            int len = s.End - removeFrom;
            if (AutoIndent && before.Length > 0 && before[^1] is '{' or '(' or '[')
            {
                char open = before[^1];
                char close = open == '{' ? '}' : open == '(' ? ')' : ']';
                string inner = nl + indent + IndentUnit;
                if (after.Length > 0 && after[0] == close)
                {
                    var t = inner + nl + indent;
                    edits.Add((new TextEdit(removeFrom, len + (after.Length > 0 ? CountLeadingSpaces(snap, s.End) : 0), t), inner.Length, inner.Length));
                }
                else edits.Add((new TextEdit(removeFrom, len, inner), inner.Length, inner.Length));
                continue;
            }
            var text = nl + indent;
            edits.Add((new TextEdit(removeFrom, len + (AutoIndent ? CountLeadingSpaces(snap, s.End) : 0), text), text.Length, text.Length));
        }
        ApplyOwn(edits, UndoCoalescing.None);
        _buffer.BreakUndoCoalescing();
    }

    /// <summary>Spaces and tabs right after <paramref name="offset"/> on its line (they move to the new line's indent).</summary>
    static int CountLeadingSpaces(TextSnapshot snap, int offset)
    {
        int end = snap.GetLineEnd(snap.GetLineFromOffset(offset));
        int i = offset;
        while (i < end && snap[i] is ' ' or '\t') i++;
        return i - offset;
    }

    /// <summary>Backspace at every caret: deletes the selection, a grapheme, a word, an indent level or an auto-closed pair.</summary>
    public void DeleteBackward(bool word = false)
    {
        if (ReadOnly) return;
        var snap = Snapshot;
        var edits = new List<(TextEdit, int, int)>();
        bool anySelection = _sels.Any(s => !s.IsEmpty);
        foreach (var s in _sels)
        {
            if (!s.IsEmpty) { edits.Add((new TextEdit(s.Start, s.End - s.Start, ""), 0, 0)); continue; }
            if (s.Caret == 0) { edits.Add((new TextEdit(0, 0, ""), 0, 0)); continue; }
            var p = snap.GetPosition(s.Caret);
            int from = PrevPosition(s.Caret, word);
            if (!word && p.Column > 0)
            {
                var text = snap.GetLineText(p.Line);
                // In the indentation: back to the previous tab stop.
                if (InsertSpaces && p.Column <= LeadingWhitespace(text) && text[p.Column - 1] == ' ')
                {
                    int display = DisplayColumn(text, p.Column);
                    int target = (display - 1) / _tabSize * _tabSize;
                    int c = p.Column;
                    while (c > 0 && text[c - 1] == ' ' && DisplayColumn(text, c) > target) c--;
                    from = snap.GetLineStart(p.Line) + c;
                }
                // Between an auto-closed pair: delete both.
                else if (AutoCloseBrackets && p.Column < text.Length && Pairs.TryGetValue(text[p.Column - 1], out char close) && text[p.Column] == close)
                {
                    edits.Add((new TextEdit(s.Caret - 1, 2, ""), 0, 0));
                    continue;
                }
            }
            edits.Add((new TextEdit(from, s.Caret - from, ""), 0, 0));
        }
        if (anySelection || word) _buffer.BreakUndoCoalescing();
        ApplyOwn(edits, anySelection ? UndoCoalescing.None : UndoCoalescing.DeleteBackward);
    }

    /// <summary>Delete at every caret: the selection, or the grapheme (word) after the caret.</summary>
    public void DeleteForward(bool word = false)
    {
        if (ReadOnly) return;
        var snap = Snapshot;
        var edits = new List<(TextEdit, int, int)>();
        bool anySelection = _sels.Any(s => !s.IsEmpty);
        foreach (var s in _sels)
        {
            if (!s.IsEmpty) { edits.Add((new TextEdit(s.Start, s.End - s.Start, ""), 0, 0)); continue; }
            int to = s.Caret;
            if (word)
            {
                var p = snap.GetPosition(s.Caret);
                var text = snap.GetLineText(p.Line);
                to = p.Column >= text.Length ? NextPosition(s.Caret, false) : snap.GetLineStart(p.Line) + NextWordEnd(text, p.Column);
            }
            else to = NextPosition(s.Caret, false);
            edits.Add((new TextEdit(s.Caret, to - s.Caret, ""), 0, 0));
        }
        if (anySelection || word) _buffer.BreakUndoCoalescing();
        ApplyOwn(edits, anySelection ? UndoCoalescing.None : UndoCoalescing.DeleteForward);
    }

    int DisplayColumn(string text, int col)
    {
        int d = 0;
        for (int i = 0; i < col && i < text.Length; i++) d += text[i] == '\t' ? _tabSize - d % _tabSize : 1;
        return d;
    }

    // =====================================================================
    // Line commands
    // =====================================================================

    /// <summary>The lines each selection covers (a selection ending at column 0 doesn't include that line), merged and sorted.</summary>
    List<(int First, int Last)> SelectedLineBlocks(bool mergeTouching = false)
    {
        var snap = Snapshot;
        var blocks = new List<(int First, int Last)>();
        foreach (var s in _sels)
        {
            var a = snap.GetPosition(s.Start);
            var b = snap.GetPosition(s.End);
            int last = b.Line > a.Line && b.Column == 0 ? b.Line - 1 : b.Line;
            blocks.Add((a.Line, last));
        }
        blocks.Sort();
        var merged = new List<(int First, int Last)>();
        foreach (var bl in blocks)
        {
            if (merged.Count > 0 && bl.First <= merged[^1].Last + (mergeTouching ? 1 : 0))
                merged[^1] = (merged[^1].First, Math.Max(merged[^1].Last, bl.Last));
            else merged.Add(bl);
        }
        return merged;
    }

    IEnumerable<int> SelectedLines() => SelectedLineBlocks().SelectMany(b => Enumerable.Range(b.First, b.Last - b.First + 1));

    /// <summary>Tab: indents the selected lines when a selection spans lines, otherwise inserts an indent at every caret.</summary>
    public void Indent()
    {
        if (ReadOnly) return;
        var snap = Snapshot;
        bool multiLine = _sels.Any(s => snap.GetLineFromOffset(s.Start) != snap.GetLineFromOffset(s.End));
        if (!multiLine)
        {
            _buffer.BreakUndoCoalescing();
            var edits = new List<(TextEdit, int, int)>();
            foreach (var s in _sels)
            {
                var p = snap.GetPosition(s.Start);
                string ins = InsertSpaces ? new string(' ', _tabSize - DisplayColumn(snap.GetLineText(p.Line), p.Column) % _tabSize) : "\t";
                edits.Add((new TextEdit(s.Start, s.End - s.Start, ins), ins.Length, ins.Length));
            }
            ApplyOwn(edits, UndoCoalescing.None);
            _buffer.BreakUndoCoalescing();
            return;
        }
        var lineEdits = new List<TextEdit>();
        foreach (int line in SelectedLines())
            if (snap.GetLineLength(line) > 0) lineEdits.Add(TextEdit.Insert(snap.GetLineStart(line), IndentUnit));
        EditMapped(lineEdits);
    }

    /// <summary>Shift+Tab: removes one indent level from the selected lines.</summary>
    public void Outdent()
    {
        if (ReadOnly) return;
        var snap = Snapshot;
        var edits = new List<TextEdit>();
        foreach (int line in SelectedLines())
        {
            var text = snap.GetLineText(line);
            int n = 0;
            if (text.Length > 0 && text[0] == '\t') n = 1;
            else while (n < text.Length && n < _tabSize && text[n] == ' ') n++;
            if (n > 0) edits.Add(TextEdit.Delete(snap.GetLineStart(line), n));
        }
        EditMapped(edits);
    }

    /// <summary>Ctrl+/: comments the selected lines with <see cref="LineCommentPrefix"/>, or uncomments them if all are commented.</summary>
    public void ToggleLineComment()
    {
        if (ReadOnly || LineCommentPrefix.Length == 0) return;
        var snap = Snapshot;
        var prefix = LineCommentPrefix;
        var lines = SelectedLines().ToList();
        var nonBlank = lines.Where(l => !snap.GetLineText(l).AsSpan().IsWhiteSpace()).ToList();
        if (nonBlank.Count == 0) nonBlank = lines;
        bool allCommented = nonBlank.All(l => snap.GetLineText(l).AsSpan().TrimStart().StartsWith(prefix, StringComparison.Ordinal));
        var edits = new List<TextEdit>();
        if (allCommented)
        {
            foreach (int l in nonBlank)
            {
                var text = snap.GetLineText(l);
                int at = LeadingWhitespace(text);
                int len = prefix.Length + (at + prefix.Length < text.Length && text[at + prefix.Length] == ' ' ? 1 : 0);
                edits.Add(TextEdit.Delete(snap.GetLineStart(l) + at, len));
            }
        }
        else
        {
            int indent = nonBlank.Min(l => LeadingWhitespace(snap.GetLineText(l)));
            foreach (int l in nonBlank)
                edits.Add(TextEdit.Insert(snap.GetLineStart(l) + Math.Min(indent, snap.GetLineLength(l)), prefix + " "));
        }
        _buffer.BreakUndoCoalescing();
        EditMapped(edits);
    }

    /// <summary>Copies the selected lines below themselves; the carets move to the copy.</summary>
    public void DuplicateLines()
    {
        if (ReadOnly) return;
        var snap = Snapshot;
        var nl = _buffer.NewLine;
        var blocks = SelectedLineBlocks();
        // One edit per block; every caret in a block is mapped into the copy.
        var sels = _sels.ToList();
        var newSels = new List<Sel>();
        var result = new List<TextEdit>();
        int delta = 0;
        foreach (var (first, last) in blocks)
        {
            int start = snap.GetLineStart(first), end = snap.GetLineEnd(last);
            var block = snap.GetText(start, end - start);
            int insertAt = end;
            result.Add(TextEdit.Insert(insertAt, nl + block));
            int shift = end - start + nl.Length;
            foreach (var s in sels.Where(s => snap.GetLineFromOffset(s.Start) >= first && snap.GetLineFromOffset(s.Start) <= last))
                newSels.Add(new Sel(s.Anchor + delta + shift, s.Caret + delta + shift));
            delta += nl.Length + block.Length;
        }
        _buffer.BreakUndoCoalescing();
        ApplyWithSelections(result, newSels);
    }

    /// <summary>Alt+Up / Alt+Down: moves the selected lines up or down one line.</summary>
    public void MoveLines(bool up)
    {
        if (ReadOnly) return;
        var snap = Snapshot;
        var blocks = SelectedLineBlocks(mergeTouching: true);
        if (up ? blocks[0].First == 0 : blocks[^1].Last >= snap.LineCount - 1) return;
        var edits = new List<TextEdit>();
        var newSels = new List<Sel>();
        foreach (var (first, last) in blocks)
        {
            int blockStart = snap.GetLineStart(first), blockEnd = snap.GetLineEnd(last);
            var block = snap.GetText(blockStart, blockEnd - blockStart);
            if (up)
            {
                int prevStart = snap.GetLineStart(first - 1), prevEnd = snap.GetLineEnd(first - 1);
                var prev = snap.GetText(prevStart, prevEnd - prevStart);
                var brk = snap.GetText(prevEnd, blockStart - prevEnd);
                edits.Add(new TextEdit(prevStart, blockEnd - prevStart, block + brk + prev));
                int shift = blockStart - prevStart;
                foreach (var s in _sels.Where(s => InBlock(snap, s, first, last)))
                    newSels.Add(new Sel(s.Anchor - shift, s.Caret - shift));
            }
            else
            {
                int nextStart = snap.GetLineStart(last + 1), nextEnd = snap.GetLineEnd(last + 1);
                var next = snap.GetText(nextStart, nextEnd - nextStart);
                var brk = snap.GetText(blockEnd, nextStart - blockEnd);
                edits.Add(new TextEdit(blockStart, nextEnd - blockStart, next + brk + block));
                int shift = next.Length + brk.Length;
                foreach (var s in _sels.Where(s => InBlock(snap, s, first, last)))
                    newSels.Add(new Sel(s.Anchor + shift, s.Caret + shift));
            }
        }
        _buffer.BreakUndoCoalescing();
        ApplyWithSelections(edits, newSels);
    }

    static bool InBlock(TextSnapshot snap, Sel s, int first, int last)
    {
        int l = snap.GetLineFromOffset(s.Start);
        return l >= first && l <= last;
    }

    /// <summary>Applies edits and sets selections already computed in the new text's offsets.</summary>
    void ApplyWithSelections(List<TextEdit> edits, List<Sel> newSels)
    {
        if (edits.Count == 0) return;
        var before = SelectionState();
        _applyingOwnEdit = true;
        TextChangedEventArgs? args;
        try { args = _buffer.Apply(edits, UndoCoalescing.None, before); }
        finally { _applyingOwnEdit = false; }
        if (args == null) return;
        int len = Snapshot.Length;
        if (newSels.Count == 0) newSels.Add(new Sel(0, 0));
        SetSelectionsCore([.. newSels.Select(s => new Sel(Math.Clamp(s.Anchor, 0, len), Math.Clamp(s.Caret, 0, len)))], Math.Min(_primary, newSels.Count - 1), raise: false);
        _buffer.SetUndoStateAfter(SelectionState());
        ScrollToCaret();
        RaiseCaretMoved();
    }

    // =====================================================================
    // Multiple carets
    // =====================================================================

    /// <summary>
    /// Ctrl+D: with an empty primary selection selects the word at the caret; otherwise adds the next occurrence of
    /// the selected text as a new (primary) selection.
    /// </summary>
    public void SelectNextOccurrence()
    {
        var snap = Snapshot;
        var primary = _sels[_primary];
        if (primary.IsEmpty)
        {
            var p = snap.GetPosition(primary.Caret);
            var (a, b) = WordAt(snap.GetLineText(p.Line), p.Column);
            if (a == b) return;
            int ls = snap.GetLineStart(p.Line);
            var list = _sels.ToList();
            list[_primary] = new Sel(ls + a, ls + b);
            SetSelectionsCore(list, _primary);
            return;
        }
        var needle = snap.GetText(primary.Start, primary.End - primary.Start);
        var text = snap.GetText();
        int from = _sels.Max(s => s.End);
        for (int pass = 0; pass < 2; pass++)
        {
            int at = text.IndexOf(needle, pass == 0 ? from : 0, StringComparison.Ordinal);
            while (at >= 0 && _sels.Any(s => s.Start == at && s.End == at + needle.Length))
                at = text.IndexOf(needle, at + 1, StringComparison.Ordinal);
            if (at >= 0)
            {
                bool forward = primary.Caret >= primary.Anchor;
                AddSelection(forward ? new TextSelection(at, at + needle.Length) : new TextSelection(at + needle.Length, at));
                return;
            }
        }
    }

    /// <summary>Puts a caret at the end of every line the selections cover (Shift+Alt+I).</summary>
    public void AddCaretsToLineEnds()
    {
        var snap = Snapshot;
        var list = SelectedLines().Select(l => { int e = snap.GetLineEnd(l); return new Sel(e, e); }).ToList();
        if (list.Count > 0) SetSelectionsCore(list, list.Count - 1);
    }

    // =====================================================================
    // Clipboard
    // =====================================================================

    /// <summary>Copies the selections (joined by line breaks), or the caret's whole line when nothing is selected.</summary>
    public void CopyToClipboard()
    {
        var text = ClipboardText(out _);
        if (text.Length > 0) Clipboard.SetText(text);
    }

    string ClipboardText(out bool wholeLine)
    {
        var snap = Snapshot;
        wholeLine = false;
        if (_sels.All(s => s.IsEmpty))
        {
            wholeLine = true;
            var lines = _sels.Select(s => snap.GetLineFromOffset(s.Caret)).Distinct();
            return string.Concat(lines.Select(l => snap.GetLineText(l) + _buffer.NewLine));
        }
        return string.Join(_buffer.NewLine, _sels.Where(s => !s.IsEmpty).Select(s => snap.GetText(s.Start, s.End - s.Start)));
    }

    public void CutToClipboard()
    {
        if (ReadOnly) return;
        var text = ClipboardText(out bool wholeLine);
        if (text.Length == 0) return;
        Clipboard.SetText(text);
        _buffer.BreakUndoCoalescing();
        if (wholeLine)
        {
            var snap = Snapshot;
            var edits = new List<TextEdit>();
            foreach (var l in _sels.Select(s => snap.GetLineFromOffset(s.Caret)).Distinct())
            {
                int s0 = snap.GetLineStart(l), e0 = snap.GetLineEndIncludingBreak(l);
                if (e0 == s0 && l > 0) s0 = snap.GetLineEnd(l - 1);    // the last, empty line: take the break before it
                edits.Add(TextEdit.Delete(s0, e0 - s0));
            }
            EditMapped(edits);
        }
        else DeleteBackward();
    }

    /// <summary>Pastes at every caret; when the clipboard has one line per caret, each caret gets its own line.</summary>
    public void PasteFromClipboard()
    {
        if (ReadOnly || Clipboard.GetText() is not { Length: > 0 } text) return;
        text = NormalizeNewLines(text);
        var nl = _buffer.NewLine;
        _buffer.BreakUndoCoalescing();
        var parts = text.Split(nl);
        if (parts.Length > 1 && parts[^1].Length == 0) parts = parts[..^1];
        var edits = new List<(TextEdit, int, int)>();
        bool distribute = _sels.Count > 1 && parts.Length == _sels.Count;
        for (int i = 0; i < _sels.Count; i++)
        {
            var s = _sels[i];
            var t = distribute ? parts[i] : text;
            edits.Add((new TextEdit(s.Start, s.End - s.Start, t), t.Length, t.Length));
        }
        ApplyOwn(edits, UndoCoalescing.None);
        _buffer.BreakUndoCoalescing();
    }

    // =====================================================================
    // Brackets
    // =====================================================================

    /// <summary>The bracket next to the primary caret and its partner (offsets), if any.</summary>
    (int A, int B)? MatchingBracketPair()
    {
        var snap = Snapshot;
        int caret = CaretOffset;
        foreach (int at in new[] { caret, caret - 1 })
        {
            if (at < 0 || at >= snap.Length) continue;
            char c = snap[at];
            int dir; char open, close;
            switch (c)
            {
                case '(': case ')': open = '('; close = ')'; break;
                case '[': case ']': open = '['; close = ']'; break;
                case '{': case '}': open = '{'; close = '}'; break;
                default: continue;
            }
            dir = c == open ? 1 : -1;
            int depth = 0;
            for (int i = at, n = 0; i >= 0 && i < snap.Length && n < 20000; i += dir, n++)
            {
                char x = snap[i];
                if (x == open) depth += dir;
                else if (x == close) depth -= dir;
                if (depth == 0) return (at, i);
            }
        }
        return null;
    }
}
