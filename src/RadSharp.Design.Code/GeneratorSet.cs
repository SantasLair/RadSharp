using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RadSharp.Design.Code;

/// <summary>
/// The source generators in a project's analyzer assemblies, loaded once per resolution into collectible load
/// contexts (one per folder, as the compiler does) that share the designer's Roslyn. Diagnostic-only analyzers are
/// never loaded: a live run only needs the code generators produce.
/// </summary>
sealed class GeneratorSet : IDisposable
{
    readonly Loader _loader = new();

    public ImmutableArray<ISourceGenerator> Generators { get; }
    /// <summary>Analyzer assemblies that failed to load, as messages for the Problems list.</summary>
    public IReadOnlyList<string> Failures { get; }

    public GeneratorSet(IReadOnlyList<string> analyzerPaths)
    {
        foreach (var p in analyzerPaths) _loader.AddDependencyLocation(p);
        var generators = ImmutableArray.CreateBuilder<ISourceGenerator>();
        var failures = new List<string>();
        foreach (var path in analyzerPaths)
        {
            var reference = new AnalyzerFileReference(path, _loader);
            reference.AnalyzerLoadFailed += (_, e) =>
                failures.Add($"{Path.GetFileName(path)}: {e.Message}{(e.Exception is { } x ? " (" + x.Message + ")" : "")}");
            try { generators.AddRange(reference.GetGenerators(LanguageNames.CSharp)); }
            catch (Exception e) when (e is IOException or BadImageFormatException or FileLoadException or ReflectionTypeLoadException)
            {
                failures.Add($"{Path.GetFileName(path)}: {e.Message}");
            }
        }
        Generators = generators.ToImmutable();
        Failures = failures;
    }

    /// <summary>Whether the load contexts were unloaded.</summary>
    public bool IsUnloaded { get; private set; }

    public void Dispose()
    {
        IsUnloaded = true;
        _loader.Unload();
    }

    sealed class Loader : IAnalyzerAssemblyLoader
    {
        readonly Dictionary<string, Folder> _folders = new(StringComparer.OrdinalIgnoreCase);
        readonly object _lock = new();

        Folder For(string path)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            lock (_lock)
            {
                if (!_folders.TryGetValue(dir, out var folder)) _folders[dir] = folder = new Folder(dir);
                return folder;
            }
        }

        public void AddDependencyLocation(string fullPath) => For(fullPath).Add(fullPath);

        public Assembly LoadFromPath(string fullPath)
        {
            var folder = For(fullPath);
            folder.Add(fullPath);
            return folder.LoadFile(fullPath);
        }

        public void Unload()
        {
            lock (_lock)
            {
                foreach (var f in _folders.Values) f.Unload();
                _folders.Clear();
            }
        }
    }

    /// <summary>One analyzer folder's assemblies; Roslyn and the framework come from the designer.</summary>
    sealed class Folder(string dir) : AssemblyLoadContext("RadSharp.Analyzers " + dir, isCollectible: true)
    {
        readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string path)
        {
            lock (_files) _files.TryAdd(Path.GetFileNameWithoutExtension(path), Path.GetFullPath(path));
        }

        public Assembly LoadFile(string path)
        {
            path = Path.GetFullPath(path);
            lock (_loaded)
            {
                if (_loaded.TryGetValue(path, out var a)) return a;
                // From memory: a project's own analyzer (a ProjectReference with OutputItemType="Analyzer") stays rebuildable.
                a = LoadFromStream(new MemoryStream(SharedFile.ReadAllBytes(path)));
                _loaded[path] = a;
                return a;
            }
        }

        protected override Assembly? Load(AssemblyName name)
        {
            var simple = name.Name ?? "";
            // Generators must see the designer's Microsoft.CodeAnalysis (and the framework it runs on).
            if (simple.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)) return null;
            if (ProjectDependencies.TrustedPlatformAssemblies.Value.ContainsKey(simple)) return null;
            string? path;
            lock (_files) _files.TryGetValue(simple, out path);
            if (path == null)
            {
                var candidate = Path.Combine(dir, simple + ".dll");
                if (File.Exists(candidate)) path = candidate;
            }
            return path != null ? LoadFile(path) : null;
        }
    }
}

/// <summary>Analyzer config options from parsed <c>.editorconfig</c>/<c>.globalconfig</c> files, plus fixed global ones.</summary>
sealed class ConfigOptionsProvider : AnalyzerConfigOptionsProvider
{
    readonly AnalyzerConfigSet? _set;
    readonly Options _global;

    public ConfigOptionsProvider(IEnumerable<AnalyzerConfig> configs, IReadOnlyDictionary<string, string> globalOverrides)
    {
        var list = configs.ToList();
        _set = list.Count > 0 ? AnalyzerConfigSet.Create(list) : null;
        var global = _set?.GlobalConfigOptions.AnalyzerOptions ?? ImmutableDictionary.Create<string, string>(AnalyzerConfigOptions.KeyComparer);
        foreach (var (k, v) in globalOverrides)
            if (!global.ContainsKey(k) || global[k].Length == 0) global = global.SetItem(k, v);
        _global = new Options(global);
    }

    public override AnalyzerConfigOptions GlobalOptions => _global;
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => For(tree.FilePath);
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => For(textFile.Path);

    AnalyzerConfigOptions For(string path) =>
        _set == null || string.IsNullOrEmpty(path) ? Options.Empty : new Options(_set.GetOptionsForSourcePath(path).AnalyzerOptions);

    sealed class Options(ImmutableDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public static readonly Options Empty = new(ImmutableDictionary.Create<string, string>(KeyComparer));
        public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) =>
            values.TryGetValue(key, out value);
        public override IEnumerable<string> Keys => values.Keys;
    }
}
