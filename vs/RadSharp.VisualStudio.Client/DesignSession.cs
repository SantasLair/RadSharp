using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using RadSharp.DesignHost.Protocol;

namespace RadSharp.VisualStudio.Client;

/// <summary>
/// The client side of one design host: documents, the window registry, and routing of host messages to documents,
/// <see cref="RemoteWindowView"/>s and the <see cref="Presenter"/> (popups and dialogs). Create and use it on the UI
/// thread; every event is raised there.
/// </summary>
public sealed class DesignSession : IDisposable
{
    private readonly Dictionary<int, DesignDocument> _docs = new Dictionary<int, DesignDocument>();
    private readonly Dictionary<int, RemoteWindowInfo> _windows = new Dictionary<int, RemoteWindowInfo>();
    private readonly Dictionary<int, RemoteWindowView> _views = new Dictionary<int, RemoteWindowView>();
    private readonly string? _hostPath;
    private readonly string _clientName;
    private DesignHostConnection _connection;
    private int _nextDocId = 1;
    private int _activeDocId;
    private bool _disposed;
    private Task? _restart;

    /// <summary>Wraps a connection that has completed its handshake, and starts its reader.</summary>
    public DesignSession(DesignHostConnection connection) : this(connection, null, DesignHostConnection.DefaultClientName) { }

    private DesignSession(DesignHostConnection connection, string? hostPath, string clientName)
    {
        _hostPath = hostPath;
        _clientName = clientName;
        Dispatcher = Dispatcher.CurrentDispatcher;
        Presenter = new RemoteWindowPresenter();
        CodeBuffers = new FileCodeBufferProvider(this);
        _connection = connection;
        Hook(connection);
    }

    /// <summary>Starts the host at <paramref name="hostPath"/> and connects. Call on the UI thread. Sessions created this way
    /// support <see cref="RestartAsync"/>.</summary>
    public static async Task<DesignSession> StartAsync(string hostPath, string clientName = DesignHostConnection.DefaultClientName)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var conn = await DesignHostConnection.LaunchAsync(hostPath, dispatcher, null, clientName);
        return new DesignSession(conn, hostPath, clientName);
    }

    /// <summary>The UI dispatcher this session lives on.</summary>
    public Dispatcher Dispatcher { get; }

    /// <summary>The current host connection (replaced by <see cref="RestartAsync"/>).</summary>
    public DesignHostConnection Connection => _connection;

    /// <summary>True while the host is connected.</summary>
    public bool IsConnected => !_disposed && _connection.IsConnected;

    /// <summary>True when <see cref="RestartAsync"/> can relaunch the host.</summary>
    public bool CanRestart => _hostPath != null && !_disposed;

    /// <summary>Shows popups and dialogs. Replace it (or subclass it) to change how they are hosted, e.g. to parent dialogs
    /// to Visual Studio's main window.</summary>
    public RemoteWindowPresenter Presenter { get; set; }

    /// <summary>Answers the host's code requests and applies its code edits. Defaults to <see cref="FileCodeBufferProvider"/>
    /// (disk only); Visual Studio replaces it with one over its editor buffers.</summary>
    public ICodeBufferProvider CodeBuffers { get; set; }

    /// <summary>Open documents.</summary>
    public IEnumerable<DesignDocument> Documents => _docs.Values;

    /// <summary>Every window the host announced and has not closed.</summary>
    public IEnumerable<RemoteWindowInfo> Windows => _windows.Values;

    public event EventHandler<DesignDocument>? DocumentStateChanged;
    public event EventHandler<DocumentSavedEventArgs>? DocumentSaved;
    public event EventHandler<NavigateToCodeEventArgs>? NavigateToCode;
    public event EventHandler<HostErrorEventArgs>? Error;
    public event EventHandler<RemoteWindowEventArgs>? WindowOpened;
    public event EventHandler<RemoteWindowEventArgs>? WindowClosed;
    public event EventHandler<RemoteWindowEventArgs>? WindowMoved;
    public event EventHandler<HostLogEventArgs>? Log;

    /// <summary>The host crashed or the pipe broke. Show an error with a restart affordance (<see cref="RestartAsync"/>).</summary>
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <summary>Opens a form file. Its panes arrive as <see cref="DesignDocument.WindowOpened"/>.</summary>
    public DesignDocument OpenDocument(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        ThrowIfDisposed();
        var doc = new DesignDocument(this, _nextDocId++, path);
        _docs.Add(doc.Id, doc);
        Send(new OpenDocument { DocId = doc.Id, Path = path });
        return doc;
    }

    /// <summary>The document with <paramref name="docId"/>, if open.</summary>
    public DesignDocument? GetDocument(int docId) => _docs.TryGetValue(docId, out var d) ? d : null;

    /// <summary>The window with <paramref name="windowId"/>, if open.</summary>
    public RemoteWindowInfo? GetWindow(int windowId) => _windows.TryGetValue(windowId, out var w) ? w : null;

    /// <summary>The view currently attached to <paramref name="windowId"/>.</summary>
    public RemoteWindowView? GetView(int windowId) => _views.TryGetValue(windowId, out var v) ? v : null;

    /// <summary>Sends a raw message. Returns false when the host is not connected.</summary>
    public bool Send(Message message) => !_disposed && _connection.Send(message);

    internal void ActivateDocument(DesignDocument doc)
    {
        _activeDocId = doc.Id;
        Send(new ActivateDocument { DocId = doc.Id });
    }

    internal void CloseDocument(DesignDocument doc)
    {
        if (doc.IsClosed) return;
        Send(new CloseDocument { DocId = doc.Id });
        doc.OnClosed();
        _docs.Remove(doc.Id);
        if (_activeDocId == doc.Id) _activeDocId = 0;
        foreach (var w in _windows.Values.Where(w => w.DocId == doc.Id).ToList()) CloseWindowLocally(w);
    }

    /// <summary>Asks the host to close a popup or dialog (the user dismissed it on the client side).</summary>
    public void RequestCloseWindow(int windowId) => Send(new CloseWindow { WindowId = windowId });

    internal void RegisterView(RemoteWindowView view, int windowId)
    {
        if (_views.TryGetValue(windowId, out var old) && !ReferenceEquals(old, view)) old.DetachInternal();
        _views[windowId] = view;
    }

    internal void UnregisterView(RemoteWindowView view, int windowId)
    {
        if (_views.TryGetValue(windowId, out var v) && ReferenceEquals(v, view)) _views.Remove(windowId);
    }

    /// <summary>
    /// Relaunches the host after a crash and reopens every document under the same ids. Views are detached; the documents
    /// raise <see cref="DesignDocument.WindowOpened"/> again for their new panes, so handlers that attach views on that
    /// event recover by themselves. Unsaved changes in the old host are lost.
    /// </summary>
    public Task RestartAsync()
    {
        ThrowIfDisposed();
        if (_hostPath == null) throw new InvalidOperationException("This session was not started from a host path.");
        if (_restart != null) return _restart; // one restart at a time: concurrent callers share it
        var task = RestartCoreAsync();
        if (!task.IsCompleted) _restart = task; // (a task that already finished has cleared the field itself)
        return task;
    }

    private async Task RestartCoreAsync()
    {
        try
        {
            DropConnectionState("The design host was restarted.");
            Unhook(_connection);
            _connection.Dispose();
            var conn = await DesignHostConnection.LaunchAsync(_hostPath!, Dispatcher, null, _clientName);
            if (_disposed)
            {
                conn.Dispose();
                return;
            }
            _connection = conn;
            Hook(conn);
            bool ok = true;
            foreach (var doc in _docs.Values.ToList()) ok &= Send(new OpenDocument { DocId = doc.Id, Path = doc.Path });
            if (_activeDocId != 0) ok &= Send(new ActivateDocument { DocId = _activeDocId });
            if (!ok) throw new IOException("The design host disconnected while its documents were being reopened.");
        }
        finally
        {
            _restart = null;
        }
    }

    private void Hook(DesignHostConnection c)
    {
        c.MessageReceived += OnMessage;
        c.Disconnected += OnDisconnected;
        c.OutputReceived += OnOutput;
        c.StartReading();
    }

    private void Unhook(DesignHostConnection c)
    {
        c.MessageReceived -= OnMessage;
        c.Disconnected -= OnDisconnected;
        c.OutputReceived -= OnOutput;
    }

    private void OnOutput(object? sender, string line) => Log?.Invoke(this, new HostLogEventArgs(1, line));

    private void OnDisconnected(object? sender, DisconnectedEventArgs e)
    {
        if (!ReferenceEquals(sender, _connection)) return;
        DropConnectionState(e.Reason);
        Log?.Invoke(this, new HostLogEventArgs(3, e.Reason));
        Disconnected?.Invoke(this, e);
    }

    /// <summary>Forgets every host window, detaches views, closes popups and dialogs, and fails pending saves.</summary>
    private void DropConnectionState(string reason)
    {
        foreach (var w in _windows.Values.ToList())
        {
            w.IsOpen = false;
            if (w.IsTransient) SafePresenter(p => p.Close(this, w));
        }
        _windows.Clear();
        foreach (var v in _views.Values.ToList()) v.DetachInternal();
        _views.Clear();
        foreach (var d in _docs.Values) d.OnConnectionLost(reason);
    }

    private void OnMessage(object? sender, MessageEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _connection)) return;
        try
        {
            Dispatch(e.Message);
        }
#pragma warning disable CA1031 // one bad message (or a throwing subscriber) must not take down the UI thread or the message queue
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log?.Invoke(this, new HostLogEventArgs(3, "Error handling " + e.Message.GetType().Name + " from the design host: " + ex.Message));
        }
    }

    private void Dispatch(Message message)
    {
        switch (message)
        {
            case Frame f:
                if (_views.TryGetValue(f.WindowId, out var fv)) fv.ApplyFrame(f);
                break;
            case DesignHost.Protocol.Cursor c:
                if (_views.TryGetValue(c.WindowId, out var cv)) cv.ApplyCursor(c.CursorName);
                break;
            case WindowOpened wo:
                OnWindowOpened(wo);
                break;
            case WindowMoved wm:
                if (_windows.TryGetValue(wm.WindowId, out var mw))
                {
                    mw.X = wm.X;
                    mw.Y = wm.Y;
                    mw.Width = wm.Width;
                    mw.Height = wm.Height;
                    if (mw.IsTransient) SafePresenter(p => p.Move(this, mw));
                    WindowMoved?.Invoke(this, new RemoteWindowEventArgs(mw));
                }
                break;
            case WindowClosed wc:
                if (_windows.TryGetValue(wc.WindowId, out var cw)) CloseWindowLocally(cw);
                break;
            case DocumentState ds:
                if (_docs.TryGetValue(ds.DocId, out var sd))
                {
                    sd.OnState(ds);
                    DocumentStateChanged?.Invoke(this, sd);
                }
                break;
            case DocumentSaved sv:
                if (_docs.TryGetValue(sv.DocId, out var vd))
                {
                    var args = new DocumentSavedEventArgs(vd, sv.Ok, sv.Error);
                    vd.OnSaved(args);
                    DocumentSaved?.Invoke(this, args);
                }
                break;
            case NavigateToCode nav:
                RaiseNavigateToCode(GetDocument(nav.DocId), nav.File, nav.Line);
                break;
            case CodeRequest req:
                AnswerCodeRequest(req);
                break;
            case ApplyCodeEdit edit:
                OnApplyCodeEdit(edit);
                break;
            case Error err:
            {
                var doc = err.DocId == 0 ? null : GetDocument(err.DocId);
                var args = new HostErrorEventArgs(doc, err.Text);
                doc?.OnError(args);
                Error?.Invoke(this, args);
                break;
            }
            case DesignHost.Protocol.Log log:
                Log?.Invoke(this, new HostLogEventArgs(log.Level, log.Text));
                break;
            case HelloAck:
                break;
            default:
                Log?.Invoke(this, new HostLogEventArgs(2, "Unexpected message from the design host: " + message.GetType().Name));
                break;
        }
    }

    /// <summary>Raises <see cref="NavigateToCode"/> on the document and the session (code-buffer providers call this after an edit).</summary>
    public void RaiseNavigateToCode(DesignDocument? document, string file, int line)
    {
        var args = new NavigateToCodeEventArgs(document, file, line);
        document?.OnNavigate(args);
        NavigateToCode?.Invoke(this, args);
    }

    private void AnswerCodeRequest(CodeRequest req)
    {
        // Always answer: the host waits for this. Any failure means "read it from disk".
        var response = new CodeResponse { RequestId = req.RequestId };
        try
        {
            if (CodeBuffers.TryGetText(req.File, out var text) && text != null)
            {
                response.Found = true;
                response.Text = text;
            }
        }
#pragma warning disable CA1031 // a provider failure must still produce an answer
        catch (Exception ex)
#pragma warning restore CA1031
        {
            response.Found = false;
            response.Text = "";
            Log?.Invoke(this, new HostLogEventArgs(2, "Could not read the editor buffer for " + req.File + ": " + ex.Message));
        }
        Send(response);
    }

    private void OnApplyCodeEdit(ApplyCodeEdit edit)
    {
        var doc = GetDocument(edit.DocId);
        try
        {
            CodeBuffers.ApplyEdit(doc, edit.File, edit.Text, edit.Line, edit.BaseHash);
        }
        catch (CodeEditConflictException ex)
        {
            var args = new HostErrorEventArgs(doc, ex.Message);
            doc?.OnError(args);
            Error?.Invoke(this, args);
        }
#pragma warning disable CA1031 // report any provider failure to the user instead of tearing down the message loop
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var args = new HostErrorEventArgs(doc, "Could not update " + edit.File + ": " + ex.Message);
            doc?.OnError(args);
            Error?.Invoke(this, args);
        }
    }

    private void OnWindowOpened(WindowOpened m)
    {
        if (_windows.TryGetValue(m.WindowId, out var existing)) CloseWindowLocally(existing);
        var w = new RemoteWindowInfo(m);
        _windows[w.Id] = w;
        GetDocument(w.DocId)?.OnWindowOpened(w);
        WindowOpened?.Invoke(this, new RemoteWindowEventArgs(w));
        if (w.IsTransient && w.IsOpen)
        {
            var owner = w.OwnerWindowId != 0 ? GetView(w.OwnerWindowId) : null;
            SafePresenter(p => p.Show(this, w, owner), closeOnFailure: w);
        }
    }

    private void CloseWindowLocally(RemoteWindowInfo w)
    {
        _windows.Remove(w.Id);
        w.IsOpen = false;
        if (w.IsTransient) SafePresenter(p => p.Close(this, w));
        if (_views.TryGetValue(w.Id, out var v)) v.DetachInternal();
        _views.Remove(w.Id);
        GetDocument(w.DocId)?.OnWindowClosed(w);
        WindowClosed?.Invoke(this, new RemoteWindowEventArgs(w));
    }

    /// <summary>Runs a presenter call. Any failure is logged, never thrown (it would escape on the UI thread); when a dialog or
    /// popup could not be shown, the host is told to close it so it does not wait on a window nobody can see.</summary>
    private void SafePresenter(Action<RemoteWindowPresenter> a, RemoteWindowInfo? closeOnFailure = null)
    {
        try { a(Presenter); }
#pragma warning disable CA1031 // presenters are user-replaceable and show WPF windows: anything can go wrong
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log?.Invoke(this, new HostLogEventArgs(3, "Could not show a design host window: " + ex.Message));
            if (closeOnFailure != null) RequestCloseWindow(closeOnFailure.Id);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DesignSession));
    }

    /// <summary>Closes popups and dialogs, detaches views and shuts the host down.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        DropConnectionState("The session was closed.");
        _disposed = true;
        Unhook(_connection);
        _connection.Dispose();
    }
}
