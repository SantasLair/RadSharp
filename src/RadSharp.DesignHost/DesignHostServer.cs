using System.Collections.Concurrent;
using System.Diagnostics;
using RadSharp.Design;
using RadSharp.DesignHost.Protocol;
using RadSharp.Diagnostics;
using RadSharp.Drawing;
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
    readonly Dictionary<string, ProjectAssembly> _projects = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, string> _docProject = [];
    /// <summary>When a rebuild was last seen changing (the host clock, ms), with the build's change stamp then.</summary>
    readonly Dictionary<string, (long Since, long Stamp)> _staleSince = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Projects whose load failed after a rebuild: when to try once more (the file may have been half written).</summary>
    readonly Dictionary<string, long> _retryAt = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _toldBusy = new(StringComparer.OrdinalIgnoreCase);
    long _nextProjectCheck;
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
        FormRenderer.PaintError += OnPaintError;
        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "RadSharp design host reader" };
        var writer = new Thread(WriteLoop) { IsBackground = true, Name = "RadSharp design host writer" };
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
            foreach (var project in _projects.Values) project.Dispose();
            _projects.Clear();
            _docProject.Clear();
            _outbox.CompleteAdding();
            writer.Join(TimeSpan.FromSeconds(2));
            Application.ThreadException -= OnThreadException;
            FormRenderer.PaintError -= OnPaintError;
            if (PlatformHost.Current == _platform) PlatformHost.Current = previousPlatform;
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    /// <summary>Runs the host on a new background thread (for tests and embedding).</summary>
    public Thread Start()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "RadSharp design host UI" };
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
        _paintErrorsThisFlush = 0;
        long now = _clock.ElapsedMilliseconds;
        if (AutoCheckProjects) CheckProjects(now);
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

    /// <summary>The least time between two reports about the same control (its message may change on every paint).</summary>
    internal TimeSpan PaintErrorInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The most paint errors reported in one pass over the windows, whatever the controls.</summary>
    const int MaxPaintErrorsPerFlush = 5;

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, System.Runtime.CompilerServices.StrongBox<long>> _paintErrorAt = new();
    int _paintErrorsThisFlush;

    void OnPaintError(Control control, Exception e)
    {
        // Only this host's designed forms (painting runs on this thread; a test process may paint others elsewhere).
        if (!_docs.Values.Any(d => ReferenceEquals(d.Doc.Form, control.Root))) return;
        // The first failure of a control is reported at once; after that one per interval, and a few per pass.
        long now = _clock.ElapsedMilliseconds;
        if (_paintErrorAt.TryGetValue(control, out var last))
        {
            if (now - last.Value < PaintErrorInterval.TotalMilliseconds) return;
            last.Value = now;
        }
        else if (_paintErrorsThisFlush >= MaxPaintErrorsPerFlush) return;   // not recorded: reported on a later paint
        else _paintErrorAt.Add(control, new System.Runtime.CompilerServices.StrongBox<long>(now));
        if (++_paintErrorsThisFlush > MaxPaintErrorsPerFlush) return;
        var name = control.Name.Length > 0 ? $"{control.Name} ({control.GetType().Name})" : control.GetType().Name;
        Log(2, $"{name} can't paint at design time: {e.GetType().Name}: {e.Message}");
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
            case SetProjectAssembly m: SetProject(m.DocId, m.Path); break;
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
        var project = ProjectOf(docId);
        if (project.Classes.Count > 0) doc.ProjectClasses = project.Find;
        try
        {
            full = Path.GetFullPath(path);
            var warnings = KeepingTheme(() => doc.Load(File.ReadAllText(full)));
            doc.FileName = full;
            foreach (var warning in warnings.Take(10)) Log(2, $"{Path.GetFileName(full)}: {warning}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SendError(docId, $"Could not open {Path.GetFileName(path)}: {e.Message}");
            ForgetProject(docId);   // the document never opened: it must not keep its project loaded
            return;
        }

        var hd = new HostDocument(this, docId, full, doc) { ProjectClasses = project };
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
        if (!_docs.Remove(docId, out var doc)) { ForgetProject(docId); return; }
        foreach (var id in _pendingCode.Where(p => p.Value.DocId == docId).Select(p => p.Key).ToList()) _pendingCode.Remove(id);
        var panes = doc.Windows.ToList();
        // Dialogs opened over this document's panes go too.
        foreach (var dialog in _windows.Values.Where(w => w.DocId == docId && !panes.Contains(w)).ToList()) CloseWindowCore(dialog);
        foreach (var w in panes) CloseWindowCore(w);
        if (doc.Surface != null) doc.Surface.Document = null;
        if (_activeDoc == docId) _activeDoc = _docs.Keys.FirstOrDefault();
        ForgetProject(docId);
    }

    /// <summary>The document is gone (closed, or never opened): its project is unloaded once no other document uses it.</summary>
    void ForgetProject(int docId)
    {
        if (_docProject.Remove(docId, out var project)) UnloadIfUnused(project);
    }

    void ReloadDoc(int docId)
    {
        if (!_docs.TryGetValue(docId, out var doc)) return;
        try
        {
            var warnings = KeepingTheme(() => doc.Doc.Load(File.ReadAllText(doc.Path)));
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
    // Project assemblies (the project's own controls)
    // =====================================================================

    /// <summary>How long a rebuilt assembly must stay unchanged before it is reloaded (the build may still be writing).</summary>
    internal TimeSpan ProjectSettleTime { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>False: the UI loop doesn't check for rebuilds itself (tests call <see cref="CheckProjects"/> with their own clock).</summary>
    internal bool AutoCheckProjects { get; set; } = true;

    /// <summary>How often the loaded project assemblies are checked for a rebuild.</summary>
    internal TimeSpan ProjectCheckInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The loaded project assemblies, by full path (for tests).</summary>
    internal IReadOnlyDictionary<string, ProjectAssembly> Projects => _projects;

    /// <summary>
    /// Project code may set <see cref="Theme.Current"/> as a side effect (a static constructor, say): the panes keep
    /// the designer's theme.
    /// </summary>
    static T KeepingTheme<T>(Func<T> action)
    {
        var theme = Theme.Current;
        try { return action(); }
        finally { Theme.Current = theme; }
    }

    /// <summary>The classes of the project <paramref name="docId"/> belongs to (none if it has no project assembly).</summary>
    ProjectClassSet ProjectOf(int docId) =>
        _docProject.TryGetValue(docId, out var path) && _projects.TryGetValue(path, out var p) ? p.ClassSet : ProjectClassSet.Empty;

    /// <summary>
    /// The document's project assembly (sent before the document is opened, or later when it changes): loaded unless
    /// another document has it already. An empty path: the document has none. If the document is open, it is read again
    /// with the new classes; the old assembly is unloaded when no document uses it any more.
    /// </summary>
    void SetProject(int docId, string path)
    {
        string? full = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try { full = Path.GetFullPath(path); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Log(2, $"Project controls: '{path}' is not a valid path ({e.Message}).");
                return;
            }
        }
        var old = _docProject.GetValueOrDefault(docId);
        if (string.Equals(old, full, StringComparison.OrdinalIgnoreCase)) return;
        if (full == null) _docProject.Remove(docId);
        else _docProject[docId] = full;
        if (full != null && !_projects.ContainsKey(full)) _projects[full] = LoadProject(full);
        if (_docs.TryGetValue(docId, out var doc)) Swap([doc], ProjectOf(docId));
        if (old != null) UnloadIfUnused(old);
    }

    ProjectAssembly LoadProject(string path)
    {
        var project = KeepingTheme(() => ProjectAssembly.Load(path));
        if (project.Error != null) Log(2, $"Project controls: {project.Error}");
        else
        {
            var names = project.Classes.Select(c => $"{c.Name} ({project.ClassSet.CategoryOf(c)})");
            var text = project.Classes.Count == 0
                ? $"Project controls: {Path.GetFileName(path)} defines none."
                : $"Project controls from {Path.GetFileName(path)}: {string.Join(", ", names)}.";
            Inform(text);
        }
        foreach (var warning in project.Warnings) Log(2, $"Project controls: {warning}");
        return project;
    }

    /// <summary>Information the IDE should show too (<see cref="Log"/> forwards only warnings and errors).</summary>
    void Inform(string text)
    {
        Log(1, text);
        Send(new Protocol.Log { Level = 1, Text = text });
    }

    void UnloadIfUnused(string path)
    {
        if (_docProject.ContainsValue(path) || !_projects.Remove(path, out var project)) return;
        _staleSince.Remove(path);
        _retryAt.Remove(path);
        _toldBusy.Remove(path);
        project.Dispose();
    }

    /// <summary>
    /// Gives <paramref name="docs"/> the classes of <paramref name="next"/>: each form is saved while the old classes are
    /// still loaded, then read again with the new ones (keeping undo, the selection and the modified state); a form that
    /// can't take them gets placeholders instead (<see cref="DesignDocument.ReloadWith"/>).
    /// </summary>
    void Swap(IEnumerable<HostDocument> docs, ProjectClassSet next)
    {
        foreach (var doc in docs.ToList())
        {
            var name = Path.GetFileName(doc.Path);
            string json;
            try { json = doc.Doc.Save(); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // The old controls can't be written. Keeping them would leave the form on code that is about to be
                // unloaded (a later save would write their base classes): go back to the last text that could be saved.
                json = doc.Doc.LastGoodText;
                Log(2, $"{name}: a control on the form can't be saved ({e.GetType().Name}: {e.Message}); the form is reloaded from its last good state, so changes made since are lost.");
            }
            try
            {
                var messages = KeepingTheme(() => doc.SwapProjectClasses(next, json));
                // Not the "unknown class" ones: a project that isn't built says so once, when it is loaded.
                foreach (var m in messages.Where(m => !m.Contains("unknown class", StringComparison.Ordinal)).Take(10))
                    Log(2, $"{name}: {m}");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // One form failing must not stop the others from moving to the new code.
                Log(3, $"{name}: can't move the form onto the rebuilt project ({e.GetType().Name}: {e.Message}).");
            }
        }
    }

    /// <summary>Reloads every project assembly that was rebuilt and has stayed unchanged for <see cref="ProjectSettleTime"/>.</summary>
    internal void CheckProjects(long now)
    {
        if (_projects.Count == 0 || now < _nextProjectCheck) return;
        _nextProjectCheck = now + (long)ProjectCheckInterval.TotalMilliseconds;
        foreach (var (path, project) in _projects.ToList())
        {
            bool retry = false;
            if (_retryAt.TryGetValue(path, out var due))
            {
                // The load failed right after a rebuild: once more when it has settled (a slow copy, an antivirus scan).
                if (now < due) continue;
                _retryAt.Remove(path);
                retry = true;
            }
            else if (!project.IsStale()) { _staleSince.Remove(path); continue; }
            if (!retry)
            {
                // Settled: unchanged for ProjectSettleTime. A file that changes again restarts the wait.
                var stamp = project.ChangeStamp();
                if (!_staleSince.TryGetValue(path, out var seen) || seen.Stamp != stamp) { _staleSince[path] = (now, stamp); continue; }
                if (now - seen.Since < ProjectSettleTime.TotalMilliseconds) continue;
            }
            if (BusyWith(path) is { } busy)
            {
                // Replacing the forms now would pull them from under what is editing them: wait until it is done.
                if (_toldBusy.Add(path)) Inform($"Project controls: {Path.GetFileName(path)} was rebuilt; it is reloaded once {busy} is done.");
                continue;
            }
            _staleSince.Remove(path);
            _toldBusy.Remove(path);
            ReloadProject(path);
            if (!retry && _projects.TryGetValue(path, out var reloaded) && reloaded.Error != null && reloaded.FileExists)
                _retryAt[path] = now + (long)ProjectSettleTime.TotalMilliseconds;
        }
    }

    /// <summary>What holds controls of a form that uses <paramref name="path"/> (null: nothing): see <see cref="HostDocument.Busy"/>.</summary>
    string? BusyWith(string path)
    {
        var docs = Using(path).ToList();
        if (docs.Count == 0) return null;
        if (_modalStack.Count > 0) return "the dialog";
        return docs.Select(d => d.Busy).FirstOrDefault(b => b != null);
    }

    IEnumerable<HostDocument> Using(string path) =>
        _docProject.Where(p => string.Equals(p.Value, path, StringComparison.OrdinalIgnoreCase) && _docs.ContainsKey(p.Key)).Select(p => _docs[p.Key]);

    /// <summary>The project was rebuilt: load the new code, move its forms onto it, then unload the old code.</summary>
    internal void ReloadProject(string path)
    {
        if (!_projects.TryGetValue(path, out var old)) return;
        var next = LoadProject(path);
        _projects[path] = next;
        try { Swap(Using(path), next.ClassSet); }
        finally { old.Dispose(); }   // whatever happened: the old code never leaks
    }

    // =====================================================================
    // Windows for forms shown by RadSharp code (dialogs)
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
