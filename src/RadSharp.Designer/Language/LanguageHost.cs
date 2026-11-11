using RadSharp.Design;
using RadSharp.Design.Code;
using RadSharp.Ide.Language;

namespace RadSharp.Designer;

/// <summary>
/// The IDE's language service for one open project: a <see cref="ProjectWorkspace"/>, the <see cref="LanguageService"/>
/// over it and a <see cref="DiagnosticsMonitor"/>, fed with the open editor buffers and the designer's unsaved forms.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> Created when the project opens; the workspace opens on the thread pool (restoring packages if
/// needed, composing Roslyn), so the window never waits. Until it is ready (<see cref="IsReady"/>, <see cref="Ready"/>)
/// editors keep their lexical colours and the designer's textual rename. Disposed when the project closes.
/// </para>
/// <para>
/// <b>Threading.</b> Everything here is called on the UI thread. Requests run on the pool against immutable workspace
/// snapshots; results come back through <c>Application.Post</c> and are dropped when they're stale (each document maps a
/// result's workspace version to the editor snapshot it was computed from, see <see cref="DocumentLanguage"/>).
/// </para>
/// <para>
/// <b>Feeding the workspace.</b> Buffers are pushed (<see cref="ProjectWorkspace.UpdateDocumentText"/>) a moment after typing
/// stops, and always right before a request, so completion sees the text as it is. Forms are pushed
/// (<see cref="ProjectWorkspace.UpdateFormText"/>) after every <see cref="DesignDocument.Changed"/>, debounced the same way.
/// </para>
/// <para>
/// <b>Live diagnostics.</b> <see cref="LiveDiagnosticsChanged"/> (UI thread) carries the project's current diagnostics as
/// <see cref="BuildMessage"/>s (<see cref="DiagnosticInfo.ToBuildMessage"/>), generated code left out; the designer
/// shows them in Problems as the source <c>"live"</c>.
/// </para>
/// </remarks>
public sealed class LanguageHost : IDisposable
{
    /// <summary>Whether projects get a language host at all (the designer's own tests turn it off; see the test assembly).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The options the next hosts open their workspace with (tests: no restore, no file watcher).</summary>
    public static ProjectWorkspaceOptions? DefaultOptions { get; set; }

    /// <summary>How long after the last keystroke or form change the workspace is updated.</summary>
    public static TimeSpan PushDelay { get; set; } = TimeSpan.FromMilliseconds(120);

    /// <summary>How long after the workspace changed the semantic colours are recomputed.</summary>
    public static TimeSpan ClassifyDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long after the workspace stops changing diagnostics are recomputed.</summary>
    public static TimeSpan DiagnosticsDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly List<DocumentLanguage> _documents = [];
    readonly Dictionary<FormDocument, (Func<string> Json, string? PushedPath)> _forms = [];
    readonly HashSet<FormDocument> _pendingForms = [];
    readonly CancellationTokenSource _lifetime = new();
    CancellationTokenSource? _formPush;
    int _formsSuspended;
    volatile bool _disposed;
    readonly object _gate = new();
    ProjectWorkspace? _opening;   // opened in the background, not yet handed to the UI thread
    readonly Dictionary<FormDocument, (Action Changed, Action Replaced)> _formHandlers = [];

    public LanguageHost(RadProject project, ProjectWorkspaceOptions? options = null)
    {
        Project = project;
        var opts = options ?? DefaultOptions ?? new ProjectWorkspaceOptions();
        var token = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var workspace = await ProjectWorkspace.OpenAsync(project, opts, token).ConfigureAwait(false);
                // Hand the workspace over under the lock: either Dispose() sees it (and disposes it) or the UI post takes it.
                // Never rely on the UI post alone: Ui() drops it once the host is disposed, which would leak the workspace.
                lock (_gate)
                {
                    if (!_disposed) _opening = workspace;
                    else workspace = null!;
                }
                if (workspace == null) return;
                Ui(() =>
                {
                    ProjectWorkspace? taken;
                    lock (_gate) { taken = _opening; _opening = null; }
                    if (taken != null) OnOpened(taken);
                });
            }
            catch (Exception e) when (IsExpected(e))
            {
                Ui(() => { Error = e.Message; _ready.TrySetResult(false); StateChanged?.Invoke(); });
            }
        }, token);
    }

    public RadProject Project { get; }
    public ProjectWorkspace? Workspace { get; private set; }
    public LanguageService? Service { get; private set; }
    public DiagnosticsMonitor? Diagnostics { get; private set; }

    /// <summary>The workspace is open and the language features work.</summary>
    public bool IsReady => Service != null && !_disposed;

    /// <summary>Completes (on the pool) once the workspace opened and the open documents were pushed: true, or false if it couldn't open.</summary>
    public Task<bool> Ready => _ready.Task;

    /// <summary>While this returns true, hovering shows no quick info (the debugger shows values instead while paused).</summary>
    public Func<bool>? QuickInfoSuppressed { get; set; }

    /// <summary>Why the workspace couldn't open, if it couldn't.</summary>
    public string? Error { get; private set; }

    /// <summary>Raised on the UI thread when the host became ready or failed.</summary>
    public event Action? StateChanged;

    // ---- Live diagnostics (for the Problems window) ----

    /// <summary>The project's current diagnostics (all files; generated code included, see <see cref="DiagnosticInfo.IsInGeneratedCode"/>).</summary>
    public IReadOnlyList<DiagnosticInfo> LiveDiagnostics { get; private set; } = [];

    /// <summary>The workspace version <see cref="LiveDiagnostics"/> were computed from (-1 before the first update).</summary>
    public long LiveDiagnosticsVersion { get; private set; } = -1;

    /// <summary><see cref="LiveDiagnostics"/> as the Problems window lists them: generated code left out.</summary>
    public IReadOnlyList<BuildMessage> LiveBuildMessages { get; private set; } = [];

    /// <summary>
    /// Raised on the UI thread after <see cref="LiveDiagnostics"/> changed, with <see cref="LiveBuildMessages"/>. Hooking
    /// it to Problems is one line: <c>host.LiveDiagnosticsChanged += m =&gt; Problems.Set("live", m);</c>
    /// </summary>
    public event Action<IReadOnlyList<BuildMessage>>? LiveDiagnosticsChanged;

    /// <summary>Completes with the first diagnostics published for <paramref name="minVersion"/> or later (a versioned wait for tests).</summary>
    public Task<Versioned<IReadOnlyList<DiagnosticInfo>>> WaitForDiagnosticsAsync(long minVersion, CancellationToken cancel = default) =>
        Diagnostics?.WaitForAsync(minVersion, cancel) ?? Task.FromException<Versioned<IReadOnlyList<DiagnosticInfo>>>(new InvalidOperationException("The workspace isn't open."));

    // ---- Opening ----

    void OnOpened(ProjectWorkspace workspace)
    {
        if (_disposed) { workspace.Dispose(); return; }
        Workspace = workspace;
        Service = new LanguageService(workspace);
        foreach (var form in _forms.Keys) _pendingForms.Add(form);
        FlushForms();
        foreach (var doc in _documents.ToList()) doc.OnHostReady();
        Diagnostics = new DiagnosticsMonitor(Service, DiagnosticsDelay);
        Diagnostics.Updated += update => Ui(() => OnDiagnostics(update));
        if (Diagnostics.Latest is { } latest) OnDiagnostics(latest);
        _ready.TrySetResult(true);
        StateChanged?.Invoke();
    }

    void OnDiagnostics(Versioned<IReadOnlyList<DiagnosticInfo>> update)
    {
        if (_disposed || update.Version < LiveDiagnosticsVersion) return;
        LiveDiagnostics = update.Value;
        LiveDiagnosticsVersion = update.Version;
        LiveBuildMessages = [.. update.Value.Where(d => !d.IsInGeneratedCode).Select(d => d.ToBuildMessage())];
        foreach (var doc in _documents) doc.OnDiagnostics(update);
        LiveDiagnosticsChanged?.Invoke(LiveBuildMessages);
    }

    // ---- Documents ----

    public IReadOnlyList<DocumentLanguage> Documents => _documents;

    /// <summary>Gives a C# code document the language features (colours, squiggles, completion, hover, signature help).</summary>
    public DocumentLanguage? Attach(CodeDocument doc)
    {
        if (_disposed || !doc.IsCSharp || doc.Editor is not CodeEditorAdapter adapter) return null;
        if (Find(doc) is { } existing) return existing;
        var language = new DocumentLanguage(this, doc, adapter.Editor);
        _documents.Add(language);
        if (IsReady) language.OnHostReady();
        return language;
    }

    public DocumentLanguage? Find(CodeDocument doc) => _documents.FirstOrDefault(d => d.Document == doc);

    /// <summary>The document's buffer stops overriding the file in the workspace (it was closed).</summary>
    public void Detach(CodeDocument doc)
    {
        if (Find(doc) is not { } language) return;
        _documents.Remove(language);
        language.Dispose();
    }

    /// <summary>Follows a form open in the designer: its unsaved state goes to the workspace after every change.</summary>
    public void AttachForm(FormDocument form)
    {
        if (_disposed || _forms.ContainsKey(form)) return;
        _forms[form] = (() => form.Document.Save(), null);
        Action changed = () => FormChanged(form);
        _formHandlers[form] = (changed, changed);
        form.Document.Changed += changed;
        form.Document.FormReplaced += changed;
        FormChanged(form);
    }

    /// <summary>The form closed: the workspace goes back to the file on disk.</summary>
    public void DetachForm(FormDocument form)
    {
        if (!_forms.Remove(form, out var entry)) return;
        _pendingForms.Remove(form);
        if (_formHandlers.Remove(form, out var handlers))
        {
            form.Document.Changed -= handlers.Changed;
            form.Document.FormReplaced -= handlers.Replaced;
        }
        if (Workspace != null && entry.PushedPath != null) Workspace.CloseForm(entry.PushedPath);
        RequestReclassify();
    }

    void FormChanged(FormDocument form)
    {
        if (_disposed || !_forms.ContainsKey(form)) return;
        _pendingForms.Add(form);
        Debounce(ref _formPush, PushDelay, () => { FlushForms(); RequestReclassify(); });
    }

    /// <summary>Holds form pushes back (a semantic control rename needs the workspace to keep the old name) until the scope ends.</summary>
    public IDisposable SuspendFormUpdates()
    {
        _formsSuspended++;
        return new Scope(() =>
        {
            if (--_formsSuspended == 0 && !_disposed) { FlushForms(); RequestReclassify(); }
        });
    }

    sealed class Scope(Action end) : IDisposable
    {
        bool _done;
        public void Dispose() { if (!_done) { _done = true; end(); } }
    }

    void FlushForms()
    {
        if (Workspace is not { } workspace || _formsSuspended > 0) return;
        foreach (var form in _pendingForms.ToList())
        {
            if (!_forms.TryGetValue(form, out var entry)) continue;
            var path = Path.GetFullPath(form.Path);
            if (entry.PushedPath != null && !SamePath(entry.PushedPath, path)) workspace.CloseForm(entry.PushedPath);   // the form moved
            workspace.UpdateFormText(path, entry.Json());
            _forms[form] = (entry.Json, path);
        }
        _pendingForms.Clear();
    }

    /// <summary>Pushes every pending buffer and form to the workspace; returns its version (-1 when not ready).</summary>
    public long Flush()
    {
        if (Workspace is not { } workspace) return -1;
        FlushForms();
        foreach (var doc in _documents) doc.Flush();
        return workspace.Version;
    }

    /// <summary>Recomputes the semantic colours of every open document (a form change can add a field).</summary>
    internal void RequestReclassify()
    {
        foreach (var doc in _documents) doc.ScheduleClassify();
    }

    // ---- Helpers ----

    /// <summary>Runs <paramref name="action"/> on the UI thread (dropped once the host is disposed).</summary>
    internal void Ui(Action action)
    {
        if (PlatformHost.Current is not { } host) return;
        host.PostToUiThread(() => { if (!_disposed) action(); });
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread after <paramref name="delay"/>, unless called again first.</summary>
    internal void Debounce(ref CancellationTokenSource? cts, TimeSpan delay, Action action)
    {
        cts?.Cancel();
        var current = cts = new CancellationTokenSource();
        var token = current.Token;
        _ = Task.Delay(delay, token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Ui(() => { if (!token.IsCancellationRequested) action(); });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Runs a language request on the pool and hands its result to <paramref name="done"/> on the UI thread. Cancelled,
    /// failed (a file that left the project) and post-dispose requests are dropped. The returned task completes after
    /// <paramref name="done"/> ran (or the request was dropped), for callers and tests that wait.
    /// </summary>
    internal Task Request<T>(Func<LanguageService, CancellationToken, Task<T>> work, Action<T> done, CancellationToken cancel = default)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Service is not { } service) { finished.SetResult(); return finished.Task; }
        var token = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work(service, cancel).ConfigureAwait(false);
                if (cancel.IsCancellationRequested || token.IsCancellationRequested) { finished.TrySetResult(); return; }
                if (PlatformHost.Current is not { } host) { finished.TrySetResult(); return; }
                host.PostToUiThread(() =>
                {
                    try { if (!_disposed && !cancel.IsCancellationRequested) done(result); }
                    finally { finished.TrySetResult(); }
                });
            }
            catch (Exception e) when (IsExpected(e)) { finished.TrySetResult(); }
        }, CancellationToken.None);
        return finished.Task;
    }

    /// <summary>
    /// What a background request may throw and the IDE shrugs off: cancellation, a file that left the project, and
    /// whatever Roslyn throws on half-typed code. A language feature that fails shows nothing; it never takes the IDE down.
    /// </summary>
    internal static bool IsExpected(Exception e) => e is not (OutOfMemoryException or StackOverflowException);

    internal static bool SamePath(string? a, string? b) =>
        a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public void Dispose()
    {
        ProjectWorkspace? opening;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            opening = _opening;
            _opening = null;
        }
        opening?.Dispose();
        _lifetime.Cancel();
        _formPush?.Cancel();
        foreach (var doc in _documents) doc.Dispose();
        _documents.Clear();
        foreach (var (form, h) in _formHandlers)
        {
            form.Document.Changed -= h.Changed;
            form.Document.FormReplaced -= h.Replaced;
        }
        _formHandlers.Clear();
        _forms.Clear();
        Diagnostics?.Dispose();
        Workspace?.Dispose();
        Service = null;
        _ready.TrySetResult(false);
    }
}
