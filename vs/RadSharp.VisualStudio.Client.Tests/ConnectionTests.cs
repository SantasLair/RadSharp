using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RadSharp.DesignHost.Protocol;
using Xunit;

namespace RadSharp.VisualStudio.Client.Tests;

public class ConnectionTests
{
    [Fact]
    public async Task Handshake_sends_Hello_and_reads_HelloAck()
    {
        using var host = new FakeHost();
        using var conn = new DesignHostConnection(host.ClientStream, null);
        var ack = await conn.HandshakeAsync("tests");
        Assert.Equal(ProtocolInfo.Version, ack.ProtocolVersion);
        Assert.Equal("fake-1.0", conn.HostVersion);
        var hello = host.WaitFor<Hello>();
        Assert.Equal(ProtocolInfo.Version, hello.ProtocolVersion);
        Assert.Equal("tests", hello.ClientName);
        Assert.True(conn.IsConnected);
    }

    [Fact]
    public async Task Handshake_rejects_another_protocol_version()
    {
        using var host = new FakeHost(autoAck: false);
        using var conn = new DesignHostConnection(host.ClientStream, null);
        var task = conn.HandshakeAsync("tests", TimeSpan.FromSeconds(5));
        host.WaitFor<Hello>();
        host.Send(new HelloAck { ProtocolVersion = ProtocolInfo.Version + 41, HostVersion = "future" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
    }

    [Fact]
    public async Task Handshake_times_out_when_the_host_is_silent()
    {
        using var host = new FakeHost(autoAck: false);
        using var conn = new DesignHostConnection(host.ClientStream, null);
        await Assert.ThrowsAsync<TimeoutException>(() => conn.HandshakeAsync("tests", TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Messages_after_the_handshake_are_delivered_in_order()
    {
        using var host = new FakeHost();
        using var conn = new DesignHostConnection(host.ClientStream, null);
        await conn.HandshakeAsync();
        var got = new System.Collections.Concurrent.ConcurrentQueue<Message>();
        conn.MessageReceived += (s, e) => got.Enqueue(e.Message);
        conn.StartReading();
        host.Send(new Log { Level = 1, Text = "one" });
        host.Send(new Error { DocId = 0, Text = "two" });
        Ui.WaitUntil(() => got.Count == 2);
        Assert.True(got.TryDequeue(out var first));
        Assert.Equal("one", Assert.IsType<Log>(first).Text);
        Assert.True(got.TryDequeue(out var second));
        Assert.Equal("two", Assert.IsType<Error>(second).Text);
    }

    [Fact]
    public async Task Disconnected_is_raised_once_when_the_host_goes_away()
    {
        using var host = new FakeHost();
        using var conn = new DesignHostConnection(host.ClientStream, null);
        await conn.HandshakeAsync();
        int count = 0;
        DisconnectedEventArgs? args = null;
        conn.Disconnected += (s, e) =>
        {
            args = e;
            Interlocked.Increment(ref count);
        };
        conn.StartReading();
        host.Kill();
        Ui.WaitUntil(() => Volatile.Read(ref count) > 0);
        Assert.False(conn.IsConnected);
        Assert.False(conn.Send(new Command { DocId = 1, Name = "Undo" }));
        Thread.Sleep(100);
        Assert.Equal(1, count);
        Assert.False(string.IsNullOrEmpty(args!.Reason));
    }

    [Fact]
    public async Task Dispose_does_not_raise_Disconnected()
    {
        using var host = new FakeHost();
        var conn = new DesignHostConnection(host.ClientStream, null);
        await conn.HandshakeAsync();
        bool raised = false;
        conn.Disconnected += (s, e) => raised = true;
        conn.StartReading();
        conn.Dispose();
        Thread.Sleep(200);
        Assert.False(raised);
    }

    [Fact]
    public async Task Launch_fails_with_a_clear_error_when_the_host_is_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), "no-such-host-" + Guid.NewGuid().ToString("N") + ".exe");
        await Assert.ThrowsAsync<FileNotFoundException>(() => DesignHostConnection.LaunchAsync(path, null));
    }

    [Fact]
    public async Task Launch_reports_a_host_that_exits_before_connecting()
    {
        // where.exe rejects --pipe and exits at once: stands in for a host that crashes on startup.
        var where = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
        var ex = await Assert.ThrowsAsync<IOException>(() => DesignHostConnection.LaunchAsync(where, null));
        Assert.Contains("exited", ex.Message);
    }
}
