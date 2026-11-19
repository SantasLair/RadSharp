using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RadSharp.Design.Code;

/// <summary>A <c>Using</c> item of the project (what <c>ImplicitUsings</c> and <c>&lt;Using Include="..."/&gt;</c> add).</summary>
public sealed record ProjectUsing(string Namespace, string? Alias, bool IsStatic)
{
    /// <summary>The <c>global using</c> directive the SDK generates for it.</summary>
    public string ToDirective() =>
        Alias is { Length: > 0 } ? $"global using {Alias} = global::{Namespace};"
        : IsStatic ? $"global using static global::{Namespace};"
        : $"global using global::{Namespace};";
}

/// <summary>
/// The outcome of <see cref="ProjectDependencies.Resolve(RadProject, TimeSpan, Action{string}?)"/>: the dependencies,
/// or null with <see cref="Error"/> saying why; <see cref="FromCache"/> when no MSBuild ran (a cached result, or a
/// cached failure for unchanged inputs).
/// </summary>
public sealed record DependencyResolution(ProjectDependencies? Dependencies, string? Error, bool FromCache);

/// <summary>
/// What a live run (F9) compiles and loads a project with, as MSBuild resolves it for a real build: the compile
/// references (NuGet packages with their transitive dependencies, framework references, built project references),
/// the analyzer assemblies (source generators, e.g. System.Text.Json's from the targeting pack), the compile settings,
/// and the run-time assets (managed assemblies and RID-specific native libraries).
/// </summary>
/// <remarks>
/// Resolved by running <c>dotnet msbuild -restore -t:ResolveReferences;GenerateMSBuildEditorConfigFile -getItem:...
/// -getProperty:...</c> in the project's folder, then cached in memory, keyed on the timestamps of the project file,
/// <c>obj/project.assets.json</c>, the <c>Directory.*.props/targets</c>, <c>global.json</c> and <c>nuget.config</c>
/// files in every folder above it (present or not), and the project references' project files and outputs; a project
/// reference whose sources changed since MSBuild last ran is resolved (and so rebuilt) again. A failure is cached too,
/// keyed on the same settings files, the restore output and the project references' project files and sources, so a
/// project that can't be resolved doesn't run a full restore on every F9; <see cref="Invalidate"/> forgets it.
/// Resolving can take minutes (a cold restore): call <see cref="ResolveAsync"/> from a UI thread.
/// </remarks>
public sealed partial class ProjectDependencies
{
    /// <summary>Compile references: full paths, RadSharp's own assemblies replaced by the designer's copies.</summary>
    public IReadOnlyList<string> References { get; private init; } = [];
    /// <summary>Analyzer assemblies the project's compile would load, minus RadSharp's generators (they run in-process).</summary>
    public IReadOnlyList<string> Analyzers { get; private init; } = [];
    /// <summary>The project's <c>.editorconfig</c>/<c>.globalconfig</c> files, including MSBuild's generated one (generator options).</summary>
    public IReadOnlyList<string> AnalyzerConfigFiles { get; private init; } = [];
    /// <summary>The project's <c>Compile</c> items: full paths, as the build compiles them (see <see cref="SourceFiles"/>).</summary>
    public IReadOnlyList<string> CompileFiles { get; private init; } = [];
    /// <summary>AdditionalFiles other than form files (those come from the designer's current state).</summary>
    public IReadOnlyList<string> AdditionalFiles { get; private init; } = [];
    public IReadOnlyList<ProjectUsing> Usings { get; private init; } = [];

    public string LangVersion { get; private init; } = "";
    public string Nullable { get; private init; } = "";
    public IReadOnlyList<string> DefineConstants { get; private init; } = [];
    public bool AllowUnsafeBlocks { get; private init; }
    public bool CheckForOverflowUnderflow { get; private init; }
    public string RootNamespace { get; private init; } = "";
    public string AssemblyName { get; private init; } = "";

    /// <summary>Managed run-time assemblies the live context loads itself, by simple name (best RID match).</summary>
    public IReadOnlyDictionary<string, string> RuntimeAssemblies { get; private init; } = new Dictionary<string, string>();
    /// <summary>Native libraries for this OS and architecture (best RID match first).</summary>
    public IReadOnlyList<string> NativeLibraries { get; private init; } = [];

    /// <summary>How long the MSBuild run took when these were resolved.</summary>
    public TimeSpan ResolveTime { get; private init; }

    /// <summary>The project files of the project's project references (full paths).</summary>
    public IReadOnlyList<string> ProjectReferences => [.. _projectReferences.Select(p => p.ProjectFile).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>True when the root folder of NuGet packages or the .NET install holds the file (it never changes).</summary>
    internal bool IsImmutable(string path) =>
        (_packageRoot.Length > 0 && path.StartsWith(_packageRoot, StringComparison.OrdinalIgnoreCase))
        || path.StartsWith(DotnetRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The C# files a live run compiles: the project's <c>Compile</c> items (linked files and <c>Compile Remove</c>
    /// included), plus files in its folder that appeared since it was resolved (a new form's code: the default glob
    /// takes them, and they shouldn't need an MSBuild run).
    /// </summary>
    public IReadOnlyList<string> SourceFiles(RadProject project)
    {
        var compile = new HashSet<string>(CompileFiles, StringComparer.OrdinalIgnoreCase);
        var inFolder = project.SourceFiles;
        var files = inFolder.Where(f => compile.Contains(f) || !_scanned.Contains(f)).ToList();
        var seen = new HashSet<string>(inFolder, StringComparer.OrdinalIgnoreCase);
        files.AddRange(CompileFiles.Where(f => !seen.Contains(f) && File.Exists(f)));
        return files;
    }

    /// <summary>The C# files in the project's folder when it was resolved.</summary>
    IReadOnlySet<string> _scanned = new HashSet<string>();
    string _packageRoot = "";
    string _key = "";
    IReadOnlyList<string> _watched = [];
    IReadOnlyList<(string ProjectFile, string Output)> _projectReferences = [];
    DateTime _resolvedAt;
    internal GeneratorSet? Generators;

    // ---- Resolving ----

    static readonly ConcurrentDictionary<string, ProjectDependencies> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, Failure> Failures = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A resolve that failed, and what it read: the same inputs fail the same way.</summary>
    sealed record Failure(string Key, string Error, DateTime At, IReadOnlyList<string> ReferenceDirectories);

    /// <summary>Raised (on the resolving thread) with the project file just before MSBuild starts for it: diagnostics and tests.</summary>
    public static event Action<string>? MSBuildStarting;

    /// <summary>Forgets every cached resolution and failure (the next live run of each project runs MSBuild again).</summary>
    public static void ClearCache()
    {
        foreach (var d in Cache.Values) d.Generators?.Dispose();
        Cache.Clear();
        Failures.Clear();
    }

    /// <summary>Forgets <paramref name="project"/>'s cached resolution and cached failure (the designer's Project › Refresh).</summary>
    public static void Invalidate(RadProject project)
    {
        lock (Locks.GetOrAdd(project.ProjectFile, _ => new object()))
        {
            Failures.TryRemove(project.ProjectFile, out _);
            if (Cache.TryRemove(project.ProjectFile, out var old)) old.Generators?.Dispose();
        }
    }

    /// <summary>
    /// The project's dependencies, from the cache when nothing they depend on changed, else from MSBuild (which
    /// restores and builds project references). Returns null with <paramref name="error"/> set if that fails: no SDK,
    /// a restore error, a timeout.
    /// </summary>
    public static ProjectDependencies? Resolve(RadProject project, TimeSpan timeout, out string? error, out bool fromCache)
    {
        var result = Resolve(project, timeout);
        error = result.Error;
        fromCache = result.FromCache;
        return result.Dependencies;
    }

    /// <summary>
    /// The project's dependencies, from the cache when nothing they depend on changed, else from MSBuild (which
    /// restores and builds project references); blocks until done. A failure (no SDK, a restore error, a timeout) is
    /// remembered: until its inputs change or <see cref="Invalidate"/> is called, resolving again returns it at once.
    /// <paramref name="progress"/> hears when MSBuild starts (it may take minutes). One resolve per project at a time:
    /// a second caller waits for the first and takes its result.
    /// </summary>
    public static DependencyResolution Resolve(RadProject project, TimeSpan timeout, Action<string>? progress = null)
    {
        lock (Locks.GetOrAdd(project.ProjectFile, _ => new object()))
        {
            if (Cache.TryGetValue(project.ProjectFile, out var cached) && cached.IsCurrent())
                return new(cached, null, true);
            if (Failures.TryGetValue(project.ProjectFile, out var failure) && failure.Key == FailureInputs(project).Key
                && !failure.ReferenceDirectories.Any(d => SourcesNewerThan(d, failure.At)))
                return new(null, failure.Error, true);

            var startedAt = DateTime.UtcNow;
            ProjectDependencies? resolved;
            string? error;
            progress?.Invoke($"Restoring and resolving {project.Name}'s packages and references (MSBuild)…");
            MSBuildStarting?.Invoke(project.ProjectFile);
            try { resolved = RunMSBuild(project, timeout, out error); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Whatever goes wrong here, F9 falls back instead of taking the designer down.
                resolved = null;
                error = $"{e.GetType().Name}: {e.Message}";
            }
            if (resolved == null)
            {
                error ??= "MSBuild failed";
                // The inputs as the attempt left them (its restore may have written project.assets.json).
                var (key, referenceDirs) = FailureInputs(project);
                Failures[project.ProjectFile] = new Failure(key, error, startedAt, referenceDirs);
                return new(null, error, false);
            }
            Failures.TryRemove(project.ProjectFile, out _);
            if (Cache.TryGetValue(project.ProjectFile, out var old) && old != resolved) old.Generators?.Dispose();
            Cache[project.ProjectFile] = resolved;
            return new(resolved, null, false);
        }
    }

    /// <summary>
    /// <see cref="Resolve(RadProject, TimeSpan, Action{string}?)"/> on a thread-pool thread. Cancelling stops the
    /// wait, not MSBuild: its result still goes to the cache, for the next resolve of the project to take.
    /// </summary>
    public static Task<DependencyResolution> ResolveAsync(RadProject project, TimeSpan timeout, Action<string>? progress = null, CancellationToken cancel = default) =>
        Task.Run(() => Resolve(project, timeout, progress), CancellationToken.None).WaitAsync(cancel);

    /// <summary>
    /// What a failed resolve read, as far as it can be known without MSBuild: the project file, its restore output,
    /// the settings files above it, and its project references, theirs and so on (their project files, and their
    /// folders' sources): MSBuild builds every one of them, and any of them can be what failed.
    /// </summary>
    static (string Key, IReadOnlyList<string> ReferenceDirectories) FailureInputs(RadProject project)
    {
        var files = new List<string> { project.ProjectFile, Path.Combine(project.Directory, "obj", "project.assets.json") };
        for (var dir = new DirectoryInfo(project.Directory); dir != null; dir = dir.Parent)
            foreach (var f in AncestorFiles) files.Add(Path.Combine(dir.FullName, f));
        var referenceDirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project.ProjectFile };
        var pending = new Queue<string>([project.ProjectFile]);
        while (pending.Count > 0)
        {
            var file = pending.Dequeue();
            try
            {
                var folder = Path.GetDirectoryName(file)!;
                foreach (Match m in ProjectReferencePattern().Matches(File.ReadAllText(file)))
                {
                    var path = Path.GetFullPath(m.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar), folder);
                    if (!seen.Add(path)) continue;
                    files.Add(path);
                    referenceDirs.Add(Path.GetDirectoryName(path)!);
                    pending.Enqueue(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        try { return (Stamp(files.Distinct(StringComparer.OrdinalIgnoreCase)), referenceDirs); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (Guid.NewGuid().ToString(), referenceDirs); }
    }

    // Include in either quotes, after any other attribute (a Condition, say).
    [GeneratedRegex("""<ProjectReference\b[^>]*?\bInclude\s*=\s*(?:"(?<path>[^"]+)"|'(?<path>[^']+)')""", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectReferencePattern();

    bool IsCurrent()
    {
        try
        {
            if (Stamp(_watched) != _key) return false;
            // What the compile and the run read (a package cache cleaned, a vendor folder moved: restore again).
            if (!References.All(File.Exists) || !RuntimeAssemblies.Values.All(File.Exists) || !NativeLibraries.All(File.Exists)) return false;
            // A source edited since MSBuild last ran (not since the output was written: a file the build doesn't read, say
            // .vscode/settings.json, never makes it rebuild, so it would stay newer than the output for good).
            foreach (var (projectFile, _) in _projectReferences)
                if (SourcesNewerThan(Path.GetDirectoryName(projectFile)!, _resolvedAt)) return false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Files whose change means a project reference must be rebuilt (not, say, its README).</summary>
    static readonly string[] SourceExtensions = [".cs", ".csproj", ".props", ".targets", ".resx", ".json"];

    static string Stamp(IEnumerable<string> files) =>
        string.Join("|", files.Select(f => File.Exists(f) ? f + "@" + File.GetLastWriteTimeUtc(f).Ticks : f + "@-"));

    static bool SourcesNewerThan(string dir, DateTime time)
    {
        if (!Directory.Exists(dir)) return true;
        int seen = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, f);
            if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or ".vs" or ".git" or "packages")) continue;
            if (!SourceExtensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
            if (File.GetLastWriteTimeUtc(f) > time) return true;
            if (++seen > 5000) break;   // a huge tree: don't stall F9 on it
        }
        return false;
    }

    // RuntimeCopyLocalItems and its siblings are the package run-time assets ResolvePackageAssets finds; only an
    // executable (or CopyLocalLockFileAssemblies) copies them into ReferenceCopyLocalPaths, and a class library's
    // controls need them in the designer as much as an application's do.
    static readonly string[] CopyLocalItemNames = ["ReferenceCopyLocalPaths", "RuntimeCopyLocalItems", "NativeCopyLocalItems", "RuntimeTargetsCopyLocalItems"];
    static readonly string[] ItemNames = ["ReferencePath", "Analyzer", .. CopyLocalItemNames, "EditorConfigFiles", "Using", "AdditionalFiles", "Compile"];
    static readonly string[] PropertyNames =
    [
        "LangVersion", "Nullable", "DefineConstants", "AllowUnsafeBlocks", "CheckForOverflowUnderflow", "RootNamespace",
        "AssemblyName", "ProjectAssetsFile", "NuGetPackageRoot", "TargetFramework", "TargetFrameworks",
    ];

    /// <summary>Settings files above a project that change how it evaluates (their appearance too).</summary>
    static readonly string[] AncestorFiles = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "nuget.config", "NuGet.Config"];

    static ProjectDependencies? RunMSBuild(RadProject project, TimeSpan timeout, out string? error)
    {
        error = null;
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = project.Directory,   // so the project's own global.json picks the SDK
        };
        foreach (var a in new[] { "msbuild", project.ProjectFile, "-restore", "-nologo", "-m:1", "-nodeReuse:false",
                     "-p:UseSharedCompilation=false", "-t:ResolveReferences;GenerateMSBuildEditorConfigFile" })
            psi.ArgumentList.Add(a);
        foreach (var i in ItemNames) psi.ArgumentList.Add("-getItem:" + i);
        foreach (var p in PropertyNames) psi.ArgumentList.Add("-getProperty:" + p);
        // One process, nothing left running afterwards (no reused nodes, no compiler server for project references).
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        // A host such as `dotnet test` exports its own MSBuild location; the project's SDK must choose its own.
        foreach (var v in new[] { "MSBuildExtensionsPath", "MSBUILD_EXE_PATH", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly" })
            psi.Environment.Remove(v);

        var watch = Stopwatch.StartNew();
        var startedAt = DateTime.UtcNow;
        Process process;
        try { process = Process.Start(psi)!; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            error = "the .NET SDK (dotnet) wasn't found";
            return null;
        }
        string stdout, stderr;
        using (process)
        {
            var outTask = process.StandardOutput.ReadToEndAsync();
            var errTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                // The process started here and what it started (restore, a project reference's compiler, an Exec):
                // killing only the dotnet process would leave its children running, holding the output pipes.
                try { process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                error = $"resolving the project's dependencies took longer than {timeout.TotalSeconds:0} s";
                return null;
            }
            process.WaitForExit();
            stdout = outTask.Result;
            stderr = errTask.Result;
            if (process.ExitCode != 0)
            {
                error = "MSBuild couldn't restore or resolve the project: " + FirstErrors(stderr + "\n" + stdout);
                return null;
            }
        }

        try { return Parse(project, stdout, watch.Elapsed, startedAt); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            error = "MSBuild's output couldn't be read: " + e.Message;
            return null;
        }
    }

    static string FirstErrors(string log)
    {
        var errors = log.Split('\n').Select(l => l.Trim()).Where(l => l.Contains(": error ", StringComparison.Ordinal)).Distinct().Take(3).ToList();
        if (errors.Count == 0) errors = [.. log.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3)];
        return errors.Count == 0 ? "(no output)" : string.Join(" ", errors);
    }

    sealed record Item(string Identity, IReadOnlyDictionary<string, string> Metadata)
    {
        public string Meta(string name) => Metadata.GetValueOrDefault(name, "");
    }

    static ProjectDependencies Parse(RadProject project, string json, TimeSpan elapsed, DateTime startedAt)
    {
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("Properties");
        string Prop(string name) => props.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        var itemsJson = doc.RootElement.GetProperty("Items");
        List<Item> Items(string name)
        {
            var list = new List<Item>();
            if (!itemsJson.TryGetProperty(name, out var array)) return list;
            foreach (var e in array.EnumerateArray())
            {
                var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in e.EnumerateObject()) meta[m.Name] = m.Value.GetString() ?? "";
                list.Add(new Item(meta.GetValueOrDefault("Identity", ""), meta));
            }
            return list;
        }
        string FullPath(Item i) => i.Meta("FullPath") is { Length: > 0 } f ? f : Path.GetFullPath(i.Identity, project.Directory);

        if (Prop("TargetFramework").Length == 0 && Prop("TargetFrameworks").Length > 0)
            throw new InvalidOperationException("multi-targeted projects (TargetFrameworks) aren't supported by live runs");

        // Compile references; RadSharp's own (and what they expose) are the designer's copies, so types match at run time.
        var shared = SharedAssemblies.Value;
        var references = new List<string>();
        var projectReferences = new List<(string, string)>();
        foreach (var r in Items("ReferencePath"))
        {
            var path = FullPath(r);
            var name = Path.GetFileNameWithoutExtension(path);
            references.Add(shared.TryGetValue(name, out var designerCopy) ? designerCopy : path);
            if (r.Meta("ReferenceSourceTarget") == "ProjectReference" && r.Meta("MSBuildSourceProjectFile") is { Length: > 0 } source)
                projectReferences.Add((source, path));
        }

        var analyzers = Items("Analyzer").Select(FullPath).Select(Path.GetFullPath)
            .Where(p => !Path.GetFileName(p).Equals("RadSharp.Generators.dll", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Run-time assets: managed ones by simple name (a RID-specific one beats lib/), native ones for this platform.
        var rids = RidFallbacks();
        var runtime = new Dictionary<string, (string Path, int Rank)>(StringComparer.OrdinalIgnoreCase);
        var natives = new List<(string Path, int Rank)>();
        foreach (var i in CopyLocalItemNames.SelectMany(Items))
        {
            var path = FullPath(i);
            var rid = i.Meta("RuntimeIdentifier");
            int rank = rid.Length == 0 ? rids.Count : rids.IndexOf(rid);
            if (rank < 0) continue;   // another platform's asset
            var assetType = i.Meta("AssetType");
            if (assetType == "native") { natives.Add((path, rank)); continue; }
            if (assetType == "resources") continue;   // satellite assemblies: not loaded by live runs
            var ext = Path.GetExtension(path);
            if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileNameWithoutExtension(path);
            if (!runtime.TryGetValue(name, out var have) || rank < have.Rank) runtime[name] = (path, rank);
        }
        // A class library's build copies nothing local: its project references' outputs and plain file references are
        // what it runs with too (packages' are in the items above; their ReferencePath may be reference assemblies).
        foreach (var r in Items("ReferencePath"))
        {
            if (r.Meta("NuGetPackageId").Length > 0 || r.Meta("FrameworkReferenceName").Length > 0) continue;
            var path = FullPath(r);
            var name = Path.GetFileNameWithoutExtension(path);
            if (path.StartsWith(DotnetRoot, StringComparison.OrdinalIgnoreCase) || shared.ContainsKey(name)) continue;
            runtime.TryAdd(name, (path, rids.Count + 1));
        }

        var usings = Items("Using").Select(u => new ProjectUsing(u.Identity, u.Meta("Alias") is { Length: > 0 } a ? a : null,
            u.Meta("Static").Equals("true", StringComparison.OrdinalIgnoreCase))).Distinct().ToList();

        // What the cache watches: the project, its restore output, settings files above it, and its project references.
        var watched = new List<string> { project.ProjectFile };
        if (Prop("ProjectAssetsFile") is { Length: > 0 } assets) watched.Add(Path.GetFullPath(assets, project.Directory));
        for (var dir = new DirectoryInfo(project.Directory); dir != null; dir = dir.Parent)
            foreach (var f in AncestorFiles)
            {
                var path = Path.Combine(dir.FullName, f);
                // Every folder's possible ones, existing or not: a Directory.Build.props added at the repository root
                // (above the project's folder) changes the evaluation as much as one next to the project.
                watched.Add(path);
            }
        foreach (var (projectFile, output) in projectReferences) { watched.Add(projectFile); watched.Add(output); }
        watched = [.. watched.Distinct(StringComparer.OrdinalIgnoreCase)];

        var packageRoot = Prop("NuGetPackageRoot");
        return new ProjectDependencies
        {
            References = references.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            CompileFiles = [.. Items("Compile").Select(FullPath).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)],
            _scanned = new HashSet<string>(project.SourceFiles, StringComparer.OrdinalIgnoreCase),
            Analyzers = analyzers,
            AnalyzerConfigFiles = Items("EditorConfigFiles").Select(FullPath).Where(File.Exists).ToList(),
            AdditionalFiles = Items("AdditionalFiles").Select(FullPath)
                .Where(p => !p.EndsWith(".radform.json", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList(),
            Usings = usings,
            LangVersion = Prop("LangVersion"),
            Nullable = Prop("Nullable"),
            DefineConstants = Prop("DefineConstants").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AllowUnsafeBlocks = Prop("AllowUnsafeBlocks").Equals("true", StringComparison.OrdinalIgnoreCase),
            CheckForOverflowUnderflow = Prop("CheckForOverflowUnderflow").Equals("true", StringComparison.OrdinalIgnoreCase),
            RootNamespace = Prop("RootNamespace"),
            AssemblyName = Prop("AssemblyName"),
            RuntimeAssemblies = runtime.ToDictionary(kv => kv.Key, kv => kv.Value.Path, StringComparer.OrdinalIgnoreCase),
            NativeLibraries = [.. natives.OrderBy(n => n.Rank).Select(n => n.Path).Distinct(StringComparer.OrdinalIgnoreCase)],
            ResolveTime = elapsed,
            _packageRoot = packageRoot.Length > 0 ? Path.GetFullPath(packageRoot) : "",
            _watched = watched,
            _key = Stamp(watched),
            _projectReferences = projectReferences,
            _resolvedAt = startedAt,
        };
    }

    // ---- Platform ----

    static readonly string DotnetRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

    /// <summary>
    /// The runtime identifiers whose assets apply here, most specific first (the portable RID graph: win-x64 → win →
    /// any; linux-musl-x64 → linux-musl → linux → unix → any; osx-arm64 → osx → unix → any).
    /// </summary>
    internal static List<string> RidFallbacks()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var list = new List<string> { rid };
        int dash = rid.LastIndexOf('-');
        string os = dash > 0 ? rid[..dash] : rid, arch = dash > 0 ? rid[(dash + 1)..] : "";
        if (os != rid) list.Add(os);
        if (os == "linux-musl") { list.Add("linux-" + arch); list.Add("linux"); }
        if (!os.StartsWith("win", StringComparison.Ordinal)) list.Add("unix");
        list.Add("any");
        return [.. list.Distinct()];
    }

    /// <summary>The designer process's trusted platform assemblies by simple name.</summary>
    internal static readonly Lazy<Dictionary<string, string>> TrustedPlatformAssemblies = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator))
            if (p.Length > 0) map.TryAdd(Path.GetFileNameWithoutExtension(p), p);
        return map;
    });

    /// <summary>
    /// The designer's assemblies a live run must share rather than load its own copies of: RadSharp's (minus the
    /// designer-only code and generator assemblies) and every non-framework assembly they reference, transitively
    /// (SkiaSharp, HarfBuzzSharp...), so a <c>Form</c> is the same type on both sides. By simple name → path.
    /// </summary>
    internal static readonly Lazy<Dictionary<string, string>> SharedAssemblies = new(() =>
    {
        var tpa = TrustedPlatformAssemblies.Value;
        var framework = RuntimeEnvironment.GetRuntimeDirectory();
        var shared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        // RadSharp's libraries an application uses, wherever this process has them: loaded or not yet (it is computed
        // once), and not any other "RadSharp.*" assembly of the process (the designer itself, a test assembly), whose
        // own references (Roslyn) are not the application's business.
        foreach (var name in RuntimeLibraries)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name && !a.IsDynamic && a.Location.Length > 0);
            var path = loaded?.Location ?? tpa.GetValueOrDefault(name);
            if (path == null) continue;
            shared[name] = path;
            queue.Enqueue(path);
        }
        while (queue.Count > 0)
            foreach (var reference in ReferencedNames(queue.Dequeue()))
            {
                // Not through the designer-only assemblies (RadSharp.Designer references RadSharp.Design.Code): their
                // dependencies, Roslyn above all, are the designer's business, and an app may use its own versions.
                if (shared.ContainsKey(reference) || IsDesignerOnly(reference) || !tpa.TryGetValue(reference, out var path)) continue;
                if (path.StartsWith(framework, StringComparison.OrdinalIgnoreCase)) continue;
                shared[reference] = path;
                queue.Enqueue(path);
            }
        return shared;
    });

    /// <summary>RadSharp's assemblies an application references (the packages, and the designer surface library).</summary>
    static string[] RuntimeLibraries => ["RadSharp.Core", "RadSharp.Platform.Sdl", "RadSharp.Design"];

    static bool IsDesignerOnly(string name) => name is "RadSharp.Design.Code" or "RadSharp.Generators";

    /// <summary>The simple names an assembly file references, read from its metadata (without loading it).</summary>
    internal static IEnumerable<string> ReferencedNames(string path)
    {
        List<string> names = [];
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return names;
            var md = pe.GetMetadataReader();
            foreach (var h in md.AssemblyReferences) names.Add(md.GetString(md.GetAssemblyReference(h).Name));
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException) { }
        return names;
    }
}
