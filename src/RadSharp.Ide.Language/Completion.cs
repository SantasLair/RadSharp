using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using RoslynCompletionService = Microsoft.CodeAnalysis.Completion.CompletionService;

namespace RadSharp.Ide.Language;

/// <summary>A code snippet offered in completion: <c>if</c>, <c>for</c>, <c>foreach</c>, <c>prop</c>.</summary>
/// <param name="Template">
/// The text, with <c>${n:default}</c> fields, <c>$0</c> for the final caret, <c>\n</c> for a new line at the
/// current indentation and <c>\t</c> for one more indent.
/// </param>
sealed record Snippet(string Shortcut, string Description, string Template, bool IsMember)
{
    public static readonly Snippet[] All =
    [
        new("if", "if statement", "if (${1:condition})\n{\n\t$0\n}", false),
        new("for", "for loop", "for (int ${1:i} = 0; ${1:i} < ${2:length}; ${1:i}++)\n{\n\t$0\n}", false),
        new("foreach", "foreach loop", "foreach (var ${1:item} in ${2:collection})\n{\n\t$0\n}", false),
        new("prop", "auto-implemented property", "public ${1:int} ${2:MyProperty} { get; set; }$0", true),
    ];

    /// <summary>The expanded text, the final caret offset and the field ranges (offsets into the text, in field order).</summary>
    public (string Text, int Caret, List<(int Field, TextRange Range)> Fields) Expand(string indent, string newLine)
    {
        var sb = new StringBuilder();
        var fields = new List<(int, TextRange)>();
        int caret = -1;
        for (int i = 0; i < Template.Length; i++)
        {
            char c = Template[i];
            if (c == '\n') sb.Append(newLine).Append(indent);
            else if (c == '\t') sb.Append("    ");
            else if (c == '$' && i + 1 < Template.Length && Template[i + 1] == '0') { caret = sb.Length; i++; }
            else if (c == '$' && i + 1 < Template.Length && Template[i + 1] == '{')
            {
                int colon = Template.IndexOf(':', i), close = Template.IndexOf('}', i);
                int field = int.Parse(Template.AsSpan(i + 2, colon - i - 2));
                var value = Template[(colon + 1)..close];
                fields.Add((field, new TextRange(sb.Length, value.Length)));
                sb.Append(value);
                i = close;
            }
            else sb.Append(c);
        }
        return (sb.ToString(), caret < 0 ? sb.Length : caret, fields);
    }
}

public sealed partial class LanguageService
{
    /// <summary>
    /// Whether typing <paramref name="typed"/> at <paramref name="position"/> (the position after it) should
    /// open the completion list: <c>.</c>, the first letter of a word, and so on.
    /// </summary>
    public async Task<bool> ShouldTriggerCompletionAsync(string path, int position, char typed, CancellationToken cancel = default)
    {
        var (_, doc) = Source(path);
        var service = RoslynCompletionService.GetService(doc);
        var text = await doc.GetTextAsync(cancel).ConfigureAwait(false);
        return service != null && service.ShouldTriggerCompletion(text, position, CompletionTrigger.CreateInsertionTrigger(typed));
    }

    /// <summary>
    /// The completion list at <paramref name="position"/>. <paramref name="typed"/> is the character just typed
    /// when the list opens as you type (null for an explicit Ctrl+Space). The items are not filtered by the
    /// partial word under the caret (<see cref="CompletionResult.Span"/>); filter them as the user types
    /// (<see cref="CompletionResult.Filter"/>) instead of asking again. Get an item's description and edit
    /// with <see cref="ResolveCompletionAsync"/>. Returns null where completion doesn't apply.
    /// </summary>
    public Task<Versioned<CompletionResult>?> GetCompletionsAsync(string path, int position, char? typed = null, CancellationToken cancel = default) =>
        Run(async () =>
        {
            var (snapshot, doc) = await SourceAsync(path, cancel).ConfigureAwait(false);
            var service = RoslynCompletionService.GetService(doc);
            if (service == null) return null;
            var trigger = typed is char c ? CompletionTrigger.CreateInsertionTrigger(c) : CompletionTrigger.Invoke;
            var list = await service.GetCompletionsAsync(doc, position, trigger, cancellationToken: cancel).ConfigureAwait(false);
            if (list.ItemsList.Count == 0) return null;

            var items = new List<CompletionEntry>(list.ItemsList.Count + Snippet.All.Length);
            bool statements = false, members = false;
            foreach (var item in list.ItemsList)
            {
                var kind = KindOf(item);
                if (kind == WellKnownTags.Keyword && item.DisplayText == "foreach") statements = true;
                if (kind == WellKnownTags.Keyword && item.DisplayText == "public") members = true;
                items.Add(new CompletionEntry(item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix, item.FilterText, item.SortText,
                    kind, item.InlineDescription) { Item = item });
            }

            // Snippets: statement ones where Roslyn offers statement keywords, prop directly inside a type.
            if (members) members = await IsMemberPositionAsync(doc, position, cancel).ConfigureAwait(false);
            foreach (var snippet in Snippet.All)
                if (snippet.IsMember ? members : statements)
                    items.Add(new CompletionEntry(snippet.Shortcut, snippet.Shortcut, snippet.Shortcut, WellKnownTags.Snippet, "snippet") { Snippet = snippet });

            var span = new TextRange(list.Span.Start, list.Span.Length);
            return new Versioned<CompletionResult>(snapshot.Version, new CompletionResult(Path.GetFullPath(path), span, items, doc));
        }, cancel);

    /// <summary>
    /// What committing <paramref name="entry"/> from <paramref name="list"/> does (the edit, the caret, and
    /// snippet fields) and its description. The edits are against the document the list was computed from and
    /// replace <see cref="CompletionResult.Span"/>; if the user has typed more characters of the word since the
    /// list opened, the caller widens the item's edit to the caret (those characters are part of the word).
    /// </summary>
    public Task<CompletionDetail> ResolveCompletionAsync(CompletionResult list, CompletionEntry entry, CancellationToken cancel = default) =>
        Run(async () =>
        {
            var doc = (Document)list.State;
            if (entry.Snippet is { } snippet)
            {
                var text = await doc.GetTextAsync(cancel).ConfigureAwait(false);
                var line = text.Lines.GetLineFromPosition(list.Span.Start);
                var lineText = line.ToString();
                var indent = lineText[..(lineText.Length - lineText.TrimStart().Length)];
                var newLine = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
                var (expanded, caret, fields) = snippet.Expand(indent, newLine);
                int at = list.Span.Start;
                var ordered = fields.OrderBy(f => f.Field).Select(f => new TextRange(at + f.Range.Start, f.Range.Length)).ToList();
                return new CompletionDetail(snippet.Description, [new TextEdit(at, list.Span.Length, expanded)],
                    ordered.Count > 0 ? ordered[0].Start : at + caret, ordered);
            }

            var item = (CompletionItem)entry.Item!;
            var service = RoslynCompletionService.GetService(doc)!;
            var description = await service.GetDescriptionAsync(doc, item, cancel).ConfigureAwait(false);
            var change = await service.GetChangeAsync(doc, item, commitCharacter: null, cancel).ConfigureAwait(false);
            var changes = change.TextChanges.IsDefaultOrEmpty ? [change.TextChange] : change.TextChanges;
            return new CompletionDetail(description?.Text ?? "",
                [.. changes.Select(c => new TextEdit(c.Span.Start, c.Span.Length, c.NewText ?? ""))], change.NewPosition, []);
        }, cancel);

    static string KindOf(CompletionItem item)
    {
        foreach (var tag in item.Tags)
            if (tag is not (WellKnownTags.Public or WellKnownTags.Protected or WellKnownTags.Private or WellKnownTags.Internal))
                return tag;
        return WellKnownTags.Keyword;
    }

    /// <summary>True between members of a type (where a property declaration can start).</summary>
    static async Task<bool> IsMemberPositionAsync(Document doc, int position, CancellationToken cancel)
    {
        var root = await doc.GetSyntaxRootAsync(cancel).ConfigureAwait(false);
        var token = root!.FindToken(Math.Max(0, position - 1));
        for (var node = token.Parent; node != null; node = node.Parent)
        {
            switch (node)
            {
                case BlockSyntax or ArrowExpressionClauseSyntax or AccessorListSyntax or ParameterListSyntax or EqualsValueClauseSyntax
                    or AttributeListSyntax or BaseListSyntax:
                    return false;
                case TypeDeclarationSyntax type:
                    return position > type.OpenBraceToken.SpanStart && (type.CloseBraceToken.IsMissing || position <= type.CloseBraceToken.SpanStart);
            }
        }
        return false;
    }
}
