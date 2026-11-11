using RadSharp.CodeEditing;
using RadSharp.Drawing;

namespace RadSharp;

/// <summary>
/// A code editor: a <see cref="TextBuffer"/> (piece table, snapshots, delta undo) shown in a virtualised view with a
/// gutter (line numbers, a glyph lane for breakpoints and diagnostics, fold markers), syntax colours, squiggles,
/// multiple carets, column selection, find/replace and a completion list. Colours, squiggles, folds and completions
/// come from the language-agnostic hooks <see cref="TokenSource"/>, <see cref="DecorationSources"/>,
/// <see cref="FoldingSource"/> and <see cref="CompletionSource"/>; a C# keyword colouriser and brace folding are
/// built in. Lines, columns and offsets in the API are 0-based UTF-16 indices; the gutter shows 1-based numbers.
/// </summary>
public partial class CodeEditor : Control
{
    TextBuffer _buffer;
    readonly List<Sel> _sels = [new(0, 0)];
    int _primary;
    bool _applyingOwnEdit;

    /// <summary>A selection plus the x the caret tries to keep when moving up and down (-1: none).</summary>
    record struct Sel(int Anchor, int Caret, float DesiredX = -1)
    {
        public readonly int Start => Math.Min(Anchor, Caret);
        public readonly int End => Math.Max(Anchor, Caret);
        public readonly bool IsEmpty => Anchor == Caret;
    }

    static CodeEditor() => CodeEditorFonts.EnsureRegistered();

    public CodeEditor()
    {
        TabStop = true;
        Cursor = Cursor.IBeam;
        SetDefaultSize(400, 300);
        _buffer = new TextBuffer();
        _buffer.Changed += OnBufferChanged;
        _tokenSource = _defaultTokens = new CSharpTokenSource();
        _foldingSource = new BraceFoldingSource();
        _vDriver = new ScrollBarDriver(_vAxis);
        _hDriver = new ScrollBarDriver(_hAxis);
        _decorationSources = new DecorationSourceList(this);
    }

    // =====================================================================
    // Published properties
    // =====================================================================

    /// <summary>The whole text. Setting it replaces the buffer's text and clears undo (like loading a file).</summary>
    [Published(Category = "Behavior")]
    public string Text
    {
        get => _buffer.GetText();
        set
        {
            _buffer.SetText(value ?? "");
            SetSelectionsCore([new Sel(0, 0)], 0);
            _scrollX = _scrollY = 0;
            Invalidate();
        }
    }

    [Published(Category = "Behavior")] public bool ReadOnly { get; set; }

    /// <summary>Columns per tab stop.</summary>
    [Published(Category = "Behavior")]
    public int TabSize { get => _tabSize; set { _tabSize = Math.Clamp(value, 1, 16); ClearLineCache(); Invalidate(); } }
    int _tabSize = 4;

    /// <summary>Tab and auto-indent insert spaces (otherwise tab characters).</summary>
    [Published(Category = "Behavior")] public bool InsertSpaces { get; set; } = true;

    /// <summary>Enter keeps the indentation of the line (and indents after an opening brace).</summary>
    [Published(Category = "Behavior")] public bool AutoIndent { get; set; } = true;

    /// <summary>Typing an opening bracket or quote also inserts the closing one; typing the closer steps over it.</summary>
    [Published(Category = "Behavior")] public bool AutoCloseBrackets { get; set; } = true;

    /// <summary>What Ctrl+/ adds at the start of lines; empty turns comment toggling off.</summary>
    [Published(Category = "Behavior")] public string LineCommentPrefix { get; set; } = "//";

    /// <summary>Open the completion list when an identifier starts (otherwise only on trigger characters and Ctrl+Space).</summary>
    [Published(Category = "Behavior")] public bool CompleteWhileTyping { get; set; } = true;

    /// <summary>The built-in colouriser: <c>"csharp"</c> (the default) or <c>"plain"</c>. Ignored once <see cref="TokenSource"/> is set.</summary>
    [Published(Category = "Behavior")]
    public string Language
    {
        get => _language;
        set
        {
            _language = value ?? "";
            bool csharp = !_language.Equals("plain", StringComparison.OrdinalIgnoreCase) && _language.Length > 0;
            if (_tokenSource == _defaultTokens || _tokenSource == null) { _tokenSource = csharp ? _defaultTokens : null; }
            if (_foldingSource is BraceFoldingSource or null) FoldingSource = csharp ? new BraceFoldingSource() : null;
            ClearLineCache();
            Invalidate();
        }
    }
    string _language = "csharp";

    [Published(Category = "Appearance")] public bool ShowLineNumbers { get => _showLineNumbers; set { _showLineNumbers = value; Invalidate(); } }
    bool _showLineNumbers = true;

    /// <summary>The gutter lane for breakpoints, bookmarks and diagnostic glyphs.</summary>
    [Published(Category = "Appearance")] public bool ShowGlyphMargin { get => _showGlyphMargin; set { _showGlyphMargin = value; Invalidate(); } }
    bool _showGlyphMargin = true;

    [Published(Category = "Appearance")] public bool ShowFolding { get => _showFolding; set { _showFolding = value; if (!value) ExpandAll(); Invalidate(); } }
    bool _showFolding = true;

    [Published(Category = "Appearance")] public bool HighlightCurrentLine { get; set; } = true;

    [Published(Category = "Appearance")] public BorderStyle BorderStyle { get; set; } = BorderStyle.None;

    /// <summary>Text size in pixels at 100 %.</summary>
    [Published(Category = "Appearance")]
    public double FontSize { get => _fontSize; set { _fontSize = Math.Clamp(value, 4, 96); ClearLineCache(); Invalidate(); } }
    double _fontSize = 13;

    /// <summary>The font family; the bundled <see cref="CodeEditorFonts.Family"/> (JetBrains Mono, no ligatures) by default.</summary>
    [Published(Category = "Appearance")]
    public string FontFamily { get => _fontFamily; set { _fontFamily = string.IsNullOrEmpty(value) ? CodeEditorFonts.Family : value; ClearLineCache(); Invalidate(); } }
    string _fontFamily = CodeEditorFonts.Family;

    /// <summary>Draw programming ligatures (only with the bundled font, which has them off by default).</summary>
    [Published(Category = "Appearance")]
    public bool Ligatures { get => _ligatures; set { _ligatures = value; ClearLineCache(); Invalidate(); } }
    bool _ligatures;

    /// <summary>Raised after the text changed (typing, undo, <see cref="ApplyEdits"/>, or another view of the same buffer).</summary>
    [Published] public event CodeEditorTextChangedEvent? TextChanged;

    /// <summary>Raised after the primary caret or the selections moved.</summary>
    [Published] public event NotifyEvent? CaretMoved;

    /// <summary>Raised when the pointer rests on text (for quick info): call <see cref="ShowHint(int, string)"/> to answer.</summary>
    [Published] public event CodeEditorHoverEvent? Hover;

    /// <summary>Raised when the gutter is clicked (for breakpoints); fold markers toggle their fold first.</summary>
    [Published] public event CodeEditorGutterClickEvent? GutterClick;

    // =====================================================================
    // Hooks
    // =====================================================================

    readonly CSharpTokenSource _defaultTokens;
    ITokenSource? _tokenSource;
    IFoldingSource? _foldingSource;
    ICompletionSource? _completionSource;
    readonly DecorationSourceList _decorationSources;

    /// <summary>Colours text; the built-in C# colouriser by default; null paints plain text.</summary>
    public ITokenSource? TokenSource
    {
        get => _tokenSource;
        set
        {
            if (_tokenSource == value) return;
            if (_tokenSource != null) _tokenSource.Changed -= OnSourceChanged;
            _tokenSource = value;
            if (value != null) value.Changed += OnSourceChanged;
            ClearLineCache();
            Invalidate();
        }
    }

    /// <summary>Squiggles, highlights and gutter glyphs; several sources can contribute (diagnostics, breakpoints, references).</summary>
    public IList<IDecorationSource> DecorationSources => _decorationSources;

    /// <summary>Fold ranges; brace and #region folding by default; null turns folding off.</summary>
    public IFoldingSource? FoldingSource
    {
        get => _foldingSource;
        set
        {
            if (_foldingSource == value) return;
            if (_foldingSource != null) _foldingSource.Changed -= OnFoldingSourceChanged;
            _foldingSource = value;
            if (value != null) value.Changed += OnFoldingSourceChanged;
            _foldsVersion = -1;
            if (value == null) { _folds.Clear(); RebuildHidden(); }
            Invalidate();
        }
    }

    public ICompletionSource? CompletionSource
    {
        get => _completionSource;
        set { CloseCompletion(); _completionSource = value; }
    }

    /// <summary>The colour scheme; null picks <see cref="CodeEditorColors.Light"/> or <see cref="CodeEditorColors.Dark"/> from the form's theme.</summary>
    public CodeEditorColors? Colors { get => _colors; set { _colors = value; ClearLineCache(); Invalidate(); } }
    CodeEditorColors? _colors;

    /// <summary>The colours in use now.</summary>
    public CodeEditorColors EffectiveColors => _colors ?? (IsDarkTheme(CurrentTheme) ? CodeEditorColors.Dark : CodeEditorColors.Light);

    static bool IsDarkTheme(Theme t)
    {
        var c = t.InputBackground;
        return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B < 128;
    }

    sealed class DecorationSourceList(CodeEditor owner) : System.Collections.ObjectModel.Collection<IDecorationSource>
    {
        protected override void InsertItem(int index, IDecorationSource item)
        {
            base.InsertItem(index, item);
            item.Changed += owner.OnSourceChanged;
            owner.Invalidate();
        }
        protected override void RemoveItem(int index)
        {
            this[index].Changed -= owner.OnSourceChanged;
            base.RemoveItem(index);
            owner.Invalidate();
        }
        protected override void SetItem(int index, IDecorationSource item)
        {
            this[index].Changed -= owner.OnSourceChanged;
            base.SetItem(index, item);
            item.Changed += owner.OnSourceChanged;
            owner.Invalidate();
        }
        protected override void ClearItems()
        {
            foreach (var s in this) s.Changed -= owner.OnSourceChanged;
            base.ClearItems();
            owner.Invalidate();
        }
    }

    void OnSourceChanged(object? sender, SourceChangedEventArgs e)
    {
        if (sender == _tokenSource) ClearLineCache();
        _decoCache = null;
        Invalidate();
    }

    void OnFoldingSourceChanged(object? sender, SourceChangedEventArgs e)
    {
        _foldsVersion = -1;
        Invalidate();
    }

    // =====================================================================
    // Buffer and snapshots
    // =====================================================================

    /// <summary>The document. Several editors can show the same buffer (a split view); each keeps its own carets and scrolling.</summary>
    public TextBuffer Buffer
    {
        get => _buffer;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value == _buffer) return;
            _buffer.Changed -= OnBufferChanged;
            _buffer = value;
            _buffer.Changed += OnBufferChanged;
            CloseCompletion();
            HideHint();
            _folds.Clear();
            _foldsVersion = -1;
            RebuildHidden();
            ClearLineCache();
            _maxColsVersion = -1;
            SetSelectionsCore([new Sel(0, 0)], 0);
            _scrollX = _scrollY = 0;
            Invalidate();
        }
    }

    public TextSnapshot Snapshot => _buffer.Current;
    public int LineCount => _buffer.LineCount;
    public bool CanUndo => _buffer.CanUndo;
    public bool CanRedo => _buffer.CanRedo;
    public bool IsModified => _buffer.IsModified;

    void OnBufferChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_applyingOwnEdit)
        {
            // Someone else edited (another view, ApplyEdits from a language service, undo): carry the carets along.
            var mapped = new List<Sel>(_sels.Count);
            foreach (var s in _sels)
            {
                bool empty = s.IsEmpty;
                int a = TextSnapshot.TranslateThrough(e.Changes, s.Anchor, empty || s.Anchor > s.Caret ? PointTrackingMode.Positive : PointTrackingMode.Negative);
                int c = TextSnapshot.TranslateThrough(e.Changes, s.Caret, empty || s.Caret > s.Anchor ? PointTrackingMode.Positive : PointTrackingMode.Negative);
                mapped.Add(new Sel(a, c));
            }
            if (e.Reason == TextChangeReason.SetText) { mapped.Clear(); mapped.Add(new Sel(0, 0)); }
            SetSelectionsCore(mapped, Math.Min(_primary, mapped.Count - 1), raise: false);
        }
        TranslateFolds(e);
        UpdateMaxColumns(e);
        _findDirty = true;
        _decoCache = null;
        _lastEditTime = OwningForm?.Now ?? 0;
        _foldsStale = true;
        if (e.Reason == TextChangeReason.SetText) { _folds.Clear(); _foldsVersion = -1; RebuildHidden(); }
        UpdateCompletionAfterChange(e);
        Invalidate();
        TextChanged?.Invoke(this, e);
        if (!_applyingOwnEdit) RaiseCaretMoved();
    }

    // =====================================================================
    // Selections
    // =====================================================================

    /// <summary>The primary selection (the last one added); Anchor is the fixed end, Caret the moving end.</summary>
    public TextSelection Selection => ToPublic(_sels[_primary]);

    /// <summary>All selections (carets), sorted by position.</summary>
    public IReadOnlyList<TextSelection> Selections => [.. _sels.Select(ToPublic)];

    public int PrimarySelectionIndex => _primary;

    static TextSelection ToPublic(Sel s) => new(s.Anchor, s.Caret);

    /// <summary>The primary caret's offset.</summary>
    public int CaretOffset => _sels[_primary].Caret;

    /// <summary>The primary caret's line and column.</summary>
    public TextPosition CaretPosition => Snapshot.GetPosition(CaretOffset);

    public string SelectedText => _sels[_primary].IsEmpty ? "" : Snapshot.GetText(_sels[_primary].Start, _sels[_primary].End - _sels[_primary].Start);

    /// <summary>Selects <paramref name="length"/> characters from <paramref name="start"/> (one selection; the caret at the end).</summary>
    public void Select(int start, int length)
    {
        int len = Snapshot.Length;
        start = Math.Clamp(start, 0, len);
        int end = Math.Clamp(start + length, 0, len);
        SetSelections([new TextSelection(start, end)]);
    }

    /// <summary>Selects from one position to another.</summary>
    public void Select(TextPosition anchor, TextPosition caret) =>
        SetSelections([new TextSelection(Snapshot.GetOffset(anchor), Snapshot.GetOffset(caret))]);

    /// <summary>Replaces all selections; the last becomes primary.</summary>
    public void SetSelections(IReadOnlyList<TextSelection> selections)
    {
        if (selections.Count == 0) throw new ArgumentException("At least one selection is needed.", nameof(selections));
        int len = Snapshot.Length;
        var list = selections.Select(s => new Sel(Math.Clamp(s.Anchor, 0, len), Math.Clamp(s.Caret, 0, len))).ToList();
        _buffer.BreakUndoCoalescing();
        SetSelectionsCore(list, list.Count - 1);
        ScrollToCaret();
    }

    public void SelectAll() => SetSelections([new TextSelection(0, Snapshot.Length)]);

    /// <summary>Moves the caret to a line and column (0-based), collapses the selection and scrolls it into view (centred if it was off screen).</summary>
    public void GoTo(int line, int column)
    {
        int offset = Snapshot.GetOffset(line, column);
        RevealOffset(offset);
        _buffer.BreakUndoCoalescing();
        SetSelectionsCore([new Sel(offset, offset)], 0);
        ScrollToCaret(center: true);
    }

    /// <summary>Adds a caret (or selection); it becomes primary.</summary>
    public void AddSelection(TextSelection selection)
    {
        int len = Snapshot.Length;
        var list = new List<Sel>(_sels) { new(Math.Clamp(selection.Anchor, 0, len), Math.Clamp(selection.Caret, 0, len)) };
        SetSelectionsCore(list, list.Count - 1);
        ScrollToCaret();
    }

    /// <summary>Back to one caret: the primary one.</summary>
    public void ClearSecondarySelections()
    {
        if (_sels.Count == 1) return;
        SetSelectionsCore([_sels[_primary]], 0);
    }

    /// <summary>Sorts and merges overlapping selections, keeps track of the primary one, and repaints.</summary>
    void SetSelectionsCore(List<Sel> list, int primary, bool raise = true)
    {
        var indexed = list.Select((s, i) => (S: s, I: i)).OrderBy(x => x.S.Start).ThenBy(x => x.S.End).ToList();
        var merged = new List<Sel>(list.Count);
        int newPrimary = 0;
        foreach (var (s, i) in indexed)
        {
            bool isPrimary = i == primary;
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (s.Start < last.End || s.Start == last.Start)
                {
                    int start = Math.Min(last.Start, s.Start), end = Math.Max(last.End, s.End);
                    bool forward = (isPrimary ? s : last).Caret >= (isPrimary ? s : last).Anchor;
                    merged[^1] = forward ? new Sel(start, end, last.DesiredX) : new Sel(end, start, last.DesiredX);
                    if (isPrimary) newPrimary = merged.Count - 1;
                    continue;
                }
            }
            merged.Add(s);
            if (isPrimary) newPrimary = merged.Count - 1;
        }
        bool changed = merged.Count != _sels.Count || newPrimary != _primary || !merged.SequenceEqual(_sels);
        _sels.Clear();
        _sels.AddRange(merged);
        _primary = newPrimary;
        if (!changed) return;
        ResetBlink();
        UpdateTextInputArea();
        UpdateCompletionAfterCaretMove();
        Invalidate();
        if (raise) RaiseCaretMoved();
    }

    void RaiseCaretMoved() => CaretMoved?.Invoke(this);

    TextSelection[] SelectionState() => [.. _sels.Select(ToPublic)];

    void RestoreSelectionState(object? state)
    {
        if (state is not TextSelection[] { Length: > 0 } sels) return;
        int len = Snapshot.Length;
        var list = sels.Select(s => new Sel(Math.Clamp(s.Anchor, 0, len), Math.Clamp(s.Caret, 0, len))).ToList();
        SetSelectionsCore(list, list.Count - 1);
    }

    // =====================================================================
    // Programmatic editing
    // =====================================================================

    /// <summary>
    /// Applies edits (relative to the current snapshot, non-overlapping) as one undo step, the way a language service
    /// formats or renames. Carets move with the text. Works even when <see cref="ReadOnly"/> is set.
    /// </summary>
    public TextChangedEventArgs? ApplyEdits(IEnumerable<TextEdit> edits) =>
        _buffer.Apply(edits, UndoCoalescing.None, SelectionState());

    public bool Undo()
    {
        if (ReadOnly || !_buffer.CanUndo) return false;
        CloseCompletion();
        _applyingOwnEdit = true;
        object? state;
        try { _buffer.Undo(out state); }
        finally { _applyingOwnEdit = false; }
        RestoreSelectionStateOrClamp(state);
        ScrollToCaret();
        return true;
    }

    public bool Redo()
    {
        if (ReadOnly || !_buffer.CanRedo) return false;
        CloseCompletion();
        _applyingOwnEdit = true;
        object? state;
        try { _buffer.Redo(out state); }
        finally { _applyingOwnEdit = false; }
        RestoreSelectionStateOrClamp(state);
        ScrollToCaret();
        return true;
    }

    void RestoreSelectionStateOrClamp(object? state)
    {
        if (state is TextSelection[] { Length: > 0 }) RestoreSelectionState(state);
        else
        {
            int len = Snapshot.Length;
            SetSelectionsCore([.. _sels.Select(s => new Sel(Math.Min(s.Anchor, len), Math.Min(s.Caret, len)))], _primary);
        }
        RaiseCaretMoved();
    }

    /// <summary>
    /// Applies the editor's own edit: <paramref name="edits"/> with the selection each produces, given as offsets
    /// into its new text (relative to where the edit's new text starts).
    /// </summary>
    void ApplyOwn(List<(TextEdit Edit, int Anchor, int Caret)> edits, UndoCoalescing coalescing)
    {
        if (edits.Count == 0) return;
        var before = SelectionState();
        var ordered = edits.OrderBy(e => e.Edit.Start).ThenBy(e => e.Edit.Length).ToList();
        _applyingOwnEdit = true;
        TextChangedEventArgs? args;
        try { args = _buffer.Apply(ordered.Select(e => e.Edit), coalescing, before); }
        finally { _applyingOwnEdit = false; }
        // New starts: the buffer drops empty edits, so compute them here from the ordered list.
        var sels = new List<Sel>(ordered.Count);
        int delta = 0;
        foreach (var (edit, anchor, caret) in ordered)
        {
            int newStart = edit.Start + delta;
            sels.Add(new Sel(newStart + anchor, newStart + caret));
            delta += edit.NewText.Length - edit.Length;
        }
        int primaryIndex = Math.Min(_primary, sels.Count - 1);
        SetSelectionsCore(sels, primaryIndex, raise: false);
        if (args != null) _buffer.SetUndoStateAfter(SelectionState());
        ScrollToCaret();
        RaiseCaretMoved();
    }

    /// <summary>Applies a set of edits and maps the carets through them (line commands: indent, comment, move).</summary>
    void EditMapped(List<TextEdit> edits, Func<Sel, IReadOnlyList<TextChange>, Sel>? map = null)
    {
        if (edits.Count == 0) return;
        var before = SelectionState();
        var old = _sels.ToList();
        _applyingOwnEdit = true;
        TextChangedEventArgs? args;
        try { args = _buffer.Apply(edits, UndoCoalescing.None, before); }
        finally { _applyingOwnEdit = false; }
        if (args == null) return;
        var sels = old.Select(s => map != null ? map(s, args.Changes) : MapSel(s, args.Changes)).ToList();
        SetSelectionsCore(sels, _primary, raise: false);
        _buffer.SetUndoStateAfter(SelectionState());
        ScrollToCaret();
        RaiseCaretMoved();
    }

    static Sel MapSel(Sel s, IReadOnlyList<TextChange> changes)
    {
        if (s.IsEmpty)
        {
            int p = TextSnapshot.TranslateThrough(changes, s.Caret, PointTrackingMode.Positive);
            return new Sel(p, p);
        }
        int start = TextSnapshot.TranslateThrough(changes, s.Start, PointTrackingMode.Negative);
        int end = TextSnapshot.TranslateThrough(changes, s.End, PointTrackingMode.Positive);
        return s.Caret >= s.Anchor ? new Sel(start, end) : new Sel(end, start);
    }

    // =====================================================================
    // Metrics
    // =====================================================================

    RenderFont? _font;
    (double Size, double Scale, string Family, bool Ligatures) _fontKey;

    internal RenderFont TextFont
    {
        get
        {
            var key = (_fontSize, UiScale, _fontFamily, _ligatures);
            if (_font == null || _fontKey != key)
            {
                var family = _ligatures && _fontFamily == CodeEditorFonts.Family ? CodeEditorFonts.LigaturesFamily : _fontFamily;
                _font = new RenderFont(FontFace.Resolve(family, false, false), (float)(_fontSize * UiScale));
                _fontKey = key;
                _charWidth = TextLayout.Get("0", _font).Width;
                ClearLineCache();
            }
            return _font;
        }
    }

    float _charWidth;
    float CharWidth { get { _ = TextFont; return _charWidth; } }

    /// <summary>Height of one line in pixels.</summary>
    public int LineHeight => (int)Math.Ceiling(TextFont.LineHeight);

    int Border => BorderStyle == BorderStyle.Single ? Math.Max(1, Px(1)) : 0;
    int BarSize => Px(12);

    int DigitCount => Math.Max(3, (int)Math.Floor(Math.Log10(Math.Max(1, LineCount))) + 1);
    int GlyphLaneWidth => _showGlyphMargin ? Px(18) : 0;
    int NumbersWidth => _showLineNumbers ? (int)Math.Ceiling(DigitCount * CharWidth) + Px(10) : 0;
    int FoldLaneWidth => _showFolding && _foldingSource != null ? Px(14) : 0;

    /// <summary>The gutter's width in pixels (0 when it shows nothing).</summary>
    public int GutterWidth
    {
        get
        {
            int w = GlyphLaneWidth + NumbersWidth + FoldLaneWidth;
            return w == 0 ? 0 : w + Px(2);
        }
    }

    int TextPadding => Px(4);

    bool ShowVBar => Bars.V;
    bool ShowHBar => Bars.H;

    /// <summary>Which scroll bars show: each one narrows the room for the other, so a second look is needed.</summary>
    (bool V, bool H) Bars
    {
        get
        {
            int ch = ContentHeight, cw = ContentWidth, hh = TextAreaHeightWithoutBar, ww = TextAreaWidthWithoutBar, bar = BarSize;
            bool v = ch > hh;
            bool h = cw > ww - (v ? bar : 0);
            if (h && !v) v = ch > hh - bar;
            return (v, h);
        }
    }
    int TextAreaHeightWithoutBar => Math.Max(0, Height - 2 * Border);
    int TextAreaWidthWithoutBar => Math.Max(0, Width - 2 * Border - GutterWidth - TextPadding);

    /// <summary>The text area (right of the gutter, inside the scroll bars) in this control's coordinates.</summary>
    public Rect TextArea
    {
        get
        {
            int b = Border, g = GutterWidth;
            bool v = ShowVBar, h = ShowHBar;
            int w = Width - 2 * b - g - (v ? BarSize : 0);
            int hh = Height - 2 * b - (h ? BarSize : 0);
            return new Rect(b + g, b, Math.Max(0, w), Math.Max(0, hh));
        }
    }

    Rect GutterRect => new(Border, Border, GutterWidth, TextArea.Height);

    int ContentHeight => RowCount * LineHeight;
    int ContentWidth => (int)Math.Ceiling(MaxColumns * CharWidth) + TextPadding + Px(24);

    /// <summary>The first visible line.</summary>
    public int FirstVisibleLine => LineOfRow(_scrollY / Math.Max(1, LineHeight));

    /// <summary>How many lines fit in the text area.</summary>
    public int VisibleLineCount => Math.Max(1, TextArea.Height / Math.Max(1, LineHeight));

    // =====================================================================
    // Longest line (horizontal scroll range)
    // =====================================================================

    int _maxCols;
    int _maxColsVersion = -1;
    bool _maxColsMaybeShrunk;

    int MaxColumns
    {
        get
        {
            if (_maxColsVersion != Snapshot.Version && (_maxColsVersion < 0 || _maxColsMaybeShrunk)) RecomputeMaxColumns();
            return _maxCols;
        }
    }

    void RecomputeMaxColumns()
    {
        var snap = Snapshot;
        int max = 0;
        // One pass over the pieces: no per-line lookups.
        var pieces = new List<Piece>();
        Node.CollectPieces(snap.Root, pieces);
        int col = 0;
        foreach (var p in pieces)
        {
            foreach (char c in p.Span)
            {
                if (c == '\n') { if (col > max) max = col; col = 0; }
                else if (c == '\t') col += _tabSize - col % _tabSize;
                else if (c != '\r') col++;
            }
        }
        _maxCols = Math.Max(max, col);
        _maxColsVersion = snap.Version;
        _maxColsMaybeShrunk = false;
    }

    void UpdateMaxColumns(TextChangedEventArgs e)
    {
        if (_maxColsVersion < 0) return;
        if (e.Reason == TextChangeReason.SetText) { _maxColsVersion = -1; return; }
        var snap = e.After;
        foreach (var c in e.Changes)
        {
            if (c.OldText.Length > 0) _maxColsMaybeShrunk = true;
            int first = snap.GetLineFromOffset(c.NewStart), last = snap.GetLineFromOffset(c.NewEnd);
            for (int l = first; l <= last && l - first < 64; l++)
                _maxCols = Math.Max(_maxCols, DisplayWidth(snap, l));
        }
        if (!_maxColsMaybeShrunk) _maxColsVersion = snap.Version;
    }

    int DisplayWidth(TextSnapshot snap, int line)
    {
        int s = snap.GetLineStart(line), e = snap.GetLineEnd(line);
        if (e - s > 4096) return e - s;
        int col = 0;
        for (int i = s; i < e; i++) col += snap[i] == '\t' ? _tabSize - col % _tabSize : 1;
        return col;
    }

    // =====================================================================
    // Line views (display text, layout and colour segments, cached per line)
    // =====================================================================

    internal sealed class LineView
    {
        public required string Text;
        public required string Display;
        /// <summary>Display index for each buffer column 0..Text.Length (null: the same).</summary>
        public int[]? Map;
        public required TextLayout Layout;
        public IReadOnlyList<TokenSpan>? Tokens;
        public (float X, TextLayout Layout, string? Classification)[]? Segments;
        public CodeEditorColors? SegmentColors;

        public int ToDisplay(int col) => Map == null ? Math.Clamp(col, 0, Text.Length) : Map[Math.Clamp(col, 0, Text.Length)];

        public float XOf(int col) => Layout.CaretX(ToDisplay(col));

        /// <summary>The buffer column nearest to <paramref name="x"/> (pixels from the line's start).</summary>
        public int ColumnAt(float x)
        {
            int d = Layout.HitTest(x);
            if (Map == null) return Math.Min(d, Text.Length);
            // The column whose display position is nearest (a click inside a tab picks its nearer edge).
            int best = 0;
            float bestDist = float.MaxValue;
            for (int c = 0; c <= Text.Length; c++)
            {
                float dist = Math.Abs(Layout.CaretX(Map[c]) - x);
                if (dist < bestDist) { bestDist = dist; best = c; }
                if (Map[c] > d + _slack) break;
            }
            return best;
        }

        const int _slack = 16;
    }

    readonly Dictionary<int, LineView> _lineCache = [];

    void ClearLineCache() => _lineCache.Clear();

    /// <summary>The view of <paramref name="line"/> in the current snapshot (cached while its text is unchanged).</summary>
    internal LineView GetLineView(int line)
    {
        var snap = Snapshot;
        var text = snap.GetLineText(line);
        var font = TextFont;
        if (_lineCache.TryGetValue(line, out var view) && view.Text == text && view.Layout.Font == font) return view;
        view = BuildLineView(text, font);
        if (_lineCache.Count > 1024) _lineCache.Clear();
        _lineCache[line] = view;
        return view;
    }

    LineView BuildLineView(string text, RenderFont font)
    {
        int[]? map = null;
        string display = text;
        if (text.Contains('\t'))
        {
            map = new int[text.Length + 1];
            var sb = new System.Text.StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length; i++)
            {
                map[i] = sb.Length;
                if (text[i] == '\t') sb.Append(' ', _tabSize - sb.Length % _tabSize);
                else sb.Append(text[i]);
            }
            map[text.Length] = sb.Length;
            display = sb.ToString();
        }
        return new LineView { Text = text, Display = display, Map = map, Layout = TextLayout.Get(display, font) };
    }

    /// <summary>Colour segments for a line view, rebuilt when its tokens or the colour scheme change.</summary>
    void EnsureSegments(LineView view, int line, CodeEditorColors colors)
    {
        IReadOnlyList<TokenSpan>? tokens = null;
        try { tokens = _tokenSource?.GetTokens(Snapshot, line); }
        catch (Exception) { tokens = null; }   // a failing source must not break painting
        if (view.Segments != null && view.SegmentColors == colors && SameTokens(view.Tokens, tokens)) return;
        view.Tokens = tokens;
        view.SegmentColors = colors;
        var segs = new List<(float, TextLayout, string?)>();
        var font = view.Layout.Font;
        int pos = 0, len = view.Text.Length;
        void AddSeg(int from, int to, string? cls)
        {
            if (to <= from) return;
            int d0 = view.ToDisplay(from), d1 = view.ToDisplay(to);
            var sub = view.Display.Substring(d0, d1 - d0);
            if (sub.AsSpan().IsWhiteSpace()) return;
            segs.Add((view.Layout.CaretX(d0), TextLayout.Get(sub, font), cls));
        }
        if (tokens != null)
        {
            foreach (var t in tokens)
            {
                int s = Math.Clamp(t.Start, pos, len), e = Math.Clamp(t.End, s, len);
                if (s > pos) AddSeg(pos, s, null);
                AddSeg(s, e, t.Classification);
                pos = e;
            }
        }
        AddSeg(pos, len, null);
        view.Segments = [.. segs];
    }

    static bool SameTokens(IReadOnlyList<TokenSpan>? a, IReadOnlyList<TokenSpan>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // =====================================================================
    // Coordinates
    // =====================================================================

    /// <summary>The point (in this control's coordinates) of the top-left of the caret position at <paramref name="offset"/>.</summary>
    public (int X, int Y) PointOfOffset(int offset)
    {
        var p = Snapshot.GetPosition(offset);
        var area = TextArea;
        int row = RowOfLine(p.Line);
        var view = GetLineView(p.Line);
        int x = area.X + TextPadding + (int)MathF.Round(view.XOf(p.Column)) - _scrollX;
        int y = area.Y + row * LineHeight - _scrollY;
        return (x, y);
    }

    /// <summary>The text offset nearest to a point in this control's coordinates.</summary>
    public int OffsetAtPoint(int x, int y)
    {
        var area = TextArea;
        int row = (y - area.Y + _scrollY) / Math.Max(1, LineHeight);
        if (y - area.Y + _scrollY < 0) row = 0;
        int line = LineOfRow(Math.Clamp(row, 0, Math.Max(0, RowCount - 1)));
        var view = GetLineView(line);
        int col = view.ColumnAt(x - area.X - TextPadding + _scrollX);
        return Snapshot.GetLineStart(line) + col;
    }

    /// <summary>The line under a y coordinate (this control's coordinates), clamped to the text.</summary>
    int LineAtY(int y)
    {
        var area = TextArea;
        int row = Math.Max(0, (y - area.Y + _scrollY)) / Math.Max(1, LineHeight);
        return LineOfRow(Math.Clamp(row, 0, Math.Max(0, RowCount - 1)));
    }

    /// <summary>The caret rectangle of the primary caret in this control's coordinates.</summary>
    public Rect CaretRect
    {
        get
        {
            int offset = CaretOffset;
            var (x, y) = PointOfOffset(offset);
            if (IsComposing)
            {
                var p = Snapshot.GetPosition(offset);
                var view = CompositionView(p.Line, out int compStart);
                x = TextArea.X + TextPadding + (int)MathF.Round(view.Layout.CaretX(compStart + _compCursor)) - _scrollX;
            }
            return new Rect(x, y, Math.Max(1, Px(2)), LineHeight);
        }
    }

    // =====================================================================
    // Scrolling
    // =====================================================================

    int _scrollX, _scrollY;
    readonly ScrollAxis _vAxis = new() { PageSize = 0, Max = 0 };
    readonly ScrollAxis _hAxis = new() { PageSize = 0, Max = 0 };
    readonly ScrollBarDriver _vDriver, _hDriver;

    /// <summary>Vertical scroll position in pixels.</summary>
    public int ScrollY { get => _scrollY; set { _scrollY = value; ClampScroll(); Invalidate(); } }
    /// <summary>Horizontal scroll position in pixels.</summary>
    public int ScrollX { get => _scrollX; set { _scrollX = value; ClampScroll(); Invalidate(); } }

    void ClampScroll()
    {
        var area = TextArea;
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, ContentHeight - area.Height));
        _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, ContentWidth - area.Width));
    }

    /// <summary>Scrolls so the primary caret is visible; with <paramref name="center"/> an off-screen caret ends up mid-view.</summary>
    public void ScrollToCaret(bool center = false)
    {
        if (Width <= 0 || Height <= 0) return;
        var area = TextArea;
        int lh = LineHeight;
        var p = Snapshot.GetPosition(CaretOffset);
        int row = RowOfLine(p.Line);
        int y = row * lh;
        bool offScreen = y < _scrollY || y + lh > _scrollY + area.Height;
        if (center && offScreen) _scrollY = y - (area.Height - lh) / 2;
        else if (y < _scrollY) _scrollY = y;
        else if (y + lh > _scrollY + area.Height) _scrollY = y + lh - area.Height;

        var view = GetLineView(p.Line);
        int x = (int)MathF.Round(view.XOf(p.Column)) + TextPadding;     // content coordinates
        int margin = Math.Min((int)(CharWidth * 4), area.Width / 3);
        if (x > _scrollX + area.Width - margin) _scrollX = x - area.Width + margin;
        else if (x < _scrollX + margin) _scrollX = Math.Max(0, x - margin);
        ClampScroll();
        UpdateTextInputArea();
        Invalidate();
    }

    /// <summary>Scrolls so <paramref name="line"/> is the first visible line.</summary>
    public void ScrollToLine(int line)
    {
        _scrollY = RowOfLine(Math.Clamp(line, 0, LineCount - 1)) * LineHeight;
        ClampScroll();
        Invalidate();
    }

    protected override void OnResize()
    {
        ClampScroll();
        PositionFindBar();
        base.OnResize();
    }

    // =====================================================================
    // Caret blink and ticks
    // =====================================================================

    double _blinkStart;
    bool _caretVisible = true;
    double _lastEditTime;

    void ResetBlink()
    {
        _blinkStart = OwningForm?.Now ?? 0;
        _caretVisible = true;
    }

    protected override void OnTick(double nowMs)
    {
        base.OnTick(nowMs);
        if (Focused)
        {
            bool visible = (nowMs - _blinkStart) % 1060 < 530;
            if (visible != _caretVisible) { _caretVisible = visible; Invalidate(); }
        }
        // Folds are refreshed once typing pauses.
        if (_foldsStale && nowMs - _lastEditTime >= FoldingDelayMs) { RefreshFolds(); }
        if (_maxColsMaybeShrunk && nowMs - _lastEditTime >= FoldingDelayMs) { RecomputeMaxColumns(); ClampScroll(); Invalidate(); }
        TickHover(nowMs);
        PollCompletion();
    }

    /// <summary>How long after the last edit fold ranges are recomputed, in milliseconds.</summary>
    public double FoldingDelayMs { get; set; } = 300;

    void UpdateTextInputArea()
    {
        if (!Focused || OwningForm is not { } form) return;
        var c = CaretRect;
        form.TextInputArea = new Rect(AbsoluteLeft + c.X, AbsoluteTop + c.Y, c.Width, c.Height);
    }

    // =====================================================================
    // Focus
    // =====================================================================

    protected override void OnGotFocus()
    {
        OwningForm?.AddTicker(this);
        ResetBlink();
        UpdateTextInputArea();
        base.OnGotFocus();
    }

    protected override void OnLostFocus()
    {
        _composition = "";
        _dragging = DragMode2.None;
        _buffer.BreakUndoCoalescing();
        if (OwningForm is { } form)
        {
            if (!IsHovered) form.RemoveTicker(this);
            form.TextInputArea = null;
        }
        CloseCompletion();
        base.OnLostFocus();
    }

    protected override void OnMouseEnter()
    {
        OwningForm?.AddTicker(this);
        base.OnMouseEnter();
    }

    protected override void OnMouseLeave()
    {
        if (!Focused) OwningForm?.RemoveTicker(this);
        _hoverPoint = null;
        if (_hintFromHover) HideHint();
        base.OnMouseLeave();
    }

    public override bool AcceptsTab => !ReadOnly;
}

/// <summary>A selection in a <see cref="CodeEditor"/>: <see cref="Anchor"/> is the fixed end, <see cref="Caret"/> the moving end (offsets).</summary>
public readonly record struct TextSelection(int Anchor, int Caret)
{
    public int Start => Math.Min(Anchor, Caret);
    public int End => Math.Max(Anchor, Caret);
    public int Length => End - Start;
    public bool IsEmpty => Anchor == Caret;
    public static TextSelection At(int offset) => new(offset, offset);
}

public delegate void CodeEditorTextChangedEvent(Control sender, TextChangedEventArgs e);
public delegate void CodeEditorHoverEvent(Control sender, CodeEditorHoverEventArgs e);
public delegate void CodeEditorGutterClickEvent(Control sender, CodeEditorGutterClickEventArgs e);

/// <summary>The pointer rested on text: where (offset, line and column) and the pointer position in the editor's coordinates.</summary>
public sealed class CodeEditorHoverEventArgs(int offset, TextPosition position, int x, int y) : EventArgs
{
    public int Offset { get; } = offset;
    public TextPosition Position { get; } = position;
    public int X { get; } = x;
    public int Y { get; } = y;
}

public enum GutterArea { Glyphs, LineNumbers, Folding }

/// <summary>A click in the gutter: the (0-based) line and which lane.</summary>
public sealed class CodeEditorGutterClickEventArgs(int line, GutterArea area, MouseButton button, KeyModifiers modifiers) : EventArgs
{
    public int Line { get; } = line;
    public GutterArea Area { get; } = area;
    public MouseButton Button { get; } = button;
    public KeyModifiers Modifiers { get; } = modifiers;
    public bool Handled { get; set; }
}
