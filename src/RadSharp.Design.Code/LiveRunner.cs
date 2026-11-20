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
    public bool IsError => Severity == "error";

    public override string ToString() => File.Length == 0
        ? $"{Severity} {Id}: {Message}"
        : $"{Path.GetFileName(File)}({Line},{Column}): {Severity} {Id}: {Message}";
}

/// <summary>How <see cref="LiveSession.Compile"/> finds what the project compiles against.</summary>
public sealed record LiveRunOptions
{
    /// <summary>
    /// Resolve the project's references, analyzers and compile settings with MSBuild (cached; see
    /// <see cref="ProjectDependencies"/>). False compiles against the designer's own assemblies with RadSharp's
    /// generators only, which is also what happens, with a warning, when resolving fails.
    /// </summary>
    public bool UseProjectDependencies { get; init; } = true;

    /// <summary>How long a restore and resolve may take before the live run falls back.</summary>
    public TimeSpan ResolveTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Resolves the project's dependencies (null: <see cref="ProjectDependencies.Resolve(RadProject, TimeSpan, Action{string}?)"/>,
    /// cached). Called on the compiling thread with the project, <see cref="ResolveTimeout"/> and <see cref="Progress"/>.
    /// </summary>
    public Func<RadProject, TimeSpan, Action<string>?, DependencyResolution>? Resolver { get; init; }

    /// <summary>Hears what a slow step is doing (MSBuild restoring the project), on the compiling thread.</summary>
    public Action<string>? Progress { get; init; }
}

/// <summary>
/// F9: compiles a project in memory with Roslyn, the way its own build would (its NuGet packages, framework and
/// project references, source generators and compile settings, resolved by MSBuild), plus RadSharp's generators on the
/// forms, including unsaved designer changes. The result loads into a collectible load context that loads the
/// project's own package assemblies and native libraries but shares the designer's RadSharp assemblies, so the live
/// form opens in a window of the designer's own host.
/// </summary>
public sealed class LiveSession : IDisposable
{
    /// <summary>Warning: the project's dependencies couldn't be resolved; the live run uses the designer's assemblies.</summary>
    public const string FallbackId = "RAD9001";
    /// <summary>Warning: an analyzer assembly (source generator) couldn't be loaded.</summary>
    public const string AnalyzerFailedId = "RAD9002";
    /// <summary>Warning: the project's LangVersion is newer than the designer's compiler.</summary>
    public const string LangVersionId = "RAD9003";

    sealed class Context(ProjectDependencies? dependencies) : AssemblyLoadContext("RadSharp.Live", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            // RadSharp (and what it exposes, e.g. SkiaSharp) and the BCL come from the designer's context, so types match.
            if (dependencies == null || name.Name is not { } simple) return null;
            if (ProjectDependencies.SharedAssemblies.Value.ContainsKey(simple)) return null;
            if (!dependencies.RuntimeAssemblies.TryGetValue(simple, out var path)) return null;
            return LoadAsset(dependencies, path);
        }

        Assembly LoadAsset(ProjectDependencies deps, string path)
        {
            // Package files never change, so load them in place (Assembly.Location works); anything else (a project
            // reference's output) from memory, so the user can rebuild it while the live run is open.
            if (deps.IsImmutable(path)) return LoadFromAssemblyPath(path);
            var pdb = Path.ChangeExtension(path, ".pdb");
            using var image = new MemoryStream(SharedFile.ReadAllBytes(path));
            using var symbols = File.Exists(pdb) ? new MemoryStream(SharedFile.ReadAllBytes(pdb)) : null;
            return LoadFromStream(image, symbols);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            if (dependencies == null) return IntPtr.Zero;
            var wanted = Path.GetFileName(unmanagedDllName);
            foreach (var path in dependencies.NativeLibraries)   // best runtime identifier first
                if (NativeNameMatches(Path.GetFileName(path), wanted)) return LoadUnmanagedDllFromPath(path);
            return IntPtr.Zero;
        }
    }

    /// <summary>Whether a native file (<c>e_sqlite3.dll</c>, <c>libe_sqlite3.so</c>) is what a DllImport name asks for.</summary>
    internal static bool NativeNameMatches(string file, string requested)
    {
        static string Bare(string n)
        {
            foreach (var ext in new[] { ".dll", ".so", ".dylib" })
                if (n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { n = n[..^ext.Length]; break; }
            int so = n.IndexOf(".so.", StringComparison.Ordinal);   // libfoo.so.1
            if (so > 0) n = n[..so];
            return n.StartsWith("lib", StringComparison.Ordinal) ? n[3..] : n;
        }
        return file.Equals(requested, StringComparison.OrdinalIgnoreCase) || Bare(file).Equals(Bare(requested), StringComparison.OrdinalIgnoreCase);
    }

    Context? _context;

    public IReadOnlyList<BuildMessage> Messages { get; }
    public bool Success => Assembly != null;
    public Assembly? Assembly { get; private set; }

    /// <summary>What the project was compiled against, or null if it used the designer's assemblies (see <see cref="FallbackId"/>).</summary>
    public ProjectDependencies? Dependencies { get; }
    /// <summary>True if <see cref="Dependencies"/> came from the cache (no MSBuild run for this compile).</summary>
    public bool DependenciesFromCache { get; }
    /// <summary>Time spent resolving dependencies (MSBuild, or the cache check) for this compile.</summary>
    public TimeSpan ResolveTime { get; }
    /// <summary>Time spent compiling (generators included) and emitting.</summary>
    public TimeSpan CompileTime { get; }

    LiveSession(IReadOnlyList<BuildMessage> messages, byte[]? image, ProjectDependencies? dependencies, bool fromCache, TimeSpan resolve, TimeSpan compile)
    {
        Messages = messages;
        Dependencies = dependencies;
        DependenciesFromCache = fromCache;
        ResolveTime = resolve;
        CompileTime = compile;
        if (image == null) return;
        _context = new Context(dependencies);
        Assembly = _context.LoadFromStream(new MemoryStream(image));
    }

    static int _counter;

    /// <summary>
    /// Compiles <paramref name="project"/>. <paramref name="formOverrides"/> maps form file paths to text
    /// that replaces the file on disk (the designer's unsaved state). Errors, and warnings about how it was compiled
    /// (<see cref="FallbackId"/>...), are in <see cref="Messages"/>.
    /// </summary>
    public static LiveSession Compile(RadProject project, IReadOnlyDictionary<string, string>? formOverrides = null, LiveRunOptions? options = null)
    {
        options ??= new();
        var messages = new List<BuildMessage>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ProjectDependencies? deps = null;
        bool fromCache = false;
        if (options.UseProjectDependencies)
        {
            var resolver = options.Resolver ?? ((p, timeout, progress) => ProjectDependencies.Resolve(p, timeout, progress));
            var resolution = resolver(project, options.ResolveTimeout, options.Progress);
            deps = resolution.Dependencies;
            fromCache = resolution.FromCache;
            if (deps == null)
                messages.Add(Notice(FallbackId, $"Live run without the project's packages, project references and source generators ({resolution.Error}" +
                    (fromCache ? "; remembered from the last attempt, nothing it read has changed" : "") +
                    "); it compiles against the designer's own assemblies only."));
        }
        var resolveTime = watch.Elapsed;
        watch.Restart();

        var langVersion = LanguageVersion.Latest;
        if (deps is { LangVersion.Length: > 0 } && !LanguageVersionFacts.TryParse(deps.LangVersion, out langVersion))
        {
            messages.Add(Notice(LangVersionId, $"LangVersion {deps.LangVersion} is newer than the designer's C# compiler; " +
                $"the live run uses its latest (C# {LanguageVersion.Latest.MapSpecifiedToEffectiveVersion().ToDisplayString()})."));
            langVersion = LanguageVersion.Latest;
        }
        var parse = new CSharpParseOptions(langVersion, preprocessorSymbols: deps?.DefineConstants);
        var globalUsings = deps != null
            ? string.Join("\n", deps.Usings.Select(u => u.ToDirective()))
            // What <ImplicitUsings>enable</ImplicitUsings> adds.
            : """
                global using global::System;
                global using global::System.Collections.Generic;
                global using global::System.IO;
                global using global::System.Linq;
                global using global::System.Net.Http;
                global using global::System.Threading;
                global using global::System.Threading.Tasks;
                """;
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(globalUsings, parse, "GlobalUsings.g.cs") };
        foreach (var file in deps != null ? deps.SourceFiles(project) : project.SourceFiles)
        {
            var tree = CSharpSyntaxTree.ParseText(SourceText.From(SharedFile.ReadAllText(file), System.Text.Encoding.UTF8), parse, file);
            // A live run creates forms directly; the app's entry point (top-level statements) isn't needed.
            if (tree.GetCompilationUnitRoot().Members.Any(m => m is Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax)) continue;
            trees.Add(tree);
        }

        var forms = project.FormFiles
            .Select(f => (Path: f, Text: formOverrides != null && formOverrides.TryGetValue(Path.GetFullPath(f), out var t) ? t : SharedFile.ReadAllText(f)))
            .ToList();

        var nullable = deps?.Nullable.ToLowerInvariant() switch
        {
            null or "enable" => NullableContextOptions.Enable,
            "warnings" => NullableContextOptions.Warnings,
            "annotations" => NullableContextOptions.Annotations,
            _ => NullableContextOptions.Disable,
        };
        var compilation = CSharpCompilation.Create(
            $"{project.Name}.Live{Interlocked.Increment(ref _counter)}",
            trees,
            deps != null ? deps.References.Select(Reference) : DesignerReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: nullable,
                allowUnsafe: deps?.AllowUnsafeBlocks ?? false, checkOverflow: deps?.CheckForOverflowUnderflow ?? false));

        // Held until the compilation is emitted: the project's generators aren't unloaded under a compile that runs them.
        using var generatorLease = new GeneratorLease();
        var generators = new List<ISourceGenerator> { new FormGenerator().AsSourceGenerator(), new MetadataGenerator().AsSourceGenerator() };
        var additional = forms.Select(f => (AdditionalText)new Text(f.Path, f.Text)).ToList();
        var configs = new List<AnalyzerConfig>();
        var rootNamespace = deps is { RootNamespace.Length: > 0 } ? deps.RootNamespace : project.RootNamespace;
        if (deps != null)
        {
            try
            {
                var set = generatorLease.Acquire(deps);
                generators.AddRange(set.Generators);
                foreach (var failure in set.Failures) messages.Add(Notice(AnalyzerFailedId, "Source generator not loaded: " + failure));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                messages.Add(Notice(AnalyzerFailedId, $"The project's source generators weren't loaded: {e.Message}"));
            }
            foreach (var f in deps.AdditionalFiles)
                if (TryRead(f) is { } text) additional.Add(new Text(f, text));
            foreach (var f in deps.AnalyzerConfigFiles)
                if (TryRead(f) is { } text) configs.Add(AnalyzerConfig.Parse(text, f));
        }
        var optionsProvider = new ConfigOptionsProvider(configs, new Dictionary<string, string> { ["build_property.RootNamespace"] = rootNamespace });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, additionalTexts: additional, parseOptions: parse, optionsProvider: optionsProvider);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        var resources = forms.Select(f =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(f.Text);
            return new ResourceDescription(Path.GetFileName(f.Path), () => new MemoryStream(bytes), isPublic: true);
        });

        using var pe = new MemoryStream();
        var result = output.Emit(pe, manifestResources: resources);
        messages.AddRange(generatorDiagnostics.Concat(result.Diagnostics)
            // Errors, and a generator that threw (a warning, but its code is missing: the errors that follow need it).
            .Where(d => d.Severity == DiagnosticSeverity.Error || d.Id is "CS8784" or "CS8785")
            .Select(ToMessage)
            .Distinct());
        bool ok = result.Success && !messages.Any(m => m.IsError);
        return new LiveSession(messages, ok ? pe.ToArray() : null, deps, fromCache, resolveTime, watch.Elapsed);
    }

    static BuildMessage Notice(string id, string message) => new("", 0, 0, "warning", id, message);

    static string? TryRead(string path)
    {
        try { return SharedFile.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A compile's hold on its dependencies' generator set (see <see cref="ProjectDependencies.AcquireGenerators"/>).</summary>
    sealed class GeneratorLease : IDisposable
    {
        ProjectDependencies? _deps;

        public GeneratorSet Acquire(ProjectDependencies deps)
        {
            var set = deps.AcquireGenerators();
            _deps = deps;
            return set;
        }

        public void Dispose()
        {
            _deps?.ReleaseGenerators();
            _deps = null;
        }
    }

    static BuildMessage ToMessage(Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        return new BuildMessage(span.Path ?? "", span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            d.Severity.ToString().ToLowerInvariant(), d.Id, d.GetMessage());
    }

    /// <summary>The designer process's own assemblies (the BCL, RadSharp, Skia...) as references: the fallback.</summary>
    static readonly Lazy<ImmutableArray<MetadataReference>> DesignerReferences = new(() =>
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => p.Length > 0 && File.Exists(p))
            .Select(Reference),
    ]);

    static readonly Dictionary<string, (long Stamp, MetadataReference Reference)> ReferenceCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A reference to an assembly file, reused while the file is unchanged (most never change: framework, packages).</summary>
    static MetadataReference Reference(string path)
    {
        long stamp = File.GetLastWriteTimeUtc(path).Ticks;
        lock (ReferenceCache)
        {
            if (ReferenceCache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Reference;
            var reference = MetadataReference.CreateFromFile(path);
            ReferenceCache[path] = (stamp, reference);
            return reference;
        }
    }

    /// <summary>Creates the form class <paramref name="className"/> from the compiled project.</summary>
    public Form CreateForm(string className)
    {
        if (Assembly == null) throw new InvalidOperationException("The project didn't compile.");
        var type = Assembly.GetTypes().FirstOrDefault(t => t.Name == className && typeof(Form).IsAssignableFrom(t))
            ?? throw new InvalidOperationException($"No form class {className} in the project.");
        return (Form)Activator.CreateInstance(type)!;
    }

    /// <summary>
    /// The control classes the project defines, for the designer's palette and surface: what the live assembly's
    /// generated metadata registers (creatable, not forms), sorted by name. With <paramref name="includeLibraries"/>
    /// also those of the project's own run-time assemblies that reference RadSharp.Core (component libraries from NuGet
    /// packages or project references), which this loads into the live context. Empty if the project didn't compile.
    /// The classes belong to this session's load context: drop every instance before disposing it.
    /// </summary>
    public IReadOnlyList<ComponentClass> GetComponentClasses(bool includeLibraries = false)
    {
        if (Assembly == null || _context == null) return [];
        var assemblies = new HashSet<Assembly> { Assembly };
        if (includeLibraries && Dependencies != null)
            foreach (var (name, path) in Dependencies.RuntimeAssemblies)
            {
                if (ProjectDependencies.SharedAssemblies.Value.ContainsKey(name)) continue;
                if (!ProjectDependencies.ReferencedNames(path).Contains("RadSharp.Core")) continue;
                try { assemblies.Add(_context.LoadFromAssemblyName(new AssemblyName(name))); }
                catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException) { }
            }
        // The generated registration is a module initializer: run it now, before any of the types is touched.
        foreach (var a in assemblies) System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(a.ManifestModule.ModuleHandle);
        return [.. ComponentRegistry.All
            .Where(c => assemblies.Contains(c.Type.Assembly) && c.CanCreate && !typeof(Form).IsAssignableFrom(c.Type))
            .OrderBy(c => c.Name, StringComparer.Ordinal)];
    }

    /// <summary>Forgets the live assemblies' component registrations (libraries' too) and unloads them.</summary>
    public void Dispose()
    {
        if (_context != null) foreach (var a in _context.Assemblies) ComponentRegistry.Unregister(a);
        Assembly = null;
        _context?.Unload();
        _context = null;
    }

    sealed class Text(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
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
