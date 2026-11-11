using System.Collections.Concurrent;
using System.Text.Json;
using RadSharp.Design.Code;
using RadSharp.Designer;
using RadSharp.Ide.Debug;
using RadSharp.Ide.Debug.Protocol;
using RadSharp.Ide.Debug.Tests;

namespace RadSharp.Tests;

/// <summary>A UI thread for tests: posted work waits in a queue until the test pumps it (no sleeps: posts wake the pump).</summary>
sealed class UiQueue
{
    readonly ConcurrentQueue<Action> _queue = new();
    readonly SemaphoreSlim _wake = new(0);
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public void Post(Action action) { _queue.Enqueue(action); _wake.Release(); }

    /// <summary>Runs whatever is queued now.</summary>
    public void Drain() { while (_queue.TryDequeue(out var a)) a(); }

    public void PumpUntil(Func<bool> done, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (true)
        {
            Drain();
            if (done()) return;
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !_wake.Wait(left)) throw new TimeoutException($"Timed out waiting for {what}.");
        }
    }

    public void PumpUntil(Task task, string what, TimeSpan? timeout = null)
    {
        task.ContinueWith(_ => _wake.Release(), TaskScheduler.Default);
        PumpUntil(() => task.IsCompleted, what, timeout);
        if (task.IsFaulted) throw task.Exception!.InnerException ?? task.Exception;
    }

    public T Run<T>(Task<T> task, string what, TimeSpan? timeout = null)
    {
        PumpUntil(task, what, timeout);
        return task.Result;
    }
}

/// <summary>A platform host on a <see cref="UiQueue"/>, answering modal dialogs through <see cref="Answer"/>.</summary>
sealed class QueuePlatform(UiQueue ui) : IPlatformServices
{
    public readonly List<string> Modals = [];
    /// <summary>Called with each modal form; return the name of the button to press (or null to just close it).</summary>
    public Func<Form, string?>? Answer;

    public void PostToUiThread(Action action) => ui.Post(action);
    public void ShowForm(Form form) => form.PerformLayout();
    public void RunModal(Form form)
    {
        form.PerformLayout();
        Modals.Add(form.Caption);
        if (Answer?.Invoke(form) is { } name && form.FindControl(name) is Button b) form.Simulate.Click(b);
        if (form.IsModal) form.Close();
    }
    public void ShowModal(Form form, Action onClosed) { RunModal(form); onClosed(); }
    public void CloseForm(Form form) { }
    public void ProcessEventsUntil(Func<bool> done) { }
    public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) => done(null);
}

/// <summary>An <see cref="IDebugHost"/> that records what the controller tells the IDE.</summary>
sealed class RecordingDebugHost : IDebugHost
{
    public readonly List<string> Lines = [];
    public readonly List<string> Offers = [];
    public string StatusText = "";
    public BuildResult? Build = new(0, false, [], "C:\\app\\App.dll");
    public int Builds, BuildCancels;
    public DebugLaunchOptions Options = new() { Program = "dotnet", Args = ["App.dll"] };

    public void Output(string line) => Lines.Add(line);
    public void Status(string text) => StatusText = text;
    public readonly Queue<Task<BuildResult?>> BuildQueue = new();   // scripted builds, used before Build
    public Task<BuildResult?> BuildAsync() { Builds++; return BuildQueue.Count > 0 ? BuildQueue.Dequeue() : Task.FromResult(Build); }
    public void CancelBuild() => BuildCancels++;
    public DebugLaunchOptions LaunchOptionsFor(string outputFile) => Options with { Args = [outputFile] };
    public void OfferRunWithoutDebugging(string reason) => Offers.Add(reason);
}

/// <summary>
/// A scripted netcoredbg stand-in over <see cref="FakeAdapter"/>: one thread, a two-frame stack in <see cref="File"/>
/// (frame 1 "Calc.Add" at <see cref="Line"/>, frame 2 "Program.Main" at line 12, then an external frame), locals
/// <c>a = 20</c> and <c>calc</c> (with <c>total = 42</c>), evaluation, exception info, and steps that move one line.
/// </summary>
sealed class ScriptedAdapter : IAsyncDisposable
{
    int _bpIds;
    volatile int _line = 3;

    public ScriptedAdapter(string file)
    {
        File = file;
        var h = Fake.Handlers;
        h["initialize"] = _ => new FakeAdapter.Reply(Body: new
        {
            supportsConfigurationDoneRequest = true,
            supportsEvaluateForHovers = true,
            supportsExceptionInfoRequest = true,
            exceptionBreakpointFilters = new[] { new { filter = "all", label = "All exceptions" }, new { filter = "user-unhandled", label = "User-unhandled" } },
        });
        Fake.After["launch"] = () => Fake.SendEvent("initialized");
        h["setBreakpoints"] = args => new FakeAdapter.Reply(Body: new
        {
            breakpoints = args.GetProperty("breakpoints").EnumerateArray().Select(b =>
            {
                int line = b.GetProperty("line").GetInt32();
                return new { id = Interlocked.Increment(ref _bpIds), verified = line != UnboundLine, line };
            }).ToArray(),
        });
        h["threads"] = _ => new FakeAdapter.Reply(Body: new { threads = new[] { new { id = 1, name = "Main Thread" } } });
        h["stackTrace"] = _ => new FakeAdapter.Reply(Body: new
        {
            stackFrames = new object[]
            {
                new { id = 1, name = "Calc.Add", source = new { name = Path.GetFileName(File), path = File }, line = _line, column = 9 },
                new { id = 2, name = "Program.Main", source = new { name = Path.GetFileName(File), path = File }, line = 12, column = 5 },
                new { id = 3, name = "[External Code]", line = 0, column = 0 },
            },
            totalFrames = 3,
        });
        h["scopes"] = args => new FakeAdapter.Reply(Body: new
        {
            scopes = new[] { new { name = "Locals", variablesReference = args.GetProperty("frameId").GetInt32() == 1 ? 100 : 200, expensive = false } },
        });
        h["variables"] = args => new FakeAdapter.Reply(Body: new
        {
            variables = args.GetProperty("variablesReference").GetInt32() switch
            {
                100 => new object[]
                {
                    new { name = "a", value = "20", type = "int", variablesReference = 0 },
                    new { name = "calc", value = "{Calc}", type = "Calc", variablesReference = 101 },
                },
                101 => [new { name = "total", value = "42", type = "int", variablesReference = 0 }],
                200 => [new { name = "args", value = "{string[0]}", type = "string[]", variablesReference = 0 }],
                _ => [],
            },
        });
        h["evaluate"] = args => args.GetProperty("expression").GetString() switch
        {
            "a" => new FakeAdapter.Reply(Body: new { result = "20", type = "int", variablesReference = 0 }),
            "a + 1" => new FakeAdapter.Reply(Body: new { result = "21", type = "int", variablesReference = 0 }),
            "calc" => new FakeAdapter.Reply(Body: new { result = "{Calc}", type = "Calc", variablesReference = 101 }),
            _ => new FakeAdapter.Reply(false, Message: "error CS0103: The name does not exist in the current context"),
        };
        h["exceptionInfo"] = _ => new FakeAdapter.Reply(Body: new
        {
            exceptionId = "System.InvalidOperationException",
            description = "kaboom",
            breakMode = "unhandled",
            details = new { message = "kaboom", typeName = "InvalidOperationException", fullTypeName = "System.InvalidOperationException" },
        });
        Fake.After["next"] = () => { _line++; Stop("step"); };
        Fake.After["stepIn"] = () => Stop("step");
        Fake.After["stepOut"] = () => Stop("step");
        Fake.After["pause"] = () => Stop("pause");
        Fake.After["continue"] = () =>
        {
            if (!ExitOnContinue) return;
            Fake.SendEvent("exited", new { exitCode = 0 });
            Fake.SendEvent("terminated");
        };
    }

    public FakeAdapter Fake { get; } = new();
    public string File { get; }
    public int Line { get => _line; set => _line = value; }
    /// <summary>setBreakpoints answers "not verified" for this line.</summary>
    public int UnboundLine { get; set; } = -1;
    public bool ExitOnContinue { get; set; } = true;
    public int Sessions { get; private set; }

    /// <summary>The <see cref="DebugController.SessionFactory"/>: a session over the in-memory adapter.</summary>
    public DebugSession CreateSession(string adapterPath, BreakpointStore store)
    {
        Sessions++;
        return new DebugSession(new DapTransport(Fake.ClientInput, Fake.ClientOutput), store);
    }

    public void Stop(string reason) => Fake.SendEvent("stopped", new { reason, threadId = 1, allThreadsStopped = true });

    public void Output(string category, string text) => Fake.SendEvent("output", new { category, output = text });

    /// <summary>The lines of every setBreakpoints request, in order.</summary>
    public List<int[]> BreakpointRequests() =>
        [.. Fake.ArgsOf("setBreakpoints").Select(a => a.GetProperty("breakpoints").EnumerateArray().Select(b => b.GetProperty("line").GetInt32()).ToArray())];

    public List<string[]> ExceptionFilterRequests() =>
        [.. Fake.ArgsOf("setExceptionBreakpoints").Select(a => a.GetProperty("filters").EnumerateArray().Select(f => f.GetString()!).ToArray())];

    public ValueTask DisposeAsync() => Fake.DisposeAsync();
}
