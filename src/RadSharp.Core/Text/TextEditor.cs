using System.Globalization;
using System.Text;
using RadSharp.Drawing;

namespace RadSharp.Text;

/// <summary>
/// The editing model behind Edit and Memo: text, caret and selection anchor, grapheme- and
/// word-aware movement and deletion, a length limit and undo/redo. Positions are UTF-16 indices that
/// always sit on grapheme boundaries, so the caret never splits an emoji or a combining sequence.
/// </summary>
public sealed class TextEditor
{
    readonly record struct State(string Text, int Caret, int Anchor);
    enum EditKind { None, Typing, DeleteBack, DeleteForward, Other }

    string _text = "";
    int[] _bounds = [0];
    readonly List<State> _undo = [];
    readonly List<State> _redo = [];
    EditKind _lastEdit;
    const int UndoLimit = 500;

    public string Text => _text;
    /// <summary>Where the caret is; the moving end of the selection.</summary>
    public int Caret { get; private set; }
    /// <summary>The fixed end of the selection; equals <see cref="Caret"/> when nothing is selected.</summary>
    public int Anchor { get; private set; }

    public int SelectionStart => Math.Min(Caret, Anchor);
    public int SelectionLength => Math.Abs(Caret - Anchor);
    public bool HasSelection => Caret != Anchor;
    public string SelectedText => _text.Substring(SelectionStart, SelectionLength);

    /// <summary>Allows line breaks and tabs in the text.</summary>
    public bool Multiline { get; set; }

    /// <summary>The most graphemes (user-perceived characters) the text may hold; 0 = no limit.</summary>
    public int MaxLength { get; set; }

    /// <summary>Raised after the text changed (typing, deleting, undo, or <see cref="SetText"/>).</summary>
    public event Action? TextChanged;

    /// <summary>Raised after the caret or selection moved (including by text changes).</summary>
    public event Action? SelectionChanged;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public int GraphemeCount => _bounds.Length - 1;

    /// <summary>How many graphemes come before <paramref name="index"/> (snapped to a boundary).</summary>
    public int GraphemeIndexOf(int index)
    {
        int i = Array.BinarySearch(_bounds, Snap(index));
        return Math.Max(0, i);
    }

    /// <summary>The UTF-16 index where grapheme number <paramref name="grapheme"/> starts.</summary>
    public int BoundaryOfGrapheme(int grapheme) => _bounds[Math.Clamp(grapheme, 0, _bounds.Length - 1)];

    // ---- Whole-text operations ----

    /// <summary>Replaces the text (from code or a form file), puts the caret at the end and clears undo.</summary>
    public void SetText(string text)
    {
        text = Sanitize(text ?? "");
        if (MaxLength > 0) text = TakeGraphemes(text, MaxLength);
        _undo.Clear();
        _redo.Clear();
        _lastEdit = EditKind.None;
        bool changed = text != _text;
        Apply(text, text.Length, text.Length);
        if (changed) TextChanged?.Invoke();
    }

    public void Select(int start, int length)
    {
        int a = Snap(start), c = Snap(start + length);
        SetSelection(c, a);
    }

    public void SelectAll() => SetSelection(_text.Length, 0);

    /// <summary>Moves the caret to <paramref name="index"/>; with <paramref name="extend"/> the anchor stays put.</summary>
    public void MoveTo(int index, bool extend = false)
    {
        int c = Snap(index);
        SetSelection(c, extend ? Anchor : c);
    }

    void SetSelection(int caret, int anchor)
    {
        if (caret == Caret && anchor == Anchor) return;
        Caret = caret;
        Anchor = anchor;
        _lastEdit = EditKind.None;
        SelectionChanged?.Invoke();
    }

    // ---- Editing ----

    /// <summary>
    /// Replaces the selection with <paramref name="input"/> (typed or pasted). Line breaks become spaces
    /// and control characters are dropped when not multiline; text beyond <see cref="MaxLength"/> is cut.
    /// Returns true if the text changed.
    /// </summary>
    public bool Insert(string input) => Replace(input, EditKind.Typing);

    bool Replace(string input, EditKind kind)
    {
        input = Sanitize(input);
        int start = SelectionStart, end = start + SelectionLength;
        if (MaxLength > 0)
        {
            int kept = CountGraphemes(_text[..start]) + CountGraphemes(_text[end..]);
            input = TakeGraphemes(input, Math.Max(0, MaxLength - kept));
        }
        if (input.Length == 0 && start == end) return false;

        bool coalesce = kind == EditKind.Typing && _lastEdit == EditKind.Typing && start == end
                        && input.Length > 0 && !char.IsWhiteSpace(input[0]) && CountGraphemes(input) == 1;
        if (!coalesce) PushUndo();
        var text = string.Concat(_text.AsSpan(0, start), input, _text.AsSpan(end));
        Apply(text, start + input.Length, start + input.Length);
        _lastEdit = kind;
        TextChanged?.Invoke();
        return true;
    }

    /// <summary>Backspace: deletes the selection, or the grapheme (or word) before the caret.</summary>
    public bool DeleteBackward(bool word = false)
    {
        if (HasSelection) return Replace("", EditKind.Other);
        if (Caret == 0) return false;
        int from = word ? PreviousWordStart(Caret) : PreviousBoundary(Caret);
        return DeleteRange(from, Caret, EditKind.DeleteBack);
    }

    /// <summary>Delete: deletes the selection, or the grapheme (or word) after the caret.</summary>
    public bool DeleteForward(bool word = false)
    {
        if (HasSelection) return Replace("", EditKind.Other);
        if (Caret == _text.Length) return false;
        int to = word ? NextWordEnd(Caret) : NextBoundary(Caret);
        return DeleteRange(Caret, to, EditKind.DeleteForward);
    }

    /// <summary>Deletes from the caret back to the start of its line (⌘+Backspace on macOS).</summary>
    public bool DeleteToLineStart() =>
        HasSelection ? Replace("", EditKind.Other) : DeleteRange(LineStart(Caret), Caret, EditKind.Other);

    bool DeleteRange(int from, int to, EditKind kind)
    {
        if (from >= to) return false;
        if (_lastEdit != kind) PushUndo();
        Apply(string.Concat(_text.AsSpan(0, from), _text.AsSpan(to)), from, from);
        _lastEdit = kind;
        TextChanged?.Invoke();
        return true;
    }

    public bool Undo() => Step(_undo, _redo);
    public bool Redo() => Step(_redo, _undo);

    bool Step(List<State> from, List<State> to)
    {
        if (from.Count == 0) return false;
        to.Add(new State(_text, Caret, Anchor));
        var s = from[^1];
        from.RemoveAt(from.Count - 1);
        Apply(s.Text, s.Caret, s.Anchor);
        _lastEdit = EditKind.None;
        TextChanged?.Invoke();
        return true;
    }

    void PushUndo()
    {
        _undo.Add(new State(_text, Caret, Anchor));
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    void Apply(string text, int caret, int anchor)
    {
        if (!ReferenceEquals(text, _text))
        {
            _text = text;
            _bounds = TextLayout.GraphemeBoundaries(text);
        }
        Caret = Snap(caret);
        Anchor = Snap(anchor);
        SelectionChanged?.Invoke();
    }

    string Sanitize(string s)
    {
        if (s.Length == 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\r')
            {
                if (i + 1 < s.Length && s[i + 1] == '\n') continue;  // \r\n → \n
                c = '\n';
            }
            if (c == '\n') { sb.Append(Multiline ? '\n' : ' '); continue; }
            if (c == '\t') { sb.Append(Multiline ? '\t' : ' '); continue; }
            if (char.IsControl(c)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---- Positions ----

    /// <summary>The nearest grapheme boundary at or before <paramref name="index"/>.</summary>
    public int Snap(int index)
    {
        index = Math.Clamp(index, 0, _text.Length);
        int i = Array.BinarySearch(_bounds, index);
        return i >= 0 ? index : _bounds[~i - 1];
    }

    public int PreviousBoundary(int index)
    {
        int i = Array.BinarySearch(_bounds, Snap(index));
        return i > 0 ? _bounds[i - 1] : 0;
    }

    public int NextBoundary(int index)
    {
        int i = Array.BinarySearch(_bounds, Snap(index));
        return i + 1 < _bounds.Length ? _bounds[i + 1] : _text.Length;
    }

    enum CharClass { Space, Word, Punctuation }

    static CharClass Classify(string s, int i)
    {
        char c = s[i];
        if (char.IsWhiteSpace(c)) return CharClass.Space;
        if (char.IsLetterOrDigit(c) || c == '_' || char.IsSurrogate(c)
            || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            return CharClass.Word;
        return CharClass.Punctuation;
    }

    /// <summary>Ctrl+Left (⌥+Left on macOS): the start of the word before the caret.</summary>
    public int PreviousWordStart(int index)
    {
        int i = Snap(index);
        while (i > 0 && Classify(_text, i - 1) == CharClass.Space) i = PreviousBoundary(i);
        if (i == 0) return 0;
        var cls = Classify(_text, i - 1);
        while (i > 0 && Classify(_text, i - 1) == cls) i = PreviousBoundary(i);
        return i;
    }

    /// <summary>Ctrl+Right: the start of the next word (the end of the text if none).</summary>
    public int NextWordStart(int index)
    {
        int i = Snap(index), n = _text.Length;
        if (i < n)
        {
            var cls = Classify(_text, i);
            if (cls != CharClass.Space) while (i < n && Classify(_text, i) == cls) i = NextBoundary(i);
        }
        while (i < n && Classify(_text, i) == CharClass.Space && _text[i] != '\n') i = NextBoundary(i);
        return i;
    }

    /// <summary>The end of the word at or after the caret (for Ctrl+Delete).</summary>
    public int NextWordEnd(int index)
    {
        int i = Snap(index), n = _text.Length;
        while (i < n && Classify(_text, i) == CharClass.Space) i = NextBoundary(i);
        if (i == n) return n;
        var cls = Classify(_text, i);
        while (i < n && Classify(_text, i) == cls) i = NextBoundary(i);
        return i;
    }

    /// <summary>The word (or run of spaces/punctuation) around <paramref name="index"/>, for double-click.</summary>
    public (int Start, int End) WordAt(int index)
    {
        if (_text.Length == 0) return (0, 0);
        int i = Math.Min(Snap(index), _text.Length - 1);
        if (_text[i] == '\n' && i > 0) i--;
        var cls = Classify(_text, i);
        int s = i, e = i;
        while (s > 0 && Classify(_text, s - 1) == cls && _text[s - 1] != '\n') s = PreviousBoundary(s);
        while (e < _text.Length && Classify(_text, e) == cls && _text[e] != '\n') e = NextBoundary(e);
        return (s, e);
    }

    public int LineStart(int index)
    {
        int i = Math.Clamp(index, 0, _text.Length);
        int nl = i == 0 ? -1 : _text.LastIndexOf('\n', i - 1);
        return nl + 1;
    }

    public int LineEnd(int index)
    {
        int nl = _text.IndexOf('\n', Math.Clamp(index, 0, _text.Length));
        return nl < 0 ? _text.Length : nl;
    }

    static int CountGraphemes(string s) => s.Length == 0 ? 0 : TextLayout.GraphemeBoundaries(s).Length - 1;

    static string TakeGraphemes(string s, int count)
    {
        if (count <= 0) return "";
        var b = TextLayout.GraphemeBoundaries(s);
        return count >= b.Length - 1 ? s : s[..b[count]];
    }
}

/// <summary>Where Cut/Copy/Paste go. The SDL host installs the system clipboard; tests get an in-memory one.</summary>
public interface IClipboard
{
    string? GetText();
    void SetText(string text);
}

public static class Clipboard
{
    sealed class InMemory : IClipboard
    {
        string? _text;
        public string? GetText() => _text;
        public void SetText(string text) => _text = text;
    }

    public static IClipboard Current { get; set; } = new InMemory();

    public static string? GetText() => Current.GetText();
    public static void SetText(string text) => Current.SetText(text);
}
