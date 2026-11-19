using RadSharp.Design;
using RadSharp.Design.Code;

namespace RadSharp.Designer;

/// <summary>How a <see cref="DesignerForm"/> compiles the open project, for F9 and for its palette.</summary>
public sealed record DesignerOptions
{
    /// <summary>
    /// Load the open project's own controls, and its libraries', onto the palette and the design surface: a design
    /// session (<see cref="DesignSession"/>) built when the project opens, when its sources change and on
    /// Project › Refresh Project Controls. Off: they stay placeholders.
    /// </summary>
    public bool ProjectControls { get; init; } = true;

    /// <summary>Rebuild the design session when a source or project file of the project (or a project reference) changes.</summary>
    public bool WatchProject { get; init; } = true;

    /// <summary>How long a burst of file changes must settle before the design session is rebuilt.</summary>
    public TimeSpan RefreshDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How often a rebuild is tried again when it fails with an exception (not compile errors: those are the project's
    /// state, shown as placeholders): a source file the editor still holds, say, right after the watcher saw it change.
    /// The last good session stays installed meanwhile, and after the last try.
    /// </summary>
    public int TransientRetries { get; init; } = 3;

    /// <summary>How long to wait before each of those tries.</summary>
    public TimeSpan TransientRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How F9 and the design session compile (the dependency resolver, its timeout).</summary>
    public LiveRunOptions LiveRun { get; init; } = new();
}

/// <summary>
/// The project's controls on the palette: a <see cref="DesignSession"/> (the project compiled into a load context of
/// its own, never the F9 one) whose classes the open documents may create. Each rebuild happens off the UI thread; on
/// the UI thread the documents' text is saved while the old classes are still registered, the old session is
/// disposed, and the documents are reloaded with the new classes, so instances of classes that went away become
/// placeholders (that keep their text) and placeholders of classes that came become real controls.
/// </summary>
public partial class DesignerForm
{
    DesignSession? _design;
    int _designGeneration;
    readonly List<FileSystemWatcher> _watchers = [];
    int _watchGeneration;
    IReadOnlyList<string> _lastProjectControls = [];

    /// <summary>The design session whose classes are on the palette (null: none, the project doesn't compile, or it is off).</summary>
    public DesignSession? DesignSession => _design;

    /// <summary>The last design-session rebuild: completes on the UI thread once it is installed, or superseded.</summary>
    public Task ProjectControlsTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Queues work for the UI thread through the host the designer was created on. With no host (a designer built
    /// before one exists, headless) there is no queue: the work runs where it is, rather than being dropped (a dropped
    /// rebuild would never complete <see cref="ProjectControlsTask"/> and would leak its session).
    /// </summary>
    void Post(Action action)
    {
        if ((_host ?? PlatformHost.Current) is { } host) host.PostToUiThread(action);
        else action();
    }

    /// <summary>
    /// Rebuilds the design session (in the background) and installs it: the palette, and every open document. With
    /// <paramref name="refresh"/> (Project › Refresh Project Controls) the project's dependencies are resolved again,
    /// even if they, or a failure to resolve them, are cached.
    /// </summary>
    public Task RefreshProjectControls(bool refresh = false)
    {
        if (_project is not { } project || !_options.ProjectControls) return Task.CompletedTask;
        int generation = ++_designGeneration;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProjectControlsTask = done.Task;
        sbMain.SimpleText = $"Loading {project.Name}'s controls…";
        var options = _options.LiveRun with
        {
            Progress = line => Post(() =>
            {
                if (generation != _designGeneration) return;
                sbMain.SimpleText = line;
                Output(line);
            }),
        };
        bool told = false;
        int faults = 0;
        void Finish(Task<DesignSession> t)
        {
            bool waiting = false;
            try
            {
                if (generation != _designGeneration || _project != project)
                {
                    if (t.IsCompletedSuccessfully) t.Result.Dispose();
                    return;
                }
                if (BusyWithDocuments() is { } busy)
                {
                    // Reloading now would pull the form from under what is editing it: wait until it is done.
                    waiting = true;
                    if (!told) Output($"{project.Name}'s controls load once {busy} is done.");
                    told = true;
                    Task.Delay(SwapRetryDelay).ContinueWith(_ => Post(() => Finish(t)), TaskScheduler.Default);
                    return;
                }
                if (t.IsCompletedSuccessfully) Install(t.Result);
                else
                {
                    // An exception, not compile errors (those come back as a session that didn't succeed): most likely
                    // transient (a file still being written). Dropping the session would turn every project control
                    // into a placeholder, so keep the last good one, and try again a few times.
                    var why = (t.Exception?.InnerException ?? t.Exception)?.Message;
                    if (++faults <= _options.TransientRetries)
                    {
                        waiting = true;
                        Output($"{project.Name}'s controls couldn't be loaded ({why}); trying again ({faults}/{_options.TransientRetries}).");
                        Task.Delay(_options.TransientRetryDelay).ContinueWith(_ => Post(() =>
                        {
                            if (generation == _designGeneration && _project == project) Start();
                            else done.TrySetResult();
                        }), TaskScheduler.Default);
                        return;
                    }
                    Output($"{project.Name}'s controls couldn't be loaded: {why}");
                    sbMain.SimpleText = _design == null
                        ? $"{project.Name}'s controls aren't loaded"
                        : $"{project.Name}'s controls couldn't be refreshed; showing the last loaded ones";
                }
            }
            finally { if (!waiting) done.TrySetResult(); }
        }
        void Start() => Task.Run(() =>
        {
            if (refresh && faults == 0) ProjectDependencies.Invalidate(project);
            return DesignSession.Compile(project, options);
        }).ContinueWith(t => Post(() => Finish(t)), TaskScheduler.Default);
        Start();
        return done.Task;
    }

    static readonly TimeSpan SwapRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>The editors that open while it is running (they hold controls of the document, not the document).</summary>
    int _modalEditors;

    /// <summary>Runs a modal editor of the document's controls: no design session is installed until it closes.</summary>
    void EditModally(Action edit)
    {
        _modalEditors++;
        try { edit(); }
        finally { _modalEditors--; }
    }

    /// <summary>
    /// What holds controls of an open document, rather than the document (null: nothing): a reload replaces every
    /// control, so an edit they make afterwards would go to a control no longer on the form, and be lost.
    /// </summary>
    string? BusyWithDocuments()
    {
        if (_modalEditors > 0) return "the editor";
        if (gridProps.IsEditing || gridEvents.IsEditing) return "the Object Inspector's edit";
        if (_forms.Any(f => f.Surface.IsTracking)) return "the drag on the form";
        if (edtName.Focused && Document is { } doc && edtName.Text.Trim() != doc.Primary.Name) return "the rename";
        return null;
    }

    void Install(DesignSession? next)
    {
        var project = _project!;
        IReadOnlyList<string> problems = [];
        if (next is { Success: true }) problems = next.Activate();
        else if (next != null)
        {
            var errors = next.Messages.Where(m => m.IsError).ToList();
            Output($"{project.Name}'s controls show as placeholders until it compiles ({errors.Count} error(s)):");
            foreach (var m in errors.Take(10)) Output("  " + m, m);
            next.Dispose();
            next = null;
        }
        foreach (var p in problems) Output(p);

        var old = _design;
        if (old != null || next != null)
        {
            // While the old classes are still registered: their instances save as themselves.
            var texts = _forms.Select(f => (Form: f, Text: f.Document.Save())).ToList();
            try
            {
                _design = next;
                foreach (var (f, text) in texts) ReloadWith(f, text, next);
            }
            finally
            {
                // Whatever happened above: no stale palette, and the old session never leaks.
                RebuildPalette();
                old?.Dispose();
            }
        }

        var names = next?.Classes.Select(c => $"{c.Name} ({next.CategoryOf(c)})").ToList() ?? [];
        if (next != null && !names.SequenceEqual(_lastProjectControls))
            Output(names.Count == 0 ? $"{project.Name} defines no controls." : "Project controls: " + string.Join(", ", names));
        _lastProjectControls = names;
        sbMain.SimpleText = next == null ? $"{project.Name}'s controls aren't loaded" : $"{project.Name}'s controls are up to date";
        if (next?.Dependencies is { } deps) WatchProject(project, deps.ProjectReferences);
    }

    /// <summary>
    /// Reloads one document with <paramref name="next"/>'s classes. If that throws (an exception the form reader doesn't
    /// absorb), the document is read again with no project classes, so every one is a placeholder that keeps its text.
    /// </summary>
    void ReloadWith(OpenForm f, string text, DesignSession? next)
    {
        try
        {
            f.Document.ProjectClasses = next == null ? null : next.Find;
            // Not the "unknown class" ones: a broken project says so once, above.
            foreach (var w in f.Document.Reload(text).Where(w => w.Contains(" threw while ", StringComparison.Ordinal)
                         || w.Contains(" has no published property ", StringComparison.Ordinal)))
                Output($"{Path.GetFileName(f.Path)}: {w}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Output($"{Path.GetFileName(f.Path)}: the project's controls couldn't be loaded onto it ({e.GetType().Name}: {e.Message}); they are placeholders on it.");
            try
            {
                f.Document.ProjectClasses = null;
                f.Document.Reload(text);
            }
            catch (Exception e2) when (e2 is not OutOfMemoryException)
            {
                Output($"{Path.GetFileName(f.Path)}: it couldn't be reloaded either ({e2.GetType().Name}: {e2.Message}); it keeps what it had.");
            }
        }
    }

    /// <summary>Drops the design session (closing the project): its documents are gone, so nothing to reload.</summary>
    void DropProjectControls()
    {
        _designGeneration++;
        _watchGeneration++;
        StopWatching();
        var old = _design;
        _design = null;
        _lastProjectControls = [];
        foreach (var f in _forms) f.Document.ProjectClasses = null;
        RebuildPalette();
        old?.Dispose();
    }

    /// <summary>
    /// Project › Refresh Project Controls: forgets the project's resolved dependencies (and a remembered failure to
    /// resolve them), then rebuilds the design session. The next F9 resolves the project again too.
    /// </summary>
    public Task RefreshProject()
    {
        if (_project is not { } project) return Task.CompletedTask;
        if (_options.ProjectControls) return RefreshProjectControls(refresh: true);
        // Off the UI thread: forgetting waits for a resolve of the project that is still running.
        Output($"{project.Name}'s dependencies will be resolved again on the next F9.");
        return Task.Run(() => ProjectDependencies.Invalidate(project));
    }

    void mnuRefreshProject_Click(object sender) => RefreshProject();

    // ---- Watching the project's files ----

    static readonly string[] WatchedExtensions = [".cs", ".csproj", ".props", ".targets"];

    void WatchProject(RadProject project, IEnumerable<string> projectReferences)
    {
        if (!_options.WatchProject) return;
        var dirs = projectReferences.Select(p => Path.GetDirectoryName(p)!).Prepend(project.Directory)
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (dirs.SequenceEqual(_watchers.Select(w => w.Path), StringComparer.OrdinalIgnoreCase)) return;
        StopWatching();
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                FileSystemEventHandler changed = (_, e) => FileChanged(project, e.FullPath);
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += (_, e) => FileChanged(project, e.FullPath);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException) { }
        }
    }

    void StopWatching()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    /// <summary>On a watcher thread: a source of the project changed (not the build's own output).</summary>
    void FileChanged(RadProject project, string path)
    {
        if (!WatchedExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return;
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(p => p is "bin" or "obj" or ".vs" or ".git")) return;
        Post(() =>
        {
            if (_project != project) return;
            int generation = ++_watchGeneration;
            Task.Delay(_options.RefreshDelay).ContinueWith(_ => Post(() =>
            {
                if (generation == _watchGeneration && _project == project) RefreshProjectControls();
            }), TaskScheduler.Default);
        });
    }
}
