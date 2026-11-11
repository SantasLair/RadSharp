namespace RadSharp.Designer;

/// <summary>
/// A place the IDE can come back to: a line and column in a code file, or a form (with the control that was selected,
/// empty for the form itself).
/// </summary>
public sealed record NavLocation(string Kind, string Path, int Line = 1, int Column = 1, string Control = "")
{
    public const string Code = "code", Form = "form";

    public static NavLocation InCode(string path, int line, int column) => new(Code, path, line, column);
    public static NavLocation InForm(string path, string control = "") => new(Form, path, 1, 1, control);

    public override string ToString() => Kind == Form
        ? $"{System.IO.Path.GetFileName(Path)}{(Control.Length > 0 ? " › " + Control : "")}"
        : $"{System.IO.Path.GetFileName(Path)}:{Line}";
}

/// <summary>
/// The back/forward list (Alt+Left, Alt+Right). The shell reports where the user is (<see cref="Capture"/>) as documents
/// change, the caret moves and controls are selected; the history decides whether that is a new place or only a small
/// move within the current one. A caret move of up to <see cref="NearLines"/> lines in the same file updates the current
/// entry; anything else adds one. The list is bounded (<see cref="Capacity"/>); going back and then somewhere new drops
/// the forward entries, as in a browser. The history never opens anything itself: <see cref="Back"/> and <see cref="Forward"/>
/// return the location to go to, and the shell shows it (which can reopen a closed document) with
/// <see cref="Navigating"/> on, so the arrival isn't recorded again.
/// </summary>
public sealed class NavigationHistory
{
    /// <summary>A caret move of at most this many lines (in the same file) is the same place.</summary>
    public const int NearLines = 10;

    readonly List<NavLocation> _entries = [];
    int _index = -1;
    int _jumpDepth;
    bool _jumpRecorded;
    bool Jumping => _jumpDepth > 0;

    public NavigationHistory(int capacity = 100) => Capacity = Math.Max(2, capacity);

    public int Capacity { get; }
    public IReadOnlyList<NavLocation> Entries => _entries;

    /// <summary>The entry the user is at now (-1 when empty).</summary>
    public int Index => _index;

    public NavLocation? Current => _index >= 0 ? _entries[_index] : null;

    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    /// <summary>True while the shell is showing a location from the history; captures are ignored.</summary>
    public bool Navigating { get; set; }

    public event Action? Changed;

    /// <summary>The next captures, up to <see cref="EndJump"/>, form one new entry however many events they come in (a jump opens a file,
    /// activates it and moves its caret, and that is one place). The place the jump left stays as it was.</summary>
    public void BeginJump()
    {
        if (_jumpDepth++ == 0) _jumpRecorded = false;
    }

    public void EndJump() { if (_jumpDepth > 0) _jumpDepth--; }

    /// <summary>Reports where the user is now.</summary>
    public void Capture(NavLocation location)
    {
        if (Navigating) return;
        if (Jumping)
        {
            if (_jumpRecorded && _index >= 0) Replace(location);
            else if (Current == location) { _jumpRecorded = true; }
            else { Push(location); _jumpRecorded = true; }
            return;
        }
        if (Current is { } current && Near(current, location)) { Replace(location); return; }
        Push(location);
    }

    static bool Near(NavLocation a, NavLocation b)
    {
        if (a.Kind != b.Kind || !SamePath(a.Path, b.Path)) return false;
        return a.Kind == NavLocation.Form
            ? a.Control == b.Control || (a.Control.Length == 0 && b.Control.Length == 0)
            : Math.Abs(a.Line - b.Line) <= NearLines;
    }

    static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    void Replace(NavLocation location)
    {
        if (_entries[_index] == location) return;
        _entries[_index] = location;
    }

    void Push(NavLocation location)
    {
        if (_index < _entries.Count - 1) _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(location);
        if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
        _index = _entries.Count - 1;
        Changed?.Invoke();
    }

    /// <summary>Steps back; the location to show, or null at the start. Entries <paramref name="exists"/> rejects (a deleted file) are dropped on the way.</summary>
    public NavLocation? Back(Func<NavLocation, bool>? exists = null) => Step(-1, exists);

    /// <summary>Steps forward; the location to show, or null at the end.</summary>
    public NavLocation? Forward(Func<NavLocation, bool>? exists = null) => Step(1, exists);

    NavLocation? Step(int delta, Func<NavLocation, bool>? exists)
    {
        int i = _index + delta;
        while (i >= 0 && i < _entries.Count)
        {
            var entry = _entries[i];
            if (exists == null || exists(entry))
            {
                _index = i;
                Changed?.Invoke();
                return entry;
            }
            _entries.RemoveAt(i);
            if (delta < 0) { _index--; i--; }
        }
        return null;
    }

    public void Clear()
    {
        _entries.Clear();
        _index = -1;
        Changed?.Invoke();
    }
}
