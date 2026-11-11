using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using RadSharp.Design.Code;

namespace RadSharp.Ide.Language;

public sealed partial class LanguageService
{
    /// <summary>
    /// Semantic rename (F2) of the symbol at <paramref name="position"/> across the project. Nothing is
    /// changed: the result holds the edits per document and a preview; apply them with
    /// <see cref="ProjectWorkspace.ApplyEdits"/> or to the editor buffers. Renaming a control's field renames
    /// the control (see <see cref="RenameControlAsync"/>, with the form file's edits included); renaming an
    /// event handler also updates the form files that bind it.
    /// </summary>
    public Task<RenameResult> RenameAsync(string path, int position, string newName, CancellationToken cancel = default) =>
        Run(async () =>
        {
            var (snapshot, doc) = await SourceAsync(path, cancel).ConfigureAwait(false);
            var symbol = await FindSymbolAsync(doc, position, cancel).ConfigureAwait(false);
            if (symbol == null) return RenameResult.Failed(snapshot.Version, "There is nothing to rename here.");
            symbol = Definition(symbol);
            if (!IsIdentifier(newName)) return RenameResult.Failed(snapshot.Version, $"'{newName}' is not a valid name.");
            if (!symbol.Locations.Any(l => l.IsInSource))
                return RenameResult.Failed(snapshot.Version, $"'{symbol.Name}' is defined outside the project and can't be renamed.");

            bool generatedOnly = symbol.Locations.Where(l => l.IsInSource).All(l => IsGeneratedPath(l.SourceTree!.FilePath));
            if (generatedOnly && symbol is IFieldSymbol field && FormFileOf(snapshot, field.ContainingType) is { } formFile)
                return await RenameControlCoreAsync(snapshot, formFile, field.Name, newName, editFormFile: true, cancel).ConfigureAwait(false);
            if (generatedOnly) return RenameResult.Failed(snapshot.Version, $"'{symbol.Name}' is generated code and can't be renamed here.");

            var renamed = await Renamer.RenameSymbolAsync(snapshot.Solution, symbol, new SymbolRenameOptions(), newName, cancel).ConfigureAwait(false);
            var documents = new List<DocumentEdits>(await DiffAsync(snapshot.Solution, renamed, cancel).ConfigureAwait(false));

            // A handler named in a form file: keep the binding.
            if (symbol is IMethodSymbol && FormFileOf(snapshot, symbol.ContainingType) is { } form)
                if (await FormEditsAsync(snapshot, form, null, [(symbol.Name, newName)], cancel).ConfigureAwait(false) is { } formEdits)
                    documents.Add(formEdits);
            return new RenameResult(snapshot.Version, true, null, documents, Preview(snapshot, Workspace, documents));
        }, cancel);

    /// <summary>
    /// Renames a control in code, for the designer: its field's references, and the handlers named after it
    /// (<c>btnOld_Click</c> → <c>btnNew_Click</c>) with their references. Call it while the workspace still has
    /// the form with the old name (before <see cref="ProjectWorkspace.UpdateFormText"/> with the renamed form),
    /// then apply the edits and update the form, renaming the handlers in the designer's document per
    /// <see cref="RenameResult.HandlerRenames"/>. With <paramref name="editFormFile"/> the form file's own edits
    /// (the control's name and its handler bindings) are included too, for a rename started from code.
    /// </summary>
    public Task<RenameResult> RenameControlAsync(string formPath, string oldName, string newName, bool editFormFile = false, CancellationToken cancel = default) =>
        Run(() => RenameControlCoreAsync(Workspace.Snapshot, Path.GetFullPath(formPath), oldName, newName, editFormFile, cancel), cancel);

    async Task<RenameResult> RenameControlCoreAsync(WorkspaceSnapshot snapshot, string formPath, string oldName, string newName, bool editFormFile, CancellationToken cancel)
    {
        if (!IsIdentifier(newName)) return RenameResult.Failed(snapshot.Version, $"'{newName}' is not a valid name.");
        if (oldName == newName) return new RenameResult(snapshot.Version, true, null, [], []);
        var className = RadProject.FormClassName(formPath);

        var solution = snapshot.Solution;
        var formClass = await FormClassAsync(solution, snapshot.Project.Id, className, cancel).ConfigureAwait(false);
        if (formClass == null) return RenameResult.Failed(snapshot.Version, $"The form class {className} wasn't found.");
        if (formClass.GetMembers(newName).Any())
            return RenameResult.Failed(snapshot.Version, $"{className} already has a member named '{newName}'.");

        // The handlers: methods of the form class named after the control, declared in the user's code.
        var handlers = formClass.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.Name.StartsWith(oldName + "_", StringComparison.Ordinal) && m.Locations.Any(l => l.IsInSource && !IsGeneratedPath(l.SourceTree!.FilePath)))
            .Select(m => m.Name).Distinct().ToList();
        var handlerRenames = new List<(string OldName, string NewName)>();
        foreach (var handler in handlers)
        {
            var renamedHandler = newName + handler[oldName.Length..];
            var cls = await FormClassAsync(solution, snapshot.Project.Id, className, cancel).ConfigureAwait(false);
            if (cls == null) break;
            if (cls.GetMembers(renamedHandler).Any()) return RenameResult.Failed(snapshot.Version, $"{className} already has a member named '{renamedHandler}'.");
            var method = cls.GetMembers(handler).OfType<IMethodSymbol>().FirstOrDefault();
            if (method == null) continue;
            solution = await Renamer.RenameSymbolAsync(solution, method, new SymbolRenameOptions(), renamedHandler, cancel).ConfigureAwait(false);
            handlerRenames.Add((handler, renamedHandler));
        }

        // The field lives in generated code, which Renamer can't edit: replace its references in the user's code.
        var formClassNow = await FormClassAsync(solution, snapshot.Project.Id, className, cancel).ConfigureAwait(false);
        if (formClassNow?.GetMembers(oldName).OfType<IFieldSymbol>().FirstOrDefault() is { } field)
        {
            var references = await SymbolFinder.FindReferencesAsync(field, solution, cancel).ConfigureAwait(false);
            var byDocument = references.SelectMany(r => r.Locations)
                .Where(l => !l.IsImplicit && l.Document.FilePath != null && !IsGeneratedPath(l.Document.FilePath))
                .GroupBy(l => l.Document.Id);
            foreach (var group in byDocument)
            {
                var document = solution.GetDocument(group.Key)!;
                var text = await document.GetTextAsync(cancel).ConfigureAwait(false);
                var changes = group.Select(l => l.Location.SourceSpan).Distinct()
                    .Where(s => text.ToString(s) == oldName || text.ToString(s) == "@" + oldName)
                    .Select(s => new TextChange(s, newName));
                solution = solution.WithDocumentText(group.Key, text.WithChanges(changes));
            }
        }

        var documents = new List<DocumentEdits>(await DiffAsync(snapshot.Solution, solution, cancel).ConfigureAwait(false));
        if (editFormFile && await FormEditsAsync(snapshot, formPath, (oldName, newName), handlerRenames, cancel).ConfigureAwait(false) is { } formEdits)
            documents.Add(formEdits);
        return new RenameResult(snapshot.Version, true, null, documents, Preview(snapshot, Workspace, documents)) { HandlerRenames = handlerRenames };
    }

    /// <summary>The form file's edits for a control rename and/or handler renames: <c>"name"</c> and handler values.</summary>
    async Task<DocumentEdits?> FormEditsAsync(WorkspaceSnapshot snapshot, string formPath, (string Old, string New)? control,
        IReadOnlyList<(string OldName, string NewName)> handlers, CancellationToken cancel)
    {
        var id = Workspace.FindId(formPath, out bool isForm);
        if (id == null || !isForm || snapshot.Solution.GetAdditionalDocument(id) is not { } form) return null;
        var text = (await form.GetTextAsync(cancel).ConfigureAwait(false)).ToString();
        var edits = new List<TextEdit>();
        if (control is var (oldName, newName))
            foreach (Match m in Regex.Matches(text, "\"name\"\\s*:\\s*\"(" + Regex.Escape(oldName) + ")\""))
                edits.Add(new TextEdit(m.Groups[1].Index, m.Groups[1].Length, newName));
        foreach (var (oldHandler, newHandler) in handlers)
            foreach (Match m in Regex.Matches(text, "\"on[A-Z][A-Za-z0-9]*\"\\s*:\\s*\"(" + Regex.Escape(oldHandler) + ")\""))
                edits.Add(new TextEdit(m.Groups[1].Index, m.Groups[1].Length, newHandler));
        if (edits.Count == 0) return null;
        edits.Sort((a, b) => a.Start.CompareTo(b.Start));
        var result = text;
        foreach (var e in Enumerable.Reverse(edits)) result = result[..e.Start] + e.NewText + result[(e.Start + e.Length)..];
        return new DocumentEdits(form.FilePath!, edits, result);
    }

    /// <summary>The form file whose generated class is <paramref name="type"/>, if any.</summary>
    static string? FormFileOf(WorkspaceSnapshot snapshot, INamedTypeSymbol? type) =>
        type == null ? null : snapshot.Project.AdditionalDocuments
            .FirstOrDefault(d => d.FilePath != null && ProjectWorkspace.IsGeneratedForm(d.FilePath) && RadProject.FormClassName(d.FilePath) == type.Name)?.FilePath;

    static async Task<INamedTypeSymbol?> FormClassAsync(Solution solution, ProjectId project, string className, CancellationToken cancel)
    {
        var compilation = await solution.GetProject(project)!.GetCompilationAsync(cancel).ConfigureAwait(false);
        return compilation?.GetSymbolsWithName(className, SymbolFilter.Type, cancel).OfType<INamedTypeSymbol>()
            .FirstOrDefault(t => t.TypeKind == TypeKind.Class);
    }

    static bool IsIdentifier(string name) =>
        SyntaxFacts.IsValidIdentifier(name.StartsWith('@') ? name[1..] : name) && (name.StartsWith('@') || SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None);
}
