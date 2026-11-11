namespace RadSharp.Design.Code;

/// <summary>How serious a <see cref="BuildMessage"/> is, for filtering and counting.</summary>
public enum ProblemSeverity { Error, Warning, Info }

public static class ProblemSeverityExtensions
{
    /// <summary>Maps the free-form <see cref="BuildMessage.Severity"/> text (<c>error</c>, <c>warning</c>, anything else is info).</summary>
    public static ProblemSeverity SeverityOf(this BuildMessage m) => m.Severity.ToLowerInvariant() switch
    {
        "error" => ProblemSeverity.Error,
        "warning" => ProblemSeverity.Warning,
        _ => ProblemSeverity.Info,
    };
}

/// <summary>
/// Something that produces problems for the Problems window: the last build's diagnostics, live diagnostics from a
/// language service, a linter. Each source owns its list and replaces it as a whole; the store shows the union.
/// </summary>
public interface IProblemSource
{
    /// <summary>A stable key (<c>build</c>, <c>live</c>); one source per id in a <see cref="ProblemStore"/>.</summary>
    string Id { get; }

    /// <summary>The current problems. A snapshot: it isn't changed in place.</summary>
    IReadOnlyList<BuildMessage> Problems { get; }

    /// <summary>Raised after <see cref="Problems"/> changed. Raised on the UI thread by the sources in this assembly;
    /// a source that works in the background marshals before raising it.</summary>
    event Action? Changed;
}

/// <summary>A message together with the id of the source that reported it.</summary>
public sealed record Problem(string Source, BuildMessage Message);

/// <summary>The simplest <see cref="IProblemSource"/>: a list replaced wholesale.</summary>
public sealed class ProblemList(string id) : IProblemSource
{
    IReadOnlyList<BuildMessage> _problems = [];
    public string Id => id;
    public IReadOnlyList<BuildMessage> Problems => _problems;
    public event Action? Changed;

    public void Replace(IEnumerable<BuildMessage> problems)
    {
        _problems = [.. problems];
        Changed?.Invoke();
    }

    public void Clear() => Replace([]);
}

/// <summary>
/// The problems of every registered <see cref="IProblemSource"/>, in registration order. Build results and live
/// results coexist and are replaced independently, since each lives in its own source.
/// </summary>
public sealed class ProblemStore
{
    readonly List<IProblemSource> _sources = [];

    public IReadOnlyList<IProblemSource> Sources => _sources;

    /// <summary>Raised when a source was added or removed, or any source changed.</summary>
    public event Action? Changed;

    /// <summary>Registers a source (replacing one with the same id).</summary>
    public void Add(IProblemSource source)
    {
        Remove(source.Id);
        _sources.Add(source);
        source.Changed += OnSourceChanged;
        Changed?.Invoke();
    }

    public bool Remove(string id)
    {
        var existing = _sources.FirstOrDefault(s => s.Id == id);
        if (existing == null) return false;
        existing.Changed -= OnSourceChanged;
        _sources.Remove(existing);
        Changed?.Invoke();
        return true;
    }

    public IProblemSource? Find(string id) => _sources.FirstOrDefault(s => s.Id == id);

    /// <summary>Replaces the problems of source <paramref name="id"/> (a <see cref="ProblemList"/> is created on first use).</summary>
    public void Set(string id, IEnumerable<BuildMessage> problems)
    {
        if (Find(id) is ProblemList list) list.Replace(problems);
        else
        {
            var created = new ProblemList(id);
            Add(created);
            created.Replace(problems);
        }
    }

    public void Clear(string id) { if (Find(id) is ProblemList list) list.Clear(); }

    void OnSourceChanged() => Changed?.Invoke();

    public IReadOnlyList<Problem> All => [.. _sources.SelectMany(s => s.Problems.Select(m => new Problem(s.Id, m)))];

    public int Count(ProblemSeverity severity) => _sources.Sum(s => s.Problems.Count(m => m.SeverityOf() == severity));
}
