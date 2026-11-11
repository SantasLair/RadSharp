using RadSharp.Ide.Language;

namespace RadSharp.Designer;

/// <summary>Rename (F2), step one: the new name.</summary>
public sealed class RenameSymbolDialog : Form
{
    readonly Edit _name;

    public RenameSymbolDialog(string oldName, Form? owner)
    {
        DialogLayout.Setup(this, "Rename", 420, 150, owner);
        DialogLayout.Put(this, new Label { Caption = $"Rename '{oldName}' to:" }, 16, 18, 388, 24);
        _name = DialogLayout.Put(this, new Edit { Name = "edtNewName", Text = oldName }, 16, 46, 388, 26);
        DialogLayout.Put(this, new Button { Name = "btnOk", Caption = "Preview…", Default = true, ModalResult = ModalResult.Ok }, 216, 100, 90, 30);
        DialogLayout.Put(this, new Button { Name = "btnCancel", Caption = "Cancel", Cancel = true, ModalResult = ModalResult.Cancel }, 314, 100, 90, 30);
        PerformLayout();
        Shown += _ => { _name.Focus(); _name.SelectAll(); };
    }

    public string NewName => _name.Text.Trim();
}

/// <summary>Rename, step two: every line that changes, old and new, before anything is applied.</summary>
public sealed class RenamePreviewDialog : Form
{
    public RenamePreviewDialog(string title, IReadOnlyList<PreviewLine> lines, string? projectDirectory, Form? owner)
    {
        DialogLayout.Setup(this, title, 720, 420, owner);
        int files = lines.Select(l => l.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        DialogLayout.Put(this, new Label { Name = "lblSummary", Caption = $"{lines.Count} line(s) in {files} file(s) change:" }, 16, 14, 688, 24);
        var list = DialogLayout.Put(this, new ListBox { Name = "lstPreview", ItemHeight = 20 }, 16, 42, 688, 320);
        list.Items = [.. lines.Select(l => $"{Display(l.File, projectDirectory)}({l.Line}):   {l.OldText}   →   {l.NewText}")];
        DialogLayout.Put(this, new Button { Name = "btnApply", Caption = "Apply", Default = true, ModalResult = ModalResult.Ok }, 516, 376, 90, 30);
        DialogLayout.Put(this, new Button { Name = "btnCancel", Caption = "Cancel", Cancel = true, ModalResult = ModalResult.Cancel }, 614, 376, 90, 30);
        PerformLayout();
    }

    static string Display(string file, string? directory) =>
        directory != null && file.StartsWith(directory, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(directory, file) : Path.GetFileName(file);
}
