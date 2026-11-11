namespace RadSharp.Ide.Language;

/// <summary>
/// Live diagnostics: recomputes the project's diagnostics a short while (<see cref="Debounce"/>, about
/// 300 ms) after the workspace stops changing, and publishes them with <see cref="Updated"/>. A change while
/// a computation runs cancels it, so only results for the current version are published.
/// </summary>
/// <remarks>
/// <see cref="Updated"/> is raised on a thread-pool thread. A UI hands the list to its squiggles and Problems
/// window with <c>Application.Post(() => problems.Show(update.Value))</c>.
/// </remarks>
public sealed class DiagnosticsMonitor : IDisposable
{
    readonly LanguageService _service;
    readonly object _gate = new();
    CancellationTokenSource? _pending;
    Versioned<IReadOnlyList<DiagnosticInfo>>? _latest;
    readonly List<(long MinVersion, TaskCompletionSource<Versioned<IReadOnlyList<DiagnosticInfo>>> Waiter)> _waiters = [];
    bool _disposed;

    public DiagnosticsMonitor(LanguageService service, TimeSpan? debounce = null)
    {
        _service = service;
        Debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        service.Workspace.Changed += OnChanged;
        Schedule(TimeSpan.Zero);
    }

    public TimeSpan Debounce { get; }

    /// <summary>The most recent published diagnostics (null until the first computation finishes).</summary>
    public Versioned<IReadOnlyList<DiagnosticInfo>>? Latest { get { lock (_gate) return _latest; } }

    /// <summary>Raised on a thread-pool thread with the diagnostics of a version that was still current when they were computed.</summary>
    public event Action<Versioned<IReadOnlyList<DiagnosticInfo>>>? Updated;

    /// <summary>Completes with the first published diagnostics for <paramref name="minVersion"/> or later.</summary>
    public Task<Versioned<IReadOnlyList<DiagnosticInfo>>> WaitForAsync(long minVersion, CancellationToken cancel = default)
    {
        lock (_gate)
        {
            if (_latest is { } latest && latest.Version >= minVersion) return Task.FromResult(latest);
            var waiter = new TaskCompletionSource<Versioned<IReadOnlyList<DiagnosticInfo>>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((minVersion, waiter));
            cancel.Register(() => waiter.TrySetCanceled(cancel));
            return waiter.Task;
        }
    }

    void OnChanged(WorkspaceChange change) => Schedule(Debounce);

    void Schedule(TimeSpan delay)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed) return;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = cts = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                var result = await _service.GetProjectDiagnosticsAsync(cts.Token).ConfigureAwait(false);
                if (!_service.Workspace.IsCurrent(result.Version)) return;   // a newer change has its own run scheduled
                Publish(result);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
    }

    void Publish(Versioned<IReadOnlyList<DiagnosticInfo>> result)
    {
        List<TaskCompletionSource<Versioned<IReadOnlyList<DiagnosticInfo>>>> ready;
        lock (_gate)
        {
            if (_disposed || (_latest != null && _latest.Version > result.Version)) return;
            _latest = result;
            ready = [.. _waiters.Where(w => w.MinVersion <= result.Version).Select(w => w.Waiter)];
            _waiters.RemoveAll(w => w.MinVersion <= result.Version);
        }
        Updated?.Invoke(result);
        foreach (var waiter in ready) waiter.TrySetResult(result);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
        _service.Workspace.Changed -= OnChanged;
    }
}
