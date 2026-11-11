using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace RadSharp.Ide.Debug.Protocol;

/// <summary>An adapter answered a request with <c>success: false</c>.</summary>
public sealed class DapRequestException : Exception
{
    public string Command { get; }
    public DapRequestException(string command, string? message)
        : base($"Debug adapter rejected '{command}': {message ?? "(no message)"}") => Command = command;
}

/// <summary>The adapter closed the connection (or crashed) before answering.</summary>
public sealed class DapConnectionClosedException : IOException
{
    public DapConnectionClosedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// DAP wire layer: <c>Content-Length</c> framed UTF-8 JSON over a pair of streams. Correlates responses to requests
/// by <c>seq</c>, and dispatches events one at a time, in order, on a dedicated background task, so a handler may
/// itself issue requests without deadlocking the read loop.
/// </summary>
public sealed class DapTransport : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<int, Pending> _pending = new();
    private readonly Channel<Action> _dispatch = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private readonly Task _dispatchLoop;
    private int _nextSeq;
    private bool _closed;
    private Exception? _closeReason;

    private sealed record Pending(string Command, TaskCompletionSource<JsonElement> Source);

    /// <param name="input">Stream the adapter writes to (we read).</param>
    /// <param name="output">Stream the adapter reads from (we write).</param>
    public DapTransport(Stream input, Stream output)
    {
        _input = input;
        _output = output;
        _dispatchLoop = Task.Run(DispatchLoop);
        _readLoop = Task.Run(ReadLoop);
    }

    /// <summary>
    /// An event arrived: name and body (default/Undefined when the event had no body). Raised on a background
    /// thread, serially and in arrival order.
    /// </summary>
    public event Action<string, JsonElement>? EventReceived;

    /// <summary>
    /// The connection ended: EOF, a framing/IO error or disposal. Raised once, after every earlier event has been
    /// delivered. The argument is null for a clean EOF or disposal.
    /// </summary>
    public event Action<Exception?>? Closed;

    /// <summary>Completes once the connection has closed and all queued events were delivered.</summary>
    public Task Completion => _dispatchLoop;

    public bool IsClosed { get { lock (_gate) return _closed; } }

    /// <summary>
    /// Sends a request and awaits the response body. Cancelling abandons the wait (a late response is dropped);
    /// the adapter is not told. Throws <see cref="DapRequestException"/> on <c>success:false</c> and
    /// <see cref="DapConnectionClosedException"/> if the connection is or goes away.
    /// </summary>
    public async Task<JsonElement> RequestAsync(string command, JsonElement? arguments = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        int seq;
        lock (_gate)
        {
            if (_closed) throw new DapConnectionClosedException("The debug adapter connection is closed.", _closeReason);
            seq = ++_nextSeq;
            _pending[seq] = new Pending(command, tcs);
        }

        using var reg = ct.Register(() =>
        {
            lock (_gate) _pending.Remove(seq);
            tcs.TrySetCanceled(ct);
        });

        try
        {
            var writer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(writer))
            {
                w.WriteStartObject();
                w.WriteNumber("seq", seq);
                w.WriteString("type", "request");
                w.WriteString("command", command);
                if (arguments is { ValueKind: not JsonValueKind.Undefined } args)
                {
                    w.WritePropertyName("arguments");
                    args.WriteTo(w);
                }
                w.WriteEndObject();
            }
            await WriteFrameAsync(writer.WrittenMemory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_gate) _pending.Remove(seq);
            var closed = ex as DapConnectionClosedException ?? new DapConnectionClosedException("Writing to the debug adapter failed.", ex);
            tcs.TrySetException(closed);
        }
        catch (OperationCanceledException)
        {
            lock (_gate) _pending.Remove(seq);
            throw;
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task WriteFrameAsync(ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(header, ct).ConfigureAwait(false);
            await _output.WriteAsync(body, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoop()
    {
        Exception? reason = null;
        try
        {
            var reader = new FrameReader(_input);
            while (!_cts.IsCancellationRequested)
            {
                var frame = await reader.ReadFrameAsync(_cts.Token).ConfigureAwait(false);
                if (frame is null) break; // clean EOF between messages
                try { await HandleAsync(frame).ConfigureAwait(false); }
                catch (JsonException ex) { reason = new DapConnectionClosedException("The debug adapter sent invalid JSON.", ex); break; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { reason = ex as DapConnectionClosedException ?? new DapConnectionClosedException("Reading from the debug adapter failed.", ex); }
        Close(reason);
    }

    private async Task HandleAsync(byte[] frame)
    {
        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "response":
            {
                if (!root.TryGetProperty("request_seq", out var rs) || !rs.TryGetInt32(out var requestSeq)) return;
                Pending? p;
                lock (_gate) { _pending.Remove(requestSeq, out p); }
                if (p is null) return; // cancelled or unknown
                var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
                if (success)
                    p.Source.TrySetResult(root.TryGetProperty("body", out var body) ? body.Clone() : default);
                else
                    p.Source.TrySetException(new DapRequestException(p.Command,
                        root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null));
                break;
            }
            case "event":
            {
                var name = root.TryGetProperty("event", out var e) ? e.GetString() ?? "" : "";
                var body = root.TryGetProperty("body", out var b) ? b.Clone() : default;
                _dispatch.Writer.TryWrite(() => EventReceived?.Invoke(name, body));
                break;
            }
            case "request":
            {
                // Reverse requests (runInTerminal, startDebugging) are not supported: refuse politely.
                var seq = root.TryGetProperty("seq", out var sq) && sq.TryGetInt32(out var sv) ? sv : 0;
                var command = root.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
                var writer = new ArrayBufferWriter<byte>();
                using (var w = new Utf8JsonWriter(writer))
                {
                    w.WriteStartObject();
                    w.WriteNumber("seq", Interlocked.Increment(ref _nextSeq));
                    w.WriteString("type", "response");
                    w.WriteNumber("request_seq", seq);
                    w.WriteBoolean("success", false);
                    w.WriteString("command", command);
                    w.WriteString("message", "Reverse request not supported by this client.");
                    w.WriteEndObject();
                }
                try { await WriteFrameAsync(writer.WrittenMemory, _cts.Token).ConfigureAwait(false); }
                catch (Exception) { /* the read loop will notice a dead connection */ }
                break;
            }
        }
    }

    private async Task DispatchLoop()
    {
        await foreach (var action in _dispatch.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { action(); }
            catch (Exception) { /* a faulty handler must not stop event delivery */ }
        }
    }

    private void Close(Exception? reason)
    {
        List<Pending> toFail;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _closeReason = reason;
            toFail = [.. _pending.Values];
            _pending.Clear();
        }
        var failure = new DapConnectionClosedException(
            reason is null ? "The debug adapter closed the connection." : "The debug adapter connection failed: " + reason.Message, reason);
        foreach (var p in toFail) p.Source.TrySetException(failure);
        _dispatch.Writer.TryWrite(() => Closed?.Invoke(reason));
        _dispatch.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        Close(null);
        try { _input.Dispose(); } catch { }
        try { _output.Dispose(); } catch { }
        try { await _readLoop.ConfigureAwait(false); } catch { }
        try { await _dispatchLoop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }

    /// <summary>Reads <c>Content-Length</c> framed messages from a stream.</summary>
    private sealed class FrameReader(Stream stream)
    {
        private readonly byte[] _buf = new byte[8192];
        private int _pos, _len;

        private async ValueTask<int> NextByteAsync(CancellationToken ct)
        {
            if (_pos == _len)
            {
                _len = await stream.ReadAsync(_buf, ct).ConfigureAwait(false);
                _pos = 0;
                if (_len == 0) return -1;
            }
            return _buf[_pos++];
        }

        public async Task<byte[]?> ReadFrameAsync(CancellationToken ct)
        {
            int contentLength = -1;
            bool any = false;
            var line = new StringBuilder();
            while (true)
            {
                line.Clear();
                while (true)
                {
                    var b = await NextByteAsync(ct).ConfigureAwait(false);
                    if (b < 0)
                    {
                        if (!any && line.Length == 0) return null;
                        throw new DapConnectionClosedException("The debug adapter closed the stream in the middle of a message header.");
                    }
                    any = true;
                    if (b == '\n') break;
                    if (b != '\r') line.Append((char)b);
                    if (line.Length > 1024) throw new DapConnectionClosedException("Malformed DAP header (line too long).");
                }
                if (line.Length == 0) break; // blank line ends the headers
                var s = line.ToString();
                var colon = s.IndexOf(':');
                if (colon > 0 && s.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(s.AsSpan(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) || contentLength < 0)
                        throw new DapConnectionClosedException("Malformed DAP Content-Length header.");
                }
            }
            if (contentLength < 0) throw new DapConnectionClosedException("DAP message without a Content-Length header.");
            if (contentLength > 64 * 1024 * 1024) throw new DapConnectionClosedException("DAP message too large.");

            var body = new byte[contentLength];
            int got = 0;
            while (got < contentLength && _pos < _len) body[got++] = _buf[_pos++];
            while (got < contentLength)
            {
                var n = await stream.ReadAsync(body.AsMemory(got), ct).ConfigureAwait(false);
                if (n == 0) throw new DapConnectionClosedException("The debug adapter closed the stream in the middle of a message body.");
                got += n;
            }
            return body;
        }
    }
}
