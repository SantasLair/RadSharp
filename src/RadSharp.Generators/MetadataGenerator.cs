using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RadSharp.Generators;

/// <summary>
/// Emits AOT-safe metadata for every control class: its form-file name, a factory, and accessors for
/// its <c>[Published]</c> properties and events. This stands in for Delphi's RTTI and drives the form
/// reader, the form writer and the designer's Object Inspector.
/// </summary>
[Generator]
public sealed class MetadataGenerator : IIncrementalGenerator
{
    internal const string ControlType = "RadSharp.Control";
    internal const string PublishedAttribute = "RadSharp.PublishedAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classes = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => Describe(ctx, ct))
            .Where(static m => m is not null)
            .Collect();

        var assemblyName = context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? "Assembly");
        context.RegisterSourceOutput(classes.Combine(assemblyName), static (spc, input) => Emit(spc, input.Left!, input.Right));
    }

    internal sealed record PropModel(string JsonName, string ClrName, string ValueType, string? Converter, string? Category, string? Problem, Location? Location);
    internal sealed record EventModel(string JsonName, string ClrName, string Parameters);
    internal sealed record ClassModel(string FullName, string Name, string? BaseFullName, bool Creatable,
        ImmutableArray<PropModel> Props, ImmutableArray<EventModel> Events);

    static ClassModel? Describe(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not INamedTypeSymbol type) return null;
        if (!IsControl(type) || type.IsGenericType || type.ContainingType != null) return null;
        if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)) return null;

        var props = ImmutableArray.CreateBuilder<PropModel>();
        var events = ImmutableArray.CreateBuilder<EventModel>();
        foreach (var member in type.GetMembers())
        {
            var attr = member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == PublishedAttribute);
            if (attr == null) continue;
            string? explicitName = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string : null;
            string? category = attr.NamedArguments.FirstOrDefault(a => a.Key == "Category").Value.Value as string;

            if (member is IPropertySymbol p)
            {
                string? problem = null;
                if (p.GetMethod is not { DeclaredAccessibility: Accessibility.Public } || p.SetMethod is not { DeclaredAccessibility: Accessibility.Public })
                    problem = "needs a public getter and setter";
                string? converter = ConverterFor(p.Type);
                if (converter == null) problem ??= $"has type '{p.Type.ToDisplayString()}', which can't be published";
                props.Add(new PropModel(explicitName ?? CamelCase(p.Name), p.Name,
                    p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), converter, category, problem,
                    p.Locations.FirstOrDefault()));
            }
            else if (member is IEventSymbol e && e.Type is INamedTypeSymbol { DelegateInvokeMethod: { } invoke })
            {
                var parameters = string.Join(", ", invoke.Parameters.Select(x =>
                    $"{x.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).TrimEnd('?')} {x.Name}"));
                events.Add(new EventModel(explicitName ?? "on" + e.Name, e.Name, parameters));
            }
        }

        var baseType = type.BaseType;
        string? baseName = baseType != null && IsControl(baseType)
            ? baseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : null;
        bool creatable = !type.IsAbstract && type.InstanceConstructors.Any(c =>
            c.Parameters.Length == 0 && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal);

        return new ClassModel(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), type.Name, baseName,
            creatable, props.ToImmutable(), events.ToImmutable());
    }

    internal static bool IsControl(ITypeSymbol? t)
    {
        for (; t != null; t = t.BaseType)
            if (t.ToDisplayString() == ControlType) return true;
        return false;
    }

    internal static string CamelCase(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

    static string? ConverterFor(ITypeSymbol t)
    {
        const string C = "global::RadSharp.Converters.";
        switch (t.SpecialType)
        {
            case SpecialType.System_String: return C + "String";
            case SpecialType.System_Int32: return C + "Int";
            case SpecialType.System_Double: return C + "Real";
            case SpecialType.System_Boolean: return C + "Bool";
        }
        var name = t.ToDisplayString();
        switch (name)
        {
            case "RadSharp.Drawing.Color": return C + "Color";
            case "RadSharp.Drawing.Color?": return C + "NullableColor";
            case "RadSharp.Drawing.Font?": return C + "NullableFont";
            case "RadSharp.Anchors": return C + "Anchors";
            case "RadSharp.Thickness": return C + "Thickness";
        }
        if (t is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_String }) return C + "Strings";
        if (t.TypeKind == TypeKind.Enum)
            return $"{C}Enum<{t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>()";
        return null;
    }

    static readonly DiagnosticDescriptor BadProperty = new(
        "RAD0002", "Property can't be published", "Published property '{0}.{1}' {2}",
        "RadSharp", DiagnosticSeverity.Error, isEnabledByDefault: true);

    static void Emit(SourceProductionContext spc, ImmutableArray<ClassModel?> models, string assemblyName)
    {
        var classes = models.Where(m => m != null).Select(m => m!)
            .GroupBy(m => m.FullName).Select(g => g.First())
            .OrderBy(m => m.FullName, System.StringComparer.Ordinal).ToList();
        if (classes.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CA2255 // module initializer in a library: registers this assembly's controls");
        sb.AppendLine("namespace RadSharp.Generated");
        sb.AppendLine("{");
        sb.AppendLine($"    internal static class ComponentRegistration_{Sanitize(assemblyName)}");
        sb.AppendLine("    {");
        sb.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("        internal static void Register()");
        sb.AppendLine("        {");
        foreach (var c in classes)
        {
            var props = new List<string>();
            foreach (var p in c.Props)
            {
                if (p.Problem != null)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(BadProperty, p.Location, c.Name, p.ClrName, p.Problem));
                    continue;
                }
                string category = p.Category == null ? "null" : Literal(p.Category);
                props.Add($"new global::RadSharp.PropertyMeta<{c.FullName}, {p.ValueType}>({Literal(p.JsonName)}, {p.Converter}, " +
                          $"static o => o.{p.ClrName}, static (o, v) => o.{p.ClrName} = v, {category})");
            }
            var events = c.Events.Select(e => $"new global::RadSharp.EventMeta({Literal(e.JsonName)}, {Literal(e.ClrName)}, {Literal(e.Parameters)})");
            string factory = c.Creatable ? $"static () => new {c.FullName}()" : "null";
            string baseType = c.BaseFullName == null ? "null" : $"typeof({c.BaseFullName})";

            sb.AppendLine("            global::RadSharp.ComponentRegistry.Register(new global::RadSharp.ComponentClass(");
            sb.AppendLine($"                {Literal(c.Name)}, typeof({c.FullName}), {baseType}, {factory},");
            sb.AppendLine("                new global::RadSharp.PropertyMeta[]");
            sb.AppendLine("                {");
            foreach (var p in props) sb.AppendLine($"                    {p},");
            sb.AppendLine("                },");
            sb.AppendLine("                new global::RadSharp.EventMeta[]");
            sb.AppendLine("                {");
            foreach (var e in events) sb.AppendLine($"                    {e},");
            sb.AppendLine("                }));");
        }
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        spc.AddSource("RadSharp.ComponentRegistration.g.cs", sb.ToString());
    }

    internal static string Literal(string s) =>
        Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(s, quote: true);

    static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return sb.ToString();
    }
}
