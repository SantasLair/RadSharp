using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using RadSharp.DesignHost.Protocol;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RadSharp.VisualStudio.Client.Tests;

/// <summary>An in-process design host over a real named pipe: answers Hello, records what the client sends, and sends
/// whatever the test asks.</summary>
internal sealed class FakeHost : IDisposable
{
    private readonly NamedPipeServerStream _server;
    private readonly MessageWriter _writer;
    private readonly List<Message> _received = new List<Message>();
    private readonly Thread _thread;

    public FakeHost(bool autoAck = true)
    {
        string name = "radsharp-test-" + Guid.NewGuid().ToString("N");
        _server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        ClientStream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = _server.BeginWaitForConnection(null, null);
        ClientStream.Connect(5000);
        _server.EndWaitForConnection(accept);
        _writer = new MessageWriter(_server);
        var reader = new MessageReader(_server);
        _thread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var m = reader.Read();
                    if (m == null) return;
                    lock (_received) _received.Add(m);
                    if (autoAck && m is Hello) _writer.Write(new HelloAck { ProtocolVersion = ProtocolInfo.Version, HostVersion = "fake-1.0" });
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true };
        _thread.Start();
    }

    public NamedPipeClientStream ClientStream { get; }

    public void Send(Message m) => _writer.Write(m);

    /// <summary>Writes raw bytes to the pipe (malformed frames).</summary>
    public void SendRaw(byte[] bytes)
    {
        _server.Write(bytes, 0, bytes.Length);
        _server.Flush();
    }

    public List<T> All<T>() where T : Message
    {
        lock (_received) return _received.OfType<T>().ToList();
    }

    public T? Find<T>(Func<T, bool>? match = null) where T : Message
    {
        lock (_received) return _received.OfType<T>().LastOrDefault(m => match == null || match(m));
    }

    /// <summary>Waits (pumping the dispatcher when on a UI thread) for a message the client sent.</summary>
    public T WaitFor<T>(Func<T, bool>? match = null, int timeoutMs = 5000) where T : Message
    {
        T? found = null;
        Ui.WaitUntil(() => (found = Find(match)) != null, timeoutMs, "client message " + typeof(T).Name);
        return found!;
    }

    public void Clear()
    {
        lock (_received) _received.Clear();
    }

    /// <summary>Simulates the host exiting.</summary>
    public void Kill() => _server.Dispose();

    public void Dispose()
    {
        _server.Dispose();
        ClientStream.Dispose();
    }
}

internal static class Ui
{
    /// <summary>Runs <paramref name="body"/> on a fresh STA thread with its own dispatcher, rethrowing its exception.</summary>
    public static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { error = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(60000)) throw new TimeoutException("STA test body did not finish.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>Processes pending dispatcher work on the current thread.</summary>
    public static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    public static void WaitUntil(Func<bool> condition, int timeoutMs = 5000, string what = "condition")
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what + ".");
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) DoEvents();
            Thread.Sleep(5);
        }
    }
}
