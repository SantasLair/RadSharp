using RadSharp.Ide.Debug.Protocol;

namespace RadSharp.Ide.Debug.Tests;

/// <summary>Regression tests for the review fixes in the debug session and breakpoint store.</summary>
public class ReviewFixTests
{
    private static readonly string F = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bp-fix-tests", "A.cs"));

    private static FakeAdapter NewAdapter()
    {
        var fake = new FakeAdapter();
        fake.Handlers["initialize"] = _ => new(Body: new { supportsConfigurationDoneRequest = true });
        fake.After["launch"] = () => fake.SendEvent("initialized");
        fake.Handlers["setBreakpoints"] = args =>
        {
            var bps = args.GetProperty("breakpoints").EnumerateArray().Select((b, i) => new { id = 100 + i, verified = true, line = b.GetProperty("line").GetInt32() }).ToArray();
            return new(Body: new { breakpoints = bps });
        };
        return fake;
    }

    private static readonly DebugLaunchOptions Launch = new() { Program = "dotnet", Args = ["app.dll"] };

    [Fact]
    public async Task Adapter_events_can_race_with_breakpoint_edits_without_throwing()
    {
        var s = new BreakpointStore();
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        Exception? failure = null;
        var editor = Task.Run(() =>
        {
            try
            {
                for (int i = 0; !stop.IsCancellationRequested; i++)
                {
                    var b = s.Add(F, i % 40 + 1);
                    s.ApplyAdapterResult(F, [b], [new DapBreakpoint { Id = 100, Verified = true }]);
                    s.Remove(b.Id);
                }
            }
            catch (Exception e) { failure = e; }
        });
        var events = Task.Run(() =>
        {
            try { while (!stop.IsCancellationRequested) s.ApplyAdapterEvent(new DapBreakpoint { Id = 100, Verified = true }); }
            catch (Exception e) { failure = e; }
        });
        await Task.WhenAll(editor, events);
        Assert.Null(failure);
    }

    [Fact]
    public async Task A_stop_that_beats_the_continue_response_is_not_hidden_by_Running()
    {
        await using var fake = NewAdapter();
        fake.Handlers["continue"] = _ =>
        {
            // The debuggee hits another breakpoint before the client reads the response.
            fake.SendEvent("stopped", new { reason = "breakpoint", threadId = 1, hitBreakpointIds = new[] { 100 } });
            return new(Body: new { allThreadsContinued = true });
        };
        var store = new BreakpointStore();
        store.Add(F, 3);
        await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput), store);
        var stopped = new TaskCompletionSource<bool>();
        await session.LaunchAsync(Launch);
        fake.SendEvent("stopped", new { reason = "pause", threadId = 1 });
        await Wait.Until(() => session.State == DebugSessionState.Stopped);

        int count = 0;
        session.Stopped += (_, _) => { if (++count == 1) stopped.TrySetResult(true); };
        await session.ContinueAsync(1);
        await Wait.For(stopped.Task);
        await Wait.Until(() => session.State == DebugSessionState.Stopped);
        Assert.Equal(1, session.StoppedThreadId);
    }

    [Fact]
    public async Task A_failed_step_leaves_the_session_stopped()
    {
        await using var fake = NewAdapter();
        fake.Handlers["next"] = _ => new(Success: false, Message: "cannot step");
        await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput));
        await session.LaunchAsync(Launch);
        fake.SendEvent("stopped", new { reason = "pause", threadId = 7 });
        await Wait.Until(() => session.State == DebugSessionState.Stopped);

        await Assert.ThrowsAnyAsync<Exception>(() => session.NextAsync(7));
        Assert.Equal(DebugSessionState.Stopped, session.State);
        Assert.Equal(7, session.StoppedThreadId);
    }

    [Fact]
    public async Task Counting_a_breakpoint_hit_from_the_top_frame_does_not_block_the_event_thread()
    {
        await using var fake = NewAdapter();
        fake.Handlers["stackTrace"] = _ => new(Defer: true);   // never answered: the old code blocked the dispatcher for 2 s
        var store = new BreakpointStore();
        store.Add(F, 3);
        await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput), store);
        var output = new TaskCompletionSource<string>();
        session.Output += (_, e) => output.TrySetResult(e.Output);
        await session.LaunchAsync(Launch);

        fake.SendEvent("stopped", new { reason = "breakpoint", threadId = 1 });   // no hitBreakpointIds, like netcoredbg
        fake.SendEvent("output", new { category = "stdout", output = "after the stop\n" });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("after the stop\n", await Wait.For(output.Task, 1500));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task A_hit_is_still_counted_from_the_top_frame_after_the_stop()
    {
        await using var fake = NewAdapter();
        fake.Handlers["stackTrace"] = _ => new(Body: new { stackFrames = new[] { new { id = 1, name = "Main", line = 3, column = 1, source = new { name = "A.cs", path = F } } }, totalFrames = 1 });
        var store = new BreakpointStore();
        var bp = store.Add(F, 3);
        await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput), store);
        await session.LaunchAsync(Launch);
        fake.SendEvent("stopped", new { reason = "breakpoint", threadId = 1 });
        await Wait.Until(() => store.Get(bp.Id)!.TimesHit == 1);
    }

    [Fact]
    public async Task An_event_handler_that_throws_is_reported_through_Output_instead_of_being_lost()
    {
        await using var fake = NewAdapter();
        await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput));
        var output = new TaskCompletionSource<string>();
        session.Output += (_, e) => { if (e.Output.StartsWith("[debugger]")) output.TrySetResult(e.Output); };
        session.ModuleChanged += (_, _) => throw new InvalidOperationException("handler broke");
        await session.LaunchAsync(Launch);

        fake.SendEvent("module", new { reason = "new", module = new { id = "m", name = "M" } });
        var text = await Wait.For(output.Task);
        Assert.Contains("module", text);
        Assert.Contains("handler broke", text);

        // A malformed body is reported too.
        var malformed = new TaskCompletionSource<string>();
        session.Output += (_, e) => { if (e.Output.Contains("malformed")) malformed.TrySetResult(e.Output); };
        fake.SendEvent("stopped", new { reason = "pause", threadId = "not a number" });
        Assert.Contains("stopped", await Wait.For(malformed.Task));
    }
}
