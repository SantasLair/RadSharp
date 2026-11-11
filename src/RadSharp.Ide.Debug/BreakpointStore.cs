using RadSharp.Ide.Debug.Protocol;

namespace RadSharp.Ide.Debug;

/// <summary>
/// One user breakpoint. Identity (<see cref="Id"/>, <see cref="File"/>, <see cref="Line"/>) and the user settings
/// are owned by the <see cref="BreakpointStore"/>; the "adapter state" members are filled in by a debug session.
/// Instances are snapshots: read them from the store, change them through the store.
/// </summary>
public sealed record SourceBreakpoint
{
    /// <summary>Stable id that survives sessions (the adapter's id does not).</summary>
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>Full path of the source file.</summary>
    public string File { get; init; } = "";
    /// <summary>1-based line the user asked for.</summary>
    public int Line { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>C# expression; the breakpoint only stops when it is true.</summary>
    public string? Condition { get; init; }
    /// <summary>
    /// Adapter hit condition, e.g. "5" (stop on the 5th hit), "&gt;=3", "%2". Null for none.
    /// </summary>
    public string? HitCondition { get; init; }

    /// <summary>How often this breakpoint caused a stop, counted over all sessions since the store was created.</summary>
    public int TimesHit { get; init; }

    // ---- adapter state (not persisted; cleared when a session ends) ----

    /// <summary>null: never sent to an adapter, or not sent in the current session. true/false: the adapter's verdict.</summary>
    public bool? Verified { get; init; }
    /// <summary>Why the adapter could not verify it, if it said.</summary>
    public string? Message { get; init; }
    /// <summary>The line the adapter actually bound to, which can differ from <see cref="Line"/>.</summary>
    public int? ActualLine { get; init; }
    /// <summary>The adapter's id in the current session.</summary>
    public int? AdapterId { get; init; }
}

public enum BreakpointChangeKind { Added, Removed, Modified, StateChanged, Cleared }

public sealed class BreakpointsChangedEventArgs(BreakpointChangeKind kind, SourceBreakpoint? breakpoint, string? file) : EventArgs
{
    public BreakpointChangeKind Kind { get; } = kind;
    /// <summary>The affected breakpoint (its new state, or the removed one); null for <see cref="BreakpointChangeKind.Cleared"/>.</summary>
    public SourceBreakpoint? Breakpoint { get; } = breakpoint;
    public string? File { get; } = file;
}

/// <summary>
/// The IDE's breakpoints, independent of any debug session. A session reads <see cref="GetEnabled"/> and re-sends
/// on launch, and reports adapter verdicts back through <see cref="ApplyAdapterResult"/>. Thread-safe; the
/// <see cref="Changed"/> event fires on whichever thread made the change (a session thread for state changes), so UI
/// code must marshal.
/// </summary>
public sealed class BreakpointStore
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly object _gate = new();
    private readonly List<SourceBreakpoint> _items = new();
    private readonly HashSet<string> _exceptionFilters = new(StringComparer.Ordinal);

    public event EventHandler<BreakpointsChangedEventArgs>? Changed;

    public static string NormalizePath(string path) => Path.GetFullPath(path);

    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && PathComparer.Equals(NormalizePath(a), NormalizePath(b));

    /// <summary>All breakpoints, ordered by file then line.</summary>
    public IReadOnlyList<SourceBreakpoint> All
    {
        get { lock (_gate) return [.. _items.OrderBy(b => b.File, PathComparer).ThenBy(b => b.Line)]; }
    }

    public IReadOnlyList<SourceBreakpoint> GetForFile(string file)
    {
        file = NormalizePath(file);
        lock (_gate) return [.. _items.Where(b => PathComparer.Equals(b.File, file)).OrderBy(b => b.Line)];
    }

    /// <summary>The enabled breakpoints of a file: what is sent to an adapter.</summary>
    public IReadOnlyList<SourceBreakpoint> GetEnabled(string file) => [.. GetForFile(file).Where(b => b.Enabled)];

    /// <summary>Files that have at least one breakpoint.</summary>
    public IReadOnlyList<string> Files
    {
        get { lock (_gate) return [.. _items.Select(b => b.File).Distinct(PathComparer)]; }
    }

    public SourceBreakpoint? Get(Guid id)
    {
        lock (_gate) return _items.FirstOrDefault(b => b.Id == id);
    }

    public SourceBreakpoint? Find(string file, int line)
    {
        file = NormalizePath(file);
        lock (_gate) return _items.FirstOrDefault(b => b.Line == line && PathComparer.Equals(b.File, file));
    }

    /// <summary>Adds a breakpoint, or returns the existing one at that file and line.</summary>
    public SourceBreakpoint Add(string file, int line, string? condition = null, string? hitCondition = null, bool enabled = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(line, 1);
        file = NormalizePath(file);
        SourceBreakpoint bp;
        lock (_gate)
        {
            var existing = _items.FirstOrDefault(b => b.Line == line && PathComparer.Equals(b.File, file));
            if (existing is not null) return existing;
            bp = new SourceBreakpoint { File = file, Line = line, Condition = Blank(condition), HitCondition = Blank(hitCondition), Enabled = enabled };
            _items.Add(bp);
        }
        Raise(BreakpointChangeKind.Added, bp);
        return bp;
    }

    /// <summary>Adds the breakpoint at file:line, or removes it if present (gutter click). Returns true if one now exists.</summary>
    public bool Toggle(string file, int line)
    {
        var existing = Find(file, line);
        if (existing is not null) { Remove(existing.Id); return false; }
        Add(file, line);
        return true;
    }

    public bool Remove(Guid id)
    {
        SourceBreakpoint? bp;
        lock (_gate)
        {
            bp = _items.FirstOrDefault(b => b.Id == id);
            if (bp is null) return false;
            _items.Remove(bp);
        }
        Raise(BreakpointChangeKind.Removed, bp);
        return true;
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
        Changed?.Invoke(this, new BreakpointsChangedEventArgs(BreakpointChangeKind.Cleared, null, null));
    }

    public bool SetEnabled(Guid id, bool enabled) => Update(id, b => b with { Enabled = enabled, Verified = null, Message = null, ActualLine = null, AdapterId = null });

    /// <summary>Edits the condition and hit condition. Blank means none.</summary>
    public bool SetConditions(Guid id, string? condition, string? hitCondition) =>
        Update(id, b => b with { Condition = Blank(condition), HitCondition = Blank(hitCondition), Verified = null, Message = null });

    /// <summary>Moves a breakpoint to another line (e.g. after the user edited the file). Fails if one exists there.</summary>
    public bool MoveTo(Guid id, int line)
    {
        lock (_gate)
        {
            var bp = _items.FirstOrDefault(b => b.Id == id);
            if (bp is null || _items.Any(o => o.Id != id && o.Line == line && PathComparer.Equals(o.File, bp.File))) return false;
        }
        return Update(id, b => b with { Line = line, Verified = null, Message = null, ActualLine = null });
    }

    private bool Update(Guid id, Func<SourceBreakpoint, SourceBreakpoint> change, BreakpointChangeKind kind = BreakpointChangeKind.Modified)
    {
        SourceBreakpoint updated;
        lock (_gate)
        {
            var i = _items.FindIndex(b => b.Id == id);
            if (i < 0) return false;
            updated = change(_items[i]);
            _items[i] = updated;
        }
        Raise(kind, updated);
        return true;
    }

    // ---- exception breakpoints ----

    /// <summary>Exception filter ids (e.g. "all", "user-unhandled" for netcoredbg) to break on.</summary>
    public IReadOnlyCollection<string> ExceptionFilters
    {
        get { lock (_gate) return [.. _exceptionFilters]; }
    }

    public void SetExceptionFilters(IEnumerable<string> filters)
    {
        lock (_gate)
        {
            _exceptionFilters.Clear();
            foreach (var f in filters) _exceptionFilters.Add(f);
        }
    }

    // ---- adapter state, written by DebugSession ----

    /// <summary>
    /// Records an adapter's reply to a setBreakpoints for <paramref name="file"/>. <paramref name="sent"/> is the
    /// list that was sent (in order) and <paramref name="results"/> the adapter's list, matched by index.
    /// </summary>
    public void ApplyAdapterResult(string file, IReadOnlyList<SourceBreakpoint> sent, IReadOnlyList<DapBreakpoint> results)
    {
        var changed = new List<SourceBreakpoint>();
        lock (_gate)
        {
            for (int n = 0; n < sent.Count; n++)
            {
                var i = _items.FindIndex(b => b.Id == sent[n].Id);
                if (i < 0) continue;
                var r = n < results.Count ? results[n] : null;
                _items[i] = _items[i] with
                {
                    Verified = r?.Verified ?? false,
                    Message = r?.Message,
                    ActualLine = r?.Line,
                    AdapterId = r?.Id,
                };
                changed.Add(_items[i]);
            }
        }
        foreach (var bp in changed) Raise(BreakpointChangeKind.StateChanged, bp);
    }

    /// <summary>Applies an asynchronous <c>breakpoint</c> event (e.g. a pending breakpoint became verified).</summary>
    public void ApplyAdapterEvent(DapBreakpoint adapter)
    {
        if (adapter.Id is not { } id) return;
        Update(_items.FirstOrDefault(b => b.AdapterId == id)?.Id ?? Guid.Empty, b => b with
        {
            Verified = adapter.Verified,
            Message = adapter.Message,
            ActualLine = adapter.Line ?? b.ActualLine,
        }, BreakpointChangeKind.StateChanged);
    }

    /// <summary>Counts a stop caused by the given adapter breakpoint ids.</summary>
    public void RecordHits(IEnumerable<int> adapterIds)
    {
        foreach (var id in adapterIds)
        {
            Guid target;
            lock (_gate) target = _items.FirstOrDefault(b => b.AdapterId == id)?.Id ?? Guid.Empty;
            if (target != Guid.Empty) Update(target, b => b with { TimesHit = b.TimesHit + 1 }, BreakpointChangeKind.StateChanged);
        }
    }

    /// <summary>Forgets all adapter state (session ended); settings and hit counts are kept.</summary>
    public void ResetAdapterState()
    {
        List<SourceBreakpoint> changed;
        lock (_gate)
        {
            for (int i = 0; i < _items.Count; i++)
                _items[i] = _items[i] with { Verified = null, Message = null, ActualLine = null, AdapterId = null };
            changed = [.. _items];
        }
        foreach (var bp in changed) Raise(BreakpointChangeKind.StateChanged, bp);
    }

    /// <summary>Resets the per-breakpoint hit counters.</summary>
    public void ResetHitCounts()
    {
        List<SourceBreakpoint> changed;
        lock (_gate)
        {
            for (int i = 0; i < _items.Count; i++) _items[i] = _items[i] with { TimesHit = 0 };
            changed = [.. _items];
        }
        foreach (var bp in changed) Raise(BreakpointChangeKind.StateChanged, bp);
    }

    private void Raise(BreakpointChangeKind kind, SourceBreakpoint bp) =>
        Changed?.Invoke(this, new BreakpointsChangedEventArgs(kind, bp, bp.File));

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
