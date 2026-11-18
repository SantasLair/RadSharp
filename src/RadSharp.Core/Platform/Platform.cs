namespace RadSharp;

/// <summary>What a file dialog shows and returns.</summary>
public sealed class FileDialogOptions
{
    public string Title { get; init; } = "";
    /// <summary>Delphi-style filter: <c>"Images|*.png;*.jpg|All files|*.*"</c>.</summary>
    public string Filter { get; init; } = "";
    public string InitialDirectory { get; init; } = "";
    public string FileName { get; init; } = "";
    public bool AllowMultiple { get; init; }

    /// <summary>The filter as (name, patterns) pairs.</summary>
    public IReadOnlyList<(string Name, string Patterns)> ParseFilter()
    {
        var parts = Filter.Split('|');
        var list = new List<(string, string)>();
        for (int i = 0; i + 1 < parts.Length; i += 2) list.Add((parts[i], parts[i + 1]));
        return list;
    }
}

/// <summary>Result of a file dialog. The host provides Path and the Stream-opening methods.</summary>
public sealed class FileResult
{
    /// <summary>The full path to the chosen file (null if cancelled on some platforms).</summary>
    public string? Path { get; }

    private readonly Func<Stream> _openRead;
    private readonly Func<Stream> _openWrite;

    public FileResult(string? path, Func<Stream> openRead, Func<Stream> openWrite)
    {
        Path = path;
        _openRead = openRead ?? throw new ArgumentNullException(nameof(openRead));
        _openWrite = openWrite ?? throw new ArgumentNullException(nameof(openWrite));
    }

    /// <summary>Opens the file for reading.</summary>
    public Stream OpenRead() => _openRead();

    /// <summary>Opens the file for writing (creates or truncates).</summary>
    public Stream OpenWrite() => _openWrite();
}

/// <summary>
/// Services only a host (window system) can provide: windows for forms, modal loops and native
/// dialogs. The SDL host installs itself at startup; tests can install a fake.
/// </summary>
public interface IPlatformServices
{
    /// <summary>Shows a form in its own window.</summary>
    void ShowForm(Form form);

    /// <summary>Shows a form modally and returns once it has closed.</summary>
    void RunModal(Form form);

    /// <summary>Closes a form's window.</summary>
    void CloseForm(Form form);

    /// <summary>Processes events (all windows) until <paramref name="done"/> returns true.</summary>
    void ProcessEventsUntil(Func<bool> done);

    /// <summary>Shows a native file or folder dialog; <paramref name="done"/> receives the chosen paths, or null if cancelled.</summary>
    void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done);

    /// <summary>Posts an action to run on the UI thread (asynchronously).</summary>
    void PostToUiThread(Action action);

    /// <summary>Shows a form modally without blocking, calling <paramref name="onClosed"/> when it closes. Non-blocking version of RunModal.</summary>
    void ShowModal(Form form, Action onClosed);

    /// <summary>Opens a URL in the user's default browser or mail client. Returns false if the host can't. Hosts that don't override it return false.</summary>
    bool OpenUrl(string url) => false;

    /// <summary>
    /// The monitors attached now, the primary one first. Coordinates are screen coordinates in the host's
    /// window units (the same as <see cref="Form.RestoredBounds"/>). Hosts without monitors return an empty list,
    /// which is also what hosts written before this member existed return.
    /// </summary>
    IReadOnlyList<MonitorInfo> GetMonitors() => [];

    /// <summary>The monitor <paramref name="form"/>'s window is on (the one with most of it), or null if it has no window or the host can't tell.</summary>
    MonitorInfo? GetMonitorOf(Form form) => null;

    /// <summary>
    /// Moves and resizes an open form's window so its normal (restored) client area is <paramref name="bounds"/>, in
    /// screen coordinates (the units of <see cref="Form.RestoredBounds"/>). A maximised or full-screen window is
    /// restored, moved and then given its state back, so it ends up maximised on the monitor it was moved to.
    /// Returns false if the form has no window or the host can't move windows.
    /// </summary>
    bool MoveForm(Form form, Rect bounds) => false;
}

/// <summary>Whether a form's window is normal, maximised, minimised or full screen.</summary>
public enum WindowState
{
    Normal,
    Maximized,
    Minimized,
    /// <summary>Covers the whole monitor it is on, without a frame (the desktop's resolution is kept).</summary>
    FullScreen,
}

/// <summary>
/// A form window's frame (Delphi's <c>BorderStyle</c> for forms). Whether the user can resize the window also
/// depends on <see cref="Form.Resizable"/>: only <see cref="Sizable"/> and <see cref="Tool"/> windows can be.
/// </summary>
public enum FormBorderStyle
{
    /// <summary>A normal window frame with minimise and maximise buttons, resizable when <see cref="Form.Resizable"/>.</summary>
    Sizable,
    /// <summary>A normal window frame that can't be resized by its border.</summary>
    Single,
    /// <summary>A dialog frame: no minimise or maximise button, not resizable.</summary>
    Dialog,
    /// <summary>A tool (utility) window: a small frame and no task bar button; resizable when <see cref="Form.Resizable"/>. Read when the window is created.</summary>
    Tool,
    /// <summary>No frame or title bar at all.</summary>
    None,
}

/// <summary>
/// A monitor, as the host reports it (see <see cref="Screen"/>). <see cref="Bounds"/> and
/// <see cref="WorkArea"/> are screen coordinates in the host's window units, the units of
/// <see cref="Form.RestoredBounds"/>; <see cref="Scale"/> is the UI scale forms on it load at.
/// </summary>
/// <param name="Id">The host's id for the monitor; stable only while the application runs.</param>
/// <param name="Name">The monitor's name (e.g. its model); use it, with <see cref="Bounds"/>, to find a monitor again in a later run.</param>
/// <param name="Bounds">The whole monitor.</param>
/// <param name="WorkArea">The part not covered by task bars, docks and menu bars.</param>
/// <param name="Scale">The monitor's UI scale (1 = 100 %).</param>
/// <param name="IsPrimary">Whether this is the primary monitor.</param>
public sealed record MonitorInfo(uint Id, string Name, Rect Bounds, Rect WorkArea, double Scale, bool IsPrimary)
{
    public override string ToString() => $"{Name} ({Bounds.Width} x {Bounds.Height} at {Bounds.X}, {Bounds.Y}, {Scale * 100:0} %)";
}

/// <summary>Where a form's window first appears.</summary>
public enum FormStartPosition
{
    /// <summary>The OS decides.</summary>
    Default,
    /// <summary>Centred on the screen the window opens on.</summary>
    CenterScreen,
    /// <summary>Centred on <see cref="Form.Owner"/> (falls back to the screen when there is none).</summary>
    CenterOwner,
    /// <summary>At <see cref="Control.Left"/>, <see cref="Control.Top"/>.</summary>
    Manual,
}

public enum FileDialogKind { Open, Save, Folder }

public static class PlatformHost
{
    public static IPlatformServices? Current { get; set; }

    internal static IPlatformServices Require() =>
        Current ?? throw new InvalidOperationException("No platform host is running (call SdlHost.Initialize() first).");
}

/// <summary>How a modal form was closed.</summary>
public enum ModalResult { None, Ok, Cancel, Abort, Retry, Ignore, Yes, No, Close }

public sealed class CloseQueryEventArgs : EventArgs
{
    /// <summary>Set to false to keep the form open.</summary>
    public bool CanClose { get; set; } = true;
}

public delegate void CloseQueryEvent(Control sender, CloseQueryEventArgs e);
