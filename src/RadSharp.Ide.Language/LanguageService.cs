using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;
using RadSharp.Design.Code;

namespace RadSharp.Ide.Language;

/// <summary>
/// C# language features over a <see cref="ProjectWorkspace"/>: classification, diagnostics, completion,
/// signature help, quick info, go to definition, find references, rename, formatting and code fixes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading and the UI.</b> Every method is async, takes a <see cref="CancellationToken"/>, and does its
/// work on the thread pool against an immutable <see cref="WorkspaceSnapshot"/>, so it is safe to call from any
/// thread (including the UI thread, where the call returns at once). This class knows nothing about RADSharp's
/// UI. A caller on the UI thread gets results back in one of two ways:
/// </para>
/// <list type="bullet">
/// <item>With RADSharp's <c>UiSynchronizationContext</c> installed, <c>await</c> resumes on the UI thread.</item>
/// <item>Otherwise, continue on the pool and hand the result over with <c>Application.Post</c>:
/// <code>
/// var version = workspace.UpdateDocumentText(path, buffer.Text);
/// _ = Task.Run(async () =>
/// {
///     var result = await language.ClassifyAsync(path, cancel: token);
///     Application.Post(() =>
///     {
///         if (workspace.IsCurrent(result.Version)) editor.SetTokens(result.Value);   // else: stale, drop it
///     });
/// });
/// </code></item>
/// </list>
/// <para>
/// Cancel the previous request when a new one supersedes it (each keystroke cancels the last completion or
/// classification); a cancelled request throws <see cref="OperationCanceledException"/>. Results carry the
/// <see cref="ProjectWorkspace.Version"/> they were computed from: drop any whose version is no longer current.
/// </para>
/// <para>
/// Offsets are 0-based positions in the document's text at that version; lines and columns are 1-based.
/// </para>
/// </remarks>
public sealed partial class LanguageService(ProjectWorkspace workspace)
{
    public ProjectWorkspace Workspace { get; } = workspace;

    static Task<T> Run<T>(Func<Task<T>> work, CancellationToken cancel) => Task.Run(work, cancel);

    (WorkspaceSnapshot Snapshot, Document Document) Source(string path)
    {
        var snapshot = Workspace.Snapshot;
        var id = Workspace.FindId(path, out bool isForm);
        if (id == null || isForm || snapshot.Solution.GetDocument(id) is not { } doc)
            throw new ArgumentException($"{path} is not a C# file of the project.", nameof(path));
        return (snapshot, doc);
    }

    /// <summary><see cref="Source"/>, with the generated code brought up to date with the forms.</summary>
    async Task<(WorkspaceSnapshot Snapshot, Document Document)> SourceAsync(string path, CancellationToken cancel)
    {
        var (snapshot, doc) = Source(path);
        await Workspace.EnsureGeneratedAsync(snapshot, cancel).ConfigureAwait(false);
        return (snapshot, doc);
    }

    // ---- Classification ----

    /// <summary>
    /// The fast pass: colours from the syntax tree alone (keywords, literals, comments, punctuation, declared
    /// names), so highlighting never lags behind typing. <paramref name="range"/> limits it to the visible part.
    /// </summary>
    public Task<Versioned<IReadOnlyList<ClassifiedRange>>> ClassifySyntaxAsync(string path, TextRange? range = null, CancellationToken cancel = default) =>
        Run(async () =>
        {
            var (snapshot, doc) = Source(path);
            var root = await doc.GetSyntaxRootAsync(cancel).ConfigureAwait(false);
            var span = range is { } r ? new TextSpan(r.Start, r.Length) : root!.FullSpan;
            return new Versioned<IReadOnlyList<ClassifiedRange>>(snapshot.Version, SyntacticClassifier.Classify(root!, span, cancel));
        }, cancel);

    /// <summary>
    /// The full pass: Roslyn's classifier with the semantic model (types, fields, locals, methods, statics…).
    /// It replaces the syntactic pass's colours for the same range.
    /// </summary>
    public Task<Versioned<IReadOnlyList<ClassifiedRange>>> ClassifyAsync(string path, TextRange? range = null, CancellationToken cancel = default) =>
        Run(async () =>
        {
            var (snapshot, doc) = await SourceAsync(path, cancel).ConfigureAwait(false);
            var text = await doc.GetTextAsync(cancel).ConfigureAwait(false);
            var span = range is { } r ? new TextSpan(r.Start, r.Length) : new TextSpan(0, text.Length);
            var spans = await Classifier.GetClassifiedSpansAsync(doc, span, cancel).ConfigureAwait(false);
            IReadOnlyList<ClassifiedRange> result =
            [
                .. spans.Where(s => !ClassificationTypeNames.AdditiveTypeNames.Contains(s.ClassificationType))
                    .OrderBy(s => s.TextSpan.Start)
                    .Select(s => new ClassifiedRange(s.TextSpan.Start, s.TextSpan.Length, s.ClassificationType)),
            ];
            return new Versioned<IReadOnlyList<ClassifiedRange>>(snapshot.Version, result);
        }, cancel);

    // ---- Diagnostics ----

    /// <summary>
    /// Diagnostics for one file: for a .cs file the compiler's and analysers' messages in it, for a
    /// .radform.json the form generator's (RAD1001 missing handler, RAD1002 unknown class…).
    /// </summary>
    public Task<Versioned<IReadOnlyList<DiagnosticInfo>>> GetDiagnosticsAsync(string path, CancellationToken cancel = default) =>
        Run(async () =>
        {
            var snapshot = Workspace.Snapshot;
            var full = Path.GetFullPath(path);
            var id = Workspace.FindId(full, out bool isForm) ?? throw new ArgumentException($"{path} is not in the project.", nameof(path));
            var generator = await Workspace.GetGeneratorDiagnosticsAsync(snapshot, cancel).ConfigureAwait(false);
            var found = new List<Diagnostic>(generator.Where(d => SamePath(d.Location.GetLineSpan().Path, full)));
            if (!isForm && snapshot.Solution.GetDocument(id) is { } doc)
            {
                var model = await doc.GetSemanticModelAsync(cancel).ConfigureAwait(false);
                found.AddRange(model!.GetDiagnostics(cancellationToken: cancel));
                if (!Workspace.Analyzers.IsEmpty)
                {
                    var analyzers = WithAnalyzers(snapshot, model.Compilation);
                    found.AddRange(await analyzers.GetAnalyzerSyntaxDiagnosticsAsync(model.SyntaxTree, cancel).ConfigureAwait(false));
                    found.AddRange(await analyzers.GetAnalyzerSemanticDiagnosticsAsync(model, null, cancel).ConfigureAwait(false));
                }
            }
            return new Versioned<IReadOnlyList<DiagnosticInfo>>(snapshot.Version, Convert(found));
        }, cancel);

    /// <summary>Every diagnostic in the project: compiler, generators and analysers, for the Problems list.</summary>
    public Task<Versioned<IReadOnlyList<DiagnosticInfo>>> GetProjectDiagnosticsAsync(CancellationToken cancel = default) =>
        Run(async () =>
        {
            var snapshot = Workspace.Snapshot;
            var compilation = await snapshot.Project.GetCompilationAsync(cancel).ConfigureAwait(false);
            var found = new List<Diagnostic>(await Workspace.GetGeneratorDiagnosticsAsync(snapshot, cancel).ConfigureAwait(false));
            found.AddRange(compilation!.GetDiagnostics(cancel));
            if (!Workspace.Analyzers.IsEmpty)
                found.AddRange(await WithAnalyzers(snapshot, compilation).GetAnalyzerDiagnosticsAsync(cancel).ConfigureAwait(false));
            return new Versioned<IReadOnlyList<DiagnosticInfo>>(snapshot.Version, Convert(found));
        }, cancel);

    CompilationWithAnalyzers WithAnalyzers(WorkspaceSnapshot snapshot, Compilation compilation)
    {
        var additional = snapshot.Project.AdditionalDocuments
            .Select(d => (AdditionalText)new FormText(d.FilePath ?? d.Name, d.TryGetText(out var t) ? t : d.GetTextAsync().GetAwaiter().GetResult()))
            .ToImmutableArray();
        return compilation.WithAnalyzers(Workspace.Analyzers, new AnalyzerOptions(additional));
    }

    sealed class FormText(string path, SourceText text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => text;
    }

    IReadOnlyList<DiagnosticInfo> Convert(IEnumerable<Diagnostic> diagnostics) =>
    [
        .. diagnostics.Where(d => d.Severity != DiagnosticSeverity.Hidden && !d.IsSuppressed)
            .Select(ToInfo)
            .Distinct()
            .OrderBy(d => d.File, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Start).ThenBy(d => d.Id, StringComparer.Ordinal),
    ];

    DiagnosticInfo ToInfo(Diagnostic d)
    {
        var line = d.Location.GetLineSpan();
        var file = line.Path ?? "";
        var span = d.Location.SourceSpan;
        return new DiagnosticInfo(file, span.Start, span.Length,
            line.StartLinePosition.Line + 1, line.StartLinePosition.Character + 1, line.EndLinePosition.Line + 1, line.EndLinePosition.Character + 1,
            d.Severity.ToString().ToLowerInvariant(), d.Id, d.GetMessage(), file.Length > 0 && IsGeneratedPath(file));
    }

    bool IsGeneratedPath(string file) => Workspace.FindId(file, out _) == null;

    static bool SamePath(string? a, string b) =>
        a != null && a.Length > 0 && string.Equals(Path.GetFullPath(a), b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ---- Quick info ----

    /// <summary>Hover text for the symbol at <paramref name="position"/> (signature, documentation, exceptions), or null.</summary>
    public Task<Versioned<QuickInfoResult?>> GetQuickInfoAsync(string path, int position, CancellationToken cancel = default) =>
        Run(async () =>
        {
            var (snapshot, doc) = await SourceAsync(path, cancel).ConfigureAwait(false);
            var service = QuickInfoService.GetService(doc);
            var item = service == null ? null : await service.GetQuickInfoAsync(doc, position, cancel).ConfigureAwait(false);
            QuickInfoResult? result = item == null ? null : new QuickInfoResult(new TextRange(item.Span.Start, item.Span.Length),
                [.. item.Sections.Select(s => new QuickInfoSection(s.Kind, s.Text))]);
            return new Versioned<QuickInfoResult?>(snapshot.Version, result);
        }, cancel);

    // ---- Navigation ----

    /// <summary>
    /// Where the symbol at <paramref name="position"/> is defined (several places for partial types). A control
    /// field leads to the control's <c>"name"</c> in its form file (Kind <c>"Form"</c>).
    /// </summary>
    public Task<Versioned<IReadOnlyList<DefinitionLocation>>> GoToDefinitionAsync(string path, int position, CancellationToken cancel = default) =>
        Run(async () =>
        {
            var (snapshot, doc) = await SourceAsync(path, cancel).ConfigureAwait(false);
            var symbol = await FindSymbolAsync(doc, position, cancel).ConfigureAwait(false);
            var result = new List<DefinitionLocation>();
            if (symbol != null)
            {
                symbol = Definition(symbol);
                var display = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                foreach (var location in symbol.Locations)
                {
                    if (!location.IsInSource)
                    {
                        result.Add(new DefinitionLocation(null, 0, 0, 0, 0, "Metadata", display, symbol.ContainingAssembly?.Name));
                        continue;
                    }
                    var file = location.SourceTree!.FilePath;
                    if (!IsGeneratedPath(file))
                    {
                        result.Add(Location(file, location, "Source", display));
                        continue;
                    }
                    if (symbol is IFieldSymbol && await FormLocationAsync(snapshot, symbol, cancel).ConfigureAwait(false) is { } form)
                        result.Add(form with { Symbol = display });
                    else
                        result.Add(Location(file, location, "Generated", display));
                }
            }
            return new Versioned<IReadOnlyList<DefinitionLocation>>(snapshot.Version, result.Distinct().ToList());
        }, cancel);

    static DefinitionLocation Location(string file, Location location, string kind, string display)
    {
        var line = location.GetLineSpan().StartLinePosition;
        return new DefinitionLocation(file, location.SourceSpan.Start, location.SourceSpan.Length, line.Line + 1, line.Character + 1, kind, display, null);
    }

    /// <summary>A control field's definition: the control's name in its form file.</summary>
    async Task<DefinitionLocation?> FormLocationAsync(WorkspaceSnapshot snapshot, ISymbol field, CancellationToken cancel)
    {
        var className = field.ContainingType?.Name;
        var form = snapshot.Project.AdditionalDocuments.FirstOrDefault(d => d.FilePath != null
            && ProjectWorkspace.IsGeneratedForm(d.FilePath) && RadProject.FormClassName(d.FilePath) == className);
        if (form == null) return null;
        var text = await form.GetTextAsync(cancel).ConfigureAwait(false);
        var match = Regex.Match(text.ToString(), "\"name\"\\s*:\\s*\"(" + Regex.Escape(field.Name) + ")\"");
        if (!match.Success) return null;
        var start = match.Groups[1].Index;
        var line = text.Lines.GetLinePosition(start);
        return new DefinitionLocation(form.FilePath, start, field.Name.Length, line.Line + 1, line.Character + 1, "Form", field.Name, null);
    }

    /// <summary>Every reference to the symbol at <paramref name="position"/>, definitions included, across the project.</summary>
    public Task<Versioned<IReadOnlyList<ReferenceInfo>>> FindReferencesAsync(string path, int position, CancellationToken cancel = default) =>
        Run(async () =>
        {
            var (snapshot, doc) = await SourceAsync(path, cancel).ConfigureAwait(false);
            var symbol = await FindSymbolAsync(doc, position, cancel).ConfigureAwait(false);
            var result = new List<ReferenceInfo>();
            if (symbol != null)
            {
                var references = await SymbolFinder.FindReferencesAsync(Definition(symbol), snapshot.Solution, cancel).ConfigureAwait(false);
                foreach (var referenced in references)
                {
                    foreach (var location in referenced.Definition.Locations.Where(l => l.IsInSource))
                        result.Add(await ReferenceAsync(location, isDefinition: true, cancel).ConfigureAwait(false));
                    foreach (var location in referenced.Locations.Where(l => !l.IsImplicit))
                        result.Add(await ReferenceAsync(location.Location, isDefinition: false, cancel).ConfigureAwait(false));
                }
            }
            IReadOnlyList<ReferenceInfo> sorted =
            [
                .. result.DistinctBy(r => (r.File, r.Start)).OrderBy(r => r.IsInGeneratedCode)
                    .ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Start),
            ];
            return new Versioned<IReadOnlyList<ReferenceInfo>>(snapshot.Version, sorted);
        }, cancel);

    async Task<ReferenceInfo> ReferenceAsync(Location location, bool isDefinition, CancellationToken cancel)
    {
        var text = await location.SourceTree!.GetTextAsync(cancel).ConfigureAwait(false);
        var line = text.Lines.GetLinePosition(location.SourceSpan.Start);
        var file = location.SourceTree.FilePath;
        return new ReferenceInfo(file, location.SourceSpan.Start, location.SourceSpan.Length, line.Line + 1, line.Character + 1,
            isDefinition, IsGeneratedPath(file), text.Lines[line.Line].ToString().Trim());
    }

    /// <summary>The symbol at a position, or just before it (the caret at the end of a word).</summary>
    static async Task<ISymbol?> FindSymbolAsync(Document doc, int position, CancellationToken cancel)
    {
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(doc, position, cancel).ConfigureAwait(false);
        if (symbol == null && position > 0) symbol = await SymbolFinder.FindSymbolAtPositionAsync(doc, position - 1, cancel).ConfigureAwait(false);
        return symbol;
    }

    static ISymbol Definition(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { ReducedFrom: { } reduced }) symbol = reduced;
        return symbol.OriginalDefinition;
    }

    // ---- Formatting ----

    /// <summary>Formats the document, or only <paramref name="range"/> (a selection, or pasted text).</summary>
    public Task<Versioned<IReadOnlyList<TextEdit>>> FormatAsync(string path, TextRange? range = null, CancellationToken cancel = default) =>
        Run(async () =>
        {
            var (snapshot, doc) = await SourceAsync(path, cancel).ConfigureAwait(false);
            var formatted = range is { } r
                ? await Formatter.FormatAsync(doc, new TextSpan(r.Start, r.Length), cancellationToken: cancel).ConfigureAwait(false)
                : await Formatter.FormatAsync(doc, cancellationToken: cancel).ConfigureAwait(false);
            var changes = await formatted.GetTextChangesAsync(doc, cancel).ConfigureAwait(false);
            IReadOnlyList<TextEdit> edits = [.. changes.Select(c => new TextEdit(c.Span.Start, c.Span.Length, c.NewText ?? ""))];
            return new Versioned<IReadOnlyList<TextEdit>>(snapshot.Version, edits);
        }, cancel);

    // ---- Edits ----

    /// <summary>The documents that differ between two solutions, as edits against the old one.</summary>
    async Task<IReadOnlyList<DocumentEdits>> DiffAsync(Solution old, Solution updated, CancellationToken cancel)
    {
        var result = new List<DocumentEdits>();
        foreach (var project in updated.GetChanges(old).GetProjectChanges())
        {
            foreach (var id in project.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true).Concat(project.GetChangedAdditionalDocuments()))
            {
                var before = ProjectWorkspace.TextDocument(old, id);
                var after = ProjectWorkspace.TextDocument(updated, id);
                if (before?.FilePath == null || after == null || IsGeneratedPath(before.FilePath)) continue;
                var oldText = await before.GetTextAsync(cancel).ConfigureAwait(false);
                var newText = await after.GetTextAsync(cancel).ConfigureAwait(false);
                // Documents diff through their syntax trees (one change per renamed token); other texts as one change.
                var changes = before is Document oldDoc && after is Document newDoc
                    ? await newDoc.GetTextChangesAsync(oldDoc, cancel).ConfigureAwait(false)
                    : newText.GetTextChanges(oldText);
                var edits = changes.Select(c => Trim(oldText, c)).Where(e => e.Length > 0 || e.NewText.Length > 0).ToList();
                if (edits.Count > 0) result.Add(new DocumentEdits(before.FilePath, edits, newText.ToString()));
            }
        }
        return [.. result.OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A change narrowed to the part that differs (common leading and trailing text dropped).</summary>
    static TextEdit Trim(SourceText old, TextChange change)
    {
        var removed = old.ToString(change.Span);
        var added = change.NewText ?? "";
        int prefix = 0, max = Math.Min(removed.Length, added.Length);
        while (prefix < max && removed[prefix] == added[prefix]) prefix++;
        int suffix = 0;
        while (suffix < max - prefix && removed[^(suffix + 1)] == added[^(suffix + 1)]) suffix++;
        return new TextEdit(change.Span.Start + prefix, removed.Length - prefix - suffix, added[prefix..(added.Length - suffix)]);
    }

    /// <summary>One preview line per changed line: the old line and the line after the edits.</summary>
    static IReadOnlyList<PreviewLine> Preview(WorkspaceSnapshot snapshot, ProjectWorkspace workspace, IReadOnlyList<DocumentEdits> documents)
    {
        var lines = new List<PreviewLine>();
        foreach (var doc in documents)
        {
            var id = workspace.FindId(doc.File, out _);
            var textDoc = id == null ? null : ProjectWorkspace.TextDocument(snapshot.Solution, id);
            if (textDoc == null) continue;
            var oldText = textDoc.GetTextAsync().GetAwaiter().GetResult();
            foreach (var group in doc.Edits.GroupBy(e => oldText.Lines.GetLineFromPosition(e.Start).LineNumber).OrderBy(g => g.Key))
            {
                var line = oldText.Lines[group.Key];
                var lineText = line.ToString();
                var updated = lineText;
                foreach (var edit in group.OrderByDescending(e => e.Start))
                {
                    int start = edit.Start - line.Start;
                    int length = Math.Min(edit.Length, Math.Max(0, updated.Length - start));
                    if (start < 0 || start > updated.Length) continue;
                    updated = updated[..start] + edit.NewText + updated[(start + length)..];
                }
                lines.Add(new PreviewLine(doc.File, group.Key + 1, lineText.Trim(), updated.Split('\n')[0].Trim()));
            }
        }
        return lines;
    }
}
