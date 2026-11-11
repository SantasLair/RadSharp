using RadSharp.Design.Code;
using RadSharp.Ide.Debug;
using RadSharp.Ide.Debug.Acquisition;
using RadSharp.Ide.Debug.Protocol;

namespace RadSharp.Designer;

/// <summary>Where the IDE is in a debugging session.</summary>
public enum DebugState
{
    /// <summary>No debuggee: designing and editing.</summary>
    Design,
    /// <summary>F5 was pressed: saving, building, getting the adapter and launching.</summary>
    Starting,
    /// <summary>The debuggee runs under the debugger.</summary>
    Running,
    /// <summary>The debuggee is stopped (breakpoint, step, pause or exception).</summary>
    Paused,
}

/// <summary>What the <see cref="DebugController"/> needs from the IDE. Every member is called on the UI thread.</summary>
public interface IDebugHost
{
    /// <summary>Appends a line to Output.</summary>
    void Output(string line);

    /// <summary>Shows a short state message in the status bar.</summary>
    void Status(string text);

    /// <summary>Saves everything and builds the project in Debug (the Ctrl+Shift+B path). Null if it could not start.</summary>
    Task<BuildResult?> BuildAsync();

    /// <summary>Cancels a build started by <see cref="BuildAsync"/>.</summary>
    void CancelBuild();

    /// <summary>How to start the built <paramref name="outputFile"/>: program, arguments, directory and environment of the run configuration.</summary>
    DebugLaunchOptions LaunchOptionsFor(string outputFile);

    /// <summary>The debugger can't start (<paramref name="reason"/>): explains it and offers Run Without Debugging (Ctrl+F5), running it if accepted.</summary>
    void OfferRunWithoutDebugging(string reason);
}

/// <summary>
/// The IDE's side of a debugging session (milestone I6), on the UI thread: the state machine
/// (<see cref="DebugState.Design"/> → <see cref="DebugState.Starting"/> → <see cref="DebugState.Running"/> ⇄
/// <see cref="DebugState.Paused"/> → <see cref="DebugState.Design"/>), the execution commands, the paused context
/// (call stack, selected frame and its locals, the exception of an exception stop) and live breakpoint and exception
/// filter synchronisation. It owns at most one <see cref="DebugSession"/>, whose background-thread events it marshals
/// with the <c>post</c> delegate (<see cref="Application.Post"/> by default) before touching any state; every
/// public member and event is UI-thread only.
/// </summary>
public sealed class DebugController : IAsyncDisposable
{
    readonly IDebugHost _host;
    readonly Action<Action> _post;
    readonly HashSet<string> _pendingSync = new(StringComparer.OrdinalIgnoreCase);
    DebugSession? _session;
    CancellationTokenSource? _startCts;
    DebugState _state = DebugState.Design;
    int _stopGeneration;
    IReadOnlyList<StackFrame> _frames = [];
    int _frameIndex = -1;
    IReadOnlyList<(Scope Scope, IReadOnlyList<Variable> Variables)> _scopes = [];
    readonly Dictionary<string, string> _partialOutput = new(StringComparer.Ordinal);
    bool _exceptionFiltersPending;
    bool _breakOnAll, _breakOnUnhandled = true;
    Task _ending = Task.CompletedTask;
    int _pendingOps;

    public DebugController(IDebugHost host, BreakpointStore? breakpoints = null, Action<Action>? post = null)
    {
        _host = host;
        _post = post ?? Application.Post;
        Breakpoints = breakpoints ?? new BreakpointStore();
        Breakpoints.Changed += OnBreakpointsChanged;
    }

    // ---- configuration ----

    /// <summary>The breakpoints (the IDE's; they survive sessions).</summary>
    public BreakpointStore Breakpoints { get; }

    /// <summary>Finds or downloads the adapter (netcoredbg); progress is 0..1. Replaceable for tests.</summary>
    public Func<IProgress<double>?, CancellationToken, Task<string>> AdapterResolver { get; set; } =
        (progress, ct) => new NetCoreDbgAcquisition().EnsureAsync(null, progress, ct);

    /// <summary>Starts a session against the adapter executable. Replaceable for tests (an in-memory adapter).</summary>
    public Func<string, BreakpointStore, DebugSession> SessionFactory { get; set; } = DebugSession.StartAdapter;

    /// <summary>Break when any exception is thrown (the adapter's <c>all</c> filter).</summary>
    public bool BreakOnAllExceptions
    {
        get => _breakOnAll;
        set { if (_breakOnAll == value) return; _breakOnAll = value; ApplyExceptionFilters(); }
    }

    /// <summary>Break on exceptions user code doesn't handle (<c>user-unhandled</c>, or <c>unhandled</c>); on by default.</summary>
    public bool BreakOnUnhandledExceptions
    {
        get => _breakOnUnhandled;
        set { if (_breakOnUnhandled == value) return; _breakOnUnhandled = value; ApplyExceptionFilters(); }
    }

    // ---- state ----

    public DebugState State => _state;
    /// <summary>A session is starting, running or paused.</summary>
    public bool IsActive => _state != DebugState.Design;
    public bool IsPaused => _state == DebugState.Paused;
    public DebugSession? Session => _session;

    /// <summary>Raised (UI thread) after <see cref="State"/> changed.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Raised (UI thread) when the paused context changed: a stop's frames and the top frame's locals arrived, another
    /// frame was selected, or the debuggee resumed (then <see cref="CurrentFrame"/> is null).
    /// </summary>
    public event Action? ContextChanged;

    /// <summary>Raised (UI thread) after an exception stop, with the exception's type and message.</summary>
    public event Action<string>? ExceptionStopped;

    /// <summary>The reason of the current stop ("breakpoint", "step", "exception", "pause"…), while paused.</summary>
    public string? StopReason { get; private set; }

    /// <summary>The thread of the current stop.</summary>
    public int? ThreadId { get; private set; }

    /// <summary>The stopped thread's frames, top first (empty while running).</summary>
    public IReadOnlyList<StackFrame> Frames => _frames;
    public int CurrentFrameIndex => _frameIndex;
    public StackFrame? CurrentFrame => _frameIndex >= 0 && _frameIndex < _frames.Count ? _frames[_frameIndex] : null;

    /// <summary>The selected frame's scopes and their variables (the Locals window's roots).</summary>
    public IReadOnlyList<(Scope Scope, IReadOnlyList<Variable> Variables)> Scopes => _scopes;

    /// <summary>The exception of the current stop ("System.InvalidOperationException: kaboom"), if it was one.</summary>
    public string? ExceptionText { get; private set; }

    /// <summary>The exit code of the last debuggee, once it exited.</summary>
    public int? LastExitCode { get; private set; }

    /// <summary>The exception filters the current adapter offers (empty until a session initialized).</summary>
    public IReadOnlyList<ExceptionBreakpointFilter> AvailableExceptionFilters { get; private set; } = [];

    /// <summary>True while a request the controller issued hasn't come back (tests wait for quiet).</summary>
    public bool Busy => _pendingOps > 0;

    /// <summary>Completes when the last session ended and its adapter was disposed.</summary>
    public Task Ending => _ending;

    // ---- commands ----

    public bool CanStart => _state == DebugState.Design;
    public bool CanContinue => _state == DebugState.Paused;
    public bool CanStep => _state == DebugState.Paused && ThreadId != null;
    public bool CanPause => _state == DebugState.Running && _session != null;
    public bool CanStop => _state != DebugState.Design;

    /// <summary>F5: starts debugging in Design, continues when paused.</summary>
    public Task StartOrContinueAsync() => _state switch
    {
        DebugState.Design => StartAsync(),
        DebugState.Paused => ContinueAsync(),
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// Start Debugging: saves and builds (Debug) through the host, acquires the adapter (progress in the status bar and
    /// Output; a failure offers Ctrl+F5 instead) and launches the built app under it. True once the debuggee runs.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (_state != DebugState.Design) return false;
        var cts = _startCts = new CancellationTokenSource();
        SetState(DebugState.Starting);
        Task<BuildResult?> building;
        try { building = _host.BuildAsync(); }
        catch (Exception e) { return Abort(cts, "The build could not run: " + e.Message); }
        BuildResult? build;
        try { build = await building.ConfigureAwait(false); }
        catch (Exception e) { return await OnUi(() => Abort(cts, "The build could not run: " + e.Message)).ConfigureAwait(false); }
        // From here on this may run off the UI thread: state and host calls go through OnUi.
        bool proceed = await OnUi(() =>
        {
            if (cts.IsCancellationRequested || _startCts != cts) return Abort(cts, null);
            if (build is not { Succeeded: true, OutputFile: not null })
                return Abort(cts, build is { Cancelled: true } || build == null ? null : "The debugger was not started: the build failed.");
            _host.Status("Starting the debugger…");
            return true;
        }).ConfigureAwait(false);
        if (!proceed) return false;
        var output = build!.OutputFile!;

        string adapter;
        try
        {
            int lastPercent = -1;
            var progress = new Progress(p => _post(() =>
            {
                int percent = (int)Math.Round(p * 100);
                if (percent == lastPercent || _startCts != cts) return;
                if (lastPercent < 0) _host.Output("Downloading the debugger (netcoredbg)…");
                lastPercent = percent;
                _host.Status($"Downloading the debugger (netcoredbg)… {percent}%");
            }));
            adapter = await AdapterResolver(progress, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return await OnUi(() => Abort(cts, null)).ConfigureAwait(false); }
        catch (Exception e)
        {
            return await OnUi(() =>
            {
                bool current = _startCts == cts;   // a stale start says nothing: Stop already ended it
                Abort(cts, null);
                if (current) _host.OfferRunWithoutDebugging("The debugger (netcoredbg) is not available: " + e.Message);
                return false;
            }).ConfigureAwait(false);
        }
        if (cts.IsCancellationRequested) return await OnUi(() => Abort(cts, null)).ConfigureAwait(false);
        var options = await OnUi(() => _host.LaunchOptionsFor(output)).ConfigureAwait(false);
        return await OnUi(() => LaunchCoreAsync(adapter, options, cts)).Unwrap().ConfigureAwait(false);
    }

    /// <summary>Launches <paramref name="options"/> under the adapter without building (what <see cref="StartAsync"/> ends with).</summary>
    public Task<bool> LaunchAsync(DebugLaunchOptions options, string adapterPath = "netcoredbg")
    {
        if (_state != DebugState.Design) return Task.FromResult(false);
        var cts = _startCts = new CancellationTokenSource();
        SetState(DebugState.Starting);
        return LaunchCoreAsync(adapterPath, options, cts);
    }

    async Task<bool> LaunchCoreAsync(string adapterPath, DebugLaunchOptions options, CancellationTokenSource cts)
    {
        if (cts.IsCancellationRequested || _startCts != cts) return Abort(cts, null);   // stopped (or superseded) while the build or download ran
        DebugSession session;
        try { session = SessionFactory(adapterPath, Breakpoints); }
        catch (Exception e)
        {
            Abort(cts, null);
            _host.OfferRunWithoutDebugging($"The debugger ({Path.GetFileName(adapterPath)}) could not start: {e.Message}");
            return false;
        }
        _session = session;
        _stopGeneration++;
        LastExitCode = null;
        _partialOutput.Clear();
        Subscribe(session);
        _host.Output($"Debugging {Path.GetFileName(options.Args.FirstOrDefault() ?? options.Program)}…");
        _host.Status("Debugging: starting…");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var caps = await session.InitializeAsync(timeout.Token).ConfigureAwait(false);
            await OnUi(() =>
            {
                AvailableExceptionFilters = caps.ExceptionBreakpointFilters ?? [];
                Breakpoints.SetExceptionFilters(ChooseExceptionFilters());
            }).ConfigureAwait(false);
            await session.LaunchAsync(options, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            await OnUi(() =>
            {
                if (_session != session) return;
                _host.Output("The debugger could not launch the app: " + (e is OperationCanceledException ? "it timed out or was cancelled." : e.Message));
            }).ConfigureAwait(false);
            await OnUi(() => EndSessionAsync(session, terminate: true)).Unwrap().ConfigureAwait(false);
            return false;
        }
        return await OnUi(() =>
        {
            if (_session != session) return false;
            if (_startCts == cts) _startCts = null;
            if (_state == DebugState.Starting) SetState(DebugState.Running);
            if (_exceptionFiltersPending) ApplyExceptionFilters();
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the start <paramref name="cts"/> belongs to: back to Design with an optional message. Does nothing when that
    /// start is no longer the current one (Stop, then F5 again, started a newer one: the first start's late completion
    /// must not reset it).
    /// </summary>
    bool Abort(CancellationTokenSource? cts, string? message)
    {
        if (_startCts != cts) return false;
        _startCts = null;
        if (message != null) _host.Output(message);
        if (_state == DebugState.Starting && _session == null) SetState(DebugState.Design);
        return false;
    }

    public Task ContinueAsync() => Resume((s, t) => s.ContinueAsync(t), "Continue");
    public Task StepOverAsync() => Resume((s, t) => s.NextAsync(t), "Step Over");
    public Task StepIntoAsync() => Resume((s, t) => s.StepInAsync(t), "Step Into");
    public Task StepOutAsync() => Resume((s, t) => s.StepOutAsync(t), "Step Out");

    async Task Resume(Func<DebugSession, int, Task> request, string what)
    {
        if (_state != DebugState.Paused || _session is not { } session || ThreadId is not { } thread) return;
        ClearPausedContext();
        SetState(DebugState.Running);
        _pendingOps++;
        try { await request(session, thread).ConfigureAwait(false); }
        catch (Exception e) { await OnUi(() => { if (_session == session) _host.Output($"{what} failed: {e.Message}"); }).ConfigureAwait(false); }
        finally { await OnUi(() => _pendingOps--).ConfigureAwait(false); }
    }

    /// <summary>Pause: stops the running debuggee wherever it is.</summary>
    public async Task PauseAsync()
    {
        if (_state != DebugState.Running || _session is not { } session) return;
        _pendingOps++;
        try
        {
            var threads = await session.GetThreadsAsync().ConfigureAwait(false);
            await session.PauseAsync(threads.FirstOrDefault()?.Id ?? 0).ConfigureAwait(false);
        }
        catch (Exception e) { await OnUi(() => _host.Output("Pause failed: " + e.Message)).ConfigureAwait(false); }
        finally { await OnUi(() => _pendingOps--).ConfigureAwait(false); }
    }

    /// <summary>Stop (Shift+F5): cancels a starting session (its build too) or ends the running one, killing the debuggee.</summary>
    public Task StopAsync()
    {
        if (_state == DebugState.Design) return _ending;
        if (_session is not { } session)
        {
            var starting = _startCts;
            starting?.Cancel();
            _host.CancelBuild();
            Abort(starting, null);
            return _ending;
        }
        _host.Status("Stopping the debugger…");
        return EndSessionAsync(session, terminate: true);
    }

    /// <summary>Restart (Ctrl+Shift+F5): stops, then starts again (building first).</summary>
    public async Task<bool> RestartAsync()
    {
        if (_state == DebugState.Design) return false;
        await StopAsync();
        return await OnUi(StartAsync).Unwrap().ConfigureAwait(false);
    }

    // ---- paused context ----

    /// <summary>Selects a frame of the call stack: it becomes the context of Locals, Watch and hover.</summary>
    public async Task SelectFrameAsync(int index)
    {
        if (_state != DebugState.Paused || index < 0 || index >= _frames.Count || _session is not { } session) return;
        int generation = _stopGeneration;
        var scopes = await LoadScopes(session, _frames[index].Id).ConfigureAwait(false);
        await OnUi(() =>
        {
            if (generation != _stopGeneration) return;
            _frameIndex = index;
            _scopes = scopes;
            ContextChanged?.Invoke();
        }).ConfigureAwait(false);
    }

    /// <summary>Evaluates an expression in the selected frame (<paramref name="context"/>: "watch", "hover", "repl"). Null when not paused.</summary>
    public async Task<EvaluateResponse?> EvaluateAsync(string expression, string context = "watch")
    {
        if (_state != DebugState.Paused || _session is not { } session) return null;
        int? frame = CurrentFrame?.Id;
        _pendingOps++;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await session.EvaluateAsync(expression, frame, context, timeout.Token).ConfigureAwait(false);
        }
        finally { await OnUi(() => _pendingOps--).ConfigureAwait(false); }
    }

    /// <summary>The children of a structured value (lazy expansion in Locals and Watch).</summary>
    public async Task<IReadOnlyList<Variable>> GetVariablesAsync(int variablesReference)
    {
        if (_state != DebugState.Paused || _session is not { } session || variablesReference <= 0) return [];
        _pendingOps++;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await session.GetVariablesAsync(variablesReference, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is DapRequestException or OperationCanceledException or DapConnectionClosedException) { return []; }
        finally { await OnUi(() => _pendingOps--).ConfigureAwait(false); }
    }

    // ---- session events (background thread → UI) ----

    void Subscribe(DebugSession s)
    {
        s.Stopped += (_, e) => _post(() => OnStopped(s, e));
        s.Continued += (_, _) => _post(() =>
        {
            if (_session != s || _state != DebugState.Paused) return;
            ClearPausedContext();
            SetState(DebugState.Running);
        });
        s.Exited += (_, e) => _post(() =>
        {
            if (_session != s) return;
            LastExitCode = e.ExitCode;
            FlushOutput();
            _host.Output($"The app exited with code {e.ExitCode}.");
        });
        s.Terminated += (_, _) => _post(() => { if (_session == s) _ = EndSessionAsync(s, terminate: false); });
        s.AdapterClosed += (_, reason) => _post(() =>
        {
            if (_session != s) return;
            if (reason != null) _host.Output("The debugger stopped unexpectedly: " + reason.Message);
            _ = EndSessionAsync(s, terminate: false);
        });
        s.Output += (_, e) => _post(() => { if (_session == s) OnOutput(e); });
    }

    void OnStopped(DebugSession session, StoppedEventArgs e)
    {
        if (_session != session) return;
        int generation = ++_stopGeneration;
        StopReason = e.Reason;
        ThreadId = e.ThreadId ?? ThreadId;
        _frames = [];
        _frameIndex = -1;
        _scopes = [];
        ExceptionText = null;
        if (_startCts != null) _startCts = null;
        SetState(DebugState.Paused);
        FlushOutput();
        if (ThreadId is not { } thread) { ContextChanged?.Invoke(); return; }
        _pendingOps++;
        _ = LoadStop(session, thread, e.Reason, generation);
    }

    async Task LoadStop(DebugSession session, int thread, string reason, int generation)
    {
        IReadOnlyList<StackFrame> frames = [];
        IReadOnlyList<(Scope, IReadOnlyList<Variable>)> scopes = [];
        string? exception = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            frames = (await session.GetStackTraceAsync(thread, ct: timeout.Token).ConfigureAwait(false)).StackFrames;
            int top = frames.ToList().FindIndex(f => f.Source?.Path != null);
            if (frames.Count > 0) scopes = await LoadScopes(session, frames[Math.Max(0, top)].Id).ConfigureAwait(false);
            if (reason == "exception")
            {
                try
                {
                    var info = await session.GetExceptionInfoAsync(thread, timeout.Token).ConfigureAwait(false);
                    var type = info.Details?.FullTypeName ?? info.Details?.TypeName ?? info.ExceptionId;
                    var message = info.Details?.Message ?? info.Description;
                    exception = string.IsNullOrEmpty(message) ? type : $"{type}: {message}";
                }
                catch (Exception ex) when (ex is DapRequestException or OperationCanceledException) { exception = "An exception was thrown."; }
            }
        }
        catch (Exception ex) when (ex is DapRequestException or OperationCanceledException or DapConnectionClosedException)
        {
            await OnUi(() => { if (generation == _stopGeneration) _host.Output("Could not read the call stack: " + ex.Message); }).ConfigureAwait(false);
        }
        await OnUi(() =>
        {
            _pendingOps--;
            if (generation != _stopGeneration || _session != session) return;
            _frames = frames;
            // The first frame with source is the one to show (the top one may be in framework code).
            _frameIndex = frames.Count == 0 ? -1 : Math.Max(0, frames.ToList().FindIndex(f => f.Source?.Path != null));
            _scopes = scopes;
            ExceptionText = exception;
            var where = CurrentFrame is { } f ? $" at {f.Name}{(f.Source?.Path is { } p ? $" ({Path.GetFileName(p)}:{f.Line})" : "")}" : "";
            _host.Status($"Paused ({reason}){where}");
            ContextChanged?.Invoke();
            if (exception != null)
            {
                _host.Output("Exception thrown: " + exception);
                ExceptionStopped?.Invoke(exception);
            }
        }).ConfigureAwait(false);
    }

    static async Task<IReadOnlyList<(Scope, IReadOnlyList<Variable>)>> LoadScopes(DebugSession session, int frameId)
    {
        var result = new List<(Scope, IReadOnlyList<Variable>)>();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            foreach (var scope in await session.GetScopesAsync(frameId, timeout.Token).ConfigureAwait(false))
            {
                IReadOnlyList<Variable> vars = scope.Expensive || scope.VariablesReference <= 0 ? [] :
                    await session.GetVariablesAsync(scope.VariablesReference, timeout.Token).ConfigureAwait(false);
                result.Add((scope, vars));
            }
        }
        catch (Exception e) when (e is DapRequestException or OperationCanceledException or DapConnectionClosedException) { }
        return result;
    }

    void OnOutput(OutputEventArgs e)
    {
        if (e.Category == "telemetry" || e.Output.Length == 0) return;
        var key = e.Category ?? "console";
        var text = (_partialOutput.GetValueOrDefault(key) ?? "") + e.Output.Replace("\r\n", "\n");
        int nl = text.LastIndexOf('\n');
        if (nl < 0) { _partialOutput[key] = text; return; }
        _partialOutput[key] = text[(nl + 1)..];
        foreach (var line in text[..nl].Split('\n')) _host.Output(line);
    }

    void FlushOutput()
    {
        foreach (var (key, rest) in _partialOutput.ToList())
            if (rest.Length > 0) { _host.Output(rest); _partialOutput[key] = ""; }
    }

    async Task EndSessionAsync(DebugSession session, bool terminate)
    {
        if (_session != session) { await _ending.ConfigureAwait(false); return; }
        _session = null;
        _startCts = null;
        _ending = Task.Run(async () =>
        {
            if (terminate)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await session.DisconnectAsync(terminateDebuggee: true, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception) { /* the adapter may already be gone; disposing kills it */ }
            }
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { }
        });
        FlushOutput();
        ClearPausedContext();
        StopReason = null;
        ThreadId = null;
        _pendingSync.Clear();
        _host.Output(terminate ? "Debugging stopped." : "Debugging ended.");
        _host.Status(terminate ? "Debugging stopped" : "Debugging ended");
        SetState(DebugState.Design);
        await _ending.ConfigureAwait(false);
    }

    void ClearPausedContext()
    {
        bool had = _frames.Count > 0 || _frameIndex >= 0;
        _stopGeneration++;
        _frames = [];
        _frameIndex = -1;
        _scopes = [];
        ExceptionText = null;
        if (had) ContextChanged?.Invoke();
    }

    void SetState(DebugState state)
    {
        if (_state == state) return;
        _state = state;
        if (state == DebugState.Running) _host.Status("Debugging: running");
        StateChanged?.Invoke();
    }

    // ---- live breakpoint and exception filter sync ----

    void OnBreakpointsChanged(object? sender, BreakpointsChangedEventArgs e)
    {
        // User edits (on the UI thread) are re-sent while a session runs; adapter verdicts need no sync.
        if (e.Kind == BreakpointChangeKind.StateChanged) return;
        _post(() =>
        {
            if (_session == null || _state is DebugState.Design or DebugState.Starting) return;
            var files = e.File is { } f ? [f] : Breakpoints.Files.ToArray();
            bool schedule = _pendingSync.Count == 0;
            foreach (var file in files) _pendingSync.Add(file);
            if (e.Kind == BreakpointChangeKind.Cleared) _pendingSync.Add("*");
            if (schedule) _post(SyncPending);
        });
    }

    void SyncPending()
    {
        if (_session is not { } session) { _pendingSync.Clear(); return; }
        bool all = _pendingSync.Remove("*");
        var files = _pendingSync.ToArray();
        _pendingSync.Clear();
        _pendingOps++;
        _ = Task.Run(async () =>
        {
            try
            {
                if (all) await session.SyncAllBreakpointsAsync().ConfigureAwait(false);
                else foreach (var f in files) await session.SyncBreakpointsAsync(f).ConfigureAwait(false);
            }
            catch (Exception e) when (e is DapRequestException or DapConnectionClosedException or OperationCanceledException) { }
            finally { _post(() => _pendingOps--); }
        });
    }

    /// <summary>The filter ids for the current settings, from what the adapter offers (or the usual netcoredbg ids before it said).</summary>
    IReadOnlyList<string> ChooseExceptionFilters()
    {
        var offered = AvailableExceptionFilters.Select(f => f.Filter).ToList();
        bool Has(string id) => offered.Count == 0 || offered.Contains(id);
        var result = new List<string>();
        if (_breakOnAll && Has("all")) result.Add("all");
        if (_breakOnUnhandled)
        {
            if (offered.Contains("user-unhandled") || offered.Count == 0) result.Add("user-unhandled");
            else if (offered.Contains("unhandled")) result.Add("unhandled");
        }
        return result;
    }

    void ApplyExceptionFilters()
    {
        Breakpoints.SetExceptionFilters(ChooseExceptionFilters());
        if (_session is not { } session) return;
        if (_state == DebugState.Starting) { _exceptionFiltersPending = true; return; }
        _exceptionFiltersPending = false;
        _pendingOps++;
        _ = Task.Run(async () =>
        {
            try { await session.SyncExceptionBreakpointsAsync().ConfigureAwait(false); }
            catch (Exception e) when (e is DapRequestException or DapConnectionClosedException or OperationCanceledException) { }
            finally { _post(() => _pendingOps--); }
        });
    }

    // ---- helpers ----

    /// <summary>Runs <paramref name="action"/> on the UI thread; the task completes after it ran.</summary>
    Task OnUi(Action action) => OnUi(() => { action(); return true; });

    Task<T> OnUi<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception e) { tcs.TrySetException(e); }
        });
        return tcs.Task;
    }

    sealed class Progress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>Ends any session (killing the debuggee) and waits for the adapter to go.</summary>
    public async ValueTask DisposeAsync()
    {
        Breakpoints.Changed -= OnBreakpointsChanged;
        var session = _session;
        _session = null;
        _startCts?.Cancel();
        if (session != null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await session.DisconnectAsync(true, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception) { }
            await session.DisposeAsync().ConfigureAwait(false);
        }
        await _ending.ConfigureAwait(false);
    }

    /// <summary>Splits a command line into arguments (double quotes group, backslash-quote is a literal quote).</summary>
    public static IReadOnlyList<string> SplitArguments(string commandLine)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"') { current.Append('"'); i++; any = true; }
            else if (c == '"') { quoted = !quoted; any = true; }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) { result.Add(current.ToString()); current.Clear(); any = false; }
            }
            else { current.Append(c); any = true; }
        }
        if (any) result.Add(current.ToString());
        return result;
    }
}
