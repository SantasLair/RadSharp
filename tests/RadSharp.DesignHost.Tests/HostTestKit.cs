using System.Collections.Concurrent;
using RadSharp.Design;
using P = RadSharp.DesignHost.Protocol;

namespace RadSharp.DesignHost.Tests;

/// <summary>Host tests share process-wide state (PlatformHost.Current, Form.DefaultScale), so they run one at a time.</summary>
[CollectionDefinition("DesignHost", DisableParallelization = true)]
public sealed class DesignHostCollection;

/// <summary>One direction of an in-memory pipe: a blocking byte queue.</summary>
sealed class ByteChannel
{
    readonly Queue<byte> _bytes = new();
    bool _closed;

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_bytes)
        {
            if (_closed) throw new IOException("The pipe is closed.");
            foreach (var b in data) _bytes.Enqueue(b);
            Monitor.PulseAll(_bytes);
        }
    }

    public int Read(Span<byte> buffer)
    {
        lock (_bytes)
        {
            while (_bytes.Count == 0 && !_closed) Monitor.Wait(_bytes);
            int n = 0;
            while (n < buffer.Length && _bytes.Count > 0) buffer[n++] = _bytes.Dequeue();
            return n;   // 0 = closed and empty
        }
    }

    public void Close()
    {
        lock (_bytes)
        {
            _closed = true;
            Monitor.PulseAll(_bytes);
        }
    }
}

/// <summary>One end of an in-memory duplex pipe.</summary>
sealed class DuplexStream(ByteChannel input, ByteChannel output) : Stream
{
    public static (Stream A, Stream B) CreatePair()
    {
        var ab = new ByteChannel();
        var ba = new ByteChannel();
        return (new DuplexStream(ba, ab), new DuplexStream(ab, ba));
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer.AsSpan(offset, count));
    public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer.AsSpan(offset, count));
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            output.Close();
            input.Close();
        }
        base.Dispose(disposing);
    }
}

/// <summary>A test client: sends messages to a host and waits for the host's replies.</summary>
sealed class HostClient : IDisposable
{
    readonly Stream _stream;
    readonly P.MessageWriter _writer;
    readonly BlockingCollection<P.Message> _inbox = [];
    readonly List<string> _seen = [];

    public HostClient()
    {
        var (client, host) = DuplexStream.CreatePair();
        _stream = client;
        Server = new DesignHostServer(host) { LogSink = (_, _) => { } };
        HostThread = Server.Start();
        _writer = new P.MessageWriter(client);
        StartReader();
    }

    /// <summary>Wraps an already-connected stream to an out-of-process host.</summary>
    public HostClient(Stream connected)
    {
        _stream = connected;
        Server = null;
        _writer = new P.MessageWriter(connected);
        StartReader();
    }

    void StartReader()
    {
        var reader = new P.MessageReader(_stream);
        new Thread(() =>
        {
            try
            {
                while (reader.Read() is { } m) _inbox.Add(m);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException) { }
            _inbox.CompleteAdding();
        }) { IsBackground = true, Name = "Test client reader" }.Start();
    }

    public DesignHostServer? Server { get; }

    /// <summary>The in-process host's UI thread.</summary>
    public Thread? HostThread { get; }

    public void Send(P.Message message) => _writer.Write(message);

    /// <summary>Waits for the next message of type <typeparamref name="T"/> matching <paramref name="match"/>, skipping others.</summary>
    public T Expect<T>(Func<T, bool>? match = null, int timeoutMs = 15000) where T : P.Message
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !_inbox.TryTake(out var m, left))
                throw new TimeoutException($"No {typeof(T).Name} arrived. Seen: {string.Join(", ", _seen.TakeLast(40))}");
            _seen.Add(Describe(m));
            if (m is T t && (match == null || match(t))) return t;
        }
    }

    /// <summary>Discards everything received so far, after the host has handled what was sent before.</summary>
    public void Settle()
    {
        if (Server != null) Ui(() => 0);
        Thread.Sleep(60);   // a couple of loop iterations: the frames and states of what ran are flushed
        while (_inbox.TryTake(out var m)) _seen.Add(Describe(m));
    }

    static string Describe(P.Message m) => m switch
    {
        P.Frame f => $"Frame({f.WindowId}: {f.X},{f.Y} {f.Width}x{f.Height})",
        P.DocumentState s => $"State(dirty={s.Dirty}, undo={s.CanUndo}, sel={s.HasSelection})",
        P.WindowOpened w => $"WindowOpened({w.WindowId} {w.Role})",
        P.Error e => $"Error({e.Text})",
        P.Log l => $"Log({l.Text})",
        _ => m.GetType().Name,
    };

    /// <summary>Runs <paramref name="func"/> on the host's UI thread.</summary>
    public T Ui<T>(Func<T> func)
    {
        var task = Server!.InvokeAsync(func);
        if (!task.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("The host's UI thread did not respond.");
        return task.Result;
    }

    public void Ui(Action action) => Ui(() => { action(); return 0; });

    // ---- Input helpers ----

    public void Move(int window, int x, int y, P.Modifiers mods = P.Modifiers.None) =>
        Send(new P.MouseMove { WindowId = window, X = x, Y = y, Mods = mods });

    public void Click(int window, int x, int y, int clicks = 1, P.MouseButton button = P.MouseButton.Left)
    {
        Move(window, x, y);
        Send(new P.MouseDown { WindowId = window, X = x, Y = y, Button = button, Clicks = clicks });
        Send(new P.MouseUp { WindowId = window, X = x, Y = y, Button = button, Clicks = clicks });
    }

    public void DoubleClick(int window, int x, int y)
    {
        Click(window, x, y, 1);
        Click(window, x, y, 2);
    }

    public void Drag(int window, int x1, int y1, int x2, int y2, int steps = 5)
    {
        Move(window, x1, y1);
        Send(new P.MouseDown { WindowId = window, X = x1, Y = y1, Clicks = 1 });
        for (int i = 1; i <= steps; i++)
            Move(window, x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
        Send(new P.MouseUp { WindowId = window, X = x2, Y = y2 });
    }

    public void Key(int window, string key, P.Modifiers mods = P.Modifiers.None)
    {
        Send(new P.KeyDown { WindowId = window, Key = key, Mods = mods });
        Send(new P.KeyUp { WindowId = window, Key = key, Mods = mods });
    }

    // ---- Documents ----

    public sealed record Panes(int Surface, int Inspector, int Palette);

    /// <summary>Hello, OpenDocument and the three pane windows.</summary>
    public Panes Open(int docId, string path)
    {
        Send(new P.OpenDocument { DocId = docId, Path = path });
        var opened = new List<P.WindowOpened>();
        while (opened.Count < 3) opened.Add(Expect<P.WindowOpened>(w => w.DocId == docId));
        int Of(P.WindowRole role) => opened.Single(w => w.Role == role).WindowId;
        return new Panes(Of(P.WindowRole.Surface), Of(P.WindowRole.Inspector), Of(P.WindowRole.Palette));
    }

    /// <summary>Attaches and sizes a window, returning its first (full) frame.</summary>
    public P.Frame Show(int window, int width, int height, float scale = 1)
    {
        Send(new P.AttachWindow { WindowId = window });
        Send(new P.ResizeWindow { WindowId = window, Width = width, Height = height, Scale = scale });
        return Expect<P.Frame>(f => f.WindowId == window && f.Width == width && f.Height == height);
    }

    /// <summary>The centre of a designed control in surface-window coordinates.</summary>
    public (int X, int Y) ControlPoint(int docId, string name) => Ui(() =>
    {
        var doc = Server!.Document(docId)!;
        var control = doc.Doc.Form.FindControl(name) ?? throw new InvalidOperationException($"No control {name}.");
        var r = DesignSurface.DesignRect(control);
        var (x, y) = doc.Surface!.DesignToScreen(r.X + r.Width / 2, r.Y + r.Height / 2);
        return (doc.Surface.AbsoluteLeft + x, doc.Surface.AbsoluteTop + y);
    });

    /// <summary>A design-coordinate point on the surface, in surface-window coordinates.</summary>
    public (int X, int Y) DesignPoint(int docId, int x, int y) => Ui(() =>
    {
        var surface = Server!.Document(docId)!.Surface!;
        var (sx, sy) = surface.DesignToScreen(x, y);
        return (surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy);
    });

    public void Dispose()
    {
        _stream.Dispose();
        HostThread?.Join(TimeSpan.FromSeconds(10));
    }
}

static class TestFiles
{
    public static string TempDir() => Directory.CreateTempSubdirectory("radsharp-host-").FullName;

    /// <summary>A copy of the Hello sample's form in a folder of its own (no project around it).</summary>
    public static string HelloForm()
    {
        var path = Path.Combine(TempDir(), "MainForm.radform.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Hello.radform.json"), path);
        return path;
    }

    public static bool NotBlank(byte[] pixels)
    {
        for (int i = 4; i < pixels.Length; i += 4)
            if (pixels[i] != pixels[0] || pixels[i + 1] != pixels[1] || pixels[i + 2] != pixels[2]) return true;
        return false;
    }
}
