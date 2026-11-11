using RadSharp.Design.Code;

namespace RadSharp.Ide.Language;

// Plain data returned by the language service. Nothing here refers to RADSharp's UI or to Roslyn types, so
// a code editor (or any other client) can consume it directly.
//
// Conventions:
// - Offsets (Start, Length) are 0-based character offsets into the document text at the result's Version.
// - Line and Column are 1-based, like BuildMessage and compiler output.

/// <summary>
/// A result computed from one snapshot of the workspace. <see cref="Version"/> is the
/// <see cref="ProjectWorkspace.Version"/> the result was computed against: if the workspace has moved on
/// (<see cref="ProjectWorkspace.IsCurrent"/> is false), the offsets may no longer match the buffer and the
/// caller should drop the result and ask again.
/// </summary>
public sealed record Versioned<T>(long Version, T Value);

/// <summary>A replacement of <see cref="Length"/> characters at <see cref="Start"/> with <see cref="NewText"/>.</summary>
public sealed record TextEdit(int Start, int Length, string NewText);

/// <summary>A span of text.</summary>
public readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// A coloured span. <see cref="Kind"/> is one of Roslyn's classification names (<c>"keyword"</c>,
/// <c>"class name"</c>, <c>"method name"</c>, <c>"field name"</c>, <c>"string"</c>, <c>"comment"</c>…;
/// see <c>Microsoft.CodeAnalysis.Classification.ClassificationTypeNames</c>), so a theme can map them.
/// </summary>
public readonly record struct ClassifiedRange(int Start, int Length, string Kind);

/// <summary>A compiler, generator or analyser message for the squiggles and the Problems list.</summary>
/// <param name="File">Full path: a .cs file, a .radform.json (generator messages), or a generated file.</param>
/// <param name="Severity"><c>"error"</c>, <c>"warning"</c> or <c>"info"</c>, as in <see cref="BuildMessage"/>.</param>
public sealed record DiagnosticInfo(
    string File, int Start, int Length, int Line, int Column, int EndLine, int EndColumn,
    string Severity, string Id, string Message, bool IsInGeneratedCode)
{
    public bool IsError => Severity == "error";

    /// <summary>The same message as the designer's error list shows it.</summary>
    public BuildMessage ToBuildMessage() => new(File, Line, Column, Severity, Id, Message);

    public override string ToString() => $"{Path.GetFileName(File)}({Line},{Column}): {Severity} {Id}: {Message}";
}

/// <summary>A completion list at one position. Items are not filtered by what has been typed; see <see cref="Filter"/>.</summary>
/// <remarks><see cref="Span"/> is the text the chosen item replaces (the partial word under the caret).</remarks>
public sealed class CompletionResult
{
    internal CompletionResult(string file, TextRange span, IReadOnlyList<CompletionEntry> items, object state)
    {
        File = file;
        Span = span;
        Items = items;
        State = state;
    }

    public string File { get; }
    public TextRange Span { get; }
    public IReadOnlyList<CompletionEntry> Items { get; }

    /// <summary>The snapshot document this list was computed from (used to resolve items).</summary>
    internal object State { get; }

    /// <summary>
    /// Items whose filter text matches <paramref name="typed"/>: prefix first, then CamelCase humps
    /// (<c>"bS"</c> matches <c>btnSave</c>), each group in sort-text order.
    /// </summary>
    public IReadOnlyList<CompletionEntry> Filter(string typed)
    {
        if (typed.Length == 0) return Items;
        var prefix = Items.Where(i => i.FilterText.StartsWith(typed, StringComparison.OrdinalIgnoreCase));
        var humps = Items.Where(i => !i.FilterText.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && Humps(i.FilterText, typed));
        return [.. prefix.OrderBy(i => i.SortText, StringComparer.OrdinalIgnoreCase), .. humps.OrderBy(i => i.SortText, StringComparer.OrdinalIgnoreCase)];
    }

    static bool Humps(string candidate, string typed)
    {
        int c = 0;
        foreach (var ch in typed)
        {
            while (c < candidate.Length && char.ToLowerInvariant(candidate[c]) != char.ToLowerInvariant(ch)) c++;
            if (c == candidate.Length) return false;
            c++;
        }
        return char.ToLowerInvariant(candidate[0]) == char.ToLowerInvariant(typed[0]);
    }
}

/// <summary>One completion item.</summary>
/// <param name="Kind">
/// What the item is: <c>"Class"</c>, <c>"Method"</c>, <c>"Field"</c>, <c>"Property"</c>, <c>"Event"</c>,
/// <c>"Local"</c>, <c>"Parameter"</c>, <c>"Keyword"</c>, <c>"Namespace"</c>, <c>"Snippet"</c>… (Roslyn's
/// <c>WellKnownTags</c> names).
/// </param>
/// <param name="InlineDescription">Short text shown after the display text (a namespace to import, "snippet").</param>
public sealed record CompletionEntry(string DisplayText, string FilterText, string SortText, string Kind, string InlineDescription)
{
    internal object? Item { get; init; }
    internal Snippet? Snippet { get; init; }

    public bool IsSnippet => Snippet != null;
    public override string ToString() => $"{DisplayText} ({Kind})";
}

/// <summary>What committing a completion item does, plus its description for a tooltip.</summary>
/// <param name="Edits">
/// The changes to apply to the document, in the document as it was when the list was computed (the item's
/// text, plus sometimes a <c>using</c> at the top). Apply them from last to first.
/// </param>
/// <param name="CaretPosition">Where the caret goes after the edits (null: at the end of the inserted text).</param>
/// <param name="Placeholders">Snippet fields to tab through, in the document after the edits.</param>
public sealed record CompletionDetail(string Description, IReadOnlyList<TextEdit> Edits, int? CaretPosition, IReadOnlyList<TextRange> Placeholders);

/// <summary>Signature help inside an argument list.</summary>
/// <param name="ApplicableSpan">The argument list the help applies to; close the popup when the caret leaves it.</param>
public sealed record SignatureHelpResult(TextRange ApplicableSpan, IReadOnlyList<SignatureInfo> Signatures, int ActiveSignature, int ActiveParameter);

/// <summary>One overload: <c>void Foo(int a, string b)</c>.</summary>
public sealed record SignatureInfo(string Label, string Documentation, IReadOnlyList<ParameterInfo> Parameters);

/// <summary>A parameter; <see cref="LabelStart"/>/<see cref="LabelLength"/> locate it inside the signature's label, for bolding.</summary>
public sealed record ParameterInfo(string Name, string Display, int LabelStart, int LabelLength, string Documentation);

/// <summary>Hover text, in sections (<c>"Description"</c>, <c>"DocumentationComments"</c>, <c>"Exception"</c>…).</summary>
public sealed record QuickInfoResult(TextRange Span, IReadOnlyList<QuickInfoSection> Sections)
{
    /// <summary>All sections as plain text, one per paragraph.</summary>
    public string Text => string.Join("\n\n", Sections.Select(s => s.Text).Where(t => t.Length > 0));
}

public sealed record QuickInfoSection(string Kind, string Text);

/// <summary>Where a symbol is defined.</summary>
/// <param name="File">The file to open, or null for a symbol from a referenced assembly (<see cref="Kind"/> = Metadata).</param>
/// <param name="Kind">
/// <c>"Source"</c> (a .cs file), <c>"Form"</c> (a control field: the control's <c>"name"</c> in the
/// .radform.json, so the shell can open the designer with it selected), <c>"Generated"</c> (other generated
/// code), or <c>"Metadata"</c>.
/// </param>
public sealed record DefinitionLocation(string? File, int Start, int Length, int Line, int Column, string Kind, string Symbol, string? AssemblyName);

/// <summary>A reference to a symbol (or its definition) with the text of its line, for a results list.</summary>
public sealed record ReferenceInfo(string File, int Start, int Length, int Line, int Column, bool IsDefinition, bool IsInGeneratedCode, string LineText);

/// <summary>The edits to one document, with its resulting text.</summary>
public sealed record DocumentEdits(string File, IReadOnlyList<TextEdit> Edits, string NewText);

/// <summary>One changed line, for a rename preview.</summary>
public sealed record PreviewLine(string File, int Line, string OldText, string NewText);

/// <summary>
/// The outcome of a rename: the edits per document (nothing is written; apply them to the open buffers and
/// files, or with <see cref="ProjectWorkspace.ApplyEdits"/>), and a line-by-line preview.
/// </summary>
public sealed record RenameResult(long Version, bool Success, string? Error, IReadOnlyList<DocumentEdits> Documents, IReadOnlyList<PreviewLine> Preview)
{
    /// <summary>For a control rename: the event handlers that were renamed, old name → new name.</summary>
    public IReadOnlyList<(string OldName, string NewName)> HandlerRenames { get; init; } = [];

    internal static RenameResult Failed(long version, string error) => new(version, false, error, [], []);
}

/// <summary>A code fix with its edits already computed.</summary>
/// <param name="Category"><c>"AddUsing"</c>, <c>"ImplementInterface"</c> or <c>"GenerateMethod"</c>.</param>
public sealed record CodeFixInfo(string Title, string DiagnosticId, string Category, IReadOnlyList<DocumentEdits> Edits);

/// <summary>A generated source file (the form generator's partial class, control metadata).</summary>
public sealed record GeneratedFile(string HintName, string FilePath, string Text);
