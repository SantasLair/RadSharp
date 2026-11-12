using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RadSharp.Generators;

namespace RadSharp.Design.Code;

/// <summary>A compiler message with its location, for the designer's error list.</summary>
public sealed record BuildMessage(string File, int Line, int Column, string Severity, string Id, string Message)
{
    public override string ToString() => $"{Path.GetFileName(File)}({Line},{Column}): {Severity} {Id}: {Message}";
}

/// <summary>
/// F9: compiles a project in memory with Roslyn (running RadSharp's own generators on the forms,
/// including unsaved designer changes), and loads it into a collectible load context that shares the
/// designer's RadSharp assemblies — so the live form opens in a window of the designer's own host.
/// </summary>
public sealed class LiveSession : IDisposable
{
    sealed class Context() : AssemblyLoadContext("RadSharp.Live", isCollectible: true)
    {
        // Everything else (RadSharp, SkiaSharp, the BCL) comes from the designer's context, so types match.
        protected override Assembly? Load(AssemblyName name) => null;
    }

    Context? _context;

    public IReadOnlyList<BuildMessage> Messages { get; }
    public bool Success => Assembly != null;
    public Assembly? Assembly { get; private set; }

    LiveSession(IReadOnlyList<BuildMessage> messages, byte[]? image)
    {
        Messages = messages;
        if (image == null) return;
        _context = new Context();
        Assembly = _context.LoadFromStream(new MemoryStream(image));
    }

    static int _counter;

    /// <summary>
    /// Compiles <paramref name="project"/>. <paramref name="formOverrides"/> maps form file paths to text
    /// that replaces the file on disk (the designer's unsaved state).
    /// </summary>
    public static LiveSession Compile(RadProject project, IReadOnlyDictionary<string, string>? formOverrides = null)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = new List<SyntaxTree>
        {
            // What <ImplicitUsings>enable</ImplicitUsings> adds.
            CSharpSyntaxTree.ParseText("""
                global using global::System;
                global using global::System.Collections.Generic;
                global using global::System.IO;
                global using global::System.Linq;
                global using global::System.Net.Http;
                global using global::System.Threading;
                global using global::System.Threading.Tasks;
                """, parse, "GlobalUsings.g.cs"),
        };
        foreach (var file in project.SourceFiles)
        {
            var tree = CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(file), System.Text.Encoding.UTF8), parse, file);
            // A live run creates forms directly; the app's entry point (top-level statements) isn't needed.
            if (tree.GetCompilationUnitRoot().Members.Any(m => m is Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax)) continue;
            trees.Add(tree);
        }

        var forms = project.FormFiles
            .Select(f => (Path: f, Text: formOverrides != null && formOverrides.TryGetValue(Path.GetFullPath(f), out var t) ? t : File.ReadAllText(f)))
            .ToList();

        var compilation = CSharpCompilation.Create(
            $"{project.Name}.Live{Interlocked.Increment(ref _counter)}",
            trees,
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new FormGenerator().AsSourceGenerator(), new MetadataGenerator().AsSourceGenerator()],
            additionalTexts: [.. forms.Select(f => (AdditionalText)new Text(f.Path, f.Text))],
            parseOptions: parse,
            optionsProvider: new Options(project.RootNamespace));
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        var resources = forms.Select(f =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(f.Text);
            return new ResourceDescription(Path.GetFileName(f.Path), () => new MemoryStream(bytes), isPublic: true);
        });

        using var pe = new MemoryStream();
        var result = output.Emit(pe, manifestResources: resources);
        var messages = generatorDiagnostics.Concat(result.Diagnostics)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(ToMessage)
            .Distinct()
            .ToList();
        return new LiveSession(messages, result.Success && messages.Count == 0 ? pe.ToArray() : null);
    }

    static BuildMessage ToMessage(Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        return new BuildMessage(span.Path ?? "", span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            d.Severity.ToString().ToLowerInvariant(), d.Id, d.GetMessage());
    }

    /// <summary>The designer process's own assemblies (the BCL, RadSharp, Skia...) as references.</summary>
    static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => p.Length > 0 && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
    ]);

    /// <summary>Creates the form class <paramref name="className"/> from the compiled project.</summary>
    public Form CreateForm(string className)
    {
        if (Assembly == null) throw new InvalidOperationException("The project didn't compile.");
        var type = Assembly.GetTypes().FirstOrDefault(t => t.Name == className && typeof(Form).IsAssignableFrom(t))
            ?? throw new InvalidOperationException($"No form class {className} in the project.");
        return (Form)Activator.CreateInstance(type)!;
    }

    /// <summary>Forgets the live assembly's component registrations and unloads it.</summary>
    public void Dispose()
    {
        if (Assembly != null) ComponentRegistry.Unregister(Assembly);
        Assembly = null;
        _context?.Unload();
        _context = null;
    }

    sealed class Text(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    sealed class Options(string rootNamespace) : AnalyzerConfigOptionsProvider
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

/// <summary>Build: <c>dotnet publish</c> with NativeAOT for this OS, streaming the output.</summary>
public static class ProjectBuilder
{
    /// <summary>Publishes the project; returns the exit code and the publish folder (null if it failed).</summary>
    public static async Task<(int ExitCode, string? OutputDirectory)> PublishAsync(
        RadProject project, Action<string> onLine, IDictionary<string, string>? environment = null, CancellationToken cancel = default)
    {
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet",
            $"publish \"{project.ProjectFile}\" -c Release -r {rid} -p:PublishAot=true -nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = project.Directory,
        };
        // NativeAOT on Windows finds the C++ linker through vswhere.
        var installer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer");
        if (OperatingSystem.IsWindows() && System.IO.Directory.Exists(installer))
            psi.Environment["PATH"] = installer + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        if (environment != null) foreach (var (k, v) in environment) psi.Environment[k] = v;

        using var process = System.Diagnostics.Process.Start(psi)!;
        string? output = null;
        void Line(string? line)
        {
            if (line == null) return;
            var arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow > 0 && (line.EndsWith(Path.DirectorySeparatorChar) || line.EndsWith('/'))) output = line[(arrow + 4)..].Trim();
            onLine(line);
        }
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancel);
        return (process.ExitCode, process.ExitCode == 0 ? output : null);
    }
}
