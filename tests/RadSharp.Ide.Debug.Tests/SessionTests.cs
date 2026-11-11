using RadSharp.Ide.Debug.Protocol;

namespace RadSharp.Ide.Debug.Tests;

public class SessionTests
{
    private static readonly string File1 = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "radsharp-tests", "Program.cs"));
    private static readonly string File2 = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "radsharp-tests", "Other.cs"));

    /// <summary>A fake that behaves like netcoredbg's start-up: initialized is sent after launch.</summary>
    private static FakeAdapter NewAdapter()
    {
        var fake = new FakeAdapter();
        fake.Handlers["initialize"] = _ => new(Body: new { supportsConfigurationDoneRequest = true, supportsConditionalBreakpoints = true });
        fake.After["launch"] = () => fake.SendEvent("initialized");
        fake.Handlers["setBreakpoints"] = args =>
        {
            var bps = args.GetProperty("breakpoints").EnumerateArray().Select((b, i) => new
            {
                id = 100 + i, verified = b.GetProperty("line").GetInt32() != 99, line = b.GetProperty("line").GetInt32(),
                message = b.GetProperty("line").GetInt32() == 99 ? "no code here" : null,
            }).ToArray();
            return new(Body: new { breakpoints = bps });
        };
        return fake;
    }

    private static DebugLaunchOptions Launch() => new() { Program = "dotnet", Args = ["app.dll"], WorkingDirectory = "/w", Environment = new Dictionary<string, string?> { ["A"] = "1" }, StopAtEntry = true };

    [Fact]
    public async Task Launch_runs_the_handshake_in_order_and_sends_launch_arguments()
    {
        await using var fake = NewAdapter();
        await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput));
        session.Breakpoints.Add(File1, 10);
        session.Breakpoints.SetExceptionFilters(["all"]);

        await session.LaunchAsync(Launch());

        Assert.Equal(["initialize", "launch", "setBreakpoints", "setExceptionBreakpoints", "configurationDone"], fake.Commands);
        var l = fake.ArgsOf("launch")[0];
        Assert.Equal("dotnet", l.GetProperty("program").GetString());
        Assert.Equal("app.dll", l.GetProperty("args")[0].GetString());
        Assert.Equal("/w", l.GetProperty("cwd").GetString());
        Assert.Equal("1", l.GetProperty("env").GetProperty("A").GetString());
        Assert.True(l.GetProperty("stopAtEntry").GetBoolean());
        Assert.Equal("all", fake.ArgsOf("setExceptionBreakpoints")[0].GetProperty("filters")[0].GetString());
        Assert.True(session.Capabilities.SupportsConfigurationDoneRequest);
        Assert.Equal(DebugSessionState.Running, session.State);
    }

    [Fact]
    public async Task Launch_works_when_the_adapter_answers_launch_only_after_configurationDone()
    {
        await using var fake = NewAdapter();
        int launchSeq = 0;
        fake.Handlers["launch"] = _ => new(Defer: true);
        fake.After["launch"] = () => { launchSeq = fake.Requests.Count; fake.SendEvent("initialized"); };
        fake.After["configurationDone"] = () => fake.Respond(2, "launch", new());
        await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput));
        await Wait.For(Task.Run(async () => { await session.LaunchAsync(Launch()); return true; }));
        Assert.True(launchSeq > 0);
    }

    [Fact]
    public async Task Breakpoints_are_resent_on_every_launch_and_verdicts_are_reported_back()
    {
        var store = new BreakpointStore();
        var a = store.Add(File1, 10, condition: "x > 1", hitCondition: "3");
        var bad = store.Add(File1, 99);
        var off = store.Add(File1, 20, enabled: false);
        var other = store.Add(File2, 5);

        // first session
        {
            await using var fake = NewAdapter();
            await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput), store);
            await session.LaunchAsync(Launch());

            var sent = fake.ArgsOf("setBreakpoints");
            Assert.Equal(2, sent.Length); // one per file
            var f1 = sent.Single(s => s.GetProperty("source").GetProperty("path").GetString() == File1);
            var lines = f1.GetProperty("breakpoints").EnumerateArray().Select(b => b.GetProperty("line").GetInt32()).ToArray();
            Assert.Equal([10, 99], lines); // the disabled one is not sent
            var first = f1.GetProperty("breakpoints")[0];
            Assert.Equal("x > 1", first.GetProperty("condition").GetString());
            Assert.Equal("3", first.GetProperty("hitCondition").GetString());

            Assert.True(store.Get(a.Id)!.Verified);
            Assert.Equal(100, store.Get(a.Id)!.AdapterId);
            Assert.False(store.Get(bad.Id)!.Verified);
            Assert.Equal("no code here", store.Get(bad.Id)!.Message);
            Assert.Null(store.Get(off.Id)!.Verified);
            Assert.True(store.Get(other.Id)!.Verified);
        }

        // the adapter went away with the session: state is cleared but the breakpoints survive
        await Wait.Until(() => store.Get(a.Id)!.Verified is null);
        Assert.Equal(4, store.All.Count);

        // second session: same store, re-sent
        {
            await using var fake = NewAdapter();
            await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput), store);
            await session.LaunchAsync(Launch());
            Assert.Equal(2, fake.ArgsOf("setBreakpoints").Length);
            Assert.True(store.Get(a.Id)!.Verified);
        }
    }

    [Fact]
    public async Task Removing_the_last_breakpoint_of_a_file_sends_an_empty_list()
    {
        await using var fake = NewAdapter();
        var store = new BreakpointStore();
        var bp = store.Add(File1, 10);
        await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput), store);
        await session.LaunchAsync(Launch());

        store.Remove(bp.Id);
        await session.SyncBreakpointsAsync(File1);
        var last = fake.ArgsOf("setBreakpoints").Last();
        Assert.Empty(last.GetProperty("breakpoints").EnumerateArray());
    }

    [Fact]
    public async Task Stopped_and_other_events_surface_as_NET_events_on_a_background_thread()
    {
        await using var fake = NewAdapter();
        await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput));
        var callerThread = Environment.CurrentManagedThreadId;
        var stopped = new TaskCompletionSource<(StoppedEventArgs, int)>();
        var continued = new TaskCompletionSource<ContinuedEventArgs>();
        var exited = new TaskCompletionSource<ExitedEventArgs>();
        var terminated = new TaskCompletionSource<bool>();
        var output = new TaskCompletionSource<OutputEventArgs>();
        var bpChanged = new TaskCompletionSource<BreakpointEventArgs>();
        var module = new TaskCompletionSource<ModuleEventArgs>();
        session.Stopped += (_, e) => stopped.TrySetResult((e, Environment.CurrentManagedThreadId));
        session.Continued += (_, e) => continued.TrySetResult(e);
        session.Exited += (_, e) => exited.TrySetResult(e);
        session.Terminated += (_, _) => terminated.TrySetResult(true);
        session.Output += (_, e) => output.TrySetResult(e);
        session.BreakpointChanged += (_, e) => bpChanged.TrySetResult(e);
        session.ModuleChanged += (_, e) => module.TrySetResult(e);

        fake.SendEvent("stopped", new { reason = "breakpoint", threadId = 4, allThreadsStopped = true, hitBreakpointIds = new[] { 7 } });
        var (s, thread) = await Wait.For(stopped.Task);
        Assert.Equal("breakpoint", s.Reason);
        Assert.Equal(4, s.ThreadId);
        Assert.NotEqual(callerThread, thread);
        Assert.Equal(DebugSessionState.Stopped, session.State);
        Assert.Equal(4, session.StoppedThreadId);

        fake.SendEvent("continued", new { threadId = 4 });
        Assert.Equal(4, (await Wait.For(continued.Task)).ThreadId);
        Assert.Equal(DebugSessionState.Running, session.State);

        fake.SendEvent("output", new { category = "stdout", output = "hi\n" });
        var o = await Wait.For(output.Task);
        Assert.Equal("stdout", o.Category);
        Assert.Equal("hi\n", o.Output);

        fake.SendEvent("breakpoint", new { reason = "changed", breakpoint = new { id = 1, verified = true, line = 12 } });
        Assert.Equal(12, (await Wait.For(bpChanged.Task)).Breakpoint.Line);

        fake.SendEvent("module", new { reason = "new", module = new { id = "abc", name = "App.dll", path = "/a/App.dll" } });
        var m = await Wait.For(module.Task);
        Assert.Equal("App.dll", m.Module.Name);
        Assert.Equal("abc", m.Module.Id!.Value.GetString());

        fake.SendEvent("exited", new { exitCode = 3 });
        Assert.Equal(3, (await Wait.For(exited.Task)).ExitCode);
        fake.SendEvent("terminated");
        await Wait.For(terminated.Task);
        Assert.Equal(DebugSessionState.Ended, session.State);
    }

    [Fact]
    public async Task Breakpoint_events_update_the_store_and_stops_count_hits()
    {
        await using var fake = NewAdapter();
        var store = new BreakpointStore();
        var bp = store.Add(File1, 10);
        await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput), store);
        await session.LaunchAsync(Launch());
        Assert.Equal(100, store.Get(bp.Id)!.AdapterId);

        fake.SendEvent("breakpoint", new { reason = "changed", breakpoint = new { id = 100, verified = true, line = 11 } });
        await Wait.Until(() => store.Get(bp.Id)!.ActualLine == 11);

        fake.SendEvent("stopped", new { reason = "breakpoint", threadId = 1, hitBreakpointIds = new[] { 100 } });
        await Wait.Until(() => store.Get(bp.Id)!.TimesHit == 1);
    }

    [Fact]
    public async Task Typed_requests_round_trip()
    {
        await using var fake = NewAdapter();
        fake.Handlers["threads"] = _ => new(Body: new { threads = new[] { new { id = 1, name = "Main" } } });
        fake.Handlers["stackTrace"] = _ => new(Body: new { stackFrames = new[] { new { id = 1000, name = "Main", line = 3, column = 1, source = new { name = "Program.cs", path = File1 } } }, totalFrames = 1 });
        fake.Handlers["scopes"] = _ => new(Body: new { scopes = new[] { new { name = "Locals", variablesReference = 5, expensive = false } } });
        fake.Handlers["variables"] = _ => new(Body: new { variables = new[] { new { name = "a", value = "20", type = "int", variablesReference = 0 } } });
        fake.Handlers["evaluate"] = _ => new(Body: new { result = "42", type = "int", variablesReference = 0 });
        fake.Handlers["exceptionInfo"] = _ => new(Body: new { exceptionId = "System.Exception", description = "boom", details = new { message = "boom", stackTrace = "at X" } });
        await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput));

        Assert.Equal("Main", (await session.GetThreadsAsync())[0].Name);
        var st = await session.GetStackTraceAsync(1, levels: 5);
        Assert.Equal(3, st.StackFrames[0].Line);
        Assert.Equal(File1, st.StackFrames[0].Source!.Path);
        var scopes = await session.GetScopesAsync(1000);
        Assert.Equal("Locals", scopes[0].Name);
        var vars = await session.GetVariablesAsync(scopes[0].VariablesReference);
        Assert.Equal(("a", "20", "int"), (vars[0].Name, vars[0].Value, vars[0].Type));
        Assert.Equal("42", (await session.EvaluateAsync("a + 22", 1000)).Result);
        Assert.Equal("boom", (await session.GetExceptionInfoAsync(1)).Details!.Message);

        var ev = fake.ArgsOf("evaluate")[0];
        Assert.Equal("watch", ev.GetProperty("context").GetString());
        Assert.Equal(1000, ev.GetProperty("frameId").GetInt32());
        Assert.Equal(5, fake.ArgsOf("stackTrace")[0].GetProperty("levels").GetInt32());

        await session.ContinueAsync(1);
        await session.NextAsync(1);
        await session.StepInAsync(1);
        await session.StepOutAsync(1);
        await session.PauseAsync(1);
        await session.TerminateAsync();
        await session.DisconnectAsync();
        Assert.Equal(["threads", "stackTrace", "scopes", "variables", "evaluate", "exceptionInfo", "continue", "next", "stepIn", "stepOut", "pause", "terminate", "disconnect"], fake.Commands);
        Assert.True(fake.ArgsOf("disconnect")[0].GetProperty("terminateDebuggee").GetBoolean());
        Assert.Equal(DebugSessionState.Ended, session.State);
    }

    [Fact]
    public async Task Adapter_crash_ends_the_session_and_fails_requests()
    {
        await using var fake = NewAdapter();
        var store = new BreakpointStore();
        var bp = store.Add(File1, 10);
        await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput), store);
        await session.LaunchAsync(Launch());
        var closed = new TaskCompletionSource<Exception?>();
        session.AdapterClosed += (_, ex) => closed.TrySetResult(ex);
        fake.Handlers["threads"] = _ => new(Defer: true);

        var pending = session.GetThreadsAsync();
        await Wait.Until(() => fake.Commands.Contains("threads"));
        fake.Crash();

        await Assert.ThrowsAsync<DapConnectionClosedException>(() => pending);
        await Wait.For(closed.Task);
        Assert.Equal(DebugSessionState.Ended, session.State);
        Assert.Null(store.Get(bp.Id)!.Verified);
        await Assert.ThrowsAsync<DapConnectionClosedException>(() => session.ContinueAsync(1));
    }

    [Fact]
    public async Task Adapter_dying_during_launch_fails_the_launch()
    {
        await using var fake = NewAdapter();
        fake.After["launch"] = () => fake.Crash(); // never sends initialized
        await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput));
        await Assert.ThrowsAsync<DapConnectionClosedException>(() => Wait.For(session.LaunchAsync(Launch()).ContinueWith(t => { t.GetAwaiter().GetResult(); return 0; })));
    }

    [Fact]
    public async Task Launch_rejected_by_the_adapter_surfaces_the_error()
    {
        await using var fake = NewAdapter();
        fake.Handlers["launch"] = _ => new(Success: false, Message: "program not found");
        await using var session = new DebugSession(new DapTransport(fake.ClientInput, fake.ClientOutput));
        var ex = await Assert.ThrowsAsync<DapRequestException>(() => session.LaunchAsync(Launch()));
        Assert.Contains("program not found", ex.Message);
    }
}
