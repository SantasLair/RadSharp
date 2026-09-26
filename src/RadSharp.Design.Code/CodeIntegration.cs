using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RadSharp.Design.Code;

/// <summary>Edits a form's code-behind with Roslyn: handler stubs and renames.</summary>
public static class CodeIntegration
{
    /// <summary>
    /// Makes sure <paramref name="className"/> has a method <paramref name="handler"/>; if not, appends an
    /// empty one with <paramref name="parameters"/> (e.g. "Control sender, MouseEventArgs e"). Returns the
    /// new source and the 1-based line to put the caret on (inside the method body).
    /// </summary>
    public static (string Source, int Line) EnsureHandler(string source, string className, string handler, string parameters)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className)
            ?? throw new InvalidOperationException($"Class {className} not found in the code file.");

        if (cls.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == handler) is { } existing)
            return (source, LineOf(source, existing.Body?.OpenBraceToken.Span.End ?? existing.SpanStart) + 1);

        // Indent like the class's other members.
        string classIndent = Indentation(source, cls.SpanStart);
        string indent = cls.Members.Count > 0 ? Indentation(source, cls.Members[0].SpanStart) : classIndent + "    ";

        int closeLineStart = source.LastIndexOf('\n', Math.Max(0, cls.CloseBraceToken.SpanStart - 1)) + 1;
        var method = $"\n{indent}void {handler}({parameters})\n{indent}{{\n{indent}    \n{indent}}}\n";
        var result = source.Insert(closeLineStart, method);
        int line = LineOf(result, closeLineStart) + 3;   // blank line, signature, brace, body

        // Types used in the signature need their namespaces.
        if (parameters.Contains("Canvas") && !result.Contains("using RadSharp.Drawing;"))
        {
            result = AddUsing(result, "RadSharp.Drawing");
            line++;
        }
        if (!result.Contains("using RadSharp;"))
        {
            result = AddUsing(result, "RadSharp");
            line++;
        }
        return (result, line);
    }

    static string AddUsing(string source, string ns)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        int at = root.Usings.Count > 0 ? root.Usings.Last().FullSpan.End : 0;
        return source.Insert(at, $"using {ns};\n");
    }

    static string Indentation(string source, int position)
    {
        int lineStart = source.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        int i = lineStart;
        while (i < source.Length && source[i] is ' ' or '\t') i++;
        return source[lineStart..i];
    }

    static int LineOf(string source, int position)
    {
        int line = 1;
        for (int i = 0; i < position && i < source.Length; i++) if (source[i] == '\n') line++;
        return line;
    }

    /// <summary>
    /// Renames every use of identifier <paramref name="oldName"/> (fields, method names, references — not
    /// strings or comments) to <paramref name="newName"/>.
    /// </summary>
    public static string RenameIdentifier(string source, string oldName, string newName)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var tokens = root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == oldName).ToList();
        if (tokens.Count == 0) return source;
        return root.ReplaceTokens(tokens, (t, _) => SyntaxFactory.Identifier(t.LeadingTrivia, newName, t.TrailingTrivia)).ToFullString();
    }

    /// <summary>
    /// The event a double-click on a control opens (Delphi's "default event"): onChange for editors and
    /// lists, otherwise onClick, then onTimer, onPaint, onShow.
    /// </summary>
    public static EventMeta? DefaultEvent(Control control)
    {
        var cls = ComponentRegistry.Of(control);
        if (control is Component)
        {
            // Components ignore Control's input events: take their own first event (onTimer, onPopup...).
            var inherited = ComponentRegistry.Find(typeof(Control))!.Events.Select(e => e.Name).ToHashSet();
            return cls.Events.FirstOrDefault(e => !inherited.Contains(e.Name)
                || cls.FindEvent(e.Name) != ComponentRegistry.Find(typeof(Control))!.FindEvent(e.Name));
        }
        string[] order = control is EditBase or ListBox or ComboBox or TrackBar or ScrollBar or TreeView or ListView or PageControl
            ? ["onChange", "onClick"]
            : control is Form ? ["onShow", "onClick"]
            : control is PaintBox ? ["onPaint", "onClick"]
            : ["onClick", "onTimer", "onPaint", "onChange", "onShow"];
        foreach (var name in order)
            if (cls.FindEvent(name) is { } ev) return ev;
        return cls.Events.FirstOrDefault();
    }

    /// <summary>The conventional handler name: <c>btnSave_Click</c>.</summary>
    public static string HandlerName(Control control, Form form, EventMeta ev) =>
        $"{(control.Name.Length > 0 ? control.Name : form.Name)}_{ev.Name[2..]}";
}
