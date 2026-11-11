using RadSharp.CodeEditing;

namespace RadSharp.Designer;

/// <summary>
/// The IDE's code editor: an <see cref="ICodeEditorView"/> over the <see cref="CodeEditor"/> control. It converts the
/// shell's 1-based lines and columns to the editor's 0-based ones, and keeps a caret reveal or scroll position asked
/// for before the editor has a size (a document restored at startup) until it is laid out.
/// </summary>
public sealed class CodeEditorAdapter : ICodeEditorView
{
    readonly CodeEditor _editor = new() { BorderStyle = BorderStyle.None };
    int? _pendingScroll;
    bool _revealPending;

    public CodeEditorAdapter()
    {
        _editor.TextChanged += (_, _) => TextChanged?.Invoke();
        _editor.Resized += _ => ApplyPending();
    }

    public Control Control => _editor;

    /// <summary>The editor control, for the language host's hooks (tokens, squiggles, completion, hover).</summary>
    public CodeEditor Editor => _editor;

    public string Text { get => _editor.Text; set => _editor.Text = value ?? ""; }
    public event Action? TextChanged;

    public void ApplyEdits(IReadOnlyList<TextEdit> edits) => _editor.ApplyEdits(edits);

    public bool IsModified => _editor.IsModified;
    public void MarkClean() => _editor.Buffer.MarkClean();

    public string Language
    {
        get => _editor.Language switch { "csharp" => "csharp", "json" => "json", _ => "plain" };
        set
        {
            var language = value is "csharp" or "json" ? value : "plain";
            if (language == "json")
            {
                // The built-in colouriser is C#'s; JSON gets its own token source and brace folding.
                _editor.Language = "plain";
                _editor.TokenSource = new JsonTokenSource();
                _editor.FoldingSource = new BraceFoldingSource();
                _editor.LineCommentPrefix = "";
                _language = "json";
                return;
            }
            if (_language == "json") _editor.TokenSource = null;
            _language = language;
            _editor.Language = language;
            _editor.LineCommentPrefix = language == "csharp" ? "//" : "";
            _editor.AutoCloseBrackets = language == "csharp";
        }
    }
    string _language = "csharp";

    public (int Line, int Column) Caret
    {
        get
        {
            var p = _editor.CaretPosition;
            return (p.Line + 1, p.Column + 1);
        }
    }

    public void SetCaret(int line, int column)
    {
        var snap = _editor.Snapshot;
        int l = Math.Clamp(line - 1, 0, snap.LineCount - 1);
        _editor.GoTo(l, Math.Clamp(column - 1, 0, snap.GetLineLength(l)));
        if (_editor.Height <= 0) _revealPending = true;
    }

    public int ScrollOffset
    {
        get => _pendingScroll ?? _editor.ScrollY;
        set
        {
            if (_editor.Height <= 0) { _pendingScroll = value; _revealPending = false; return; }
            _editor.ScrollY = value;
        }
    }

    void ApplyPending()
    {
        if (_editor.Height <= 0) return;
        if (_pendingScroll is { } s) { _pendingScroll = null; _editor.ScrollY = s; }
        else if (_revealPending) _editor.ScrollToCaret(center: true);
        _revealPending = false;
    }

    public bool ReadOnly { get => _editor.ReadOnly; set => _editor.ReadOnly = value; }
    public bool CanUndo => _editor.CanUndo;
    public bool CanRedo => _editor.CanRedo;
    public void Undo() => _editor.Undo();
    public void Redo() => _editor.Redo();
    public void Cut() => _editor.CutToClipboard();
    public void Copy() => _editor.CopyToClipboard();
    public void Paste() => _editor.PasteFromClipboard();
    public void Delete()
    {
        if (!_editor.ReadOnly) _editor.DeleteForward();
    }
    public void SelectAll() => _editor.SelectAll();
    public void Focus() => _editor.Focus();
}
