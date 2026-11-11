using System.Text.Json.Nodes;

namespace RadSharp.Designer;

/// <summary>
/// The seam between <see cref="CodeDocument"/> and the control that edits its text. Today
/// <see cref="MemoCodeEditorView"/> (a <see cref="Memo"/>) stands in; the real editor (<c>RadSharp.CodeEditor</c>,
/// milestone I1) plugs in by implementing this interface and setting <see cref="CodeEditors.Factory"/>.
/// Lines and columns are 1-based; text uses <c>\n</c> line breaks (the document converts on load and save).
/// </summary>
public interface ICodeEditorView
{
    /// <summary>The control placed in the document area.</summary>
    Control Control { get; }

    /// <summary>The whole buffer. Setting it replaces the buffer (and its undo history) and raises <see cref="TextChanged"/>.</summary>
    string Text { get; set; }

    /// <summary>Raised after every change to <see cref="Text"/>, typed or set.</summary>
    event Action? TextChanged;

    (int Line, int Column) Caret { get; }

    /// <summary>Moves the caret (clamped to the text) and scrolls it into view.</summary>
    void SetCaret(int line, int column);

    /// <summary>Vertical scroll position in pixels (for session restore).</summary>
    int ScrollOffset { get; set; }

    bool ReadOnly { get; set; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    void Undo();
    void Redo();
    void Cut();
    void Copy();
    void Paste();
    void Delete();
    void SelectAll();
    void Focus();
}

/// <summary>Creates the editor every <see cref="CodeDocument"/> uses.</summary>
public static class CodeEditors
{
    /// <summary>The editor factory. The I1 code editor replaces this with one that builds its own view.</summary>
    public static Func<ICodeEditorView> Factory { get; set; } = () => new MemoCodeEditorView();
}

/// <summary>
/// A code file open in the IDE (<c>.cs</c>, read/write), shown in an <see cref="ICodeEditorView"/>. A form's
/// code-behind knows its form (<see cref="FormFile"/>) so F12 can switch between them in place.
/// </summary>
public sealed class CodeDocument : IDocumentView, IEditTarget
{
    readonly ICodeEditorView _editor;
    string _saved = "";
    bool _crlf;
    bool _dirty;

    public CodeDocument(string path, ICodeEditorView? editor = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        _editor = editor ?? CodeEditors.Factory();
        _editor.Control.Name = "";
        Load();
        _editor.TextChanged += Refresh;
    }

    public string Kind => "code";
    public string Path { get; private set; }
    string? IDocumentView.Path => Path;
    public string Title => System.IO.Path.GetFileName(Path);

    /// <summary>The file was renamed or moved (by the project tree): the open document follows it.</summary>
    public void MovedTo(string newPath)
    {
        Path = System.IO.Path.GetFullPath(newPath);
        DiskTimestamp = DocumentFiles.Stamp(Path);
        StateChanged?.Invoke();
    }
    public bool IsDirty => _dirty;
    public Control View => _editor.Control;
    public ICodeEditorView Editor => _editor;
    public DateTime DiskTimestamp { get; private set; }

    /// <summary>The form this file is the code-behind of, if it is one.</summary>
    public string? FormFile { get; set; }

    /// <summary>The buffer (with <c>\n</c> line breaks). Setting it keeps the document dirty until saved.</summary>
    public string Text
    {
        get => _editor.Text;
        set { if (value != _editor.Text) _editor.Text = Normalize(value); }
    }

    public event Action? StateChanged;

    static string Normalize(string text) => text.Replace("\r\n", "\n");

    void Load()
    {
        var raw = File.ReadAllText(Path);
        _crlf = raw.Contains("\r\n", StringComparison.Ordinal);
        _saved = Normalize(raw);
        _editor.Text = _saved;
        DiskTimestamp = DocumentFiles.Stamp(Path);
    }

    void Refresh()
    {
        bool dirty = _editor.Text != _saved;
        if (dirty == _dirty) return;
        _dirty = dirty;
        StateChanged?.Invoke();
    }

    /// <summary>Puts the caret at 1-based <paramref name="line"/>/<paramref name="column"/>, scrolled into view.</summary>
    public void GoTo(int line, int column) => _editor.SetCaret(line, column);

    public bool Save()
    {
        var text = _editor.Text;
        try { File.WriteAllText(Path, _crlf ? text.Replace("\n", "\r\n") : text); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        _saved = text;
        DiskTimestamp = DocumentFiles.Stamp(Path);
        Refresh();
        return true;
    }

    public bool CanClose() => true;
    public void Close() { }
    public void Focus() => _editor.Focus();

    public bool Reload()
    {
        var (line, column) = _editor.Caret;
        int scroll = _editor.ScrollOffset;
        try { Load(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        _editor.SetCaret(line, column);
        _editor.ScrollOffset = scroll;
        Refresh();
        return true;
    }

    public JsonObject? SaveState()
    {
        var (line, column) = _editor.Caret;
        return new JsonObject { ["line"] = line, ["column"] = column, ["scroll"] = _editor.ScrollOffset };
    }

    public void RestoreState(JsonObject state)
    {
        try
        {
            int line = state["line"]?.GetValue<int>() ?? 1, column = state["column"]?.GetValue<int>() ?? 1;
            _editor.SetCaret(line, column);
            if (state["scroll"]?.GetValue<int>() is { } scroll) _editor.ScrollOffset = scroll;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { }
    }

    public bool CanUndo => _editor.CanUndo;
    public bool CanRedo => _editor.CanRedo;
    public void Undo() => _editor.Undo();
    public void Redo() => _editor.Redo();
    public void Cut() => _editor.Cut();
    public void Copy() => _editor.Copy();
    public void Paste() => _editor.Paste();
    public void Delete() => _editor.Delete();
    public void SelectAll() => _editor.SelectAll();
}

/// <summary>
/// The placeholder code editor: a <see cref="Memo"/> without word wrap that takes tabs. Fine for small files;
/// <c>Memo</c> re-lays out every line per keystroke, which is why I1 builds a real editor (see ide-plan.md).
/// </summary>
public sealed class MemoCodeEditorView : ICodeEditorView
{
    readonly CodeMemo _memo = new() { WordWrap = false, WantTabs = true, BorderStyle = BorderStyle.None, HideSelection = false };

    public MemoCodeEditorView() => _memo.Change += _ => TextChanged?.Invoke();

    public Control Control => _memo;
    public Memo Memo => _memo;

    public string Text { get => _memo.Text; set => _memo.Text = value; }
    public event Action? TextChanged;

    public (int Line, int Column) Caret
    {
        get
        {
            var text = _memo.Text;
            int caret = Math.Min(_memo.CaretPosition, text.Length);
            int lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
            int line = 1;
            for (int i = 0; i < lineStart; i++) if (text[i] == '\n') line++;
            return (line, caret - lineStart + 1);
        }
    }

    public void SetCaret(int line, int column)
    {
        var text = _memo.Text;
        int index = 0;
        for (int l = 1; l < line && index <= text.Length; l++)
        {
            int nl = text.IndexOf('\n', index);
            if (nl < 0) { index = text.Length; break; }
            index = nl + 1;
        }
        int lineEnd = text.IndexOf('\n', Math.Min(index, text.Length));
        if (lineEnd < 0) lineEnd = text.Length;
        index = Math.Min(index + Math.Max(0, column - 1), lineEnd);
        _memo.Select(index, 0);
        _memo.RevealCaret();
    }

    public int ScrollOffset { get => _memo.VerticalScroll; set => _memo.VerticalScroll = value; }
    public bool ReadOnly { get => _memo.ReadOnly; set => _memo.ReadOnly = value; }
    public bool CanUndo => _memo.CanUndo;
    public bool CanRedo => _memo.CanRedo;
    public void Undo() => _memo.Undo();
    public void Redo() => _memo.Redo();
    public void Cut() => _memo.CutToClipboard();
    public void Copy() => _memo.CopyToClipboard();
    public void Paste() => _memo.PasteFromClipboard();
    public void Delete() => _memo.DeleteSelection();
    public void SelectAll() => _memo.SelectAll();
    public void Focus() => _memo.Focus();

    /// <summary>A memo that exposes its scroll position and redo state, and keeps the caret in view once it has a size.</summary>
    sealed class CodeMemo : Memo
    {
        int? _pendingScroll;
        bool _revealPending;

        public bool CanRedo => Editor.CanRedo;

        public void DeleteSelection()
        {
            if (!ReadOnly && Editor.HasSelection) Editor.DeleteForward();
        }

        public void RevealCaret()
        {
            if (Height <= 0) { _revealPending = true; return; }
            EnsureCaretVisible();
            Invalidate();
        }

        public int VerticalScroll
        {
            get => _pendingScroll ?? ScrollY;
            set
            {
                if (Height <= 0) { _pendingScroll = value; return; }
                ScrollY = value;
                ClampScroll();
                Invalidate();
            }
        }

        protected override void OnResize()
        {
            base.OnResize();
            if (Height <= 0) return;
            if (_revealPending) { _revealPending = false; EnsureCaretVisible(); }
            if (_pendingScroll is { } s) { _pendingScroll = null; ScrollY = s; ClampScroll(); }
        }
    }
}
