using System.Text.Json.Nodes;

namespace RadSharp.Designer;

/// <summary>
/// A read-only unified diff of a file against git HEAD (Project › Compare with HEAD), shown in the code editor. It
/// has no file of its own, so it isn't saved and is not restored with the session.
/// </summary>
public sealed class DiffDocument : IDocumentView, IEditTarget
{
    readonly ICodeEditorView _editor;

    public DiffDocument(string title, string diff, string sourceFile, ICodeEditorView? editor = null)
    {
        Title = title;
        SourceFile = sourceFile;
        _editor = editor ?? CodeEditors.Factory();
        _editor.Control.Name = "";
        _editor.Text = diff;
        _editor.ReadOnly = true;
    }

    /// <summary>The file this diff is of.</summary>
    public string SourceFile { get; }

    public string Kind => "diff";
    public string Title { get; }
    public string? Path => null;
    public bool IsDirty => false;
    public Control View => _editor.Control;
    public string Text => _editor.Text;
    public DateTime DiskTimestamp => default;
#pragma warning disable CS0067
    public event Action? StateChanged;
#pragma warning restore CS0067

    public bool Save() => true;
    public bool CanClose() => true;
    public void Close() { }
    public void Focus() => _editor.Focus();
    public bool Reload() => true;
    public JsonObject? SaveState() => null;
    public void RestoreState(JsonObject state) { }

    public bool CanUndo => false;
    public bool CanRedo => false;
    public void Undo() { }
    public void Redo() { }
    public void Cut() { }
    public void Copy() => _editor.Copy();
    public void Paste() { }
    public void Delete() { }
    public void SelectAll() => _editor.SelectAll();
}
