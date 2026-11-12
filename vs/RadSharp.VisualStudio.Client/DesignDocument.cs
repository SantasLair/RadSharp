using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using RadSharp.DesignHost.Protocol;

namespace RadSharp.VisualStudio.Client;

/// <summary>Editor command names for <see cref="DesignDocument.Command"/>.</summary>
public static class DesignCommands
{
    public const string Undo = "Undo";
    public const string Redo = "Redo";
    public const string Cut = "Cut";
    public const string Copy = "Copy";
    public const string Paste = "Paste";
    public const string Delete = "Delete";
    public const string SelectAll = "SelectAll";
}

/// <summary>A window the host announced with WindowOpened. Position and size are device pixels.</summary>
public sealed class RemoteWindowInfo
{
    internal RemoteWindowInfo(WindowOpened m)
    {
        Id = m.WindowId;
        DocId = m.DocId;
        Role = m.Role;
        OwnerWindowId = m.OwnerWindowId;
        X = m.X;
        Y = m.Y;
        Width = m.Width;
        Height = m.Height;
        Title = m.Title ?? "";
        Modal = m.Modal;
    }

    public int Id { get; }
    public int DocId { get; }
    public WindowRole Role { get; }

    /// <summary>The owner window of a popup or dialog; 0 for panes.</summary>
    public int OwnerWindowId { get; }

    /// <summary>For popups and dialogs: device pixels relative to the owner window's top-left.</summary>
    public int X { get; internal set; }
    public int Y { get; internal set; }
    public int Width { get; internal set; }
    public int Height { get; internal set; }
    public string Title { get; }
    public bool Modal { get; }

    /// <summary>True for <see cref="WindowRole.Popup"/> and <see cref="WindowRole.Dialog"/>.</summary>
    public bool IsTransient => Role == WindowRole.Popup || Role == WindowRole.Dialog;

    /// <summary>False after WindowClosed (or after the host connection was replaced).</summary>
    public bool IsOpen { get; internal set; } = true;

    public override string ToString() => Role + " #" + Id + " (doc " + DocId + ", " + Width + "x" + Height + ")";
}

/// <summary>A form file open in the host. Create with <see cref="DesignSession.OpenDocument"/>. Events are raised on
/// the UI thread.</summary>
public sealed class DesignDocument
{
    private readonly List<RemoteWindowInfo> _windows = new List<RemoteWindowInfo>();
    private readonly List<PendingSave> _pendingSaves = new List<PendingSave>();

    /// <summary>How long <see cref="SaveAsync"/> waits for the host's answer before failing. The host always answers, so this
    /// only guards against a lost reply wedging later saves.</summary>
    public static TimeSpan SaveTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private sealed class PendingSave
    {
        public PendingSave(string path, TaskCompletionSource<DocumentSavedEventArgs> tcs, DispatcherTimer? timer)
        {
            Path = path;
            Tcs = tcs;
            Timer = timer;
        }

        public string Path { get; }
        public TaskCompletionSource<DocumentSavedEventArgs> Tcs { get; }
        public DispatcherTimer? Timer { get; }
    }

    internal DesignDocument(DesignSession session, int id, string path)
    {
        Session = session;
        Id = id;
        Path = path;
    }

    public DesignSession Session { get; }

    /// <summary>The protocol document id.</summary>
    public int Id { get; }

    /// <summary>The file this document was opened from (or last successfully saved to).</summary>
    public string Path { get; private set; }

    public bool IsDirty { get; private set; }
    public bool CanUndo { get; private set; }
    public bool CanRedo { get; private set; }
    public bool HasSelection { get; private set; }
    public bool IsClosed { get; private set; }

    /// <summary>The open windows of this document: its panes and any popups or dialogs.</summary>
    public IReadOnlyList<RemoteWindowInfo> Windows => _windows;

    /// <summary>The open pane with <paramref name="role"/>, if the host announced one.</summary>
    public RemoteWindowInfo? GetWindow(WindowRole role) => _windows.FirstOrDefault(w => w.Role == role && w.IsOpen);

    /// <summary>Dirty / undo / redo / selection changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>The host answered a save.</summary>
    public event EventHandler<DocumentSavedEventArgs>? Saved;

    /// <summary>The host wants a source location shown (double-click on a control or event).</summary>
    public event EventHandler<NavigateToCodeEventArgs>? NavigateToCode;

    /// <summary>An error the user should see.</summary>
    public event EventHandler<HostErrorEventArgs>? Error;

    /// <summary>The host announced a window for this document. Panes arrive after <see cref="DesignSession.OpenDocument"/>
    /// and again after <see cref="DesignSession.RestartAsync"/>; attach a <see cref="RemoteWindowView"/> to show one.</summary>
    public event EventHandler<RemoteWindowEventArgs>? WindowOpened;

    /// <summary>A window of this document closed.</summary>
    public event EventHandler<RemoteWindowEventArgs>? WindowClosed;

    /// <summary>Tells the host this document's tab is active (the Inspector and Palette follow it).</summary>
    public void Activate() => Session.ActivateDocument(this);

    /// <summary>The file changed on disk; the host reloads it.</summary>
    public void Reload() => Session.Send(new ReloadDocument { DocId = Id });

    /// <summary>Runs an editor command (<see cref="DesignCommands"/>).</summary>
    public void Command(string name) => Session.Send(new Command { DocId = Id, Name = name });

    /// <summary>Saves to <paramref name="path"/> (default: <see cref="Path"/>). The answer arrives as <see cref="Saved"/>.</summary>
    public void Save(string? path = null) => SaveAsync(path);

    /// <summary>Saves and completes when the host answers. Await it on the UI thread; do not block on it there, since
    /// the answer is delivered through the dispatcher.</summary>
    public Task<DocumentSavedEventArgs> SaveAsync(string? path = null)
    {
        var tcs = new TaskCompletionSource<DocumentSavedEventArgs>();
        string target = string.IsNullOrEmpty(path) ? Path : path!;
        if (IsClosed || !Session.Send(new SaveDocument { DocId = Id, Path = target }))
        {
            tcs.SetResult(new DocumentSavedEventArgs(this, false, IsClosed ? "The document is closed." : "The design host is not running."));
            return tcs.Task;
        }
        DispatcherTimer? timer = null;
        if (SaveTimeout > TimeSpan.Zero)
            timer = new DispatcherTimer(DispatcherPriority.Normal, Session.Dispatcher) { Interval = SaveTimeout };
        var pending = new PendingSave(target, tcs, timer);
        if (timer != null)
            timer.Tick += (s, e) => OnSaveTimedOut(pending);
        _pendingSaves.Add(pending);
        timer?.Start();
        return tcs.Task;
    }

    /// <summary>Closes the document in the host.</summary>
    public void Close() => Session.CloseDocument(this);

    internal void OnState(DocumentState m)
    {
        IsDirty = m.Dirty;
        CanUndo = m.CanUndo;
        CanRedo = m.CanRedo;
        HasSelection = m.HasSelection;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void OnSaved(DocumentSavedEventArgs e)
    {
        // Replies come in the order the saves were sent. The path only changes once the host says the save worked.
        PendingSave? pending = null;
        if (_pendingSaves.Count > 0)
        {
            pending = _pendingSaves[0];
            _pendingSaves.RemoveAt(0);
            pending.Timer?.Stop();
            if (e.Ok) Path = pending.Path;
        }
        try { Saved?.Invoke(this, e); }
        finally { pending?.Tcs.TrySetResult(e); }
    }

    private void OnSaveTimedOut(PendingSave pending)
    {
        pending.Timer?.Stop();
        // The reply is lost or very late; which later reply belongs to which save is now unknowable, so fail everything pending.
        FailPendingSaves("The design host did not answer the save in time.");
    }

    private void FailPendingSaves(string reason)
    {
        var list = _pendingSaves.ToArray();
        _pendingSaves.Clear();
        foreach (var p in list)
        {
            p.Timer?.Stop();
            p.Tcs.TrySetResult(new DocumentSavedEventArgs(this, false, reason));
        }
    }

    internal void OnNavigate(NavigateToCodeEventArgs e) => NavigateToCode?.Invoke(this, e);
    internal void OnError(HostErrorEventArgs e) => Error?.Invoke(this, e);

    internal void OnWindowOpened(RemoteWindowInfo w)
    {
        _windows.Add(w);
        WindowOpened?.Invoke(this, new RemoteWindowEventArgs(w));
    }

    internal void OnWindowClosed(RemoteWindowInfo w)
    {
        _windows.Remove(w);
        WindowClosed?.Invoke(this, new RemoteWindowEventArgs(w));
    }

    internal void OnClosed()
    {
        IsClosed = true;
        FailPendingSaves("The document is closed.");
    }

    /// <summary>The host went away: drop its windows and fail pending saves.</summary>
    internal void OnConnectionLost(string reason)
    {
        foreach (var w in _windows) w.IsOpen = false;
        _windows.Clear();
        FailPendingSaves(reason);
    }
}

/// <summary>Answer to a save.</summary>
public sealed class DocumentSavedEventArgs : EventArgs
{
    public DocumentSavedEventArgs(DesignDocument document, bool ok, string error)
    {
        Document = document;
        Ok = ok;
        Error = error ?? "";
    }

    public DesignDocument Document { get; }
    public bool Ok { get; }
    public string Error { get; }
}

/// <summary>A request to show <see cref="File"/> at <see cref="Line"/> in the code editor.</summary>
public sealed class NavigateToCodeEventArgs : EventArgs
{
    public NavigateToCodeEventArgs(DesignDocument? document, string file, int line)
    {
        Document = document;
        File = file ?? "";
        Line = line;
    }

    public DesignDocument? Document { get; }
    public string File { get; }

    /// <summary>The line as the host sent it (RadSharp's <c>EnsureHandler</c> line).</summary>
    public int Line { get; }
}

/// <summary>An error from the host. <see cref="Document"/> is null when it is not tied to a document.</summary>
public sealed class HostErrorEventArgs : EventArgs
{
    public HostErrorEventArgs(DesignDocument? document, string text)
    {
        Document = document;
        Text = text ?? "";
    }

    public DesignDocument? Document { get; }
    public string Text { get; }
}

/// <summary>A window event.</summary>
public sealed class RemoteWindowEventArgs : EventArgs
{
    public RemoteWindowEventArgs(RemoteWindowInfo window) => Window = window;
    public RemoteWindowInfo Window { get; }
}

/// <summary>A log line: host stderr/stdout (level 1), a protocol Log message, or a client diagnostic.</summary>
public sealed class HostLogEventArgs : EventArgs
{
    public HostLogEventArgs(int level, string text)
    {
        Level = level;
        Text = text ?? "";
    }

    /// <summary>0 debug, 1 info, 2 warning, 3 error.</summary>
    public int Level { get; }
    public string Text { get; }
}
