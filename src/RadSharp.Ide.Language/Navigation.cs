using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RadSharp.Ide.Language;

/// <summary>A declared symbol found by name (go to symbol in workspace, Ctrl+T).</summary>
/// <param name="Kind">
/// <c>"Class"</c>, <c>"Struct"</c>, <c>"Interface"</c>, <c>"Enum"</c>, <c>"Delegate"</c>, <c>"Method"</c>, <c>"Property"</c>,
/// <c>"Field"</c>, <c>"Event"</c>… A control field of a form is reported with <see cref="Location"/> kind <c>"Form"</c>.
/// </param>
/// <param name="Container">The declaring type or namespace, for display.</param>
/// <param name="Location">Where to go: a source file, or the control's <c>"name"</c> in its form file.</param>
public sealed record WorkspaceSymbol(string Name, string Kind, string Container, DefinitionLocation Location);

/// <summary>A declaration in one file (go to symbol in file, Ctrl+Shift+O). Offsets are 0-based, lines and columns 1-based.</summary>
/// <param name="Start">Where the name is; <paramref name="FullStart"/> and <paramref name="FullLength"/> span the whole declaration (a method with its body).</param>
public sealed record DocumentSymbol(string Name, string Kind, string Container, int Start, int Length, int Line, int Column, int FullStart = 0, int FullLength = 0)
{
    public bool Contains(int offset) => offset >= FullStart && offset <= FullStart + FullLength;
}

public sealed partial class LanguageService
{
    // ---- Workspace symbols ----

    /// <summary>
    /// The declarations whose name matches <paramref name="query"/> (a case-insensitive subsequence, so camel-case
    /// humps work: <c>"fcb"</c> finds <c>FormCodeBehind</c>), best matches first, at most <paramref name="max"/>.
    /// Generated code is left out, except control fields, which lead to the control in its form file.
    /// </summary>
    public Task<Versioned<IReadOnlyList<WorkspaceSymbol>>> SearchSymbolsAsync(string query, int max = 50, CancellationToken cancel = default) =>
        Run(async () =>
        {
            var snapshot = Workspace.Snapshot;
            await Workspace.EnsureGeneratedAsync(snapshot, cancel).ConfigureAwait(false);
            query = query.Trim();
            var found = new List<(int Rank, WorkspaceSymbol Symbol)>();
            if (query.Length > 0)
            {
                var compilation = await snapshot.Project.GetCompilationAsync(cancel).ConfigureAwait(false);
                var symbols = compilation == null ? [] : compilation.GetSymbolsWithName(n => NameMatches(n, query),
                    SymbolFilter.TypeAndMember, cancel).ToList();
                foreach (var symbol in symbols)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (symbol is IMethodSymbol { MethodKind: not MethodKind.Ordinary } || symbol.IsImplicitlyDeclared) continue;
                    if (symbol.Locations.FirstOrDefault(l => l.IsInSource) is not { } location) continue;
                    var file = location.SourceTree!.FilePath;
                    DefinitionLocation? where = null;
                    if (!IsGeneratedPath(file)) where = Location(file, location, "Source", symbol.Name);
                    else if (symbol is IFieldSymbol) where = await FormLocationAsync(snapshot, symbol, cancel).ConfigureAwait(false);
                    if (where == null) continue;
                    var container = symbol.ContainingType?.Name ?? symbol.ContainingNamespace?.ToDisplayString() ?? "";
                    found.Add((Rank(symbol.Name, query), new WorkspaceSymbol(symbol.Name, KindOf(symbol), container, where)));
                }
            }
            IReadOnlyList<WorkspaceSymbol> result =
            [
                .. found.OrderBy(f => f.Rank).ThenBy(f => f.Symbol.Name.Length).ThenBy(f => f.Symbol.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(f => f.Symbol).Take(Math.Max(1, max)),
            ];
            return new Versioned<IReadOnlyList<WorkspaceSymbol>>(snapshot.Version, result);
        }, cancel);

    static bool NameMatches(string name, string query)
    {
        int q = 0;
        foreach (var ch in name)
            if (q < query.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(query[q])) q++;
        return q == query.Length;
    }

    /// <summary>0 exact, 1 prefix, 2 substring, 3 subsequence.</summary>
    static int Rank(string name, string query) =>
        string.Equals(name, query, StringComparison.OrdinalIgnoreCase) ? 0
        : name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1
        : name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : 3;

    static string KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol t => t.TypeKind switch
        {
            TypeKind.Struct => "Struct",
            TypeKind.Interface => "Interface",
            TypeKind.Enum => "Enum",
            TypeKind.Delegate => "Delegate",
            _ => "Class",
        },
        IMethodSymbol => "Method",
        IPropertySymbol => "Property",
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "EnumMember",
        IFieldSymbol => "Field",
        IEventSymbol => "Event",
        _ => symbol.Kind.ToString(),
    };

    // ---- Document symbols ----

    /// <summary>
    /// The declarations of C# source text, in order, from its syntax tree alone (no workspace needed, so it works while
    /// the workspace is still opening and on text that is not saved yet).
    /// </summary>
    public static IReadOnlyList<DocumentSymbol> GetDocumentSymbols(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var source = tree.GetText();
        var result = new List<DocumentSymbol>();

        void Add(string name, string kind, string container, SyntaxToken identifier, SyntaxNode? node = null)
        {
            var span = identifier.Span;
            var pos = source.Lines.GetLinePosition(span.Start);
            var full = (node ?? identifier.Parent)?.Span ?? span;
            result.Add(new DocumentSymbol(name, kind, container, span.Start, span.Length, pos.Line + 1, pos.Character + 1, full.Start, full.Length));
        }

        void Walk(SyntaxNode node, string container)
        {
            foreach (var child in node.ChildNodes())
            {
                switch (child)
                {
                    case BaseNamespaceDeclarationSyntax ns:
                        Walk(ns, ns.Name.ToString());
                        break;
                    case BaseTypeDeclarationSyntax type:
                    {
                        var kind = type switch
                        {
                            StructDeclarationSyntax or RecordDeclarationSyntax { RawKind: (int)SyntaxKind.RecordStructDeclaration } => "Struct",
                            InterfaceDeclarationSyntax => "Interface",
                            EnumDeclarationSyntax => "Enum",
                            _ => "Class",
                        };
                        Add(type.Identifier.Text, kind, container, type.Identifier, type);
                        if (type is EnumDeclarationSyntax e)
                            foreach (var m in e.Members) Add(m.Identifier.Text, "EnumMember", type.Identifier.Text, m.Identifier);
                        else Walk(type, type.Identifier.Text);
                        break;
                    }
                    case DelegateDeclarationSyntax d:
                        Add(d.Identifier.Text, "Delegate", container, d.Identifier, d);
                        break;
                    case MethodDeclarationSyntax m:
                        Add(m.Identifier.Text, "Method", container, m.Identifier, m);
                        break;
                    case ConstructorDeclarationSyntax c:
                        Add(c.Identifier.Text, "Constructor", container, c.Identifier, c);
                        break;
                    case PropertyDeclarationSyntax p:
                        Add(p.Identifier.Text, "Property", container, p.Identifier, p);
                        break;
                    case EventDeclarationSyntax ev:
                        Add(ev.Identifier.Text, "Event", container, ev.Identifier, ev);
                        break;
                    case FieldDeclarationSyntax f:
                        foreach (var v in f.Declaration.Variables) Add(v.Identifier.Text, "Field", container, v.Identifier, f);
                        break;
                    case EventFieldDeclarationSyntax ef:
                        foreach (var v in ef.Declaration.Variables) Add(v.Identifier.Text, "Event", container, v.Identifier, ef);
                        break;
                }
            }
        }

        Walk(tree.GetRoot(), "");
        return result;
    }

    /// <summary>
    /// The identifier tokens named <paramref name="name"/> in C# source text (not comments or strings), as (start, length) pairs,
    /// from the syntax tree alone.
    /// </summary>
    public static IReadOnlyList<TextRange> FindIdentifierSpans(string text, string name)
    {
        if (name.Length == 0) return [];
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        return [.. root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == name).Select(t => new TextRange(t.Span.Start, t.Span.Length))];
    }
}
