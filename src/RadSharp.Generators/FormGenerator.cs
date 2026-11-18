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

    internal static readonly DiagnosticDescriptor HidesMember = Rule("RAD1006", "Control name hides an inherited member",
        "Control name '{0}' hides the inherited member '{1}'; rename the control.", DiagnosticSeverity.Error);

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

        // The members a field named like a control would hide: those of the class the form derives from (the user's own
        // base class if the class declares one, otherwise the root class of the form file).
        var baseType = userClass?.BaseType is { SpecialType: not SpecialType.System_Object } declared ? declared : null;
        var rootClass = ResolveBase(compilation, root);
        var hideFrom = baseType ?? rootClass;
        string? Hidden(string name) => hideFrom == null ? null : FindInheritedMember(compilation, hideFrom, name);

        var fields = new List<(string Name, string Type, string Init)>();
        var bindings = new List<Binding>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        // One type per other assembly the form's classes come from: their registrations must run first.
        var libraries = new Dictionary<string, string>(StringComparer.Ordinal);
        void Note(INamedTypeSymbol? t)
        {
            if (t == null || SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, compilation.Assembly)) return;
            libraries[t.ContainingAssembly.Name] = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
        var rootType = ResolveClass(compilation, root, spc, At);
        Note(rootType);

        Walk(root, isRoot: true);

        void Walk(JsonNode node, bool isRoot)
        {
            var type = isRoot ? rootType : ResolveClass(compilation, node, spc, At);
            Note(type);
            var nameNode = node["name"];
            string? name = nameNode?.Kind == JsonKind.String ? nameNode.String : null;
            if (!isRoot && !string.IsNullOrEmpty(name))
            {
                if (!SyntaxFacts.IsValidIdentifier(name!))
                    spc.ReportDiagnostic(Diagnostic.Create(BadName, At(nameNode!.Line, nameNode.Column), name));
                else if (!names.Add(name!))
                    spc.ReportDiagnostic(Diagnostic.Create(DuplicateName, At(nameNode!.Line, nameNode.Column), name));
                else if (Hidden(name!) is { } hidden)
                    spc.ReportDiagnostic(Diagnostic.Create(HidesMember, At(nameNode!.Line, nameNode.Column), name, hidden));
                else
                {
                    var t = type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "global::RadSharp.Control";
                    fields.Add((name!, t, $"({t})FindControl({MetadataGenerator.Literal(name!)})!"));
                }
            }

            string owner = isRoot ? "this" : name ?? "";
            foreach (var member in node.Members!)
            {
                if (IsEventKey(member.Key) && owner.Length > 0)  // unnamed controls can't be bound
                    Bind(member.Key, member.Value, type, owner, isRoot ? name ?? className : owner);
            }

            // Menu items: bound through their path from the (named) menu control; named items get a field.
            if (node["items"] is { Kind: JsonKind.Array } items && !isRoot && !string.IsNullOrEmpty(name))
                WalkItems(items, $"{name}.Items");

            if (node["children"] is { Kind: JsonKind.Array } children)
                foreach (var child in children.Items!)
                    if (child.Kind == JsonKind.Object) Walk(child, isRoot: false);
        }

        void WalkItems(JsonNode items, string prefix)
        {
            var itemType = compilation.GetTypeByMetadataName("RadSharp.MenuItem");
            for (int i = 0; i < items.Items!.Count; i++)
            {
                var item = items.Items[i];
                if (item.Kind != JsonKind.Object) continue;
                string path = $"{prefix}[{i}]";
                string target = path;
                var itemName = item["name"];
                if (itemName is { Kind: JsonKind.String, String: { Length: > 0 } n })
                {
                    if (!SyntaxFacts.IsValidIdentifier(n)) spc.ReportDiagnostic(Diagnostic.Create(BadName, At(itemName.Line, itemName.Column), n));
                    else if (!names.Add(n)) spc.ReportDiagnostic(Diagnostic.Create(DuplicateName, At(itemName.Line, itemName.Column), n));
                    else if (Hidden(n) is { } hidden) spc.ReportDiagnostic(Diagnostic.Create(HidesMember, At(itemName.Line, itemName.Column), n, hidden));
                    else { fields.Add((n, "global::RadSharp.MenuItem", path)); target = n; }
                }
                var caption = item["caption"]?.String ?? "item";
                foreach (var member in item.Members!)
                    if (IsEventKey(member.Key)) Bind(member.Key, member.Value, itemType, target, target == path ? $"menu item \"{caption}\"" : target);
                if (item["items"] is { Kind: JsonKind.Array } sub) WalkItems(sub, path + ".Items");
            }
        }

        void Bind(string key, JsonNode value, INamedTypeSymbol? type, string target, string label)
        {
            if (value.Kind != JsonKind.String || string.IsNullOrEmpty(value.String)) return;
            var handler = value.String!;
            var ev = type == null ? null : FindPublishedEvent(type, key);
            if (ev == null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(UnknownEvent, At(value.KeyLine, value.KeyColumn), type?.Name ?? "control", key));
                return;
            }
            if (!SyntaxFacts.IsValidIdentifier(handler))
            {
                spc.ReportDiagnostic(Diagnostic.Create(BadName, At(value.Line, value.Column), handler));
                return;
            }
            if (userClass == null || !HasMethod(userClass, handler))
            {
                var invoke = ((INamedTypeSymbol)ev.Type).DelegateInvokeMethod!;
                var parameters = string.Join(", ", invoke.Parameters.Select(p =>
                    $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).TrimEnd('?')} {p.Name}"));
                spc.ReportDiagnostic(Diagnostic.Create(MissingHandler, At(value.Line, value.Column), handler, label, key, className, parameters));
                return;
            }
            bindings.Add(new Binding(target, ev.Name, handler));
        }

        var rootClassName = root["class"]?.String ?? "Form";
        spc.AddSource($"{className}.Form.g.cs", Emit(ns, className,
            rootType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "global::RadSharp.Form",
            rootClassName, fileName, fields, bindings, libraries.Values.OrderBy(v => v, StringComparer.Ordinal).ToList()));
    }

    /// <summary>The class the form file's root names (what the generated half derives from), or null if unknown.</summary>
    static INamedTypeSymbol? ResolveBase(Compilation compilation, JsonNode root)
    {
        var name = root["class"] is { Kind: JsonKind.String } c ? c.String! : "Form";
        return compilation.GetTypeByMetadataName("RadSharp." + name)
            ?? compilation.GetSymbolsWithName(name, SymbolFilter.Type).OfType<INamedTypeSymbol>().FirstOrDefault(MetadataGenerator.IsControl)
            ?? compilation.GetTypeByMetadataName("RadSharp.Form");
    }

    /// <summary>
    /// "Type.Member" for the first member called <paramref name="name"/> up <paramref name="type"/>'s base chain that code in
    /// this assembly can see (public, protected, or internal within reach): a field of that name would hide it. Property
    /// accessors and constructors are not counted.
    /// </summary>
    internal static string? FindInheritedMember(Compilation compilation, INamedTypeSymbol type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            foreach (var m in t.GetMembers(name))
            {
                if (m is IMethodSymbol { MethodKind: not MethodKind.Ordinary and not MethodKind.UserDefinedOperator }) continue;
                if (m.IsImplicitlyDeclared) continue;
                bool visible = m.DeclaredAccessibility switch
                {
                    Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal => true,
                    Accessibility.Internal or Accessibility.ProtectedAndInternal => compilation.IsSymbolAccessibleWithin(m, compilation.Assembly),
                    _ => false,
                };
                if (visible) return $"{t.Name}.{m.Name}";
            }
        }
        return null;
    }

    static bool IsEventKey(string key) => key.Length >= 3 && key.StartsWith("on", StringComparison.Ordinal) && char.IsUpper(key[2]);

    static INamedTypeSymbol? ResolveClass(Compilation compilation, JsonNode node, SourceProductionContext spc, Func<int, int, Location> at)
    {
        var classNode = node["class"];
        if (classNode?.Kind != JsonKind.String) return null;
        var name = classNode.String!;
        var byCsName = compilation.GetTypeByMetadataName("RadSharp." + name);
        if (byCsName != null && PublishedName(byCsName) != name) byCsName = null;   // published under another form-file name
        var type = byCsName
            ?? FindPublishedAs(compilation.GlobalNamespace.GetNamespaceMembers().FirstOrDefault(n => n.Name == "RadSharp"), name)
            ?? FindPublishedAs(compilation.Assembly.GlobalNamespace, name, nested: true, own: true)
            ?? compilation.GetSymbolsWithName(name, SymbolFilter.Type).OfType<INamedTypeSymbol>()
                .FirstOrDefault(MetadataGenerator.IsControl)
            ?? FindInReferences(compilation, name);
        if (type == null || !MetadataGenerator.IsControl(type))
        {
            spc.ReportDiagnostic(Diagnostic.Create(UnknownClass, at(classNode.Line, classNode.Column), name));
            return null;
        }
        return type;
    }

    /// <summary>The form-file class name of a control class: its <c>[Component("Name")]</c>, else its C# name.</summary>
    static string PublishedName(INamedTypeSymbol t)
    {
        var attr = t.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == MetadataGenerator.ComponentAttribute);
        return attr != null && attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string { Length: > 0 } s ? s : t.Name;
    }

    /// <summary>
    /// A control class in <paramref name="ns"/> (searched through its nested namespaces when <paramref name="nested"/>) published
    /// under <paramref name="name"/> by <c>[Component]</c> (e.g. "Action" for ActionItem). <paramref name="own"/>: the namespace is
    /// the project's own, where internal classes count.
    /// </summary>
    static INamedTypeSymbol? FindPublishedAs(INamespaceSymbol? ns, string name, bool nested = false, bool own = false)
    {
        if (ns == null) return null;
        foreach (var t in ns.GetTypeMembers())
            if ((t.DeclaredAccessibility == Accessibility.Public || (own && t.DeclaredAccessibility == Accessibility.Internal))
                && MetadataGenerator.IsControl(t)
                && t.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == MetadataGenerator.ComponentAttribute) && PublishedName(t) == name) return t;
        if (nested)
            foreach (var child in ns.GetNamespaceMembers())
                if (FindPublishedAs(child, name, nested, own) is { } found) return found;
        return null;
    }

    /// <summary>A control class with this simple name in a referenced assembly (component libraries).</summary>
    static INamedTypeSymbol? FindInReferences(Compilation compilation, string name)
    {
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            var an = assembly.Name;
            if (an.StartsWith("System", StringComparison.Ordinal) || an.StartsWith("Microsoft", StringComparison.Ordinal)
                || an is "mscorlib" or "netstandard" or "SkiaSharp" or "HarfBuzzSharp" or "SDL3-CS") continue;
            if (Search(assembly.GlobalNamespace) is { } found) return found;
        }
        return null;

        INamedTypeSymbol? Search(INamespaceSymbol ns)
        {
            foreach (var t in ns.GetTypeMembers())
                if (t.DeclaredAccessibility == Accessibility.Public && MetadataGenerator.IsControl(t) && PublishedName(t) == name) return t;
            foreach (var child in ns.GetNamespaceMembers())
                if (Search(child) is { } t) return t;
            return null;
        }
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
        List<(string Name, string Type, string Init)> fields, List<Binding> bindings, List<string> libraries)
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
        foreach (var (name, type, _) in fields)
            sb.AppendLine($"{indent}    internal {type} {name} = null!;");
        if (fields.Count > 0) sb.AppendLine();
        sb.AppendLine($"{indent}    /// <summary>The class {resource} was designed as; used when the form is saved.</summary>");
        sb.AppendLine($"{indent}    protected override string? DesignedClassName => {MetadataGenerator.Literal(rootClassName)};");
        sb.AppendLine();
        sb.AppendLine($"{indent}    /// <summary>Loads the form designed in {resource} and binds its event handlers.</summary>");
        sb.AppendLine($"{indent}    private void InitializeComponent()");
        sb.AppendLine($"{indent}    {{");
        foreach (var lib in libraries)
            sb.AppendLine($"{indent}        global::System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof({lib}).Module.ModuleHandle);");
        sb.AppendLine($"{indent}        global::RadSharp.FormReader.LoadInto(this, typeof({className}).Assembly, {MetadataGenerator.Literal(resource)});");
        foreach (var (name, _, init) in fields)
            sb.AppendLine($"{indent}        {name} = {init};");
        foreach (var b in bindings)
            sb.AppendLine($"{indent}        {b.Target}.{b.ClrEvent} += {b.Handler};");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}}}");
        if (ns.Length > 0) sb.AppendLine("}");
        return sb.ToString();
    }
}
