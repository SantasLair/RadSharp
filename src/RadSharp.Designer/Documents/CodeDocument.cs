using System.Text.Json.Nodes;
using RadSharp.CodeEditing;

namespace RadSharp.Designer;

/// <summary>
/// The seam between <see cref="CodeDocument"/> and the control that edits its text: <see cref="CodeEditorAdapter"/>
/// over the <see cref="CodeEditor"/> control (set as <see cref="CodeEditors.Factory"/>). Lines and columns are
/// 1-based; offsets are 0-based. The text keeps the file's own line breaks (CRLF, LF or mixed): the buffer preserves
/// them, so nothing converts on load or save.
/// </summary>
public interface ICodeEditorView
{
    /// <summary>The control placed in the document area.</summary>
    Control Control { get; }

    /// <summary>The whole buffer. Setting it replaces the buffer (and its undo history) and raises <see cref="TextChanged"/>.</summary>
    string Text { get; set; }

    /// <summary>Raised after every change to <see cref="Text"/>, typed, set or applied.</summary>
    event Action? TextChanged;

    /// <summary>
    /// Changes the text with <paramref name="edits"/> (against the current text, non-overlapping) as one undo step,
    /// keeping the carets (they move with the text) and the undo history. Works when <see cref="ReadOnly"/> is set.
    /// </summary>
    void ApplyEdits(IReadOnlyList<TextEdit> edits);

    /// <summary>True when the text differs from when it was loaded or <see cref="MarkClean"/> was last called (undo back to it clears it).</summary>
    bool IsModified { get; }

    /// <summary>Remembers the current text as saved.</summary>
    void MarkClean();

    /// <summary>The colouriser: <c>"csharp"</c>, <c>"json"</c> or <c>"plain"</c>.</summary>
    string Language { get; set; }

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
    /// <summary>The editor factory: a <see cref="CodeEditorAdapter"/> over a new <see cref="CodeEditor"/>.</summary>
    public static Func<ICodeEditorView> Factory { get; set; } = () => new CodeEditorAdapter();
}

/// <summary>
/// A code file open in the IDE (<c>.cs</c>, or any text file such as a <c>.radform.json</c> opened as text), shown in an
/// <see cref="ICodeEditorView"/>. A form's code-behind knows its form (<see cref="FormFile"/>) so F12 can switch
/// between them in place. The tab's dirty marker follows the buffer's undo history (<see cref="ICodeEditorView.IsModified"/>):
/// typing and undoing back to the saved text is clean again. A file that is read-only on disk opens read-only.
/// </summary>
public sealed class CodeDocument : IDocumentView, IEditTarget
{
    readonly ICodeEditorView _editor;
    bool _dirty;

    public CodeDocument(string path, ICodeEditorView? editor = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        _editor = editor ?? CodeEditors.Factory();
        _editor.Control.Name = "";
        _editor.Language = LanguageOf(Path);
        Load();
        _editor.TextChanged += Refresh;
    }

    public string Kind => "code";
    public string Path { get; private set; }
    string? IDocumentView.Path => Path;

    /// <summary>The file was renamed or moved (by the project tree): the open document follows it.</summary>
    public void MovedTo(string newPath)
    {
        var old = Path;
        Path = System.IO.Path.GetFullPath(newPath);
        DiskTimestamp = DocumentFiles.Stamp(Path);
        Moved?.Invoke(old);
        StateChanged?.Invoke();
    }

    /// <summary>Raised by <see cref="MovedTo"/> with the old path (the language host renames the document).</summary>
    public event Action<string>? Moved;
    public string Title => System.IO.Path.GetFileName(Path) + (ReadOnly ? " [read-only]" : "");
    public bool IsDirty => _dirty;
    public Control View => _editor.Control;
    public ICodeEditorView Editor => _editor;
    public DateTime DiskTimestamp { get; private set; }

    /// <summary>The form this file is the code-behind of, if it is one.</summary>
    public string? FormFile { get; set; }

    /// <summary>True for a C# file (the language service works on these).</summary>
    public bool IsCSharp => _editor.Language == "csharp";

    /// <summary>The file is read-only on disk, so the buffer is too.</summary>
    public bool ReadOnly => _editor.ReadOnly;

    /// <summary>The colouriser for a file: C# for <c>.cs</c>, JSON for <c>.json</c> (forms opened as text), plain otherwise.</summary>
    public static string LanguageOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".json" => "json",
        _ => "plain",
    };

    /// <summary>
    /// The buffer, with the file's line breaks. Setting it applies the difference as minimal edits (one undo step),
    /// so the caret and the undo history survive; the document stays dirty until saved.
    /// </summary>
    public string Text
    {
        get => _editor.Text;
        set => ApplyText(value);
    }

    /// <summary>Replaces the text with <paramref name="text"/> through the smallest edits (see <see cref="BufferDiff"/>), as one undo step.</summary>
    public void ApplyText(string text)
    {
        var edits = BufferDiff.Edits(_editor.Text, text);
        if (edits.Count > 0) _editor.ApplyEdits(edits);
    }

    public event Action? StateChanged;

    void Load()
    {
        _editor.Text = File.ReadAllText(Path);
        _editor.MarkClean();
        bool readOnly;
        try { readOnly = (File.GetAttributes(Path) & FileAttributes.ReadOnly) != 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { readOnly = false; }
        if (readOnly != _editor.ReadOnly) { _editor.ReadOnly = readOnly; StateChanged?.Invoke(); }
        DiskTimestamp = DocumentFiles.Stamp(Path);
    }

    void Refresh()
    {
        bool dirty = _editor.IsModified;
        if (dirty == _dirty) return;
        _dirty = dirty;
        StateChanged?.Invoke();
    }

    /// <summary>Puts the caret at 1-based <paramref name="line"/>/<paramref name="column"/>, scrolled into view.</summary>
    public void GoTo(int line, int column) => _editor.SetCaret(line, column);

    public bool Save()
    {
        var text = _editor.Text;
        try { File.WriteAllText(Path, text); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        _editor.MarkClean();
        DiskTimestamp = DocumentFiles.Stamp(Path);
        Refresh();
        Saved?.Invoke();
        return true;
    }

    /// <summary>Raised after the file was written (the language host stops overriding it with unsaved text then).</summary>
    public event Action? Saved;

    /// <summary>Raised when the document closes.</summary>
    public event Action? Closed;

    public bool CanClose() => true;
    public void Close() => Closed?.Invoke();
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
