using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;

namespace RadSharp.Design;

/// <summary>
/// A project's built assembly (its <c>TargetPath</c>, as an IDE built it) loaded into a designer, so its own controls,
/// and those of the component libraries it uses, can be placed on and drawn in the forms being designed. The Visual
/// Studio design host uses it: Visual Studio has already built the project. (The standalone designer has no build of
/// its own to load, so it compiles the project itself: <c>DesignSession</c>. Both apply the same rules through
/// <see cref="ProjectClassSet"/>.)
/// <list type="bullet">
/// <item>It goes into a collectible load context: <see cref="Dispose"/> unloads it, so a rebuilt project is loaded
/// again. Assemblies the designer has (RadSharp, SkiaSharp, the .NET libraries) come from the designer's own context,
/// so the types match.</item>
/// <item>Managed files are read into memory, never locked: the project can be rebuilt while it is loaded. A native
/// library is copied to a temporary folder first, and only if the project's code asks for one.</item>
/// <item>The project's forms are not offered: a designer designs them from their files and never runs their
/// constructors (which may open databases or windows).</item>
/// <item>Its libraries' controls come too: every run-time assembly in its <c>.deps.json</c> that references
/// RadSharp.Core (a project reference, a NuGet package of controls), under the library's name on the palette.</item>
/// </list>
/// </summary>
public sealed class ProjectAssembly : IDisposable
{
    Context? _context;
    readonly DateTime _failedStamp;
    readonly List<string> _warnings = [];

    ProjectAssembly(string path) => Path = path;

    /// <param name="stampBeforeReading">The file's time stamp from before it was read: a build that finishes while it
    /// is being read gives a newer one, so the failure is retried rather than taken for the final state.</param>
    ProjectAssembly(string path, string error, DateTime stampBeforeReading) : this(path)
    {
        Error = error;
        _failedStamp = stampBeforeReading;
    }

    /// <summary>The assembly file this was loaded from (the project's build output).</summary>
    public string Path { get; }

    /// <summary>The loaded assembly; null if loading failed (see <see cref="Error"/>) or after <see cref="Dispose"/>.</summary>
    public Assembly? Assembly { get; private set; }

    /// <summary>True if the assembly file exists (a failed load of an existing file is worth a second try; of a missing one, not).</summary>
    public bool FileExists => File.Exists(Path);

    /// <summary>Why it couldn't be loaded, or null.</summary>
    public string? Error { get; }

    /// <summary>What the user should know about the loaded classes (one that throws, a setup that failed), one line each.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>The usable classes: for the documents (<see cref="ProjectClassSet.Find"/>) and the palette.</summary>
    public ProjectClassSet ClassSet { get; private set; } = ProjectClassSet.Empty;

    /// <summary>The usable classes, the project's own first.</summary>
    public IReadOnlyList<ComponentClass> Classes => ClassSet.Classes;

    /// <summary>
    /// On the UI thread: loads <paramref name="path"/> and its controls (see the class remarks). Never throws for a bad
    /// or missing file: the result has <see cref="Error"/> set instead, and offers nothing.
    /// </summary>
    public static ProjectAssembly Load(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var name = System.IO.Path.GetFileName(path);
        var stampBefore = Stamp(path);
        if (!File.Exists(path)) return new ProjectAssembly(path, $"{name} isn't built yet: build the project to see its controls.", stampBefore);
        Context context;
        try { context = new Context(path); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new ProjectAssembly(path, $"Can't read {name}'s dependencies: {e.Message}", stampBefore);
        }
        var result = new ProjectAssembly(path) { _context = context };
        var skew = CoreSkew(path);
        if (skew != null) result._warnings.Add(skew);
        try
        {
            var own = context.LoadFile(path);
            result.Assembly = own;
            var assemblies = new List<Assembly> { own };
            foreach (var library in context.ComponentLibraries())
            {
                try { assemblies.Add(context.LoadFromAssemblyName(new AssemblyName(library))); }
                catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    result._warnings.Add($"The library {library} couldn't be loaded ({e.Message}); its controls stay placeholders.");
                }
            }
            var found = ProjectClassSet.Registered(assemblies);
            result.ClassSet = ProjectClassSet.Activate(own, found, result._warnings, skew);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            result.Dispose();
            var inner = e is TargetInvocationException or TypeInitializationException && e.InnerException != null ? e.InnerException! : e;
            var hint = skew != null && (ProjectClassSet.IsMissingMember(inner) || inner is FileLoadException) ? " " + skew : "";
            return new ProjectAssembly(path, $"Can't load {name}: {inner.GetType().Name}: {inner.Message}{hint}", stampBefore);
        }
        return result;
    }

    /// <summary>
    /// True when a file it loaded (the assembly or one of the project's own dependencies) has been rewritten since or
    /// has gone, or, after a failed load, the assembly file has appeared or changed: the project was rebuilt.
    /// </summary>
    public bool IsStale()
    {
        if (_context == null) return Error != null && Stamp(Path) != _failedStamp;
        foreach (var (file, stamp) in _context.Files)
            if (Stamp(file) != stamp) return true;
        return false;
    }

    /// <summary>
    /// The RadSharp.Core version the project's own assembly was built against (from its assembly references), or null
    /// if it can't be read or has no such reference.
    /// </summary>
    public static Version? BuiltAgainstCore(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            foreach (var h in md.AssemblyReferences)
            {
                var reference = md.GetAssemblyReference(h);
                if (md.GetString(reference.Name) == "RadSharp.Core") return reference.Version;
            }
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>The RadSharp.Core version the designer itself has.</summary>
    public static Version DesignerCoreVersion => typeof(Control).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>
    /// The warning for a project built against another RadSharp.Core (major or minor version) than the designer's, or
    /// null if they match (or the reference can't be read).
    /// </summary>
    static string? CoreSkew(string path)
    {
        var built = BuiltAgainstCore(path);
        var have = DesignerCoreVersion;
        if (built == null || (built.Major == have.Major && built.Minor == have.Minor)) return null;
        return $"{System.IO.Path.GetFileName(path)} was built against RadSharp.Core {built}; the designer has {have}; " +
            $"members that don't exist in {have} show as placeholders or errors.";
    }

    /// <summary>
    /// A value that changes whenever any file of the build (the assembly, its symbols and <c>.deps.json</c>, the
    /// dependencies it loaded) is written, grows or goes: a rebuild is finished once it has stopped changing.
    /// </summary>
    public long ChangeStamp()
    {
        var files = new List<string> { Path, System.IO.Path.ChangeExtension(Path, ".pdb"), System.IO.Path.ChangeExtension(Path, ".deps.json") };
        if (_context != null) files.AddRange(_context.Files.Select(f => f.File));
        var hash = new HashCode();
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            hash.Add(file, StringComparer.OrdinalIgnoreCase);
            hash.Add(Stamp(file));
            try { hash.Add(File.Exists(file) ? new FileInfo(file).Length : -1); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { hash.Add(-2L); }
        }
        return hash.ToHashCode();
    }

    static DateTime Stamp(string file)
    {
        try { return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// <summary>
    /// Forgets the classes of every assembly it loaded and unloads them (once nothing holds an instance: reload the
    /// documents that used them first, see <see cref="DesignDocument.ReloadWith"/>).
    /// </summary>
    public void Dispose()
    {
        if (_context is not { } context) return;
        _context = null;
        foreach (var assembly in context.Assemblies) ComponentRegistry.Unregister(assembly);
        Assembly = null;
        ClassSet = ProjectClassSet.Empty;
        try { context.Unload(); }
        catch (InvalidOperationException) { }   // already unloading
    }

    /// <summary>
    /// The project's load context: assemblies the designer already has (RadSharp, SkiaSharp, the BCL) resolve to the
    /// designer's copies; the rest come from where the project's <c>.deps.json</c> says (its output folder, the NuGet
    /// cache), read into memory.
    /// </summary>
    sealed class Context : AssemblyLoadContext
    {
        static readonly HashSet<string> HostAssemblies = new(
            ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
                .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(System.IO.Path.GetFileNameWithoutExtension)
                .OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        static int _count;
        readonly AssemblyDependencyResolver _resolver;
        readonly string _path, _directory;
        readonly object _lock = new();
        readonly List<(string File, DateTime Stamp)> _files = [];
        string? _nativeDirectory;

        public Context(string path) : base($"RadSharp.Project {System.IO.Path.GetFileNameWithoutExtension(path)}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(path);
            _path = path;
            _directory = System.IO.Path.GetDirectoryName(path)!;
        }

        /// <summary>The managed files loaded so far, with their write times when they were read.</summary>
        public IReadOnlyList<(string File, DateTime Stamp)> Files { get { lock (_lock) return [.. _files]; } }

        // Loading a project's assembly is what a designer does; designers aren't trimmed.
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Design time only: designers aren't trimmed.")]
        public Assembly LoadFile(string path)
        {
            var stamp = Stamp(path);
            var pdb = System.IO.Path.ChangeExtension(path, ".pdb");
            using var image = new MemoryStream(File.ReadAllBytes(path));
            using var symbols = ReadSymbols(pdb);
            var assembly = LoadFromStream(image, symbols);
            lock (_lock) _files.Add((path, stamp));
            return assembly;
        }

        /// <summary>The symbols, or null (the assembly loads without them) if the file is missing, locked or unreadable.</summary>
        static MemoryStream? ReadSymbols(string pdb)
        {
            try { return File.Exists(pdb) ? new MemoryStream(File.ReadAllBytes(pdb)) : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }

        static bool IsShared(string name) =>
            HostAssemblies.Contains(name) || Default.Assemblies.Any(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == null || IsShared(name.Name)) return null;   // the designer's copy
            var path = _resolver.ResolveAssemblyToPath(name);
            if (path == null)
            {
                var local = System.IO.Path.Combine(_directory, name.Name + ".dll");
                if (File.Exists(local)) path = local;
            }
            return path == null ? null : LoadFile(path);
        }

        /// <summary>
        /// The simple names of the project's run-time assemblies (from its <c>.deps.json</c>, else the DLLs next to it)
        /// that reference RadSharp.Core, other than the project itself and the designer's own.
        /// </summary>
        public IEnumerable<string> ComponentLibraries()
        {
            var own = System.IO.Path.GetFileNameWithoutExtension(_path);
            foreach (var name in RuntimeAssemblyNames().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (name.Equals(own, StringComparison.OrdinalIgnoreCase) || IsShared(name)) continue;
                var path = _resolver.ResolveAssemblyToPath(new AssemblyName(name));
                if (path == null)
                {
                    var local = System.IO.Path.Combine(_directory, name + ".dll");
                    if (File.Exists(local)) path = local;
                }
                if (path != null && ReferencesRadSharp(path)) yield return name;
            }
        }

        IEnumerable<string> RuntimeAssemblyNames()
        {
            var deps = System.IO.Path.ChangeExtension(_path, ".deps.json");
            if (!File.Exists(deps)) return Directory.EnumerateFiles(_directory, "*.dll").Select(f => System.IO.Path.GetFileNameWithoutExtension(f));
            var names = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(deps));
                if (!doc.RootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object) return names;
                foreach (var target in targets.EnumerateObject())
                {
                    if (target.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var library in target.Value.EnumerateObject())
                        if (library.Value.ValueKind == JsonValueKind.Object && library.Value.TryGetProperty("runtime", out var runtime)
                            && runtime.ValueKind == JsonValueKind.Object)
                            foreach (var file in runtime.EnumerateObject())
                                if (file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                    names.Add(System.IO.Path.GetFileNameWithoutExtension(file.Name));
                    break;   // the first target is the project's own framework (a RID-specific one follows it)
                }
            }
            catch (JsonException) { }
            return names;
        }

        static bool ReferencesRadSharp(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) return false;
                var md = pe.GetMetadataReader();
                foreach (var h in md.AssemblyReferences)
                    if (md.GetString(md.GetAssemblyReference(h).Name) == "RadSharp.Core") return true;
            }
            catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException) { }
            return false;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path == null) return IntPtr.Zero;
            // A native library can't be loaded from memory: copy it, so the project's own copy stays writable.
            var copy = System.IO.Path.Combine(NativeDirectory(), System.IO.Path.GetFileName(path));
            if (!File.Exists(copy)) File.Copy(path, copy);
            return LoadUnmanagedDllFromPath(copy);
        }

        string NativeDirectory()
        {
            lock (_lock)
            {
                if (_nativeDirectory != null) return _nativeDirectory;
                var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RadSharp.DesignHost");
                CleanUp(root);
                _nativeDirectory = System.IO.Path.Combine(root, $"{Environment.ProcessId}-{Interlocked.Increment(ref _count)}");
                Directory.CreateDirectory(_nativeDirectory);
                return _nativeDirectory;
            }
        }

        /// <summary>Removes the native copies of designer processes that are no longer running.</summary>
        static void CleanUp(string root)
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = System.IO.Path.GetFileName(dir);
                int dash = name.IndexOf('-');
                if (dash <= 0 || !int.TryParse(name[..dash], out var pid) || pid == Environment.ProcessId) continue;
                try { using var p = System.Diagnostics.Process.GetProcessById(pid); continue; }
                catch (ArgumentException) { }   // not running
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
