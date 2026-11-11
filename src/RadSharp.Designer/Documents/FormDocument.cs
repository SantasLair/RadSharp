using System.Text.Json.Nodes;
using RadSharp.Design;
using RadSharp.Design.Code;

namespace RadSharp.Designer;

/// <summary>A form open in the designer: its file, its <see cref="DesignDocument"/> and the design surface showing it.</summary>
public sealed class FormDocument : IDocumentView, IEditTarget
{
    bool _dirty;

    public FormDocument(string path, DesignDocument document, DesignSurface surface)
    {
        Path = path;
        Document = document;
        Surface = surface;
        DiskTimestamp = DocumentFiles.Stamp(path);
        document.Changed += Refresh;
        document.FormReplaced += Refresh;
    }

    public string Kind => "form";
    public string Path { get; }
    string? IDocumentView.Path => Path;
    public DesignDocument Document { get; }
    public DesignSurface Surface { get; }
    public string ClassName => RadProject.FormClassName(Path);
    /// <summary>The code-behind file (<c>Form1.cs</c> beside <c>Form1.radform.json</c>).</summary>
    public string CodeFile => RadProject.CodeFileFor(Path);
    public string Title => ClassName;
    public bool IsDirty => _dirty;
    public Control View => Surface;
    public DateTime DiskTimestamp { get; private set; }

    public event Action? StateChanged;

    void Refresh()
    {
        bool dirty = Document.Modified;
        if (dirty == _dirty) return;
        _dirty = dirty;
        StateChanged?.Invoke();
    }

    public bool Save()
    {
        try
        {
            File.WriteAllText(Path, Document.Save());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        Document.MarkSaved();
        DiskTimestamp = DocumentFiles.Stamp(Path);
        Refresh();
        return true;
    }

    public bool CanClose() => true;
    public void Close() { }
    public void Focus() => Surface.Focus();

    public bool Reload()
    {
        try
        {
            Document.Load(File.ReadAllText(Path));
            Document.FileName = Path;
        }
        catch (Exception e) when (e is IOException or FormFileException or UnauthorizedAccessException) { return false; }
        DiskTimestamp = DocumentFiles.Stamp(Path);
        Refresh();
        Surface.Invalidate();
        return true;
    }

    public JsonObject? SaveState() => null;
    public void RestoreState(JsonObject state) { }

    // ---- Edit commands (the same as the design surface's own keys) ----

    public bool CanUndo => Document.CanUndo;
    public bool CanRedo => Document.CanRedo;
    public void Undo() => Document.Undo();
    public void Redo() => Document.Redo();
    public void Cut() { Surface.CopySelection(); Document.DeleteSelection(); }
    public void Copy() => Surface.CopySelection();
    public void Paste() => Surface.PasteClipboard();
    public void Delete() => Document.DeleteSelection();
    public void SelectAll() => Document.SelectMany(Document.PasteTarget().Children.Where(c => c.IsShown || c is Component));
}
