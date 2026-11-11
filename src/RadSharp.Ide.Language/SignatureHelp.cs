using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RadSharp.Ide.Language;

public sealed partial class LanguageService
{
    static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat;

    /// <summary>
    /// The overloads of the method, constructor or indexer whose argument list contains <paramref name="position"/>,
    /// with the overload that best fits the arguments so far and the parameter being typed; null outside an
    /// argument list. Ask again on <c>(</c> and <c>,</c>, and as the caret moves.
    /// </summary>
    public Task<Versioned<SignatureHelpResult?>> GetSignatureHelpAsync(string path, int position, CancellationToken cancel = default) =>
        Run(async () =>
        {
            var (snapshot, doc) = await SourceAsync(path, cancel).ConfigureAwait(false);
            var root = await doc.GetSyntaxRootAsync(cancel).ConfigureAwait(false);
            var model = await doc.GetSemanticModelAsync(cancel).ConfigureAwait(false);
            var result = Compute(root!, model!, position, cancel);
            return new Versioned<SignatureHelpResult?>(snapshot.Version, result);
        }, cancel);

    static SignatureHelpResult? Compute(SyntaxNode root, SemanticModel model, int position, CancellationToken cancel)
    {
        var token = root.FindToken(position);
        if (token.SpanStart >= position && token.GetPreviousToken() is { RawKind: not 0 } previous) token = previous;

        for (var node = token.Parent; node != null; node = node.Parent)
        {
            if (node is not BaseArgumentListSyntax args) continue;
            var (open, close) = args switch
            {
                ArgumentListSyntax a => (a.OpenParenToken, a.CloseParenToken),
                BracketedArgumentListSyntax b => (b.OpenBracketToken, b.CloseBracketToken),
                _ => default,
            };
            // Inside the brackets: after the opening one, and before the closing one (or anywhere if it's missing).
            if (position <= open.SpanStart || (!close.IsMissing && position > close.SpanStart)) continue;

            var (candidates, chosen) = Candidates(args.Parent, model, cancel);
            if (candidates.Count == 0) continue;

            int argument = args.Arguments.GetSeparators().Count(s => s.SpanStart < position);
            string? named = argument < args.Arguments.Count ? args.Arguments[argument].NameColon?.Name.Identifier.ValueText : null;

            // The overload: the bound one, else the first with enough parameters.
            int active = chosen != null ? candidates.FindIndex(c => SymbolEqualityComparer.Default.Equals(c, chosen)) : -1;
            if (active < 0) active = Math.Max(0, candidates.FindIndex(c => Parameters(c).Length > argument || Parameters(c).Any(p => p.IsParams)));

            var signatures = candidates.Select(Signature).ToList();
            var parameters = Parameters(candidates[active]);
            int parameter = named != null ? parameters.ToList().FindIndex(p => p.Name == named) : argument;
            if (parameters.Length > 0 && parameter >= parameters.Length && parameters[^1].IsParams) parameter = parameters.Length - 1;

            int end = close.IsMissing ? args.Span.End : close.Span.End;
            return new SignatureHelpResult(new TextRange(open.SpanStart, end - open.SpanStart), signatures, active, parameter);
        }
        return null;
    }

    static (List<ISymbol> Candidates, ISymbol? Chosen) Candidates(SyntaxNode? owner, SemanticModel model, CancellationToken cancel)
    {
        bool Visible(ISymbol s) => owner == null || model.IsAccessible(owner.SpanStart, s);
        switch (owner)
        {
            case InvocationExpressionSyntax invocation:
            {
                var group = model.GetMemberGroup(invocation.Expression, cancel).Where(Visible).ToList();
                var info = model.GetSymbolInfo(invocation, cancel);
                if (group.Count == 0) group = [.. info.CandidateSymbols];
                return (group, info.Symbol);
            }
            case BaseObjectCreationExpressionSyntax creation:
            {
                var type = model.GetTypeInfo(creation, cancel).Type as INamedTypeSymbol;
                var ctors = type?.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared || c.Parameters.Length > 0 || type.InstanceConstructors.Length == 1)
                    .Where(Visible).Cast<ISymbol>().ToList() ?? [];
                return (ctors, model.GetSymbolInfo(creation, cancel).Symbol);
            }
            case ConstructorInitializerSyntax initializer:
            {
                var info = model.GetSymbolInfo(initializer, cancel);
                var type = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault())?.ContainingType;
                return (type?.InstanceConstructors.Where(Visible).Cast<ISymbol>().ToList() ?? [], info.Symbol);
            }
            case ElementAccessExpressionSyntax access:
            {
                var type = model.GetTypeInfo(access.Expression, cancel).Type;
                var indexers = type?.GetMembers().OfType<IPropertySymbol>().Where(p => p.IsIndexer).Where(Visible).Cast<ISymbol>().ToList() ?? [];
                return (indexers, model.GetSymbolInfo(access, cancel).Symbol);
            }
            default:
                return ([], null);
        }
    }

    static System.Collections.Immutable.ImmutableArray<IParameterSymbol> Parameters(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.Parameters,
        IPropertySymbol p => p.Parameters,
        _ => [],
    };

    static SignatureInfo Signature(ISymbol symbol)
    {
        var (summary, paramDocs) = Documentation(symbol);
        var label = new System.Text.StringBuilder();
        bool indexer = symbol is IPropertySymbol;
        switch (symbol)
        {
            case IMethodSymbol { MethodKind: MethodKind.Constructor } ctor:
                label.Append(ctor.ContainingType.ToDisplayString(TypeFormat));
                break;
            case IMethodSymbol method:
                label.Append(method.ReturnType.ToDisplayString(TypeFormat)).Append(' ')
                    .Append(method.ContainingType.ToDisplayString(TypeFormat)).Append('.').Append(method.Name);
                if (method.IsGenericMethod) label.Append('<').Append(string.Join(", ", method.TypeArguments.Select(t => t.ToDisplayString(TypeFormat)))).Append('>');
                break;
            case IPropertySymbol property:
                label.Append(property.Type.ToDisplayString(TypeFormat)).Append(' ').Append(property.ContainingType.ToDisplayString(TypeFormat));
                break;
        }
        label.Append(indexer ? '[' : '(');
        var parameters = new List<ParameterInfo>();
        foreach (var p in Parameters(symbol))
        {
            if (parameters.Count > 0) label.Append(", ");
            var display = p.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat
                .AddParameterOptions(SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeName
                    | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeType));
            parameters.Add(new ParameterInfo(p.Name, display, label.Length, display.Length, paramDocs.GetValueOrDefault(p.Name, "")));
            label.Append(display);
        }
        label.Append(indexer ? ']' : ')');
        return new SignatureInfo(label.ToString(), summary, parameters);
    }

    /// <summary>The summary and per-parameter text of a symbol's XML documentation.</summary>
    static (string Summary, Dictionary<string, string> Parameters) Documentation(ISymbol symbol)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var xml = symbol.GetDocumentationCommentXml(expandIncludes: false);
        if (string.IsNullOrWhiteSpace(xml)) return ("", parameters);
        try
        {
            var doc = XElement.Parse(xml.Trim().StartsWith("<member", StringComparison.Ordinal) ? xml : $"<member>{xml}</member>");
            foreach (var p in doc.Elements("param"))
                if (p.Attribute("name")?.Value is { } name) parameters[name] = Plain(p);
            return (doc.Element("summary") is { } s ? Plain(s) : "", parameters);
        }
        catch (System.Xml.XmlException) { return ("", parameters); }
    }

    /// <summary>Doc-comment XML as text: <c>&lt;see cref="T:X.Y"/&gt;</c> becomes <c>Y</c>.</summary>
    static string Plain(XElement element)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var node in element.Nodes())
        {
            if (node is XText text) sb.Append(text.Value);
            else if (node is XElement e)
            {
                var reference = e.Attribute("cref")?.Value ?? e.Attribute("langword")?.Value ?? e.Attribute("name")?.Value;
                if (reference != null && e.IsEmpty) sb.Append(reference[(reference.LastIndexOfAny([':', '.']) + 1)..]);
                else sb.Append(Plain(e));
            }
        }
        return string.Join(' ', sb.ToString().Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }
}
