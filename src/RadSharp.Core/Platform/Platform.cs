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
    void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<string[]?> done);
}

public enum FileDialogKind { Open, Save, Folder }

public static class PlatformHost
{
    public static IPlatformServices? Current { get; set; }

    internal static IPlatformServices Require() =>
        Current ?? throw new InvalidOperationException("No platform host is running (call Application.Initialize() first).");
}

/// <summary>How a modal form was closed.</summary>
public enum ModalResult { None, Ok, Cancel, Abort, Retry, Ignore, Yes, No, Close }

public sealed class CloseQueryEventArgs : EventArgs
{
    /// <summary>Set to false to keep the form open.</summary>
    public bool CanClose { get; set; } = true;
}

public delegate void CloseQueryEvent(Control sender, CloseQueryEventArgs e);
