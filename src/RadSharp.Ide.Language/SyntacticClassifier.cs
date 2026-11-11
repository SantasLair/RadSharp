using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RadSharp.Ide.Language;

/// <summary>
/// Colours from the syntax tree only: no binding, so it runs in well under a millisecond per screen and
/// keeps up with typing. Names that need the semantic model (is <c>Foo</c> a type or a local?) come out as
/// <c>"identifier"</c> unless their declaration says what they are; the semantic pass refines them.
/// </summary>
static class SyntacticClassifier
{
    public static IReadOnlyList<ClassifiedRange> Classify(SyntaxNode root, TextSpan span, CancellationToken cancel)
    {
        var result = new List<ClassifiedRange>();
        foreach (var token in root.DescendantTokens(span, descendIntoTrivia: true))
        {
            cancel.ThrowIfCancellationRequested();
            foreach (var trivia in token.LeadingTrivia) Trivia(trivia, span, result);
            if (token.Span.Length > 0 && token.Span.IntersectsWith(span) && Kind(token) is { } kind)
                result.Add(new ClassifiedRange(token.SpanStart, token.Span.Length, kind));
            foreach (var trivia in token.TrailingTrivia) Trivia(trivia, span, result);
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    static void Trivia(SyntaxTrivia trivia, TextSpan span, List<ClassifiedRange> result)
    {
        if (!trivia.Span.IntersectsWith(span) || trivia.HasStructure) return;   // structured trivia's tokens are visited directly
        string? kind = trivia.Kind() switch
        {
            SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia => ClassificationTypeNames.Comment,
            SyntaxKind.DisabledTextTrivia => ClassificationTypeNames.ExcludedCode,
            SyntaxKind.DocumentationCommentExteriorTrivia => ClassificationTypeNames.XmlDocCommentDelimiter,
            _ => null,
        };
        if (kind != null) result.Add(new ClassifiedRange(trivia.SpanStart, trivia.Span.Length, kind));
    }

    static string? Kind(SyntaxToken token)
    {
        var kind = token.Kind();
        if (token.Parent?.FirstAncestorOrSelf<DocumentationCommentTriviaSyntax>() != null)
            return kind == SyntaxKind.XmlTextLiteralToken ? ClassificationTypeNames.XmlDocCommentText : ClassificationTypeNames.XmlDocCommentName;
        if (token.Parent is DirectiveTriviaSyntax || token.Parent?.FirstAncestorOrSelf<DirectiveTriviaSyntax>() != null)
            return SyntaxFacts.IsKeywordKind(kind) || kind == SyntaxKind.HashToken ? ClassificationTypeNames.PreprocessorKeyword : ClassificationTypeNames.PreprocessorText;
        if (SyntaxFacts.IsKeywordKind(kind))
            return IsControlKeyword(kind) ? ClassificationTypeNames.ControlKeyword : ClassificationTypeNames.Keyword;
        switch (kind)
        {
            case SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.SingleLineRawStringLiteralToken
                or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.Utf8StringLiteralToken or SyntaxKind.InterpolatedStringStartToken
                or SyntaxKind.InterpolatedStringEndToken or SyntaxKind.InterpolatedVerbatimStringStartToken
                or SyntaxKind.InterpolatedSingleLineRawStringStartToken or SyntaxKind.InterpolatedMultiLineRawStringStartToken
                or SyntaxKind.InterpolatedRawStringEndToken:
                return token.Text.StartsWith('@') || token.Text.StartsWith("$@") ? ClassificationTypeNames.VerbatimStringLiteral : ClassificationTypeNames.StringLiteral;
            case SyntaxKind.CharacterLiteralToken:
                return ClassificationTypeNames.StringLiteral;
            case SyntaxKind.NumericLiteralToken:
                return ClassificationTypeNames.NumericLiteral;
            case SyntaxKind.IdentifierToken:
                return Identifier(token);
        }
        if (SyntaxFacts.IsPunctuation(kind))
            return SyntaxFacts.IsBinaryExpressionOperatorToken(kind) || SyntaxFacts.IsPrefixUnaryExpressionOperatorToken(kind)
                || SyntaxFacts.IsAssignmentExpressionOperatorToken(kind) || kind is SyntaxKind.EqualsGreaterThanToken or SyntaxKind.QuestionToken
                ? ClassificationTypeNames.Operator
                : ClassificationTypeNames.Punctuation;
        return null;
    }

    /// <summary>A name, classified by the declaration it names (contextual keywords like <c>var</c> too).</summary>
    static string Identifier(SyntaxToken token)
    {
        // Contextual keywords the parser leaves as identifiers.
        if (token.Parent is IdentifierNameSyntax { IsVar: true } or IdentifierNameSyntax { Identifier.Text: "dynamic" or "nint" or "nuint" }
            || token.Text is "nameof" && token.Parent?.Parent is InvocationExpressionSyntax)
            return ClassificationTypeNames.Keyword;
        return token.Parent switch
        {
            ClassDeclarationSyntax c when c.Identifier == token => ClassificationTypeNames.ClassName,
            RecordDeclarationSyntax r when r.Identifier == token => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? ClassificationTypeNames.RecordStructName : ClassificationTypeNames.RecordClassName,
            StructDeclarationSyntax s when s.Identifier == token => ClassificationTypeNames.StructName,
            InterfaceDeclarationSyntax i when i.Identifier == token => ClassificationTypeNames.InterfaceName,
            EnumDeclarationSyntax e when e.Identifier == token => ClassificationTypeNames.EnumName,
            DelegateDeclarationSyntax d when d.Identifier == token => ClassificationTypeNames.DelegateName,
            EnumMemberDeclarationSyntax => ClassificationTypeNames.EnumMemberName,
            MethodDeclarationSyntax m when m.Identifier == token => ClassificationTypeNames.MethodName,
            ConstructorDeclarationSyntax or DestructorDeclarationSyntax => ClassificationTypeNames.ClassName,
            PropertyDeclarationSyntax p when p.Identifier == token => ClassificationTypeNames.PropertyName,
            EventDeclarationSyntax ev when ev.Identifier == token => ClassificationTypeNames.EventName,
            ParameterSyntax => ClassificationTypeNames.ParameterName,
            TypeParameterSyntax => ClassificationTypeNames.TypeParameterName,
            VariableDeclaratorSyntax v => v.Parent?.Parent switch
            {
                FieldDeclarationSyntax f => f.Modifiers.Any(SyntaxKind.ConstKeyword) ? ClassificationTypeNames.ConstantName : ClassificationTypeNames.FieldName,
                EventFieldDeclarationSyntax => ClassificationTypeNames.EventName,
                _ => ClassificationTypeNames.LocalName,
            },
            SingleVariableDesignationSyntax or ForEachStatementSyntax or CatchDeclarationSyntax => ClassificationTypeNames.LocalName,
            NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax => ClassificationTypeNames.NamespaceName,
            _ => ClassificationTypeNames.Identifier,
        };
    }

    static bool IsControlKeyword(SyntaxKind kind) => kind is SyntaxKind.IfKeyword or SyntaxKind.ElseKeyword or SyntaxKind.WhileKeyword
        or SyntaxKind.ForKeyword or SyntaxKind.ForEachKeyword or SyntaxKind.DoKeyword or SyntaxKind.SwitchKeyword or SyntaxKind.CaseKeyword
        or SyntaxKind.DefaultKeyword or SyntaxKind.TryKeyword or SyntaxKind.CatchKeyword or SyntaxKind.FinallyKeyword or SyntaxKind.GotoKeyword
        or SyntaxKind.BreakKeyword or SyntaxKind.ContinueKeyword or SyntaxKind.ReturnKeyword or SyntaxKind.ThrowKeyword or SyntaxKind.YieldKeyword;
}
