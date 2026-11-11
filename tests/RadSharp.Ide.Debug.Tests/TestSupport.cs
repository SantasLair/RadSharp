using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace RadSharp.Ide.Debug.Tests;

/// <summary>One direction of an in-memory byte pipe: the writer side.</summary>
internal sealed class PipeWriterStream(Channel<byte[]> channel) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (!channel.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray())) throw new IOException("pipe closed");
    }
    protected override void Dispose(bool disposing) { if (disposing) channel.Writer.TryComplete(); base.Dispose(disposing); }
}

/// <summary>The reader side of an in-memory byte pipe.</summary>
internal sealed class PipeReaderStream(Channel<byte[]> channel) : Stream
{
    private byte[] _cur = [];
    private int _pos;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_pos >= _cur.Length)
        {
            if (!await channel.Reader.WaitToReadAsync(ct)) return 0;
            if (channel.Reader.TryRead(out var next)) { _cur = next; _pos = 0; }
        }
        var n = Math.Min(buffer.Length, _cur.Length - _pos);
        _cur.AsMemory(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// A scripted in-memory debug adapter with its own, independent DAP framing code (so framing is not tested against
/// itself). Reads requests from the client, answers via handlers, and can push events or crash.
/// </summary>
internal sealed class FakeAdapter : IAsyncDisposable
{
    public record Reply(bool Success = true, object? Body = null, string? Message = null, bool Defer = false);

    private readonly Channel<byte[]> _toClient = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<byte[]> _toAdapter = Channel.CreateUnbounded<byte[]>();
    private readonly Stream _fromClient;
    private readonly Stream _toClientStream;
    private int _seq;
    private readonly Task _loop;
    private readonly object _lock = new();

    public Stream ClientInput { get; }
    public Stream ClientOutput { get; }

    /// <summary>Every request received: (command, arguments).</summary>
    public List<(string Command, JsonElement Args)> Requests { get; } = new();
    /// <summary>Raw JSON of every non-request message the client sent (its replies to reverse requests).</summary>
    public List<string> Responses { get; } = new();
    public Dictionary<string, Func<JsonElement, Reply>> Handlers { get; } = new();
    /// <summary>Hooks run after the response to a command has been sent (to emit events in a realistic order).</summary>
    public Dictionary<string, Action> After { get; } = new();

    public FakeAdapter()
    {
        ClientInput = new PipeReaderStream(_toClient);
        ClientOutput = new PipeWriterStream(_toAdapter);
        _fromClient = new PipeReaderStream(_toAdapter);
        _toClientStream = new PipeWriterStream(_toClient);
        _loop = Task.Run(Loop);
    }

    public string[] Commands { get { lock (_lock) return Requests.Select(r => r.Command).ToArray(); } }

    /// <summary>The arguments of every request for one command.</summary>
    public JsonElement[] ArgsOf(string command)
    {
        lock (_lock) return Requests.Where(r => r.Command == command).Select(r => r.Args).ToArray();
    }

    private async Task Loop()
    {
        try
        {
            while (true)
            {
                var msg = await ReadFrame();
                if (msg is null) return;
                using var doc = JsonDocument.Parse(msg);
                var root = doc.RootElement;
                if (root.GetProperty("type").GetString() != "request")
                {
                    lock (_lock) Responses.Add(Encoding.UTF8.GetString(msg));
                    continue;
                }
                var cmd = root.GetProperty("command").GetString()!;
                var seq = root.GetProperty("seq").GetInt32();
                var args = root.TryGetProperty("arguments", out var a) ? a.Clone() : default;
                lock (_lock) Requests.Add((cmd, args));
                var reply = Handlers.TryGetValue(cmd, out var h) ? h(args) : new Reply();
                if (!reply.Defer) Respond(seq, cmd, reply);
                if (After.TryGetValue(cmd, out var after)) after();
            }
        }
        catch (Exception) { }
    }

    public void Respond(int seq, string cmd, Reply reply)
    {
        var obj = new Dictionary<string, object?>
        {
            ["seq"] = Interlocked.Increment(ref _seq), ["type"] = "response", ["request_seq"] = seq,
            ["success"] = reply.Success, ["command"] = cmd,
        };
        if (reply.Message is not null) obj["message"] = reply.Message;
        if (reply.Body is not null) obj["body"] = reply.Body;
        WriteFrame(JsonSerializer.SerializeToUtf8Bytes(obj));
    }

    public void SendEvent(string name, object? body = null)
    {
        var obj = new Dictionary<string, object?> { ["seq"] = Interlocked.Increment(ref _seq), ["type"] = "event", ["event"] = name };
        if (body is not null) obj["body"] = body;
        WriteFrame(JsonSerializer.SerializeToUtf8Bytes(obj));
    }

    public void SendRaw(byte[] bytes, int chunk = int.MaxValue)
    {
        for (int i = 0; i < bytes.Length; i += chunk) _toClientStream.Write(bytes, i, Math.Min(chunk, bytes.Length - i));
    }

    public void WriteFrame(byte[] body, int chunk = int.MaxValue)
    {
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        SendRaw([.. header, .. body], chunk);
    }

    public void SendRawRequest(int seq, string command) =>
        WriteFrame(JsonSerializer.SerializeToUtf8Bytes(new { seq, type = "request", command }));

    /// <summary>The adapter process dies: the client's input hits EOF.</summary>
    public void Crash() => _toClientStream.Dispose();

    private async Task<byte[]?> ReadFrame()
    {
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await _fromClient.ReadAsync(one) == 0) return null;
            header.Append((char)one[0]);
        }
        var len = int.Parse(header.ToString().Split("\r\n")[0]["Content-Length: ".Length..]);
        var buf = new byte[len];
        int got = 0;
        while (got < len)
        {
            var n = await _fromClient.ReadAsync(buf.AsMemory(got));
            if (n == 0) return null;
            got += n;
        }
        return buf;
    }

    public async ValueTask DisposeAsync()
    {
        _toClientStream.Dispose();
        _fromClient.Dispose();
        await Task.WhenAny(_loop, Task.Delay(1000));
    }
}

internal static class Wait
{
    public static async Task<T> For<T>(Task<T> task, int ms = 5000)
    {
        if (await Task.WhenAny(task, Task.Delay(ms)) != task) throw new TimeoutException("Timed out waiting for event.");
        return await task;
    }

    public static async Task Until(Func<bool> cond, int ms = 5000)
    {
        var end = Environment.TickCount64 + ms;
        while (!cond())
        {
            if (Environment.TickCount64 > end) throw new TimeoutException("Condition not met.");
            await Task.Delay(10);
        }
    }
}
