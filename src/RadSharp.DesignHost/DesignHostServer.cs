using System.Collections.Concurrent;
using System.Diagnostics;
using RadSharp.Design;
using RadSharp.DesignHost.Protocol;
using RadSharp.Diagnostics;
using RadSharp.Threading;


namespace RadSharp.DesignHost;

/// <summary>
/// The headless design host over one duplex stream (a named pipe in production, an in-memory pair in tests).
/// A reader thread queues incoming messages; one UI thread (the one calling <see cref="Run"/>) drains the queue,
/// ticks every form about every 16 ms, renders each attached window's dirty rectangle and sends it as a
/// <c>Frame</c>. A writer thread sends outgoing messages so the UI thread never blocks on the pipe.
/// </summary>
public sealed class DesignHostServer
{
    const int TickMs = 16;

    /// <summary>A queued message and what to run once the writer is done with it (written or dropped).</summary>
    readonly record struct Outgoing(Message Message, Action? Done);

    readonly Stream _stream;
    readonly ConcurrentQueue<Action> _queue = new();
    readonly AutoResetEvent _signal = new(false);
    readonly BlockingCollection<Outgoing> _outbox = [];
    readonly Dictionary<Protocol.MouseButton, HostWindow> _mouseCapture = [];
    readonly Dictionary<int, HostWindow> _windows = [];
    readonly Dictionary<Form, HostWindow> _byForm = [];
    readonly Dictionary<int, HostDocument> _docs = [];
    readonly List<HostWindow> _modalStack = [];
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly HeadlessPlatform _platform;
    readonly Dictionary<int, PendingCode> _pendingCode = [];
    int _nextWindowId = 1, _activeDoc, _nextRequestId = 1;
    long _nextTick;
    double _scale = 1;
    volatile bool _stopping;

    /// <summary>Creates a host that talks over <paramref name="stream"/> (readable and writable).</summary>
    public DesignHostServer(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _platform = new HeadlessPlatform(this);
    }

    /// <summary>The host's version string sent in HelloAck.</summary>
    public static string HostVersion =>
        typeof(DesignHostServer).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Raised (on any thread) for each diagnostic line; the default writes to stderr.</summary>
    public Action<int, string> LogSink { get; set; } = (level, text) =>
    {
        lock (Console.Error) Console.Error.WriteLine($"[{LevelName(level)}] {text}");
    };

    static string LevelName(int level) => level switch { 0 => "debug", 1 => "info", 2 => "warning", _ => "error" };

    // =====================================================================
    // Running
    // =====================================================================

    /// <summary>
    /// Runs the host on the calling thread (which becomes the UI thread) until the stream closes or
    /// <see cref="Stop"/> is called.
    /// </summary>
    public void Run()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new UiSynchronizationContext(Post));
        var previousPlatform = PlatformHost.Current;
        PlatformHost.Current = _platform;
        Application.ThreadException += OnThreadException;
        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "RADSharp design host reader" };
        var writer = new Thread(WriteLoop) { IsBackground = true, Name = "RADSharp design host writer" };
        reader.Start();
        writer.Start();
        try
        {
            Pump(() => false);
        }
        finally
        {
            foreach (var w in _windows.Values) w.Dispose();
            _windows.Clear();
            _byForm.Clear();
            _docs.Clear();
            _pendingCode.Clear();
            _outbox.CompleteAdding();
            writer.Join(TimeSpan.FromSeconds(2));
            Application.ThreadException -= OnThreadException;
            if (PlatformHost.Current == _platform) PlatformHost.Current = previousPlatform;
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    /// <summary>Runs the host on a new background thread (for tests and embedding).</summary>
    public Thread Start()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "RADSharp design host UI" };
        thread.Start();
        return thread;
    }

    /// <summary>Asks the UI loop to exit (from any thread).</summary>
    public void Stop()
    {
        _stopping = true;
        _signal.Set();
    }

    /// <summary>Queues an action for the UI thread (from any thread).</summary>
    internal void Post(Action action)
    {
        _queue.Enqueue(action);
        _signal.Set();
    }

    /// <summary>Runs <paramref name="func"/> on the UI thread and returns its result (for tests).</summary>
    internal Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    /// <summary>
    /// The UI loop: runs queued actions, ticks the forms, renders and sends dirty rectangles, until
    /// <paramref name="done"/> returns true or the host stops. Modal loops call it nested.
    /// </summary>
    internal void Pump(Func<bool> done)
    {
        while (!_stopping && !done())
        {
            int wait = (int)Math.Clamp(_nextTick - _clock.ElapsedMilliseconds, 0, TickMs);
            if (_queue.IsEmpty && wait > 0) _signal.WaitOne(wait);
            while (!_stopping && _queue.TryDequeue(out var action))
            {
                Guard.Run(action);
                if (done()) break;
            }
            if (_stopping) break;
            long now = _clock.ElapsedMilliseconds;
            if (now >= _nextTick)
            {
                _nextTick = now + TickMs;
                foreach (var w in _windows.Values.ToList()) Guard.Run(() => w.Form.ProcessTick(now));
            }
            Guard.Run(Flush);
        }
    }

    /// <summary>Sends what changed: document states, cursors and the dirty rectangle of every attached window.</summary>
    void Flush()
    {
        ExpireCodeRequests();
        long now = _clock.ElapsedMilliseconds;
        foreach (var doc in _docs.Values)
        {
            if (!doc.StateDirty && !doc.ModifiedStale) continue;
            doc.StateDirty = false;
            bool dirty = doc.SentState?.Dirty ?? false;
            // Modified serialises the whole form: only after a real change, and at most once per tick.
            if (doc.ModifiedStale && (doc.SentState == null || now - doc.LastModifiedCheck >= TickMs))
            {
                dirty = doc.Doc.Modified;
                doc.ModifiedStale = false;
                doc.LastModifiedCheck = now;
                doc.ModifiedEvaluations++;
            }
            var state = (dirty, doc.Doc.CanUndo, doc.Doc.CanRedo, doc.Doc.Selection.Count > 0);
            if (doc.SentState == state) continue;
            doc.SentState = state;
            Send(new DocumentState { DocId = doc.DocId, Dirty = state.Item1, CanUndo = state.Item2, CanRedo = state.Item3, HasSelection = state.Item4 });
        }
        foreach (var w in _windows.Values.ToList())
        {
            // Dialogs and popups resize and move themselves: follow them.
            var geometry = w.TrackFormGeometry();
            if (geometry.Warning != null) Log(2, geometry.Warning);
            if (geometry.Changed)
                Send(new WindowMoved { WindowId = w.Id, X = w.X, Y = w.Y, Width = w.Width, Height = w.Height });
            if (!w.Attached) continue;
            var cursor = w.Form.CurrentCursor;
            if (cursor != w.SentCursor)
            {
                w.SentCursor = cursor;
                Send(new Protocol.Cursor { WindowId = w.Id, CursorName = cursor.ToString() });
            }
            // The back buffer is always brought up to date, but a new frame is only cut when the writer has
            // finished the previous one: what changed meanwhile merges into one rectangle (backpressure).
            w.Render();
            if (!w.FrameInFlight && w.TakePending() is { Count: > 0 } frames)
            {
                w.FrameInFlight = true;
                for (int i = 0; i < frames.Count; i++)
                    Send(frames[i], i == frames.Count - 1 ? () => w.FrameInFlight = false : null);
            }
        }
    }

    void OnThreadException(object? sender, ThreadExceptionEventArgs e)
    {
        e.Handled = true;   // no default error dialog: report it to the client instead
        Log(3, "Unhandled exception: " + e.Exception);
    }

    internal void Log(int level, string text)
    {
        try { LogSink(level, text); }
        catch (IOException) { }
        if (level >= 2) Send(new Protocol.Log { Level = level, Text = text });
    }

    // =====================================================================
    // Transport
    // =====================================================================

    void ReadLoop()
    {
        var reader = new MessageReader(_stream);
        try
        {
            while (!_stopping)
            {
                var message = reader.Read();
                if (message == null) break;
                Post(() => Dispatch(message));
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // IO errors, malformed or hostile input: whatever it is, end the session cleanly rather than crash.
            if (!_stopping) SafeLog(3, "Connection lost: " + e.Message);
        }
        Post(Stop);
    }

    void WriteLoop()
    {
        var writer = new MessageWriter(_stream);
        foreach (var item in _outbox.GetConsumingEnumerable())
        {
            try
            {
                writer.Write(item.Message);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // The stream is broken: nothing more can be sent.
                if (!_stopping) SafeLog(3, "Connection lost: " + e.Message);
                item.Done?.Invoke();
                Stop();
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // E.g. a message over the frame limit: drop just that message and keep the session alive.
                SafeLog(3, $"Dropped an outgoing {item.Message.GetType().Name}: {e.Message}");
            }
            finally
            {
                item.Done?.Invoke();
            }
        }
    }

    void SafeLog(int level, string text)
    {
        try { LogSink(level, text); }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }

    /// <summary>Messages waiting for the writer (for tests).</summary>
    internal int OutboxCount => _outbox.Count;

    /// <summary>Frames waiting for the writer (for tests).</summary>
    internal int OutboxFrameCount => _outbox.ToArray().Count(o => o.Message is Protocol.Frame);

    /// <summary>Queues a message for the client (from any thread). <paramref name="done"/> runs once the writer is finished with it.</summary>
    internal void Send(Message message, Action? done = null)
    {
        try { _outbox.Add(new Outgoing(message, done)); }
        catch (InvalidOperationException) { done?.Invoke(); }   // shutting down
    }

    internal void SendError(int docId, string text)
    {
        LogSink(2, text);
        Send(new Protocol.Error { DocId = docId, Text = text });
    }

    // =====================================================================
    // Dispatch
    // =====================================================================

    void Dispatch(Message message)
    {
        switch (message)
        {
            case Hello:
                Send(new HelloAck { ProtocolVersion = ProtocolInfo.Version, HostVersion = HostVersion });
                break;
            case OpenDocument m: OpenDoc(m.DocId, m.Path); break;
            case CloseDocument m: CloseDoc(m.DocId); break;
            case ActivateDocument m: if (_docs.ContainsKey(m.DocId)) _activeDoc = m.DocId; break;
            case ReloadDocument m: ReloadDoc(m.DocId); break;
            case SaveDocument m: SaveDoc(m.DocId, m.Path); break;
            case AttachWindow m:
                if (_windows.TryGetValue(m.WindowId, out var attach))
                {
                    attach.Attached = true;
                    attach.SentCursor = Cursor.Default;
                    attach.Form.InvalidateAll();
                }
                break;
            case ResizeWindow m: if (_windows.TryGetValue(m.WindowId, out var resize)) Resize(resize, m.Width, m.Height, m.Scale); break;
            case FocusWindow m:
                // Like clicking away from a desktop window: close dropdowns and inline editors (which commits them).
                if (!m.Focused && _windows.TryGetValue(m.WindowId, out var blur)) blur.Form.CloseAllPopups();
                break;
            case MouseMessage m: DispatchMouse(m); break;
            case KeyMessage m: DispatchKey(m); break;
            case TextInput m:
                if (InputWindow(m.WindowId) is { } textWindow) textWindow.Form.ProcessTextInput(m.Text);
                break;
            case Command m:
                if (!_docs.TryGetValue(m.DocId, out var doc)) break;
                if (!doc.Execute(m.Name)) Log(2, $"Unknown command \"{m.Name}\".");
                break;
            case CodeResponse m:
                if (_pendingCode.Remove(m.RequestId, out var pending)) pending.Continue(m.Found ? m.Text : null);
                else Log(1, $"Ignoring CodeResponse {m.RequestId}: no such pending request.");
                break;
            case CloseWindow m:
                if (_windows.TryGetValue(m.WindowId, out var close) && close.Role is WindowRole.Dialog or WindowRole.Popup)
                    close.Form.Close();
                break;
            default:
                Log(2, $"Unexpected message {message.GetType().Name}.");
                break;
        }
    }

    /// <summary>The window that may take input: while a modal window is open, only the topmost modal one.</summary>
    HostWindow? InputWindow(int windowId)
    {
        if (!_windows.TryGetValue(windowId, out var w)) return null;
        if (_modalStack.Count > 0 && _modalStack[^1] != w) return null;
        return w;
    }

    void DispatchMouse(MouseMessage m)
    {
        if (m is Protocol.MouseMove)
        {
            // Pointer movement still reaches windows behind a modal one (hover only), as on the desktop host.
            if (_windows.TryGetValue(m.WindowId, out var moved)) moved.Form.ProcessMouseMove(m.X, m.Y, InputMap.Modifiers(m.Mods));
            return;
        }
        var mods = InputMap.Modifiers(m.Mods);
        if (m is Protocol.MouseUp)
        {
            // A button release belongs to the window that got the press, even if a modal window opened since:
            // otherwise the drag or capture would be stranded.
            if (_mouseCapture.Remove(m.Button, out var pressed) && _windows.ContainsKey(pressed.Id))
            {
                pressed.Form.ProcessMouseUp(m.X, m.Y, InputMap.Button(m.Button), mods);
                return;
            }
            if (InputWindow(m.WindowId) is { } up) up.Form.ProcessMouseUp(m.X, m.Y, InputMap.Button(m.Button), mods);
            return;
        }
        if (InputWindow(m.WindowId) is not { } w) return;
        var form = w.Form;
        switch (m)
        {
            case Protocol.MouseDown:
                _mouseCapture[m.Button] = w;
                form.ProcessMouseDown(m.X, m.Y, InputMap.Button(m.Button), mods, Math.Max(1, m.Clicks));
                break;
            case Protocol.MouseWheel: form.ProcessMouseWheel(m.X, m.Y, m.Dx, m.Dy, mods); break;
        }
    }

    void DispatchKey(KeyMessage m)
    {
        if (InputWindow(m.WindowId) is not { } w) return;
        if (InputMap.Key(m.Key) is not { } key) return;
        var mods = InputMap.Modifiers(m.Mods);
        if (m is Protocol.KeyDown) w.Form.ProcessKeyDown(key, mods, m.Repeat);
        else w.Form.ProcessKeyUp(key, mods);
    }

    /// <summary>
    /// The client view's size (device pixels) and DPI scale. Forms are laid out in device pixels at a scale
    /// applied once (as a loaded form file is): a new scale rebuilds a pane's form; new dialogs pick it up
    /// through <see cref="Form.DefaultScale"/>.
    /// </summary>
    void Resize(HostWindow w, int width, int height, float scale)
    {
        if (width <= 0 || height <= 0)
        {
            if (width < 0 || height < 0) Log(2, $"ResizeWindow {w.Id}: {width} x {height} is negative; treating the window as hidden.");
            width = height = 0;
        }
        else if (!HostWindow.IsValidSize(width, height))
        {
            Log(2, $"ResizeWindow {w.Id}: {width} x {height} is too large (limit {HostWindow.MaxDimension} per side, {HostWindow.MaxPixels / (1024 * 1024)} M pixels); ignored.");
            return;
        }
        double s = scale > 0 && float.IsFinite(scale) ? scale : 1;
        _scale = s;
        Form.DefaultScale = s;
        if (Math.Abs(w.Scale - s) > 1e-6)
        {
            if (w.Role is WindowRole.Surface or WindowRole.Inspector or WindowRole.Palette && _docs.TryGetValue(w.DocId, out var doc))
            {
                var old = w.Form;
                var form = doc.BuildPane(w.Role, s);
                _byForm.Remove(old);
                w.Form = form;
                _byForm[form] = w;
            }
            w.Scale = s;
        }
        if (!w.Resize(width, height))
            Log(3, $"ResizeWindow {w.Id}: could not allocate a {width} x {height} back buffer; keeping {w.Width} x {w.Height}.");
    }

    // =====================================================================
    // Code requests
    // =====================================================================

    sealed record PendingCode(int DocId, long Deadline, Action<string?> Continue, Action OnTimeout);

    /// <summary>How long to wait for a <see cref="CodeResponse"/> before giving up on the edit.</summary>
    internal TimeSpan CodeRequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Asks the client for the editor buffer of <paramref name="file"/> without blocking the UI loop.
    /// <paramref name="continuation"/> runs on the UI thread with the buffer text, or null when the client has no
    /// buffer (read the disk). If the client does not answer in time <paramref name="onTimeout"/> runs instead
    /// (never a silent fallback to the disk: the editor may hold unsaved text). Both are dropped if the document closes first.
    /// </summary>
    internal void RequestCode(int docId, string file, Action<string?> continuation, Action onTimeout)
    {
        int id = _nextRequestId++;
        _pendingCode[id] = new PendingCode(docId, _clock.ElapsedMilliseconds + (long)CodeRequestTimeout.TotalMilliseconds, continuation, onTimeout);
        Send(new CodeRequest { RequestId = id, DocId = docId, File = file });
    }

    void ExpireCodeRequests()
    {
        if (_pendingCode.Count == 0) return;
        long now = _clock.ElapsedMilliseconds;
        foreach (var (id, p) in _pendingCode.Where(p => p.Value.Deadline <= now).ToList())
        {
            _pendingCode.Remove(id);
            Log(2, $"No CodeResponse for request {id} within {CodeRequestTimeout.TotalSeconds:0.#} s; giving up.");
            Guard.Run(p.OnTimeout);
        }
    }

    // =====================================================================
    // Documents
    // =====================================================================

    void OpenDoc(int docId, string path)
    {
        if (_docs.ContainsKey(docId)) { SendError(docId, $"Document {docId} is already open."); return; }
        string full;
        var doc = new DesignDocument();
        try
        {
            full = Path.GetFullPath(path);
            var warnings = doc.Load(File.ReadAllText(full));
            doc.FileName = full;
            foreach (var warning in warnings.Take(10)) Log(2, $"{Path.GetFileName(full)}: {warning}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SendError(docId, $"Could not open {Path.GetFileName(path)}: {e.Message}");
            return;
        }

        var hd = new HostDocument(this, docId, full, doc);
        _docs[docId] = hd;
        hd.SurfaceWindow = AddPane(hd, WindowRole.Surface, hd.Title);
        hd.InspectorWindow = AddPane(hd, WindowRole.Inspector, "Object Inspector");
        hd.PaletteWindow = AddPane(hd, WindowRole.Palette, "Palette");
        _activeDoc = docId;
    }

    HostWindow AddPane(HostDocument doc, WindowRole role, string title)
    {
        var form = doc.BuildPane(role, _scale);
        var w = new HostWindow(_nextWindowId++, role, doc.DocId, form) { Scale = _scale };
        _windows[w.Id] = w;
        _byForm[form] = w;
        // 0 x 0 until the client sends ResizeWindow.
        Send(new WindowOpened { WindowId = w.Id, DocId = doc.DocId, Role = role, Title = title });
        return w;
    }

    void CloseDoc(int docId)
    {
        if (!_docs.Remove(docId, out var doc)) return;
        foreach (var id in _pendingCode.Where(p => p.Value.DocId == docId).Select(p => p.Key).ToList()) _pendingCode.Remove(id);
        var panes = doc.Windows.ToList();
        // Dialogs opened over this document's panes go too.
        foreach (var dialog in _windows.Values.Where(w => w.DocId == docId && !panes.Contains(w)).ToList()) CloseWindowCore(dialog);
        foreach (var w in panes) CloseWindowCore(w);
        if (doc.Surface != null) doc.Surface.Document = null;
        if (_activeDoc == docId) _activeDoc = _docs.Keys.FirstOrDefault();
    }

    void ReloadDoc(int docId)
    {
        if (!_docs.TryGetValue(docId, out var doc)) return;
        try
        {
            var warnings = doc.Doc.Load(File.ReadAllText(doc.Path));
            foreach (var warning in warnings.Take(10)) Log(2, $"{Path.GetFileName(doc.Path)}: {warning}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SendError(docId, $"Could not reload {Path.GetFileName(doc.Path)}: {e.Message}");
        }
        doc.StateDirty = doc.ModifiedStale = true;
        doc.Surface?.Invalidate();
    }

    void SaveDoc(int docId, string path)
    {
        if (!_docs.TryGetValue(docId, out var doc))
        {
            Send(new DocumentSaved { DocId = docId, Ok = false, Error = $"Document {docId} is not open." });
            return;
        }
        try
        {
            var target = string.IsNullOrEmpty(path) ? doc.Path : Path.GetFullPath(path);
            WriteAtomically(target, doc.Doc.Save());
            doc.Doc.MarkSaved();
            // Only now, after the write succeeded, does a Save As move the document.
            doc.Path = target;
            doc.Doc.FileName = target;
            doc.StateDirty = doc.ModifiedStale = true;
            Send(new DocumentSaved { DocId = docId, Ok = true });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Send(new DocumentSaved { DocId = docId, Ok = false, Error = e.Message });
        }
    }

    /// <summary>Writes a temp file next to <paramref name="target"/>, then replaces / moves it into place.</summary>
    static void WriteAtomically(string target, string text)
    {
        var dir = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(dir)) throw new ArgumentException("The path has no directory: " + target);
        var temp = Path.Combine(dir, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            File.WriteAllText(temp, text);
            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // =====================================================================
    // Windows for forms shown by RADSharp code (dialogs)
    // =====================================================================

    /// <summary>Announces a form shown with Show / ShowModal as a <see cref="WindowRole.Dialog"/> window over its owner.</summary>
    internal HostWindow ShowForm(Form form, bool modal)
    {
        if (_byForm.TryGetValue(form, out var existing)) return existing;
        var owner = (form.Owner != null && _byForm.TryGetValue(form.Owner, out var o) ? o : null)
            ?? _modalStack.LastOrDefault()
            ?? (_docs.TryGetValue(_activeDoc, out var active) ? active.SurfaceWindow : null)
            ?? _windows.Values.FirstOrDefault();
        form.PerformLayout();
        int width = Math.Max(1, form.Width), height = Math.Max(1, form.Height);
        var (x, y) = form.StartPosition == FormStartPosition.Manual || owner == null
            ? (form.Left, form.Top)
            : ((owner.Width - width) / 2, (owner.Height - height) / 2);
        var w = new HostWindow(_nextWindowId++, WindowRole.Dialog, owner?.DocId ?? 0, form, owner?.Id ?? 0) { Modal = modal, Scale = form.Scale };
        _windows[w.Id] = w;
        _byForm[form] = w;
        if (modal) _modalStack.Add(w);
        w.X = x;
        w.Y = y;
        w.Resize(width, height);
        Send(new WindowOpened
        {
            WindowId = w.Id, DocId = w.DocId, Role = WindowRole.Dialog, OwnerWindowId = w.OwnerId,
            X = x, Y = y, Width = width, Height = height, Title = form.Caption, Modal = modal,
        });
        form.NotifyShown();
        return w;
    }

    internal bool IsOpen(HostWindow window) => _windows.ContainsKey(window.Id);

    internal void CloseForm(Form form)
    {
        if (_byForm.TryGetValue(form, out var w)) CloseWindowCore(w);
    }

    void CloseWindowCore(HostWindow w)
    {
        if (!_windows.Remove(w.Id)) return;
        _byForm.Remove(w.Form);
        _modalStack.Remove(w);
        foreach (var button in _mouseCapture.Where(c => c.Value == w).Select(c => c.Key).ToList()) _mouseCapture.Remove(button);
        w.Dispose();
        Send(new WindowClosed { WindowId = w.Id });
    }

    // =====================================================================
    // For tests
    // =====================================================================

    internal HostDocument? Document(int docId) => _docs.GetValueOrDefault(docId);
    internal HostWindow? Window(int windowId) => _windows.GetValueOrDefault(windowId);
}
