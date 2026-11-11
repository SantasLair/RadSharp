using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using RadSharp.Design.Code;

namespace RadSharp.Ide.Language;

/// <summary>Options for <see cref="ProjectWorkspace.OpenAsync"/>.</summary>
public sealed class ProjectWorkspaceOptions
{
    /// <summary>When to run <c>dotnet restore</c> for package references.</summary>
    public RestoreMode Restore { get; init; } = RestoreMode.IfNeeded;

    /// <summary>Follow changes to .cs and .radform.json files on disk (files open in an editor are left alone).</summary>
    public bool WatchFiles { get; init; } = true;

    /// <summary>Extra analysers to run for diagnostics, besides the compiler and RADSharp's generators.</summary>
    public ImmutableArray<DiagnosticAnalyzer> Analyzers { get; init; } = [];
}

/// <summary>What changed in a <see cref="ProjectWorkspace"/>.</summary>
public enum WorkspaceChangeKind { DocumentChanged, DocumentAdded, DocumentRemoved, FormChanged, FormAdded, FormRemoved }

/// <summary>A change to the workspace, raised by <see cref="ProjectWorkspace.Changed"/>.</summary>
public sealed record WorkspaceChange(long Version, WorkspaceChangeKind Kind, string Path);

/// <summary>An immutable view of the workspace at one <see cref="Version"/>; safe to read from any thread.</summary>
public sealed class WorkspaceSnapshot
{
    internal WorkspaceSnapshot(long version, Solution solution, ProjectId projectId, long formsVersion)
    {
        Version = version;
        FormsVersion = formsVersion;
        Solution = solution;
        Project = solution.GetProject(projectId)!;
    }

    public long Version { get; }
    public Solution Solution { get; }
    public Project Project { get; }

    /// <summary>The <see cref="Version"/> of the last form change in this snapshot.</summary>
    internal long FormsVersion { get; }
}

/// <summary>
/// A Roslyn workspace over a <see cref="RadProject"/>: its source files, its form files, the code RADSharp's
/// generators produce from them (fields, <c>InitializeComponent</c>, control metadata) and its references.
/// </summary>
/// <remarks>
/// <para>
/// The project is compiled the way <see cref="LiveSession.Compile(RadProject, IReadOnlyDictionary{string, string}?, bool)"/> compiles it (same parse options, implicit
/// usings, host references and generators; see <c>CompilationSetup</c>), except that the entry point file is
/// included and the output kind follows the .csproj, so <c>Program.cs</c> can be edited too.
/// </para>
/// <para>
/// The form generator runs inside the workspace, incrementally, on every snapshot that is asked for its
/// compilation; <see cref="UpdateFormText"/> feeds it the designer's unsaved form, so a control dropped on the
/// surface is a field in completion before anything is saved. Open editor buffers come in through
/// <see cref="UpdateDocumentText"/>; files changed on disk are picked up by a watcher.
/// </para>
/// <para>
/// <b>Threading.</b> All members are thread-safe. Updates are cheap (they swap an immutable snapshot) and can
/// be made from the UI thread; queries go through <see cref="LanguageService"/>, which works on a snapshot
/// on the thread pool. Every update increments <see cref="Version"/>, and every result is tagged with the
/// version it was computed from, so a caller drops a result whose version is no longer current. The
/// <see cref="Changed"/> event is raised on the thread that made the change (a thread-pool thread for file
/// watcher changes); a UI that reacts to it marshals with <c>Application.Post</c>.
/// </para>
/// </remarks>
public sealed class ProjectWorkspace : IDisposable
{
    static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// One composition of Roslyn's MEF parts (workspaces and features) for the process; composing it is the
    /// expensive part of creating a workspace.
    /// </summary>
    static readonly Lazy<MefHostServices> Host = new(() => MefHostServices.Create(MefHostServices.DefaultAssemblies));

    readonly object _gate = new();
    readonly AdhocWorkspace _workspace;
    readonly ProjectId _projectId;
    readonly Dictionary<string, DocumentId> _documents = new(PathComparer);
    readonly Dictionary<string, DocumentId> _forms = new(PathComparer);
    readonly HashSet<string> _open = new(PathComparer);
    readonly Dictionary<string, long> _documentVersions = new(PathComparer);
    readonly ProjectWorkspaceOptions _options;
    readonly ImmutableArray<DiagnosticAnalyzer> _analyzers;
    FileWatcher? _watcher;
    WorkspaceSnapshot _snapshot;
    long _version = 1;
    long _formsVersion;
    long _generatedFormsVersion;

    ProjectWorkspace(RadProject project, ProjectWorkspaceOptions options, ProjectReferences references,
        IReadOnlyList<(string Path, string Text)> sources, IReadOnlyList<(string Path, string Text)> forms)
    {
        Project = project;
        References = references;
        _options = options;
        _analyzers = options.Analyzers;
        _workspace = new AdhocWorkspace(Host.Value, "RadSharp.Ide");
        _projectId = ProjectId.CreateNewId(project.Name);
        RootNamespace = project.RootNamespace;

        var obj = Path.Combine(project.Directory, "obj");
        var documents = new List<DocumentInfo>
        {
            DocumentInfo.Create(DocumentId.CreateNewId(_projectId), CompilationSetup.GlobalUsingsFileName,
                loader: Loader(CompilationSetup.GlobalUsingsText), filePath: Path.Combine(obj, CompilationSetup.GlobalUsingsFileName), isGenerated: true),
        };
        foreach (var (path, text) in sources)
        {
            var id = DocumentId.CreateNewId(_projectId, path);
            _documents[path] = id;
            documents.Add(DocumentInfo.Create(id, Path.GetFileName(path), loader: Loader(text), filePath: path));
        }
        var additional = new List<DocumentInfo>();
        foreach (var (path, text) in forms)
        {
            var id = DocumentId.CreateNewId(_projectId, path);
            _forms[path] = id;
            additional.Add(DocumentInfo.Create(id, Path.GetFileName(path), loader: Loader(text), filePath: path));
        }
        // The generators read the root namespace as an MSBuild property (RadSharp.Core.targets makes it visible).
        // Formatting, code fixes and snippets write new lines the way the project's files already end them.
        var endOfLine = sources.Count(s => s.Text.Contains("\r\n")) * 2 > sources.Count ? "crlf" : "lf";
        var globalConfig = DocumentInfo.Create(DocumentId.CreateNewId(_projectId), ".radsharp.globalconfig",
            loader: Loader($"is_global = true\nbuild_property.RootNamespace = {RootNamespace}\nend_of_line = {endOfLine}\n"),
            filePath: Path.Combine(obj, ".radsharp.globalconfig"));

        var info = ProjectInfo.Create(_projectId, VersionStamp.Create(), project.Name, project.Name, LanguageNames.CSharp,
                filePath: project.ProjectFile,
                compilationOptions: CompilationSetup.LibraryOptions.WithOutputKind(OutputKindOf(project.ProjectFile)),
                parseOptions: CompilationSetup.ParseOptions,
                documents: documents,
                metadataReferences: MetadataReferences(references.Paths),
                analyzerReferences: [Generators.Value],
                additionalDocuments: additional)
            .WithAnalyzerConfigDocuments([globalConfig])
            .WithCompilationOutputInfo(default(CompilationOutputInfo).WithAssemblyPath(Path.Combine(obj, project.Name + ".dll")));
        _workspace.AddProject(info);
        _snapshot = new WorkspaceSnapshot(_version, _workspace.CurrentSolution, _projectId, 0);
    }

    /// <summary>
    /// Opens a workspace on <paramref name="project"/>: resolves its references (restoring packages if
    /// needed), reads its sources and forms, and starts the file watcher.
    /// </summary>
    public static async Task<ProjectWorkspace> OpenAsync(RadProject project, ProjectWorkspaceOptions? options = null, CancellationToken cancel = default)
    {
        options ??= new ProjectWorkspaceOptions();
        var references = await ProjectReferences.ResolveAsync(project, options.Restore, cancel).ConfigureAwait(false);
        var sources = new List<(string, string)>();
        foreach (var file in project.SourceFiles) sources.Add((Path.GetFullPath(file), await File.ReadAllTextAsync(file, cancel).ConfigureAwait(false)));
        var forms = new List<(string, string)>();
        foreach (var file in project.FormFiles) forms.Add((Path.GetFullPath(file), await File.ReadAllTextAsync(file, cancel).ConfigureAwait(false)));
        cancel.ThrowIfCancellationRequested();
        var workspace = new ProjectWorkspace(project, options, references, sources, forms);
        if (options.WatchFiles) workspace._watcher = new FileWatcher(workspace);
        return workspace;
    }

    public RadProject Project { get; }

    /// <summary>The project's root namespace (what generated forms default to).</summary>
    public string RootNamespace { get; }

    /// <summary>The resolved references and any problems resolving them (a failed restore, a missing build output).</summary>
    public ProjectReferences References { get; }

    /// <summary>The underlying Roslyn workspace, for callers that need more than <see cref="LanguageService"/> offers. Treat it as read-only.</summary>
    public Workspace Workspace => _workspace;

    /// <summary>The current version; incremented by every change.</summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>True if nothing has changed since <paramref name="version"/>.</summary>
    public bool IsCurrent(long version) => version == Version;

    /// <summary>The current immutable snapshot.</summary>
    public WorkspaceSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    /// <summary>The analysers <see cref="LanguageService"/> runs besides the compiler and generators.</summary>
    internal ImmutableArray<DiagnosticAnalyzer> Analyzers => _analyzers;

    /// <summary>
    /// Raised after every change, on the thread that made it. Handlers must be quick; a UI marshals with
    /// <c>Application.Post</c>.
    /// </summary>
    public event Action<WorkspaceChange>? Changed;

    /// <summary>The project's .cs files (full paths).</summary>
    public IReadOnlyList<string> SourceFiles { get { lock (_gate) return [.. _documents.Keys.Order(PathComparer)]; } }

    /// <summary>The project's .radform.json files (full paths).</summary>
    public IReadOnlyList<string> FormFiles { get { lock (_gate) return [.. _forms.Keys.Order(PathComparer)]; } }

    /// <summary>A per-file counter, incremented whenever that file's text changes in the workspace (0 if unknown).</summary>
    public long GetDocumentVersion(string path)
    {
        lock (_gate) return _documentVersions.TryGetValue(Path.GetFullPath(path), out var v) ? v : 0;
    }

    /// <summary>The workspace's current text of a source or form file, or null if it isn't in the project.</summary>
    public string? GetText(string path)
    {
        var snapshot = Snapshot;
        var id = FindId(path, out _);
        var doc = id == null ? null : TextDocument(snapshot.Solution, id);
        return doc != null && doc.TryGetText(out var text) ? text.ToString() : doc?.GetTextAsync().GetAwaiter().GetResult().ToString();
    }

    /// <summary>
    /// Replaces a source file's text with an editor buffer's (unsaved) text. The file stays "open": changes to
    /// it on disk are ignored until <see cref="CloseDocument"/>. A path not yet in the project is added.
    /// Returns the new <see cref="Version"/>.
    /// </summary>
    public long UpdateDocumentText(string path, string text) => SetText(Path.GetFullPath(path), text, open: true, isForm: false);

    /// <summary>
    /// Replaces a form file's text with the designer's unsaved JSON (<c>DesignDocument.Save()</c>). The
    /// generated code follows on the next query. Returns the new <see cref="Version"/>.
    /// </summary>
    public long UpdateFormText(string formPath, string json) => SetText(Path.GetFullPath(formPath), json, open: true, isForm: true);

    /// <summary>Stops overriding a source file with a buffer: it reverts to the text on disk (or leaves the project if deleted).</summary>
    public long CloseDocument(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_gate) _open.Remove(full);
        return ReloadFromDisk(full);
    }

    /// <summary>Stops overriding a form file with the designer's text: it reverts to the file on disk.</summary>
    public long CloseForm(string formPath) => CloseDocument(formPath);

    /// <summary>
    /// Re-reads a .cs or .radform.json file from disk (adding or removing it as needed), unless it is open
    /// through <see cref="UpdateDocumentText"/> or <see cref="UpdateFormText"/>. The file watcher calls this.
    /// </summary>
    public long ReloadFromDisk(string path)
    {
        var full = Path.GetFullPath(path);
        bool isForm = full.EndsWith(".radform.json", StringComparison.OrdinalIgnoreCase);
        if (!isForm && !full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return Version;
        lock (_gate) if (_open.Contains(full)) return _version;
        // An IOException (the file is still being written) propagates; the watcher retries.
        return File.Exists(full) ? SetText(full, File.ReadAllText(full), open: false, isForm) : Remove(full);
    }

    /// <summary>
    /// Applies edits from a rename or code fix to the workspace (as buffer text, like
    /// <see cref="UpdateDocumentText"/>), and writes them to disk when <paramref name="save"/> is true.
    /// </summary>
    public long ApplyEdits(IEnumerable<DocumentEdits> edits, bool save = false)
    {
        long version = Version;
        foreach (var e in edits)
        {
            bool isForm = e.File.EndsWith(".radform.json", StringComparison.OrdinalIgnoreCase);
            if (save)
            {
                File.WriteAllText(e.File, e.NewText);
                version = SetText(Path.GetFullPath(e.File), e.NewText, open: false, isForm);
            }
            else version = SetText(Path.GetFullPath(e.File), e.NewText, open: true, isForm);
        }
        return version;
    }

    /// <summary>The generated sources of the current snapshot (form partial classes, control metadata).</summary>
    public async Task<IReadOnlyList<GeneratedFile>> GetGeneratedFilesAsync(CancellationToken cancel = default)
    {
        var docs = await Snapshot.Project.GetSourceGeneratedDocumentsAsync(cancel).ConfigureAwait(false);
        var result = new List<GeneratedFile>();
        foreach (var d in docs)
            result.Add(new GeneratedFile(d.HintName, d.FilePath ?? d.HintName, (await d.GetTextAsync(cancel).ConfigureAwait(false)).ToString()));
        return result;
    }

    internal DocumentId? FindId(string path, out bool isForm)
    {
        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_documents.TryGetValue(full, out var id)) { isForm = false; return id; }
            isForm = _forms.TryGetValue(full, out id);
            return id;
        }
    }

    /// <summary>A source, form or config document by id.</summary>
    internal static TextDocument? TextDocument(Solution solution, DocumentId id) =>
        solution.GetDocument(id) ?? solution.GetAdditionalDocument(id) ?? (TextDocument?)solution.GetAnalyzerConfigDocument(id);

    internal static bool IsGeneratedForm(string path) => path.EndsWith(".radform.json", StringComparison.OrdinalIgnoreCase);

    long SetText(string path, string text, bool open, bool isForm)
    {
        WorkspaceChange change;
        lock (_gate)
        {
            if (open) _open.Add(path);
            var map = isForm ? _forms : _documents;
            var solution = _workspace.CurrentSolution;
            var source = SourceText.From(text, System.Text.Encoding.UTF8);
            WorkspaceChangeKind kind;
            if (map.TryGetValue(path, out var id))
            {
                var current = isForm ? solution.GetAdditionalDocument(id) : solution.GetDocument(id);
                if (current != null && current.TryGetText(out var old) && old.ContentEquals(source)) return _version;
                solution = isForm ? solution.WithAdditionalDocumentText(id, source) : solution.WithDocumentText(id, source);
                kind = isForm ? WorkspaceChangeKind.FormChanged : WorkspaceChangeKind.DocumentChanged;
            }
            else
            {
                id = DocumentId.CreateNewId(_projectId, path);
                map[path] = id;
                solution = isForm
                    ? solution.AddAdditionalDocument(id, Path.GetFileName(path), source, filePath: path)
                    : solution.AddDocument(DocumentInfo.Create(id, Path.GetFileName(path), loader: Loader(text), filePath: path));
                kind = isForm ? WorkspaceChangeKind.FormAdded : WorkspaceChangeKind.DocumentAdded;
            }
            change = Commit(solution, kind, path);
        }
        Changed?.Invoke(change);
        return change.Version;
    }

    long Remove(string path)
    {
        WorkspaceChange change;
        lock (_gate)
        {
            var solution = _workspace.CurrentSolution;
            if (_documents.Remove(path, out var id)) { solution = solution.RemoveDocument(id); change = Commit(solution, WorkspaceChangeKind.DocumentRemoved, path); }
            else if (_forms.Remove(path, out id)) { solution = solution.RemoveAdditionalDocument(id); change = Commit(solution, WorkspaceChangeKind.FormRemoved, path); }
            else return _version;
        }
        Changed?.Invoke(change);
        return change.Version;
    }

    WorkspaceChange Commit(Solution solution, WorkspaceChangeKind kind, string path)
    {
        bool form = kind is WorkspaceChangeKind.FormChanged or WorkspaceChangeKind.FormAdded or WorkspaceChangeKind.FormRemoved;
        if (!_workspace.TryApplyChanges(solution)) throw new InvalidOperationException("The workspace rejected the change.");
        _version++;
        if (form) _formsVersion = _version;
        _documentVersions[path] = _documentVersions.TryGetValue(path, out var v) ? v + 1 : 1;
        Volatile.Write(ref _snapshot, new WorkspaceSnapshot(_version, _workspace.CurrentSolution, _projectId, _formsVersion));
        return new WorkspaceChange(_version, kind, path);
    }

    static TextLoader Loader(string text) =>
        TextLoader.From(TextAndVersion.Create(SourceText.From(text, System.Text.Encoding.UTF8), VersionStamp.Create()));

    static IEnumerable<MetadataReference> MetadataReferences(IReadOnlyList<string> paths)
    {
        // The host's references are shared by every workspace and live session (they're the same files).
        var host = CompilationSetup.HostReferences;
        var hostPaths = CompilationSetup.HostReferencePaths;
        for (int i = 0; i < paths.Count; i++)
            yield return i < hostPaths.Length && paths[i] == hostPaths[i] ? host[i] : MetadataReference.CreateFromFile(paths[i]);
    }

    static OutputKind OutputKindOf(string projectFile)
    {
        var text = File.ReadAllText(projectFile);
        int i = text.IndexOf("<OutputType>", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return OutputKind.DynamicallyLinkedLibrary;
        int j = text.IndexOf("</OutputType>", i, StringComparison.OrdinalIgnoreCase);
        return text[(i + "<OutputType>".Length)..j].Trim().ToLowerInvariant() switch
        {
            "exe" => OutputKind.ConsoleApplication,
            "winexe" => OutputKind.WindowsApplication,
            _ => OutputKind.DynamicallyLinkedLibrary,
        };
    }

    /// <summary>
    /// Makes sure the generated code reflects the snapshot's forms. Completion and other fast features use
    /// Roslyn's "frozen partial" semantics, which reuse the last generated documents instead of running the
    /// generators; after a form edit that would hide a newly dropped control, so the generators run once first.
    /// </summary>
    internal async Task EnsureGeneratedAsync(WorkspaceSnapshot snapshot, CancellationToken cancel)
    {
        if (snapshot.FormsVersion <= Interlocked.Read(ref _generatedFormsVersion)) return;
        await snapshot.Project.GetSourceGeneratedDocumentsAsync(cancel).ConfigureAwait(false);
        ForgetReusedSemanticModels();
        long seen;
        while ((seen = Interlocked.Read(ref _generatedFormsVersion)) < snapshot.FormsVersion
            && Interlocked.CompareExchange(ref _generatedFormsVersion, snapshot.FormsVersion, seen) != seen) { }
    }

    /// <summary>
    /// Roslyn keeps, per document, the semantic model of the method body being edited and reuses it (by
    /// speculative binding) while the document's declarations don't change. It doesn't count generated code,
    /// so after a form edit that adds a control, completion inside a method would keep binding against the old
    /// generated class and miss the new field. Its cache is internal; clear it (by reflection, pinned to the
    /// Roslyn version this project references) when the forms change. If the field isn't found, this does
    /// nothing, and <c>Dropped_controls_field_completes_before_saving</c> fails, so a Roslyn upgrade can't
    /// silently break it.
    /// </summary>
    void ForgetReusedSemanticModels()
    {
        try
        {
            var service = ReuseService.Value is { } getService ? getService.Invoke(_workspace.Services, null) : null;
            var field = service?.GetType().GetField("_semanticModelMap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null || field.GetValue(service) is not { } map) return;
            var clear = map.GetType().GetMethod("Clear", Type.EmptyTypes);
            if (clear?.Invoke(map, null) is { } empty) field.SetValue(service, empty);
        }
        catch (Exception)   // any reflection failure (including a faulted ReuseService Lazy) means "nothing cleared"
        {
            // Roslyn changed under us: nothing is cleared (a stale model may survive one form edit), and the caller goes on.
        }
    }

    static readonly Lazy<System.Reflection.MethodInfo?> ReuseService = new(() =>
    {
        var type = typeof(Workspace).Assembly.GetType("Microsoft.CodeAnalysis.SemanticModelReuse.ISemanticModelReuseWorkspaceService");
        var get = typeof(Microsoft.CodeAnalysis.Host.HostWorkspaceServices).GetMethod(nameof(Microsoft.CodeAnalysis.Host.HostWorkspaceServices.GetService), Type.EmptyTypes);
        return type == null || get == null ? null : get.MakeGenericMethod(type);
    });

    // ---- Generator diagnostics ----

    // The workspace runs the generators for their output, but Roslyn doesn't expose their diagnostics (RAD1001
    // missing handler and friends), so a second GeneratorDriver runs them for diagnostics. It's kept across
    // snapshots and updated incrementally: only changed form texts are replaced.
    readonly SemaphoreSlim _driverGate = new(1, 1);
    GeneratorDriver? _driver;
    readonly Dictionary<string, (SourceText Text, AdditionalText Additional)> _driverTexts = new(PathComparer);
    (long Version, ImmutableArray<Diagnostic> Diagnostics)? _generatorDiagnostics;

    /// <summary>The generators' diagnostics for a snapshot (computed once per version).</summary>
    internal async Task<ImmutableArray<Diagnostic>> GetGeneratorDiagnosticsAsync(WorkspaceSnapshot snapshot, CancellationToken cancel)
    {
        if (_generatorDiagnostics is { } cached && cached.Version == snapshot.Version) return cached.Diagnostics;
        var compilation = await snapshot.Project.GetCompilationAsync(cancel).ConfigureAwait(false);
        if (compilation == null) return [];

        // The compilation without the generated trees: what the compiler hands the generators in a build.
        var own = new HashSet<SyntaxTree>();
        foreach (var doc in snapshot.Project.Documents)
            if (await doc.GetSyntaxTreeAsync(cancel).ConfigureAwait(false) is { } tree) own.Add(tree);
        var input = compilation.RemoveSyntaxTrees(compilation.SyntaxTrees.Where(t => !own.Contains(t)));

        var forms = new List<(string Path, SourceText Text)>();
        foreach (var doc in snapshot.Project.AdditionalDocuments)
            if (doc.FilePath != null && IsGeneratedForm(doc.FilePath)) forms.Add((doc.FilePath, await doc.GetTextAsync(cancel).ConfigureAwait(false)));

        await _driverGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (_generatorDiagnostics is { } again && again.Version == snapshot.Version) return again.Diagnostics;
            var driver = _driver ?? CSharpGeneratorDriver.Create(CompilationSetup.CreateGenerators(), parseOptions: CompilationSetup.ParseOptions,
                optionsProvider: new CompilationSetup.Options(RootNamespace));
            var current = forms.Select(f => f.Path).ToHashSet(PathComparer);
            foreach (var stale in _driverTexts.Keys.Where(p => !current.Contains(p)).ToList())
            {
                driver = driver.RemoveAdditionalTexts([_driverTexts[stale].Additional]);
                _driverTexts.Remove(stale);
            }
            foreach (var (path, text) in forms)
            {
                if (_driverTexts.TryGetValue(path, out var known) && known.Text == text) continue;
                var additional = new SourceAdditionalText(path, text);
                driver = known.Additional != null ? driver.ReplaceAdditionalText(known.Additional, additional) : driver.AddAdditionalTexts([additional]);
                _driverTexts[path] = (text, additional);
            }
            driver = driver.RunGenerators(input, cancel);
            _driver = driver;
            var diagnostics = driver.GetRunResult().Diagnostics;
            if (_generatorDiagnostics is not { } prior || prior.Version < snapshot.Version) _generatorDiagnostics = (snapshot.Version, diagnostics);
            return diagnostics;
        }
        finally { _driverGate.Release(); }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _workspace.Dispose();
    }

    sealed class SourceAdditionalText(string path, SourceText text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => text;
    }

    /// <summary>
    /// RADSharp's generators as an analyser reference, which the workspace runs as part of every compilation.
    /// It's a file reference to the generators' assembly (Roslyn's remote/checksum layer only understands file
    /// references), loaded through a loader that hands back the copy already in this process. One instance is
    /// shared by every workspace.
    /// </summary>
    static readonly Lazy<AnalyzerReference> Generators = new(() =>
    {
        var assembly = typeof(RadSharp.Generators.FormGenerator).Assembly;
        return new AnalyzerFileReference(assembly.Location, new HostAssemblyLoader(assembly));
    });

    sealed class HostAssemblyLoader(System.Reflection.Assembly generators) : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }

        public System.Reflection.Assembly LoadFromPath(string fullPath) =>
            PathComparer.Equals(Path.GetFullPath(fullPath), Path.GetFullPath(generators.Location))
                ? generators
                : System.Reflection.Assembly.LoadFrom(fullPath);
    }

    /// <summary>Follows .cs and .radform.json files on disk, batching bursts of events.</summary>
    sealed class FileWatcher : IDisposable
    {
        readonly ProjectWorkspace _owner;
        readonly FileSystemWatcher _watcher;
        readonly HashSet<string> _pending = new(PathComparer);
        readonly System.Threading.Timer _timer;

        public FileWatcher(ProjectWorkspace owner)
        {
            _owner = owner;
            _timer = new System.Threading.Timer(_ => Flush());
            _watcher = new FileSystemWatcher(owner.Project.Directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += (_, e) => Queue(e.FullPath);
            _watcher.Created += (_, e) => Queue(e.FullPath);
            _watcher.Deleted += (_, e) => Queue(e.FullPath);
            _watcher.Renamed += (_, e) => { Queue(e.OldFullPath); Queue(e.FullPath); };
            _watcher.EnableRaisingEvents = true;
        }

        void Queue(string path)
        {
            if (!(path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || IsGeneratedForm(path))) return;
            var relative = Path.GetRelativePath(_owner.Project.Directory, path);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or ".vs" or ".vscode" or "packages" or ".git")) return;
            lock (_pending) _pending.Add(path);
            _timer.Change(100, Timeout.Infinite);
        }

        void Flush()
        {
            List<string> paths;
            lock (_pending) { paths = [.. _pending]; _pending.Clear(); }
            foreach (var path in paths)
            {
                try { _owner.ReloadFromDisk(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { lock (_pending) _pending.Add(path); _timer.Change(250, Timeout.Infinite); }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }   // the workspace refused the change: it is being disposed
            }
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _timer.Dispose();
        }
    }
}
