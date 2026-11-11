namespace RadSharp.CodeEditing;

/// <summary>
/// A small lexical C# colouriser, the editor's default <see cref="ITokenSource"/>: keywords, comments, strings
/// (regular, verbatim, interpolated, raw), characters, numbers, preprocessor lines, operators and punctuation.
/// It knows nothing about symbols; the language service replaces it with Roslyn's classifier. State that spans
/// lines (block comments, verbatim and raw strings) is cached per line and recomputed lazily from the first
/// changed line, so an edit costs only the lines being painted.
/// </summary>
public sealed class CSharpTokenSource : ITokenSource
{
    static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "byte", "char", "checked", "class", "const", "decimal", "delegate", "double",
        "enum", "event", "explicit", "extern", "false", "fixed", "float", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "sbyte", "sealed", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "this", "true", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "add", "and", "alias", "ascending", "args", "async", "await", "by",
        "descending", "dynamic", "equals", "file", "from", "get", "global", "group", "init", "into", "join", "let",
        "managed", "nameof", "nint", "not", "notnull", "nuint", "on", "or", "orderby", "partial", "record", "remove",
        "required", "scoped", "select", "set", "unmanaged", "value", "var", "where", "with", "field",
    };

    static readonly HashSet<string> ControlKeywords = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "continue", "default", "do", "else", "finally", "for", "foreach", "goto", "if",
        "return", "switch", "throw", "try", "while", "yield", "when",
    };

    enum State : byte { Normal, BlockComment, Verbatim, Raw }

    readonly record struct LineState(State State, int RawQuotes);

    TextSnapshot? _snapshot;
    readonly List<LineState> _starts = [new(State.Normal, 0)];    // valid start state for lines 0.._starts.Count-1
    readonly Dictionary<int, TokenSpan[]> _tokens = [];

    public event EventHandler<SourceChangedEventArgs>? Changed { add { } remove { } }

    public IReadOnlyList<TokenSpan>? GetTokens(TextSnapshot snapshot, int line)
    {
        Sync(snapshot);
        if (_tokens.TryGetValue(line, out var cached)) return cached;
        // Make sure the start states up to this line are known.
        while (_starts.Count <= line)
        {
            int l = _starts.Count - 1;
            var end = Lex(snapshot.GetLineText(l), _starts[l], null);
            _starts.Add(end);
        }
        var spans = new List<TokenSpan>();
        Lex(snapshot.GetLineText(line), _starts[line], spans);
        var result = spans.ToArray();
        if (_tokens.Count > 4096) _tokens.Clear();
        _tokens[line] = result;
        return result;
    }

    /// <summary>Drops cached state from the first line any edit since the cached snapshot touched.</summary>
    void Sync(TextSnapshot snapshot)
    {
        if (_snapshot == snapshot) return;
        int firstChanged = 0;
        if (_snapshot != null && _snapshot.Buffer == snapshot.Buffer && _snapshot.Version < snapshot.Version)
        {
            firstChanged = int.MaxValue;
            for (var s = _snapshot; s != null && s.Version < snapshot.Version; s = s.Next)
            {
                var changes = s.ChangesToNext;
                if (changes == null || changes.Count == 0) continue;
                firstChanged = Math.Min(firstChanged, s.GetLineFromOffset(changes[0].OldStart));
            }
            if (firstChanged == int.MaxValue) firstChanged = 0;
        }
        _snapshot = snapshot;
        if (_starts.Count > firstChanged + 1) _starts.RemoveRange(firstChanged + 1, _starts.Count - firstChanged - 1);
        if (firstChanged == 0) { _starts.Clear(); _starts.Add(new(State.Normal, 0)); }
        if (firstChanged == 0) _tokens.Clear();
        else foreach (var key in _tokens.Keys.Where(k => k >= firstChanged).ToList()) _tokens.Remove(key);
    }

    static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '@';
    static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Lexes one line from <paramref name="start"/>; adds spans if <paramref name="spans"/> is given and returns the state at the end.</summary>
    static LineState Lex(string s, LineState start, List<TokenSpan>? spans)
    {
        int i = 0, n = s.Length;
        void Add(int from, int to, string kind) { if (spans != null && to > from) spans.Add(new TokenSpan(from, to - from, kind)); }

        // Continue a construct from the previous line.
        switch (start.State)
        {
            case State.BlockComment:
            {
                int end = s.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0) { Add(0, n, Classifications.Comment); return start; }
                Add(0, end + 2, Classifications.Comment);
                i = end + 2;
                break;
            }
            case State.Verbatim:
            {
                int end = VerbatimEnd(s, 0);
                if (end < 0) { Add(0, n, Classifications.VerbatimString); return start; }
                Add(0, end, Classifications.VerbatimString);
                i = end;
                break;
            }
            case State.Raw:
            {
                int end = RawEnd(s, 0, start.RawQuotes);
                if (end < 0) { Add(0, n, Classifications.String); return start; }
                Add(0, end, Classifications.String);
                i = end;
                break;
            }
        }

        // A preprocessor directive takes the whole line.
        int first = i;
        while (first < n && char.IsWhiteSpace(s[first])) first++;
        if (i == 0 && first < n && s[first] == '#')
        {
            int w = first + 1;
            while (w < n && char.IsLetter(s[w])) w++;
            Add(first, w, Classifications.Preprocessor);
            int comment = s.IndexOf("//", w, StringComparison.Ordinal);
            Add(w, comment < 0 ? n : comment, Classifications.PreprocessorText);
            if (comment >= 0) Add(comment, n, Classifications.Comment);
            return new(State.Normal, 0);
        }

        while (i < n)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            int from = i;

            if (c == '/' && i + 1 < n && s[i + 1] == '/')
            {
                Add(i, n, s.AsSpan(i).StartsWith("///") ? "xml doc comment" : Classifications.Comment);
                return new(State.Normal, 0);
            }
            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) { Add(i, n, Classifications.Comment); return new(State.BlockComment, 0); }
                Add(i, end + 2, Classifications.Comment);
                i = end + 2;
                continue;
            }

            // Strings: prefixes $, @, $@, @$, $$..., then quotes.
            int p = i;
            bool verbatim = false;
            while (p < n && (s[p] == '$' || s[p] == '@'))
            {
                if (s[p] == '@') verbatim = true;
                p++;
            }
            if (p < n && s[p] == '"')
            {
                int quotes = 0;
                while (p + quotes < n && s[p + quotes] == '"') quotes++;
                if (quotes >= 3 && !verbatim)
                {
                    int end = RawEnd(s, p + quotes, quotes);
                    if (end < 0) { Add(from, n, Classifications.String); return new(State.Raw, quotes); }
                    Add(from, end, Classifications.String);
                    i = end;
                    continue;
                }
                if (verbatim)
                {
                    int end = VerbatimEnd(s, p + 1);
                    if (end < 0) { Add(from, n, Classifications.VerbatimString); return new(State.Verbatim, 0); }
                    Add(from, end, Classifications.VerbatimString);
                    i = end;
                    continue;
                }
                int q = p + 1;
                while (q < n && s[q] != '"') q += s[q] == '\\' ? 2 : 1;
                q = Math.Min(n, q + 1);
                Add(from, q, Classifications.String);
                i = q;
                continue;
            }

            if (c == '\'')
            {
                int q = i + 1;
                while (q < n && s[q] != '\'') q += s[q] == '\\' ? 2 : 1;
                q = Math.Min(n, q + 1);
                Add(from, q, Classifications.String);
                i = q;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(s[i + 1])))
            {
                int q = i + 1;
                while (q < n && (char.IsAsciiLetterOrDigit(s[q]) || s[q] == '_' || s[q] == '.'
                                 || ((s[q] == '+' || s[q] == '-') && (s[q - 1] == 'e' || s[q - 1] == 'E') && !s.AsSpan(i).StartsWith("0x", StringComparison.OrdinalIgnoreCase))))
                {
                    if (s[q] == '.' && !(q + 1 < n && char.IsAsciiDigit(s[q + 1]))) break;
                    q++;
                }
                Add(from, q, Classifications.Number);
                i = q;
                continue;
            }

            if (IsIdentStart(c))
            {
                int q = i + 1;
                while (q < n && IsIdentPart(s[q])) q++;
                var word = s.AsSpan(c == '@' ? i + 1 : i, q - (c == '@' ? i + 1 : i)).ToString();
                if (c != '@' && ControlKeywords.Contains(word)) Add(from, q, Classifications.ControlKeyword);
                else if (c != '@' && Keywords.Contains(word)) Add(from, q, Classifications.Keyword);
                else Add(from, q, Classifications.Identifier);
                i = q;
                continue;
            }

            Add(from, i + 1, "(){}[];,.".Contains(c) ? Classifications.Punctuation : Classifications.Operator);
            i++;
        }
        return new(State.Normal, 0);
    }

    /// <summary>The index just past the closing quote of a verbatim string ("" is an escaped quote), or -1.</summary>
    static int VerbatimEnd(string s, int i)
    {
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                if (i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return -1;
    }

    /// <summary>The index just past a run of <paramref name="quotes"/> closing quotes, or -1.</summary>
    static int RawEnd(string s, int i, int quotes)
    {
        var close = new string('"', quotes);
        int at = s.IndexOf(close, i, StringComparison.Ordinal);
        if (at < 0) return -1;
        int end = at + quotes;
        while (end < s.Length && s[end] == '"') end++;
        return end;
    }
}

/// <summary>
/// The default <see cref="IFoldingSource"/>: blocks between braces that span lines (skipping braces inside
/// comments and strings, as the <see cref="CSharpTokenSource"/> sees them) and <c>#region</c>/<c>#endregion</c>.
/// </summary>
public sealed class BraceFoldingSource : IFoldingSource
{
    TextSnapshot? _snapshot;
    IReadOnlyList<FoldRange>? _ranges;
    readonly CSharpTokenSource _lexer = new();

    public event EventHandler<SourceChangedEventArgs>? Changed { add { } remove { } }

    public IReadOnlyList<FoldRange>? GetFoldRanges(TextSnapshot snapshot)
    {
        if (_snapshot == snapshot && _ranges != null) return _ranges;
        var ranges = new List<FoldRange>();
        var braces = new Stack<int>();
        var regions = new Stack<(int Line, string Text)>();
        for (int line = 0; line < snapshot.LineCount; line++)
        {
            var text = snapshot.GetLineText(line);
            var trimmed = text.AsSpan().TrimStart();
            if (trimmed.StartsWith("#region")) { regions.Push((line, trimmed.ToString())); continue; }
            if (trimmed.StartsWith("#endregion"))
            {
                if (regions.Count > 0) { var (start, label) = regions.Pop(); if (line > start) ranges.Add(new FoldRange(start, line, label)); }
                continue;
            }
            if (text.IndexOfAny(['{', '}']) < 0) { _lexer.GetTokens(snapshot, line); continue; }
            var tokens = _lexer.GetTokens(snapshot, line) ?? [];
            foreach (var t in tokens)
            {
                if (t.Classification != Classifications.Punctuation || t.Length != 1) continue;
                char c = text[t.Start];
                // A brace alone on its line folds from the header line above it ("void F()" / "{").
                if (c == '{') braces.Push(line > 0 && text.AsSpan(0, t.Start).IsWhiteSpace() && !snapshot.GetLineText(line - 1).AsSpan().IsWhiteSpace() ? line - 1 : line);
                else if (c == '}' && braces.Count > 0)
                {
                    int start = braces.Pop();
                    if (line > start) ranges.Add(new FoldRange(start, line));
                }
            }
        }
        ranges.Sort((a, b) => a.StartLine != b.StartLine ? a.StartLine.CompareTo(b.StartLine) : b.EndLine.CompareTo(a.EndLine));
        // One range per start line (the outermost).
        var unique = new List<FoldRange>();
        foreach (var r in ranges) if (unique.Count == 0 || unique[^1].StartLine != r.StartLine) unique.Add(r);
        _snapshot = snapshot;
        _ranges = unique;
        return unique;
    }
}
