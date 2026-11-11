using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RadSharp.Ide.Debug.Protocol;

namespace RadSharp.Ide.Debug;

public enum DebugSessionState
{
    /// <summary>Created, launch not yet complete.</summary>
    NotStarted,
    /// <summary>The debuggee is running.</summary>
    Running,
    /// <summary>The debuggee is stopped (breakpoint, step, pause, exception).</summary>
    Stopped,
    /// <summary>The debuggee exited, the session was disconnected, or the adapter went away.</summary>
    Ended,
}

/// <summary>What to run. <see cref="Program"/> is the executable; for a managed dll use "dotnet" and put the dll first in <see cref="Args"/>.</summary>
public sealed record DebugLaunchOptions
{
    public string Program { get; init; } = "";
    public IReadOnlyList<string> Args { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
    public bool StopAtEntry { get; init; }
    public bool? JustMyCode { get; init; }
}

/// <summary>
/// A debugging session against a Debug Adapter Protocol adapter (netcoredbg). Typed requests, .NET events and
/// breakpoint synchronisation with a <see cref="BreakpointStore"/>.
/// <para>
/// <b>Threading.</b> All events are raised on a background thread (a dedicated dispatch task, one event at a time
/// and in order). A UI must marshal to its own thread. Handlers may call back into the session. Requests may be
/// issued from any thread.
/// </para>
/// </summary>
public sealed class DebugSession : IAsyncDisposable
{
    private readonly DapTransport _transport;
    private readonly Process? _process;
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<string> _sentFiles = new();
    private readonly object _gate = new();
    private DebugSessionState _state = DebugSessionState.NotStarted;
    private bool _initializeDone;
    private int? _stoppedThreadId;

    public DebugSession(DapTransport transport, BreakpointStore? breakpoints = null)
        : this(transport, null, breakpoints) { }

    private DebugSession(DapTransport transport, Process? process, BreakpointStore? breakpoints)
    {
        _transport = transport;
        _process = process;
        Breakpoints = breakpoints ?? new BreakpointStore();
        _transport.EventReceived += OnEvent;
        _transport.Closed += OnClosed;
    }

    /// <summary>Starts the adapter executable (netcoredbg) with <c>--interpreter=vscode</c> and connects to it over stdio.</summary>
    public static DebugSession StartAdapter(string adapterPath, BreakpointStore? breakpoints = null)
    {
        var psi = new ProcessStartInfo(adapterPath, "--interpreter=vscode")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + adapterPath);
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine(); // drain so the adapter never blocks on a full stderr pipe
        var transport = new DapTransport(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        return new DebugSession(transport, process, breakpoints);
    }

    /// <summary>The breakpoints this session syncs with. Survives the session.</summary>
    public BreakpointStore Breakpoints { get; }

    public DebugSessionState State { get { lock (_gate) return _state; } }

    /// <summary>The thread of the last stop, while stopped.</summary>
    public int? StoppedThreadId { get { lock (_gate) return _stoppedThreadId; } }

    /// <summary>What the adapter supports, available after <see cref="InitializeAsync"/>.</summary>
    public DapCapabilities Capabilities { get; private set; } = new();

    // ---- events (background thread) ----

    public event EventHandler<StoppedEventArgs>? Stopped;
    public event EventHandler<ContinuedEventArgs>? Continued;
    public event EventHandler<ExitedEventArgs>? Exited;
    public event EventHandler<TerminatedEventArgs>? Terminated;
    public event EventHandler<OutputEventArgs>? Output;
    public event EventHandler<BreakpointEventArgs>? BreakpointChanged;
    public event EventHandler<ModuleEventArgs>? ModuleChanged;
    public event EventHandler<ThreadEventArgs>? ThreadChanged;
    /// <summary>Raised once when the adapter connection ends. The argument is non-null if it crashed or broke.</summary>
    public event EventHandler<Exception?>? AdapterClosed;
    /// <summary>The state changed (also raised on the background thread).</summary>
    public event EventHandler<DebugSessionState>? StateChanged;

    // ---- lifecycle ----

    public async Task<DapCapabilities> InitializeAsync(CancellationToken ct = default)
    {
        var body = await _transport.RequestAsync("initialize", Json(new InitializeArguments(), DapJson.Default.InitializeArguments), ct).ConfigureAwait(false);
        Capabilities = body.ValueKind == JsonValueKind.Object ? body.Deserialize(DapJson.Default.DapCapabilities) ?? new() : new();
        _initializeDone = true;
        return Capabilities;
    }

    /// <summary>
    /// The whole start-up handshake: initialize (if not done), launch, wait for <c>initialized</c>, send every
    /// breakpoint in the store plus the exception filters, then configurationDone. The debuggee runs on return
    /// (or is stopped at its entry point if requested).
    /// </summary>
    public async Task LaunchAsync(DebugLaunchOptions options, CancellationToken ct = default)
    {
        if (!_initializeDone) await InitializeAsync(ct).ConfigureAwait(false);

        var args = new LaunchArguments
        {
            Program = options.Program,
            Args = options.Args.Count > 0 ? [.. options.Args] : null,
            Cwd = options.WorkingDirectory,
            Env = options.Environment is null ? null : new Dictionary<string, string?>(options.Environment),
            StopAtEntry = options.StopAtEntry,
            JustMyCode = options.JustMyCode,
        };
        // Some adapters answer launch only after configurationDone, so do not await it yet.
        var launch = _transport.RequestAsync("launch", Json(args, DapJson.Default.LaunchArguments), ct);

        var done = await Task.WhenAny(_initialized.Task, launch).ConfigureAwait(false);
        if (done == launch) await launch.ConfigureAwait(false); // failed or finished early: surface it
        await _initialized.Task.WaitAsync(ct).ConfigureAwait(false);

        await SyncAllBreakpointsAsync(ct).ConfigureAwait(false);
        await SyncExceptionBreakpointsAsync(ct).ConfigureAwait(false);
        await ConfigurationDoneAsync(ct).ConfigureAwait(false);
        await launch.WaitAsync(ct).ConfigureAwait(false);
        SetState(DebugSessionState.Running, onlyFrom: DebugSessionState.NotStarted);
    }

    public Task ConfigurationDoneAsync(CancellationToken ct = default) =>
        _transport.RequestAsync("configurationDone", null, ct);

    // ---- breakpoints ----

    /// <summary>Sends the store's enabled breakpoints of one file (an empty list clears the file) and records the verdicts.</summary>
    public async Task SyncBreakpointsAsync(string file, CancellationToken ct = default)
    {
        file = BreakpointStore.NormalizePath(file);
        var sent = Breakpoints.GetEnabled(file);
        var args = new SetBreakpointsArguments
        {
            Source = new DapSource { Name = Path.GetFileName(file), Path = file },
            Breakpoints = [.. sent.Select(b => new DapSourceBreakpoint { Line = b.Line, Condition = b.Condition, HitCondition = b.HitCondition })],
        };
        var body = await _transport.RequestAsync("setBreakpoints", Json(args, DapJson.Default.SetBreakpointsArguments), ct).ConfigureAwait(false);
        var response = body.ValueKind == JsonValueKind.Object ? body.Deserialize(DapJson.Default.SetBreakpointsResponse) : null;
        lock (_gate) { if (sent.Count > 0) _sentFiles.Add(file); else _sentFiles.Remove(file); }
        Breakpoints.ApplyAdapterResult(file, sent, response?.Breakpoints ?? []);
    }

    /// <summary>Re-sends every file that has breakpoints, plus files that were sent earlier and may now be empty.</summary>
    public async Task SyncAllBreakpointsAsync(CancellationToken ct = default)
    {
        string[] files;
        lock (_gate) files = [.. Breakpoints.Files.Concat(_sentFiles).Distinct(StringComparer.Ordinal)];
        foreach (var f in files) await SyncBreakpointsAsync(f, ct).ConfigureAwait(false);
    }

    /// <summary>Sends the exception filters from the store (e.g. "all", "user-unhandled"); an empty set clears them.</summary>
    public Task SyncExceptionBreakpointsAsync(CancellationToken ct = default) =>
        SetExceptionBreakpointsAsync(Breakpoints.ExceptionFilters, ct);

    public Task SetExceptionBreakpointsAsync(IEnumerable<string> filters, CancellationToken ct = default) =>
        _transport.RequestAsync("setExceptionBreakpoints",
            Json(new SetExceptionBreakpointsArguments { Filters = [.. filters] }, DapJson.Default.SetExceptionBreakpointsArguments), ct);

    // ---- execution control ----

    public async Task ContinueAsync(int threadId, CancellationToken ct = default)
    {
        await _transport.RequestAsync("continue", Json(new ContinueArguments { ThreadId = threadId }, DapJson.Default.ContinueArguments), ct).ConfigureAwait(false);
        SetState(DebugSessionState.Running, onlyFrom: DebugSessionState.Stopped);
    }

    public Task NextAsync(int threadId, CancellationToken ct = default) => Step("next", threadId, ct);
    public Task StepInAsync(int threadId, CancellationToken ct = default) => Step("stepIn", threadId, ct);
    public Task StepOutAsync(int threadId, CancellationToken ct = default) => Step("stepOut", threadId, ct);

    private async Task Step(string command, int threadId, CancellationToken ct)
    {
        await _transport.RequestAsync(command, Json(new ThreadArguments { ThreadId = threadId }, DapJson.Default.ThreadArguments), ct).ConfigureAwait(false);
        SetState(DebugSessionState.Running, onlyFrom: DebugSessionState.Stopped);
    }

    public Task PauseAsync(int threadId, CancellationToken ct = default) =>
        _transport.RequestAsync("pause", Json(new ThreadArguments { ThreadId = threadId }, DapJson.Default.ThreadArguments), ct);

    // ---- inspection ----

    public async Task<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken ct = default) =>
        (await Call("threads", (object?)null, null, DapJson.Default.ThreadsResponse, ct).ConfigureAwait(false)).Threads;

    public async Task<StackTraceResponse> GetStackTraceAsync(int threadId, int? startFrame = null, int? levels = null, CancellationToken ct = default) =>
        await Call("stackTrace", new StackTraceArguments { ThreadId = threadId, StartFrame = startFrame, Levels = levels },
            DapJson.Default.StackTraceArguments, DapJson.Default.StackTraceResponse, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<Scope>> GetScopesAsync(int frameId, CancellationToken ct = default) =>
        (await Call("scopes", new ScopesArguments { FrameId = frameId }, DapJson.Default.ScopesArguments, DapJson.Default.ScopesResponse, ct).ConfigureAwait(false)).Scopes;

    public async Task<IReadOnlyList<Variable>> GetVariablesAsync(int variablesReference, CancellationToken ct = default) =>
        (await Call("variables", new VariablesArguments { VariablesReference = variablesReference }, DapJson.Default.VariablesArguments, DapJson.Default.VariablesResponse, ct).ConfigureAwait(false)).Variables;

    /// <param name="context">"watch", "repl", "hover" or "clipboard".</param>
    public async Task<EvaluateResponse> EvaluateAsync(string expression, int? frameId = null, string context = "watch", CancellationToken ct = default) =>
        await Call("evaluate", new EvaluateArguments { Expression = expression, FrameId = frameId, Context = context },
            DapJson.Default.EvaluateArguments, DapJson.Default.EvaluateResponse, ct).ConfigureAwait(false);

    /// <summary>Details of the exception the thread is stopped on (when stopped with reason "exception").</summary>
    public async Task<ExceptionInfoResponse> GetExceptionInfoAsync(int threadId, CancellationToken ct = default) =>
        await Call("exceptionInfo", new ExceptionInfoArguments { ThreadId = threadId },
            DapJson.Default.ExceptionInfoArguments, DapJson.Default.ExceptionInfoResponse, ct).ConfigureAwait(false);

    // ---- ending ----

    /// <summary>Asks the adapter to terminate the debuggee gracefully (the <c>terminate</c> request, if supported).</summary>
    public Task TerminateAsync(CancellationToken ct = default) => _transport.RequestAsync("terminate", null, ct);

    /// <summary>Ends the session. By default also kills the debuggee.</summary>
    public async Task DisconnectAsync(bool terminateDebuggee = true, CancellationToken ct = default)
    {
        try
        {
            await _transport.RequestAsync("disconnect",
                Json(new DisconnectArguments { TerminateDebuggee = terminateDebuggee }, DapJson.Default.DisconnectArguments), ct).ConfigureAwait(false);
        }
        finally { SetState(DebugSessionState.Ended); }
    }

    public async ValueTask DisposeAsync()
    {
        if (State is not DebugSessionState.Ended && !_transport.IsClosed)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await DisconnectAsync(true, cts.Token).ConfigureAwait(false);
            }
            catch (Exception) { }
        }
        _transport.EventReceived -= OnEvent;
        _transport.Closed -= OnClosed;
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_process is not null)
        {
            try
            {
                if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
            }
            catch (Exception) { }
            _process.Dispose();
        }
    }

    // ---- internals ----

    private async Task<TResp> Call<TArg, TResp>(string command, TArg? args, JsonTypeInfo<TArg>? argInfo, JsonTypeInfo<TResp> respInfo, CancellationToken ct)
        where TResp : new()
    {
        JsonElement? element = args is null || argInfo is null ? null : Json(args, argInfo);
        var body = await _transport.RequestAsync(command, element, ct).ConfigureAwait(false);
        return body.ValueKind == JsonValueKind.Object ? body.Deserialize(respInfo) ?? new TResp() : new TResp();
    }

    private static JsonElement Json<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.SerializeToElement(value, info);

    private void SetState(DebugSessionState state, DebugSessionState? onlyFrom = null)
    {
        lock (_gate)
        {
            if (_state == state || _state == DebugSessionState.Ended) return;
            if (onlyFrom is { } from && _state != from) return;
            _state = state;
            if (state != DebugSessionState.Stopped) _stoppedThreadId = null;
        }
        StateChanged?.Invoke(this, state);
    }

    private void OnEvent(string name, JsonElement body)
    {
        try
        {
            switch (name)
            {
                case "initialized":
                    _initialized.TrySetResult();
                    break;
                case "stopped":
                {
                    var e = Read(body, DapJson.Default.StoppedEventArgs);
                    lock (_gate) _stoppedThreadId = e.ThreadId;
                    if (e.HitBreakpointIds is { Count: > 0 }) Breakpoints.RecordHits(e.HitBreakpointIds);
                    SetState(DebugSessionState.Stopped);
                    lock (_gate) _stoppedThreadId = e.ThreadId;
                    Stopped?.Invoke(this, e);
                    break;
                }
                case "continued":
                    SetState(DebugSessionState.Running, onlyFrom: DebugSessionState.Stopped);
                    Continued?.Invoke(this, Read(body, DapJson.Default.ContinuedEventArgs));
                    break;
                case "exited":
                    Exited?.Invoke(this, Read(body, DapJson.Default.ExitedEventArgs));
                    break;
                case "terminated":
                    SetState(DebugSessionState.Ended);
                    Breakpoints.ResetAdapterState();
                    Terminated?.Invoke(this, Read(body, DapJson.Default.TerminatedEventArgs));
                    break;
                case "output":
                    Output?.Invoke(this, Read(body, DapJson.Default.OutputEventArgs));
                    break;
                case "breakpoint":
                {
                    var e = Read(body, DapJson.Default.BreakpointEventArgs);
                    if (e.Reason != "removed") Breakpoints.ApplyAdapterEvent(e.Breakpoint);
                    BreakpointChanged?.Invoke(this, e);
                    break;
                }
                case "module":
                    ModuleChanged?.Invoke(this, Read(body, DapJson.Default.ModuleEventArgs));
                    break;
                case "thread":
                    ThreadChanged?.Invoke(this, Read(body, DapJson.Default.ThreadEventArgs));
                    break;
            }
        }
        catch (JsonException) { /* a malformed event body is ignored */ }
    }

    private void OnClosed(Exception? reason)
    {
        SetState(DebugSessionState.Ended);
        Breakpoints.ResetAdapterState();
        lock (_gate) _sentFiles.Clear();
        _initialized.TrySetException(new DapConnectionClosedException("The debug adapter closed before the session started.", reason));
        _ = _initialized.Task.Exception; // observed
        AdapterClosed?.Invoke(this, reason);
    }

    private static T Read<T>(JsonElement body, JsonTypeInfo<T> info) where T : class, new() =>
        body.ValueKind == JsonValueKind.Object ? body.Deserialize(info) ?? new T() : new T();
}
