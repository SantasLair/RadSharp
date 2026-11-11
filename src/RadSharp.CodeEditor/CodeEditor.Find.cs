using System.Text.RegularExpressions;
using RadSharp.CodeEditing;
using RadSharp.Drawing;

namespace RadSharp;

public partial class CodeEditor
{
    string _findText = "";
    bool _matchCase, _wholeWord, _useRegex;
    bool _findDirty = true;
    List<(int Start, int Length)> _matches = [];
    string? _findError;
    FindReplaceBar? _findBar;

    /// <summary>The most matches the find bar counts and highlights.</summary>
    public const int MaxFindMatches = 10_000;

    /// <summary>What the find bar searches for (setting it updates the highlights).</summary>
    public string FindText { get => _findText; set { _findText = value ?? ""; _findDirty = true; Invalidate(); } }
    public bool FindMatchCase { get => _matchCase; set { _matchCase = value; _findDirty = true; Invalidate(); } }
    public bool FindWholeWord { get => _wholeWord; set { _wholeWord = value; _findDirty = true; Invalidate(); } }
    public bool FindUseRegex { get => _useRegex; set { _useRegex = value; _findDirty = true; Invalidate(); } }

    /// <summary>Why the pattern can't be used (an invalid regular expression), or null.</summary>
    public string? FindError { get { EnsureMatches(); return _findError; } }

    /// <summary>Every match of <see cref="FindText"/> in the current text (up to <see cref="MaxFindMatches"/>).</summary>
    public IReadOnlyList<(int Start, int Length)> FindMatches { get { EnsureMatches(); return _matches; } }

    /// <summary>The index in <see cref="FindMatches"/> of the match the primary selection covers, or -1.</summary>
    public int CurrentMatchIndex
    {
        get
        {
            EnsureMatches();
            var s = _sels[_primary];
            int i = _matches.BinarySearch((s.Start, s.End - s.Start), Comparer<(int Start, int Length)>.Create((a, b) => a.Start.CompareTo(b.Start)));
            return i >= 0 && _matches[i].Length == s.End - s.Start ? i : -1;
        }
    }

    public bool FindBarVisible => _findBar is { Visible: true };
    internal FindReplaceBar? FindBar => _findBar;

    Regex? BuildRegex()
    {
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant | (_matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        var pattern = _useRegex ? _findText : Regex.Escape(_findText);
        if (_wholeWord) pattern = $@"(?<![\w]){pattern}(?![\w])";
        return new Regex(pattern, options, TimeSpan.FromSeconds(1));
    }

    void EnsureMatches()
    {
        if (!_findDirty) return;
        _findDirty = false;
        _matches = [];
        _findError = null;
        if (_findText.Length == 0) return;
        var text = Snapshot.GetText();
        try
        {
            if (!_useRegex && !_wholeWord)
            {
                var cmp = _matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                for (int at = text.IndexOf(_findText, cmp); at >= 0 && _matches.Count < MaxFindMatches; at = text.IndexOf(_findText, at + _findText.Length, cmp))
                    _matches.Add((at, _findText.Length));
                return;
            }
            var regex = BuildRegex()!;
            for (var m = regex.Match(text); m.Success && _matches.Count < MaxFindMatches; m = m.NextMatch())
            {
                if (m.Length == 0) continue;   // empty matches (^, \b...) highlight nothing
                _matches.Add((m.Index, m.Length));
            }
        }
        catch (ArgumentException e) { _findError = e.Message; _matches = []; }
        catch (RegexMatchTimeoutException) { _findError = "The search took too long."; }
    }

    /// <summary>Matches overlapping [from, to] when the find bar is showing: (start, length, is the current one).</summary>
    List<(int Start, int Length, bool Current)> FindMatchesIn(int from, int to)
    {
        var result = new List<(int, int, bool)>();
        if (!FindBarVisible || _findText.Length == 0) return result;
        EnsureMatches();
        int current = CurrentMatchIndex;
        int lo = 0, hi = _matches.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (_matches[mid].Start + _matches[mid].Length < from) lo = mid + 1; else hi = mid; }
        for (int i = lo; i < _matches.Count && _matches[i].Start <= to; i++)
            result.Add((_matches[i].Start, _matches[i].Length, i == current));
        return result;
    }

    /// <summary>Selects the next match after the primary selection (wrapping). Returns false if there are none.</summary>
    public bool FindNext()
    {
        EnsureMatches();
        if (_matches.Count == 0) return false;
        int from = _sels[_primary].End;
        var cur = _sels[_primary];
        int i = _matches.FindIndex(m => m.Start >= from && !(m.Start == cur.Start && m.Length == cur.End - cur.Start));
        if (i < 0) i = 0;
        SelectMatch(i);
        return true;
    }

    public bool FindPrevious()
    {
        EnsureMatches();
        if (_matches.Count == 0) return false;
        int from = _sels[_primary].Start;
        int i = _matches.FindLastIndex(m => m.Start < from);
        if (i < 0) i = _matches.Count - 1;
        SelectMatch(i);
        return true;
    }

    void SelectMatch(int i)
    {
        var (s, len) = _matches[i];
        RevealOffset(s);
        _buffer.BreakUndoCoalescing();
        SetSelectionsCore([new Sel(s, s + len)], 0);
        ScrollToCaret(center: true);
    }

    /// <summary>Replaces the current match (if the selection is on one) and moves to the next. Regex replacements may use $1 etc.</summary>
    public bool ReplaceCurrent(string replacement)
    {
        if (ReadOnly) return false;
        int i = CurrentMatchIndex;
        if (i < 0) return FindNext();
        var (s, len) = _matches[i];
        var text = ReplacementFor(s, len, replacement ?? "");
        _buffer.BreakUndoCoalescing();
        ApplyOwn([(new TextEdit(s, len, text), text.Length, text.Length)], UndoCoalescing.None);
        EnsureMatches();
        FindNext();
        return true;
    }

    /// <summary>Replaces every match as one undo step; returns how many were replaced.</summary>
    public int ReplaceAll(string replacement)
    {
        if (ReadOnly) return 0;
        EnsureMatches();
        if (_matches.Count == 0) return 0;
        var edits = _matches.Select(m => new TextEdit(m.Start, m.Length, ReplacementFor(m.Start, m.Length, replacement ?? ""))).ToList();
        int count = edits.Count;
        _buffer.BreakUndoCoalescing();
        EditMapped(edits);
        return count;
    }

    string ReplacementFor(int start, int length, string replacement)
    {
        if (!_useRegex) return replacement;
        try
        {
            var regex = BuildRegex()!;
            var m = regex.Match(Snapshot.GetText(), start);
            return m.Success && m.Index == start && m.Length == length ? m.Result(replacement) : replacement;
        }
        catch (ArgumentException) { return replacement; }
    }

    /// <summary>Opens the find bar (with the replace row if <paramref name="replace"/>), seeded with a one-line selection, and focuses it.</summary>
    public void ShowFind(bool replace = false)
    {
        if (_findBar == null)
        {
            _findBar = new FindReplaceBar(this);
            Add(_findBar);
        }
        var sel = _sels[_primary];
        if (!sel.IsEmpty && sel.End - sel.Start < 200)
        {
            var t = Snapshot.GetText(sel.Start, sel.End - sel.Start);
            if (!t.Contains('\n')) FindText = t;
        }
        _findBar.ShowReplace = replace;
        _findBar.Visible = true;
        _findBar.SyncFromEditor();
        PositionFindBar();
        OwningForm?.PerformLayout();
        _findBar.FocusFind();
        Invalidate();
    }

    public void HideFind()
    {
        if (_findBar == null || !_findBar.Visible) return;
        bool hadFocus = OwningForm?.FocusedControl is { } f && f.IsSelfOrDescendantOf(_findBar);
        _findBar.Visible = false;
        if (hadFocus) Focus();
        Invalidate();
    }

    internal void PositionFindBar()
    {
        if (_findBar == null) return;
        int w = Math.Min(_findBar.PreferredWidth, Math.Max(Px(120), Width - 2 * Border - GutterWidth - Px(8)));
        int h = _findBar.PreferredHeight;
        int right = Width - Border - (ShowVBar ? BarSize : 0) - Px(12);
        _findBar.SetBounds(Math.Max(Border, right - w), Border, w, h);
    }
}

/// <summary>The editor's find/replace bar: a search box with case, whole-word and regex toggles, a match count,
/// previous/next/close buttons, and (Ctrl+H) a replace box with Replace and Replace All.</summary>
internal sealed class FindReplaceBar : Control
{
    readonly CodeEditor _editor;
    readonly Edit _find = new() { Name = "txtFind", Placeholder = "Find" };
    readonly Edit _replace = new() { Name = "txtReplace", Placeholder = "Replace" };
    bool _showReplace;
    bool _syncing;
    int _hot = -1;

    internal enum Btn { Case, Word, Regex, Prev, Next, Close, Replace, ReplaceAll }

    public FindReplaceBar(CodeEditor editor)
    {
        _editor = editor;
        Name = "findBar";
        Cursor = Cursor.Default;
        _find.AutoSelect = true;
        Add(_find);
        Add(_replace);
        _find.Change += _ => { if (!_syncing) _editor.FindText = _find.Text; };
        _find.KeyDown += (_, e) => OnBoxKey(e, replaceBox: false);
        _replace.KeyDown += (_, e) => OnBoxKey(e, replaceBox: true);
    }

    public Edit FindBox => _find;
    public Edit ReplaceBox => _replace;

    public bool ShowReplace
    {
        get => _showReplace;
        set { _showReplace = value; _replace.Visible = value; _editor.PositionFindBar(); Layout(); Invalidate(); }
    }

    public int PreferredWidth => Px(440);
    public int PreferredHeight => _showReplace ? Px(64) : Px(34);

    public void SyncFromEditor()
    {
        _syncing = true;
        try { if (_find.Text != _editor.FindText) _find.Text = _editor.FindText; }
        finally { _syncing = false; }
        Layout();
    }

    public void FocusFind()
    {
        _find.Focus();
        _find.SelectAll();
    }

    protected override void OnResize()
    {
        Layout();
        base.OnResize();
    }

    void Layout()
    {
        int boxW = Math.Max(Px(60), Width - Px(440 - 220));
        _find.SetBounds(Px(6), Px(5), boxW, Px(24));
        _replace.SetBounds(Px(6), Px(35), boxW, Px(24));
        _replace.Visible = _showReplace;
    }

    int BoxRight => _find.Left + _find.Width;

    Rect ButtonRect(Btn b)
    {
        int x = BoxRight + Px(6), y = Px(5), s = Px(24);
        return b switch
        {
            Btn.Case => new Rect(x, y, s, s),
            Btn.Word => new Rect(x + Px(26), y, s, s),
            Btn.Regex => new Rect(x + Px(52), y, s, s),
            Btn.Prev => new Rect(Width - Px(70), y, Px(22), s),
            Btn.Next => new Rect(Width - Px(47), y, Px(22), s),
            Btn.Close => new Rect(Width - Px(24), y, Px(20), s),
            Btn.Replace => new Rect(x, Px(35), Px(70), s),
            _ => new Rect(x + Px(74), Px(35), Px(44), s),
        };
    }

    IEnumerable<Btn> Buttons => _showReplace
        ? [Btn.Case, Btn.Word, Btn.Regex, Btn.Prev, Btn.Next, Btn.Close, Btn.Replace, Btn.ReplaceAll]
        : [Btn.Case, Btn.Word, Btn.Regex, Btn.Prev, Btn.Next, Btn.Close];

    Btn? ButtonAt(int x, int y)
    {
        foreach (var b in Buttons)
        {
            var r = ButtonRect(b);
            if (x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height) return b;
        }
        return null;
    }

    public void Press(Btn b)
    {
        switch (b)
        {
            case Btn.Case: _editor.FindMatchCase = !_editor.FindMatchCase; break;
            case Btn.Word: _editor.FindWholeWord = !_editor.FindWholeWord; break;
            case Btn.Regex: _editor.FindUseRegex = !_editor.FindUseRegex; break;
            case Btn.Prev: _editor.FindPrevious(); break;
            case Btn.Next: _editor.FindNext(); break;
            case Btn.Close: _editor.HideFind(); return;
            case Btn.Replace: _editor.ReplaceCurrent(_replace.Text); break;
            case Btn.ReplaceAll: _editor.ReplaceAll(_replace.Text); break;
        }
        Invalidate();
    }

    void OnBoxKey(KeyEventArgs e, bool replaceBox)
    {
        var cmd = Shortcut.CommandModifier;
        switch (e.Key)
        {
            case Key.Enter when e.Modifiers is KeyModifiers.None or KeyModifiers.Shift:
                if (replaceBox) Press(Btn.Replace);
                else if (e.Shift) _editor.FindPrevious(); else _editor.FindNext();
                e.Handled = true;
                break;
            case Key.Enter when e.Modifiers == (cmd | KeyModifiers.Alt) && replaceBox:
                Press(Btn.ReplaceAll);
                e.Handled = true;
                break;
            case Key.Escape when e.Modifiers == KeyModifiers.None:
                _editor.HideFind();
                e.Handled = true;
                break;
            case Key.F3:
                if (e.Shift) _editor.FindPrevious(); else _editor.FindNext();
                e.Handled = true;
                break;
            case Key.C when e.Modifiers == KeyModifiers.Alt: Press(Btn.Case); e.Handled = true; break;
            case Key.W when e.Modifiers == KeyModifiers.Alt: Press(Btn.Word); e.Handled = true; break;
            case Key.R when e.Modifiers == KeyModifiers.Alt: Press(Btn.Regex); e.Handled = true; break;
            case Key.H when e.Modifiers == cmd: ShowReplace = !ShowReplace; _editor.ShowFind(ShowReplace); e.Handled = true; break;
            case Key.F when e.Modifiers == cmd: _find.Focus(); _find.SelectAll(); e.Handled = true; break;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButton.Left && ButtonAt(e.X, e.Y) is { } b) Press(b);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hot = ButtonAt(e.X, e.Y) is { } b ? (int)b : -1;
        if (hot != _hot) { _hot = hot; Invalidate(); }
    }

    protected override void OnMouseLeave()
    {
        _hot = -1;
        Invalidate();
        base.OnMouseLeave();
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var colors = _editor.EffectiveColors;
        var r = new Rect(0, 0, Width, Height);
        canvas.FillRect(new Rect(Px(2), Px(2), Width, Height), new Color(0, 0, 0, 28));
        canvas.FillRect(r, colors.PopupBackground);
        canvas.DrawBorder(r, colors.PopupBorder, 1);
        var font = canvas.DefaultFont;
        foreach (var b in Buttons)
        {
            var br = ButtonRect(b);
            bool on = b switch { Btn.Case => _editor.FindMatchCase, Btn.Word => _editor.FindWholeWord, Btn.Regex => _editor.FindUseRegex, _ => false };
            if (on) canvas.FillRoundRect(br, Px(3), t.Accent.Blend(colors.PopupBackground, 0.65));
            else if (_hot == (int)b) canvas.FillRoundRect(br, Px(3), colors.PopupSelection);
            if (b is Btn.Replace or Btn.ReplaceAll) canvas.DrawRoundBorder(br, Px(3), colors.PopupBorder, 1);
            string label = b switch
            {
                Btn.Case => "Aa", Btn.Word => "W", Btn.Regex => ".*", Btn.Prev => "↑", Btn.Next => "↓",
                Btn.Close => "×", Btn.Replace => "Replace", _ => "All",
            };
            canvas.DrawText(label, font, colors.PopupText, br, TextAlign.Center, VerticalAlign.Center);
        }
        // Match count between the toggles and the arrows.
        int from = ButtonRect(Btn.Regex).X + ButtonRect(Btn.Regex).Width + Px(6);
        int to = ButtonRect(Btn.Prev).X - Px(4);
        string count;
        if (_editor.FindText.Length == 0) count = "";
        else if (_editor.FindError != null) count = "Invalid";
        else if (_editor.FindMatches.Count == 0) count = "No results";
        else
        {
            int cur = _editor.CurrentMatchIndex;
            int n = _editor.FindMatches.Count;
            count = cur >= 0 ? $"{cur + 1} of {n}" : $"{n} found";
        }
        if (to > from)
            canvas.DrawText(count, font, _editor.FindError != null ? colors.ErrorSquiggle : colors.PopupDetail,
                new Rect(from, Px(5), to - from, Px(24)), TextAlign.Left, VerticalAlign.Center, ellipsis: true);
    }
}
