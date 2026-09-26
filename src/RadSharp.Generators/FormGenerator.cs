using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace RadSharp.Generators;

/// <summary>
/// Turns each <c>*.radform.json</c> (an MSBuild AdditionalFile) into the generated half of a partial
/// form class: typed fields for named controls and <c>InitializeComponent()</c>, which streams the
/// embedded form file in and binds event handlers at compile time. A handler named in the form but
/// missing from the class is a compile error.
/// </summary>
[Generator]
public sealed class FormGenerator : IIncrementalGenerator
{
    internal const string Extension = ".radform.json";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var forms = context.AdditionalTextsProvider
            .Where(static t => t.Path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            .Select(static (t, ct) => (t.Path, Text: t.GetText(ct)?.ToString() ?? ""));

        var rootNamespace = context.AnalyzerConfigOptionsProvider.Select(static (o, _) =>
            o.GlobalOptions.TryGetValue("build_property.RootNamespace", out var ns) ? ns : "");

        context.RegisterSourceOutput(forms.Combine(context.CompilationProvider).Combine(rootNamespace),
            static (spc, input) => Generate(spc, input.Left.Left.Path, input.Left.Left.Text, input.Left.Right, input.Right));
    }

    static DiagnosticDescriptor Rule(string id, string title, string format, DiagnosticSeverity severity) =>
        new(id, title, format, "RadSharp.Forms", severity, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidJson = Rule("RAD1000", "Invalid form file", "The form file isn't valid: {0}", DiagnosticSeverity.Error);
    internal static readonly DiagnosticDescriptor MissingHandler = Rule("RAD1001", "Event handler not found",
        "Handler '{0}' for {1}.{2} was not found in class '{3}'. Add: void {0}({4}) {{ }}", DiagnosticSeverity.Error);
    internal static readonly DiagnosticDescriptor UnknownClass = Rule("RAD1002", "Unknown control class", "Unknown control class '{0}'; its field is typed as Control", DiagnosticSeverity.Warning);
    internal static readonly DiagnosticDescriptor UnknownEvent = Rule("RAD1003", "Unknown event", "{0} has no published event '{1}'; the handler isn't bound", DiagnosticSeverity.Warning);
    internal static readonly DiagnosticDescriptor BadName = Rule("RAD1004", "Invalid control or handler name", "'{0}' is not a valid C# identifier", DiagnosticSeverity.Error);
    internal static readonly DiagnosticDescriptor DuplicateName = Rule("RAD1005", "Duplicate control name", "The control name '{0}' is used more than once in this form", DiagnosticSeverity.Error);

    sealed class Binding(string target, string clrEvent, string handler) { public string Target = target, ClrEvent = clrEvent, Handler = handler; }

    static void Generate(SourceProductionContext spc, string path, string text, Compilation compilation, string rootNamespace)
    {
        var fileName = Path.GetFileName(path);
        var className = fileName.Substring(0, fileName.Length - Extension.Length);
        var sourceText = SourceText.From(text);

        Location At(int line, int column)
        {
            var pos = new LinePosition(line, column);
            int offset = line < sourceText.Lines.Count ? sourceText.Lines[line].Start + column : 0;
            return Location.Create(path, new TextSpan(Math.Min(offset, text.Length), 0), new LinePositionSpan(pos, pos));
        }

        JsonNode doc;
        try { doc = JsonLite.Parse(text); }
        catch (JsonException e)
        {
            spc.ReportDiagnostic(Diagnostic.Create(InvalidJson, At(e.Line, e.Column), e.Message));
            return;
        }
        if (!SyntaxFacts.IsValidIdentifier(className))
        {
            spc.ReportDiagnostic(Diagnostic.Create(BadName, At(0, 0), className));
            return;
        }
        var root = doc["root"];
        if (doc.Kind != JsonKind.Object || root is not { Kind: JsonKind.Object })
        {
            spc.ReportDiagnostic(Diagnostic.Create(InvalidJson, At(0, 0), "missing \"root\" object"));
            return;
        }

        // The user's half of the class decides the namespace; otherwise use the project's root namespace.
        var userClass = compilation.GetSymbolsWithName(className, SymbolFilter.Type).OfType<INamedTypeSymbol>().FirstOrDefault();
        string ns = userClass != null
            ? (userClass.ContainingNamespace.IsGlobalNamespace ? "" : userClass.ContainingNamespace.ToDisplayString())
            : rootNamespace;

        var fields = new List<(string Name, string Type)>();
        var bindings = new List<Binding>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var rootType = ResolveClass(compilation, root, spc, At);

        Walk(root, isRoot: true);

        void Walk(JsonNode node, bool isRoot)
        {
            var type = isRoot ? rootType : ResolveClass(compilation, node, spc, At);
            var nameNode = node["name"];
            string? name = nameNode?.Kind == JsonKind.String ? nameNode.String : null;
            if (!isRoot && !string.IsNullOrEmpty(name))
            {
                if (!SyntaxFacts.IsValidIdentifier(name!))
                    spc.ReportDiagnostic(Diagnostic.Create(BadName, At(nameNode!.Line, nameNode.Column), name));
                else if (!names.Add(name!))
                    spc.ReportDiagnostic(Diagnostic.Create(DuplicateName, At(nameNode!.Line, nameNode.Column), name));
                else
                    fields.Add((name!, type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "global::RadSharp.Control"));
            }

            foreach (var member in node.Members!)
            {
                var key = member.Key;
                if (key.Length < 3 || !key.StartsWith("on", StringComparison.Ordinal) || !char.IsUpper(key[2])) continue;
                var value = member.Value;
                if (value.Kind != JsonKind.String || string.IsNullOrEmpty(value.String)) continue;
                var handler = value.String!;
                string owner = isRoot ? "this" : name ?? "";
                if (owner.Length == 0) continue;  // unnamed controls can't be bound

                var ev = type == null ? null : FindPublishedEvent(type, key);
                if (ev == null)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(UnknownEvent, At(value.KeyLine, value.KeyColumn), type?.Name ?? "control", key));
                    continue;
                }
                if (!SyntaxFacts.IsValidIdentifier(handler))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(BadName, At(value.Line, value.Column), handler));
                    continue;
                }
                if (userClass == null || !HasMethod(userClass, handler))
                {
                    var invoke = ((INamedTypeSymbol)ev.Type).DelegateInvokeMethod!;
                    var parameters = string.Join(", ", invoke.Parameters.Select(p =>
                        $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).TrimEnd('?')} {p.Name}"));
                    spc.ReportDiagnostic(Diagnostic.Create(MissingHandler, At(value.Line, value.Column),
                        handler, isRoot ? name ?? className : owner, key, className, parameters));
                    continue;
                }
                bindings.Add(new Binding(owner, ev.Name, handler));
            }

            if (node["children"] is { Kind: JsonKind.Array } children)
                foreach (var child in children.Items!)
                    if (child.Kind == JsonKind.Object) Walk(child, isRoot: false);
        }

        var rootClassName = root["class"]?.String ?? "Form";
        spc.AddSource($"{className}.Form.g.cs", Emit(ns, className,
            rootType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "global::RadSharp.Form",
            rootClassName, fileName, fields, bindings));
    }

    static INamedTypeSymbol? ResolveClass(Compilation compilation, JsonNode node, SourceProductionContext spc, Func<int, int, Location> at)
    {
        var classNode = node["class"];
        if (classNode?.Kind != JsonKind.String) return null;
        var name = classNode.String!;
        var type = compilation.GetTypeByMetadataName("RadSharp." + name)
            ?? compilation.GetSymbolsWithName(name, SymbolFilter.Type).OfType<INamedTypeSymbol>()
                .FirstOrDefault(MetadataGenerator.IsControl);
        if (type == null || !MetadataGenerator.IsControl(type))
        {
            spc.ReportDiagnostic(Diagnostic.Create(UnknownClass, at(classNode.Line, classNode.Column), name));
            return null;
        }
        return type;
    }

    static IEventSymbol? FindPublishedEvent(INamedTypeSymbol type, string jsonName)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            foreach (var ev in t.GetMembers().OfType<IEventSymbol>())
            {
                var attr = ev.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == MetadataGenerator.PublishedAttribute);
                if (attr == null) continue;
                var published = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string s ? s : "on" + ev.Name;
                if (published == jsonName) return ev;
            }
        }
        return null;
    }

    static bool HasMethod(INamedTypeSymbol type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
            if (t.GetMembers(name).OfType<IMethodSymbol>().Any()) return true;
        return false;
    }

    static string Emit(string ns, string className, string baseType, string rootClassName, string resource,
        List<(string Name, string Type)> fields, List<Binding> bindings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        string indent = "";
        if (ns.Length > 0)
        {
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            indent = "    ";
        }
        sb.AppendLine($"{indent}partial class {className} : {baseType}");
        sb.AppendLine($"{indent}{{");
        foreach (var (name, type) in fields)
            sb.AppendLine($"{indent}    internal {type} {name} = null!;");
        if (fields.Count > 0) sb.AppendLine();
        sb.AppendLine($"{indent}    /// <summary>The class {resource} was designed as; used when the form is saved.</summary>");
        sb.AppendLine($"{indent}    protected override string? DesignedClassName => {MetadataGenerator.Literal(rootClassName)};");
        sb.AppendLine();
        sb.AppendLine($"{indent}    /// <summary>Loads the form designed in {resource} and binds its event handlers.</summary>");
        sb.AppendLine($"{indent}    private void InitializeComponent()");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        global::RadSharp.FormReader.LoadInto(this, typeof({className}).Assembly, {MetadataGenerator.Literal(resource)});");
        foreach (var (name, type) in fields)
            sb.AppendLine($"{indent}        {name} = ({type})FindControl({MetadataGenerator.Literal(name)})!;");
        foreach (var b in bindings)
            sb.AppendLine($"{indent}        {b.Target}.{b.ClrEvent} += {b.Handler};");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}}}");
        if (ns.Length > 0) sb.AppendLine("}");
        return sb.ToString();
    }
}
