using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using RadSharp.DesignHost.Protocol;

namespace RadSharp.VisualStudio.Client;

/// <summary>Why the connection to the design host ended.</summary>
public sealed class DisconnectedEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public DisconnectedEventArgs(string reason, Exception? exception, int? exitCode)
    {
        Reason = reason;
        Exception = exception;
        ExitCode = exitCode;
    }

    /// <summary>Human-readable reason, suitable for an error banner.</summary>
    public string Reason { get; }

    /// <summary>The read or write error, if there was one.</summary>
    public Exception? Exception { get; }

    /// <summary>The host's exit code when it had already exited.</summary>
    public int? ExitCode { get; }
}

/// <summary>
/// One pipe connection to RadSharpDesignHost.exe. Incoming messages are raised on the WPF dispatcher given to the
/// constructor (or on the reader thread when there is none). <see cref="Send"/> may be called from any thread.
/// </summary>
/// <remarks>
/// Usage: <see cref="LaunchAsync"/> (or the stream constructor and <see cref="HandshakeAsync"/>), subscribe to
/// <see cref="MessageReceived"/>, then <see cref="StartReading"/>. Splitting the last two steps means no message
/// can arrive before the subscriber is ready.
/// </remarks>
public sealed class DesignHostConnection : IDisposable
{
    /// <summary>Default client name sent in Hello.</summary>
    public const string DefaultClientName = "RadSharp.VisualStudio.Client";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly Stream _stream;
    private readonly Dispatcher? _dispatcher;
    private readonly MessageWriter _writer;
    private readonly MessageReader _reader;
    private readonly Process? _process;
    private readonly List<Message> _early = new List<Message>();
    private readonly object _gate = new object();
    private readonly LinkedList<Delivery> _pending = new LinkedList<Delivery>();
    private bool _drainScheduled;
    private int _messagesRead;
    private Thread? _readerThread;
    private int _state; // 0 open, 1 disconnected, 2 disposed

    /// <summary>The dispatcher priority incoming messages are delivered at. Below Render (7), so a busy UI thread paints between
    /// deliveries instead of starving; at the same level as user input, so the host's answers to input are not
    /// delayed behind it. Background would starve under steady input.</summary>
    private const DispatcherPriority DeliveryPriority = DispatcherPriority.Input;

    /// <summary>How long one dispatcher operation delivers messages before yielding to Render and input.</summary>
    private static readonly TimeSpan DeliverySlice = TimeSpan.FromMilliseconds(8);

    /// <summary>Most pending entries scanned for a frame the new one supersedes.</summary>
    private const int CoalesceScan = 256;

    /// <summary>A message (or a connection event) waiting for the dispatcher.</summary>
    private readonly struct Delivery
    {
        public Delivery(Message? message, Action? action)
        {
            Message = message;
            Action = action;
        }

        public Message? Message { get; }
        public Action? Action { get; }
    }

    /// <summary>Messages the reader thread has read so far (tests).</summary>
    internal int MessagesRead => Volatile.Read(ref _messagesRead);

    /// <summary>Wraps an already connected duplex stream (tests, or a host started some other way).</summary>
    public DesignHostConnection(Stream stream, Dispatcher? dispatcher) : this(stream, dispatcher, null) { }

    private DesignHostConnection(Stream stream, Dispatcher? dispatcher, Process? process)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _dispatcher = dispatcher;
        _process = process;
        _writer = new MessageWriter(stream);
        _reader = new MessageReader(stream);
    }

    /// <summary>A message from the host. Raised on the dispatcher.</summary>
    public event EventHandler<MessageEventArgs>? MessageReceived;

    /// <summary>The connection broke (host crashed or exited, or a protocol error). Raised once, on the dispatcher.
    /// Not raised by <see cref="Dispose"/>.</summary>
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <summary>A line the host wrote to stderr or stdout, or a client diagnostic. Raised on the dispatcher.</summary>
    public event EventHandler<string>? OutputReceived;

    /// <summary>True until the connection breaks or is disposed.</summary>
    public bool IsConnected => Volatile.Read(ref _state) == 0;

    /// <summary>The host's version string from HelloAck.</summary>
    public string? HostVersion { get; private set; }

    /// <summary>The host process, when this connection launched it.</summary>
    public Process? Process => _process;

    /// <summary>
    /// Starts the host per the process contract (<c>--pipe &lt;name&gt; --parent-pid &lt;pid&gt;</c>), connects with
    /// retries for up to 10 s and completes the Hello / HelloAck handshake. A path ending in <c>.dll</c> is run with
    /// <c>dotnet</c>. Call <see cref="StartReading"/> after subscribing to the events.
    /// </summary>
    /// <param name="hostPath">Full path of RadSharpDesignHost.exe.</param>
    /// <param name="dispatcher">Dispatcher that receives events, normally the UI thread's.</param>
    /// <param name="output">Optional sink for host output lines during startup (any thread). After startup use
    /// <see cref="OutputReceived"/>.</param>
    /// <param name="clientName">Client name sent in Hello.</param>
    /// <param name="cancellationToken">Cancels the connect loop.</param>
    public static Task<DesignHostConnection> LaunchAsync(string hostPath, Dispatcher? dispatcher, Action<string>? output = null,
        string clientName = DefaultClientName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(hostPath)) throw new ArgumentException("Host path is required.", nameof(hostPath));
        return Task.Run(() => LaunchCore(hostPath, dispatcher, output, clientName, cancellationToken), cancellationToken);
    }

    private static DesignHostConnection LaunchCore(string hostPath, Dispatcher? dispatcher, Action<string>? output, string clientName,
        CancellationToken ct)
    {
        if (!File.Exists(hostPath)) throw new FileNotFoundException("Design host not found: " + hostPath, hostPath);

        string pipeName = "radsharp-designhost-" + Guid.NewGuid().ToString("N");
        int parentPid;
        using (var self = Process.GetCurrentProcess()) parentPid = self.Id;
        string args = "--pipe " + pipeName + " --parent-pid " + parentPid;
        var psi = hostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("dotnet", "\"" + hostPath + "\" " + args)
            : new ProcessStartInfo(hostPath, args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardOutput = true;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.WorkingDirectory = Path.GetDirectoryName(hostPath) ?? "";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        DesignHostConnection? connection = null;
        var startupLines = new List<string>();
        void OnLine(object s, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            var c = Volatile.Read(ref connection);
            if (c != null) c.RaiseOutput(e.Data);
            else
            {
                lock (startupLines) startupLines.Add(e.Data);
                output?.Invoke(e.Data);
            }
        }
        process.ErrorDataReceived += OnLine;
        process.OutputDataReceived += OnLine;
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        NamedPipeClientStream? pipe = null;
        try
        {
            var deadline = DateTime.UtcNow + ConnectTimeout;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new IOException("The design host exited with code " + process.ExitCode + " before accepting a connection." + Tail(startupLines));
                var attempt = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    attempt.Connect(200);
                    pipe = attempt;
                    break;
                }
                catch (Exception e) when (e is TimeoutException || e is IOException)
                {
                    attempt.Dispose();
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException("Could not connect to the design host within " + ConnectTimeout.TotalSeconds + " s." + Tail(startupLines));
                    Thread.Sleep(50);
                }
            }

            var conn = new DesignHostConnection(pipe, dispatcher, process);
            conn.HandshakeCore(clientName, ConnectTimeout);
            lock (startupLines)
            {
                foreach (var line in startupLines) conn.RaiseOutput(line);
                Volatile.Write(ref connection, conn);
            }
            return conn;
        }
        catch
        {
            pipe?.Dispose();
            KillQuietly(process);
            process.Dispose();
            throw;
        }
    }

    private static string Tail(List<string> lines)
    {
        lock (lines)
        {
            if (lines.Count == 0) return "";
            int from = Math.Max(0, lines.Count - 10);
            return Environment.NewLine + string.Join(Environment.NewLine, lines.GetRange(from, lines.Count - from));
        }
    }

    /// <summary>Sends Hello and waits for HelloAck. Does not start the reader; see <see cref="StartReading"/>.</summary>
    public Task<HelloAck> HandshakeAsync(string clientName = DefaultClientName, TimeSpan? timeout = null) =>
        Task.Run(() => HandshakeCore(clientName, timeout ?? ConnectTimeout));

    private HelloAck HandshakeCore(string clientName, TimeSpan timeout)
    {
        _writer.Write(new Hello { ProtocolVersion = ProtocolInfo.Version, ClientName = clientName });
        var read = Task.Run(() =>
        {
            while (true)
            {
                var m = _reader.Read() ?? throw new EndOfStreamException("The design host closed the connection during the handshake.");
                if (m is HelloAck ack) return ack;
                lock (_gate) _early.Add(m);
            }
        });
        bool done;
        try { done = read.Wait(timeout); }
        catch (AggregateException e) { throw new IOException("Design host handshake failed: " + e.InnerException?.Message, e.InnerException); }
        if (!done)
        {
            _stream.Dispose();
            throw new TimeoutException("The design host did not answer Hello within " + timeout.TotalSeconds + " s.");
        }
        var helloAck = read.Result;
        if (helloAck.ProtocolVersion != ProtocolInfo.Version)
        {
            _stream.Dispose();
            throw new InvalidOperationException("Design host speaks protocol " + helloAck.ProtocolVersion + "; this client speaks " +
                ProtocolInfo.Version + ".");
        }
        HostVersion = helloAck.HostVersion;
        return helloAck;
    }

    /// <summary>Starts the background reader. Messages received during the handshake are delivered first.</summary>
    public void StartReading()
    {
        lock (_gate)
        {
            if (_readerThread != null) return;
            foreach (var m in _early) PostMessage(m);
            _early.Clear();
            _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "RadSharp design host reader" };
            _readerThread.Start();
        }
    }

    private void ReadLoop()
    {
        try
        {
            while (true)
            {
                var m = _reader.Read();
                if (m == null)
                {
                    OnBroken("The design host closed the connection.", null);
                    return;
                }
                Interlocked.Increment(ref _messagesRead);
                PostMessage(m);
            }
        }
        catch (Exception e)
        {
            OnBroken(Volatile.Read(ref _state) == 2 ? "Disposed." : "Lost the connection to the design host: " + e.Message, e);
        }
    }

    /// <summary>Sends a message. Thread-safe. Returns false (and raises <see cref="Disconnected"/>) when the pipe is broken.</summary>
    public bool Send(Message message)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        if (!IsConnected) return false;
        try
        {
            _writer.Write(message);
            return true;
        }
        catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException)
        {
            OnBroken("Could not send to the design host: " + e.Message, e);
            return false;
        }
    }

    private void OnBroken(string reason, Exception? e)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
        try { _stream.Dispose(); } catch (IOException) { }
        var process = _process;
        if (process == null)
        {
            PostAction(() => Disconnected?.Invoke(this, new DisconnectedEventArgs(reason, e, null)));
            return;
        }
        // The exit code is a courtesy: wait for it off the calling thread (which may be the UI thread, via Send).
        Task.Run(() =>
        {
            int? exitCode = null;
            try
            {
                if (process.WaitForExit(500)) exitCode = process.ExitCode;
            }
            catch (InvalidOperationException) { }
            var text = exitCode != null ? reason + " (exit code " + exitCode + ")" : reason;
            PostAction(() => Disconnected?.Invoke(this, new DisconnectedEventArgs(text, e, exitCode)));
        });
    }

    private void RaiseOutput(string line) => PostAction(() => OutputReceived?.Invoke(this, line));

    private bool CanDeliver => Volatile.Read(ref _state) != 2 && (_dispatcher == null || !_dispatcher.HasShutdownStarted);

    private void PostMessage(Message m)
    {
        if (_dispatcher == null)
        {
            MessageReceived?.Invoke(this, new MessageEventArgs(m));
            return;
        }
        Enqueue(new Delivery(m, null));
    }

    private void PostAction(Action a)
    {
        if (_dispatcher == null)
        {
            a();
            return;
        }
        Enqueue(new Delivery(null, a));
    }

    /// <summary>
    /// Queues a delivery for the dispatcher. One operation drains the whole queue in order, so ordering holds (WindowOpened
    /// before its frames, WindowClosed after them, Disconnected last) and a slow UI thread costs one pending operation, not one
    /// per message. Frames are partial (dirty rectangles), so they cannot simply be replaced by the latest: a pending frame is
    /// dropped only when a later frame of the same window fully covers its rectangle with no WindowOpened / WindowClosed of
    /// that window in between, which leaves the pixels the same.
    /// </summary>
    private void Enqueue(Delivery d)
    {
        if (!CanDeliver) return; // disposed, or the UI thread is gone: nobody to deliver to
        bool schedule;
        lock (_pending)
        {
            if (d.Message is Frame f) Supersede(f);
            _pending.AddLast(d);
            schedule = !_drainScheduled;
            _drainScheduled = true;
        }
        if (schedule) ScheduleDrain();
    }

    private void Supersede(Frame f)
    {
        int scanned = 0;
        for (var node = _pending.Last; node != null && scanned < CoalesceScan; scanned++)
        {
            var prev = node.Previous;
            switch (node.Value.Message)
            {
                case Frame old when old.WindowId == f.WindowId:
                    if (Covers(f, old)) _pending.Remove(node);
                    break;
                case WindowOpened wo when wo.WindowId == f.WindowId:
                    return;
                case WindowClosed wc when wc.WindowId == f.WindowId:
                    return;
            }
            node = prev;
        }
    }

    private static bool Covers(Frame outer, Frame inner) =>
        (long)outer.X <= inner.X && (long)outer.Y <= inner.Y
        && (long)outer.X + outer.Width >= (long)inner.X + inner.Width
        && (long)outer.Y + outer.Height >= (long)inner.Y + inner.Height;

    private void ScheduleDrain()
    {
        try
        {
            _dispatcher!.BeginInvoke(DeliveryPriority, new Action(Drain));
        }
        catch (Exception e) when (e is InvalidOperationException || e is OperationCanceledException)
        {
            lock (_pending)
            {
                _pending.Clear();
                _drainScheduled = false;
            }
        }
    }

    private void Drain()
    {
        bool completed = false;
        try
        {
            DrainCore();
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                // A handler threw: keep the queue moving instead of leaving _drainScheduled set with nobody scheduled.
                bool more;
                lock (_pending)
                {
                    more = _pending.First != null;
                    if (!more) _drainScheduled = false;
                }
                if (more) ScheduleDrain();
            }
        }
    }

    private void DrainCore()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            Delivery d;
            lock (_pending)
            {
                if (_pending.First == null || Volatile.Read(ref _state) == 2)
                {
                    _pending.Clear();
                    _drainScheduled = false;
                    return;
                }
                d = _pending.First.Value;
                _pending.RemoveFirst();
            }
            if (d.Message != null) MessageReceived?.Invoke(this, new MessageEventArgs(d.Message));
            else d.Action?.Invoke();
            if (started.Elapsed > DeliverySlice)
            {
                bool more;
                lock (_pending)
                {
                    more = _pending.First != null;
                    if (!more) _drainScheduled = false;
                }
                if (more) ScheduleDrain(); // _drainScheduled stays true
                return;
            }
        }
    }

    /// <summary>Closes the pipe (the host exits when it sees that) and kills the host if it is still running 3 s later.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _state, 2) == 2) return;
        try { _stream.Dispose(); } catch (IOException) { }
        var p = _process;
        if (p == null) return;
        Task.Run(() =>
        {
            try
            {
                if (!p.WaitForExit(3000)) KillQuietly(p);
            }
            catch (InvalidOperationException) { }
            finally { p.Dispose(); }
        });
    }

    private static void KillQuietly(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception) { }
    }
}

/// <summary>A received protocol message.</summary>
public sealed class MessageEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public MessageEventArgs(Message message) => Message = message;

    /// <summary>The message.</summary>
    public Message Message { get; }
}
