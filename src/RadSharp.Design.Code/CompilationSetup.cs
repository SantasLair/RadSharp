using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RadSharp.Generators;

namespace RadSharp.Design.Code;

/// <summary>
/// How a RADSharp project is turned into a Roslyn compilation: parse options, the implicit usings, the
/// references and the generators. Shared by the live run (<see cref="LiveSession.Compile"/>) and the IDE's
/// language-service workspace, so the two see the same program.
/// </summary>
internal static class CompilationSetup
{
    public static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>File name of the implicit-usings document.</summary>
    public const string GlobalUsingsFileName = "GlobalUsings.g.cs";

    /// <summary>What <c>&lt;ImplicitUsings&gt;enable&lt;/ImplicitUsings&gt;</c> adds.</summary>
    public const string GlobalUsingsText = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    /// <summary>Options for a library compilation of the project (what a live run builds).</summary>
    public static CSharpCompilationOptions LibraryOptions =>
        new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);

    /// <summary>True if the file holds top-level statements (the app's entry point, which a live run skips).</summary>
    public static bool IsEntryPoint(SyntaxTree tree) =>
        tree.GetCompilationUnitRoot().Members.Any(m => m is GlobalStatementSyntax);

    /// <summary>RADSharp's own generators (forms and control metadata), run in-process.</summary>
    public static ImmutableArray<ISourceGenerator> CreateGenerators() =>
        [new FormGenerator().AsSourceGenerator(), new MetadataGenerator().AsSourceGenerator()];

    /// <summary>The designer process's own assemblies (the BCL, RADSharp, Skia...) as references.</summary>
    public static ImmutableArray<MetadataReference> HostReferences => References.Value;

    /// <summary>The file paths behind <see cref="HostReferences"/>.</summary>
    public static ImmutableArray<string> HostReferencePaths => ReferencePaths.Value;

    static readonly Lazy<ImmutableArray<string>> ReferencePaths = new(() =>
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => p.Length > 0 && File.Exists(p)),
    ]);

    static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
        [.. ReferencePaths.Value.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))]);

    /// <summary>A form file (or other additional file) with in-memory text.</summary>
    public sealed class Text(string path, string text) : AdditionalText
    {
        readonly SourceText _text = SourceText.From(text);
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    /// <summary>The analyzer options the generators read: <c>build_property.RootNamespace</c>.</summary>
    public sealed class Options(string rootNamespace) : AnalyzerConfigOptionsProvider
    {
        readonly Global _global = new(rootNamespace);
        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty.Instance;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Empty.Instance;

        sealed class Global(string ns) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                value = ns;
                return key == "build_property.RootNamespace";
            }
        }

        sealed class Empty : AnalyzerConfigOptions
        {
            public static readonly Empty Instance = new();
            public override bool TryGetValue(string key, out string value) { value = ""; return false; }
        }
    }
}
