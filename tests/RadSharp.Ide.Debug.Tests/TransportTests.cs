using System.Text;
using System.Text.Json;
using RadSharp.Ide.Debug.Protocol;

namespace RadSharp.Ide.Debug.Tests;

public class TransportTests
{
    private static JsonElement Obj(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Response_split_into_single_bytes_is_reassembled()
    {
        await using var fake = new FakeAdapter();
        await using var t = new DapTransport(fake.ClientInput, fake.ClientOutput);
        fake.Handlers["threads"] = _ => new(Defer: true);

        var req = t.RequestAsync("threads");
        await Wait.Until(() => fake.Commands.Length == 1);
        fake.WriteFrame(Encoding.UTF8.GetBytes("""{"seq":1,"type":"response","request_seq":1,"success":true,"command":"threads","body":{"threads":[{"id":7,"name":"main"}]}}"""), chunk: 1);

        var body = await Wait.For(req);
        Assert.Equal(7, body.GetProperty("threads")[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Content_length_counts_bytes_not_characters()
    {
        await using var fake = new FakeAdapter();
        await using var t = new DapTransport(fake.ClientInput, fake.ClientOutput);
        fake.Handlers["evaluate"] = _ => new(Body: new { result = "héllo → 世界 😀" });
        var body = await Wait.For(t.RequestAsync("evaluate"));
        Assert.Equal("héllo → 世界 😀", body.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Several_messages_in_one_chunk_are_all_delivered()
    {
        await using var fake = new FakeAdapter();
        await using var t = new DapTransport(fake.ClientInput, fake.ClientOutput);
        var names = new List<string>();
        t.EventReceived += (n, _) => { lock (names) names.Add(n); };

        var frames = new List<byte>();
        foreach (var n in new[] { "a", "b", "c" })
        {
            var body = Encoding.UTF8.GetBytes($$"""{"seq":1,"type":"event","event":"{{n}}"}""");
            frames.AddRange(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            frames.AddRange(body);
        }
        fake.SendRaw([.. frames]);
        await Wait.Until(() => { lock (names) return names.Count == 3; });
        Assert.Equal(["a", "b", "c"], names);
    }

    [Fact]
    public async Task Responses_correlate_by_seq_even_out_of_order()
    {
        await using var fake = new FakeAdapter();
        await using var t = new DapTransport(fake.ClientInput, fake.ClientOutput);
        fake.Handlers["first"] = _ => new(Defer: true);
        fake.Handlers["second"] = _ => new(Body: new { which = "second" });

        var first = t.RequestAsync("first");
        await Wait.Until(() => fake.Commands.Length == 1);
        var second = t.RequestAsync("second");

        var secondBody = await Wait.For(second);
        Assert.Equal("second", secondBody.GetProperty("which").GetString());
        Assert.False(first.IsCompleted);

        fake.Respond(1, "first", new(Body: new { which = "first" }));
        Assert.Equal("first", (await Wait.For(first)).GetProperty("which").GetString());
    }

    [Fact]
    public async Task Failed_response_throws_with_the_adapter_message()
    {
        await using var fake = new FakeAdapter();
        await using var t = new DapTransport(fake.ClientInput, fake.ClientOutput);
        fake.Handlers["evaluate"] = _ => new(Success: false, Message: "no such variable");
        var ex = await Assert.ThrowsAsync<DapRequestException>(() => t.RequestAsync("evaluate"));
        Assert.Equal("evaluate", ex.Command);
        Assert.Contains("no such variable", ex.Message);
    }

    [Fact]
    public async Task Request_arguments_are_sent_as_given()
    {
        await using var fake = new FakeAdapter();
        await using var t = new DapTransport(fake.ClientInput, fake.ClientOutput);
        await Wait.For(t.RequestAsync("continue", Obj("""{"threadId":3}""")));
        Assert.Equal(3, fake.Requests[0].Args.GetProperty("threadId").GetInt32());
    }

    [Fact]
    public async Task Events_are_dispatched_in_order_and_handlers_may_issue_requests()
    {
        await using var fake = new FakeAdapter();
        await using var t = new DapTransport(fake.ClientInput, fake.ClientOutput);
        fake.Handlers["stackTrace"] = _ => new(Body: new { stackFrames = Array.Empty<object>() });
        var order = new List<string>();
        var nested = new TaskCompletionSource<bool>();
        t.EventReceived += (name, _) =>
        {
            lock (order) order.Add(name);
            if (name == "stopped")
            {
                // would deadlock if events were dispatched on the read loop
                t.RequestAsync("stackTrace").GetAwaiter().GetResult();
                nested.SetResult(true);
            }
        };
        fake.SendEvent("one");
        fake.SendEvent("stopped");
        fake.SendEvent("three");
        await Wait.For(nested.Task);
        await Wait.Until(() => { lock (order) return order.Count == 3; });
        Assert.Equal(["one", "stopped", "three"], order);
    }

    [Fact]
    public async Task A_throwing_event_handler_does_not_stop_later_events()
    {
        await using var fake = new FakeAdapter();
        await using var t = new DapTransport(fake.ClientInput, fake.ClientOutput);
        var got = new TaskCompletionSource<string>();
        t.EventReceived += (n, _) => { if (n == "bad") throw new InvalidOperationException(); got.TrySetResult(n); };
        fake.SendEvent("bad");
        fake.SendEvent("good");
        Assert.Equal("good", await Wait.For(got.Task));
    }

    [Fact]
    public async Task Cancelled_request_throws_and_a_late_response_is_ignored()
    {
        await using var fake = new FakeAdapter();
        await using var t = new DapTransport(fake.ClientInput, fake.ClientOutput);
        fake.Handlers["slow"] = _ => new(Defer: true);
        using var cts = new CancellationTokenSource();
        var req = t.RequestAsync("slow", ct: cts.Token);
        await Wait.Until(() => fake.Commands.Length == 1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => req);

        fake.Respond(1, "slow", new(Body: new { late = true })); // must be ignored
        fake.Handlers["fast"] = _ => new(Body: new { ok = true });
        var body = await Wait.For(t.RequestAsync("fast"));
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.False(t.IsClosed);
    }

    [Fact]
    public async Task Pre_cancelled_token_fails_without_sending()
    {
        await using var fake = new FakeAdapter();
        await using var t = new DapTransport(fake.ClientInput, fake.ClientOutput);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.RequestAsync("x", ct: new CancellationToken(true)));
        Assert.Empty(fake.Commands);
    }

    [Fact]
    public async Task Adapter_crash_fails_pending_requests_and_raises_Closed_after_earlier_events()
    {
        await using var fake = new FakeAdapter();
        await using var t = new DapTransport(fake.ClientInput, fake.ClientOutput);
        var log = new List<string>();
        var closed = new TaskCompletionSource<Exception?>();
        t.EventReceived += (n, _) => { lock (log) log.Add("event:" + n); };
        t.Closed += ex => { lock (log) log.Add("closed"); closed.TrySetResult(ex); };
        fake.Handlers["hang"] = _ => new(Defer: true);

        var pending = t.RequestAsync("hang");
        await Wait.Until(() => fake.Commands.Length == 1);
        fake.SendEvent("output");
        fake.Crash();

        await Assert.ThrowsAsync<DapConnectionClosedException>(() => pending);
        await Wait.For(closed.Task);
        lock (log) Assert.Equal(["event:output", "closed"], log);
        Assert.True(t.IsClosed);
        await Assert.ThrowsAsync<DapConnectionClosedException>(() => t.RequestAsync("again"));
    }

    [Fact]
    public async Task Stream_truncated_mid_message_is_reported_as_a_failure()
    {
        await using var fake = new FakeAdapter();
        await using var t = new DapTransport(fake.ClientInput, fake.ClientOutput);
        var closed = new TaskCompletionSource<Exception?>();
        t.Closed += ex => closed.TrySetResult(ex);
        fake.SendRaw(Encoding.ASCII.GetBytes("Content-Length: 100\r\n\r\n{\"seq\":1"));
        fake.Crash();
        Assert.IsType<DapConnectionClosedException>(await Wait.For(closed.Task));
    }

    [Fact]
    public async Task Invalid_json_closes_the_connection()
    {
        await using var fake = new FakeAdapter();
        await using var t = new DapTransport(fake.ClientInput, fake.ClientOutput);
        var closed = new TaskCompletionSource<Exception?>();
        t.Closed += ex => closed.TrySetResult(ex);
        fake.WriteFrame(Encoding.UTF8.GetBytes("not json"));
        Assert.NotNull(await Wait.For(closed.Task));
    }

    [Fact]
    public async Task Clean_eof_raises_Closed_without_an_error()
    {
        await using var fake = new FakeAdapter();
        await using var t = new DapTransport(fake.ClientInput, fake.ClientOutput);
        var closed = new TaskCompletionSource<Exception?>();
        t.Closed += ex => closed.TrySetResult(ex);
        fake.Crash();
        Assert.Null(await Wait.For(closed.Task));
    }

    [Fact]
    public async Task Reverse_requests_are_refused()
    {
        await using var fake = new FakeAdapter();
        await using var t = new DapTransport(fake.ClientInput, fake.ClientOutput);
        fake.SendRawRequest(42, "runInTerminal");
        await Wait.Until(() => { lock (fake.Responses) return fake.Responses.Count > 0; });
        Assert.Contains("\"request_seq\":42", fake.Responses[0]);
        Assert.Contains("\"success\":false", fake.Responses[0]);
    }
}
