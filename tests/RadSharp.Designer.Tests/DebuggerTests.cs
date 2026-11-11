using RadSharp.CodeEditing;
using RadSharp.Design.Code;
using RadSharp.Designer;
using RadSharp.Ide.Debug;
using RadSharp.Ide.Debug.Protocol;

namespace RadSharp.Tests;

/// <summary>
/// Milestone I6, the debugger UI, against an in-memory adapter: the <see cref="DebugController"/> state machine and
/// commands, live breakpoint and exception filter sync, exception stops, breakpoint lines that follow edits, and
/// persistence. (The real netcoredbg run is in <see cref="DebuggerIntegrationTests"/>.)
/// </summary>
[Collection("Designer")]   // the designer-level tests set PlatformHost.Current
public class DebuggerTests
{
    static string TempFile(string text)
    {
        var dir = Directory.CreateTempSubdirectory("radsharp-i6-").FullName;
        var path = Path.Combine(dir, "Calc.cs");
        File.WriteAllText(path, text);
        return path;
    }

    static readonly string Source = string.Join("\n", Enumerable.Range(1, 14).Select(i => $"// line {i}")) + "\n";

    static (DebugController Controller, RecordingDebugHost Host, UiQueue Ui, ScriptedAdapter Adapter) NewController(string? file = null)
    {
        var ui = new UiQueue();
        var host = new RecordingDebugHost();
        var adapter = new ScriptedAdapter(file ?? TempFile(Source));
        var c = new DebugController(host, null, ui.Post) { SessionFactory = adapter.CreateSession };
        return (c, host, ui, adapter);
    }

    static void Launch(DebugController c, UiQueue ui) =>
        Assert.True(ui.Run(c.LaunchAsync(new DebugLaunchOptions { Program = "dotnet", Args = ["App.dll"] }), "launch"));

    // ---- State machine ----

    [Fact]
    public async Task The_state_machine_goes_Design_Starting_Running_Paused_and_back_with_commands_per_state()
    {
        var (c, host, ui, adapter) = NewController();
        await using var _ = adapter;
        var states = new List<DebugState>();
        c.StateChanged += () => states.Add(c.State);
        var bp = c.Breakpoints.Add(adapter.File, 3);

        Assert.Equal(DebugState.Design, c.State);
        Assert.True(c.CanStart);
        Assert.False(c.CanStop || c.CanStep || c.CanContinue || c.CanPause);

        Launch(c, ui);
        Assert.Equal(DebugState.Running, c.State);
        Assert.True(c.CanPause && c.CanStop);
        Assert.False(c.CanStart || c.CanStep || c.CanContinue);
        Assert.Equal([3], adapter.BreakpointRequests().Single());
        Assert.True(c.Breakpoints.Get(bp.Id)!.Verified);
        Assert.Contains(host.Lines, l => l.StartsWith("Debugging App.dll"));

        // A breakpoint stop: frames, the top frame selected, its locals.
        adapter.Stop("breakpoint");
        ui.PumpUntil(() => c.State == DebugState.Paused && c.Frames.Count == 3, "the stop's frames");
        Assert.Equal("breakpoint", c.StopReason);
        Assert.Equal(0, c.CurrentFrameIndex);
        Assert.Equal(3, c.CurrentFrame!.Line);
        Assert.Equal("20", c.Scopes.Single().Variables.Single(v => v.Name == "a").Value);
        Assert.True(c.CanStep && c.CanContinue && c.CanStop);
        Assert.False(c.CanPause || c.CanStart);
        ui.PumpUntil(() => c.Breakpoints.Get(bp.Id)!.TimesHit == 1, "the hit count");   // counted asynchronously after the stop
        Assert.Equal(1, c.Breakpoints.Get(bp.Id)!.TimesHit);
        Assert.StartsWith("Paused (breakpoint) at Calc.Add", host.StatusText);

        // Step over: running until the step's stop, one line further.
        var step = c.StepOverAsync();
        Assert.Equal(DebugState.Running, c.State);
        Assert.Null(c.CurrentFrame);
        ui.PumpUntil(() => c.State == DebugState.Paused && c.CurrentFrame?.Line == 4, "the step to line 4");
        ui.PumpUntil(step, "the step request");

        // Another frame becomes the context; evaluation uses it.
        ui.PumpUntil(c.SelectFrameAsync(1), "frame 1");
        Assert.Equal(1, c.CurrentFrameIndex);
        Assert.Equal("args", c.Scopes.Single().Variables.Single().Name);
        ui.PumpUntil(c.SelectFrameAsync(0), "frame 0");
        Assert.Equal("21", ui.Run(c.EvaluateAsync("a + 1"), "evaluate")!.Result);
        Assert.Equal("total", Assert.Single(ui.Run(c.GetVariablesAsync(101), "members")).Name);

        // Continue: the app exits, the session ends.
        ui.PumpUntil(c.ContinueAsync(), "continue");
        ui.PumpUntil(() => c.State == DebugState.Design, "the session to end");
        ui.PumpUntil(c.Ending, "the adapter to be disposed");
        Assert.Equal(0, c.LastExitCode);
        Assert.Contains("The app exited with code 0.", host.Lines);
        Assert.Equal([DebugState.Starting, DebugState.Running, DebugState.Paused, DebugState.Running, DebugState.Paused, DebugState.Running, DebugState.Design], states);
        Assert.Null(c.Session);
        Assert.Null(c.Breakpoints.Get(bp.Id)!.Verified);   // adapter state is forgotten with the session
    }

    [Fact]
    public async Task Stop_disconnects_terminating_the_debuggee_and_Pause_stops_a_running_one()
    {
        var (c, host, ui, adapter) = NewController();
        await using var _ = adapter;
        Launch(c, ui);
        ui.PumpUntil(c.PauseAsync(), "pause");
        ui.PumpUntil(() => c.State == DebugState.Paused && c.StopReason == "pause", "the pause stop");

        var stop = c.StopAsync();
        Assert.Equal(DebugState.Design, c.State);              // the IDE is back in Design at once
        ui.PumpUntil(stop, "stop");
        var disconnect = Assert.Single(adapter.Fake.ArgsOf("disconnect"));
        Assert.True(disconnect.GetProperty("terminateDebuggee").GetBoolean());
        Assert.Contains("Debugging stopped.", host.Lines);
        Assert.Null(c.Session);
    }

    [Fact]
    public async Task Start_builds_first_and_explains_a_failed_build_or_a_missing_adapter()
    {
        var (c, host, ui, adapter) = NewController();
        await using var _ = adapter;

        host.Build = new BuildResult(1, false, [], null);
        Assert.False(ui.Run(c.StartAsync(), "a failed start"));
        Assert.Equal(DebugState.Design, c.State);
        Assert.Contains("The debugger was not started: the build failed.", host.Lines);
        Assert.Equal(0, adapter.Sessions);

        host.Build = new BuildResult(0, false, [], "C:\\app\\App.dll");
        c.AdapterResolver = (_, _) => Task.FromException<string>(new IOException("no network"));
        Assert.False(ui.Run(c.StartAsync(), "a start without adapter"));
        Assert.Equal(DebugState.Design, c.State);
        Assert.Contains(host.Offers, o => o.Contains("netcoredbg") && o.Contains("no network"));

        // With the adapter: progress shows in the status bar and Output, then the app launches.
        c.AdapterResolver = (progress, _) => { progress?.Report(0.5); return Task.FromResult("netcoredbg"); };
        Assert.True(ui.Run(c.StartAsync(), "a start"));
        Assert.Equal(DebugState.Running, c.State);
        Assert.Contains("Downloading the debugger (netcoredbg)…", host.Lines);
        Assert.Equal(3, host.Builds);
        Assert.Equal("C:\\app\\App.dll", adapter.Fake.ArgsOf("launch").Single().GetProperty("args")[0].GetString());
        ui.PumpUntil(c.StopAsync(), "stop");
    }

    [Fact]
    public async Task Output_events_stream_to_Output_by_line()
    {
        var (c, host, ui, adapter) = NewController();
        await using var _ = adapter;
        Launch(c, ui);
        adapter.Output("stdout", "hel");
        adapter.Output("stdout", "lo\nwor");
        adapter.Output("stderr", "oops\n");
        adapter.Output("stdout", "ld\n");
        ui.PumpUntil(() => host.Lines.Contains("world"), "the output");
        Assert.Equal(["hello", "oops", "world"], host.Lines.Where(l => l is "hello" or "oops" or "world"));
        ui.PumpUntil(c.StopAsync(), "stop");
    }

    // ---- Breakpoints and exception filters sync live ----

    [Fact]
    public async Task Breakpoint_edits_are_sent_to_the_running_session()
    {
        var (c, _, ui, adapter) = NewController();
        await using var _ = adapter;
        c.Breakpoints.Add(adapter.File, 3);
        Launch(c, ui);
        Assert.Single(adapter.BreakpointRequests());

        var added = c.Breakpoints.Add(adapter.File, 7);
        ui.PumpUntil(() => adapter.BreakpointRequests().LastOrDefault() is [3, 7] && !c.Busy, "the new breakpoint sent");
        Assert.True(c.Breakpoints.Get(added.Id)!.Verified);

        c.Breakpoints.SetEnabled(added.Id, false);
        ui.PumpUntil(() => adapter.BreakpointRequests().LastOrDefault() is [3] && !c.Busy, "the disabled one withdrawn");

        // A line the adapter can't bind is unverified.
        adapter.UnboundLine = 9;
        var unbound = c.Breakpoints.Add(adapter.File, 9);
        ui.PumpUntil(() => c.Breakpoints.Get(unbound.Id)!.Verified == false, "the verdict");
        ui.PumpUntil(c.StopAsync(), "stop");
    }

    [Fact]
    public async Task Exception_filters_default_to_user_unhandled_and_toggle_live()
    {
        var (c, _, ui, adapter) = NewController();
        await using var _ = adapter;
        Launch(c, ui);
        Assert.Equal(["user-unhandled"], adapter.ExceptionFilterRequests().Single());
        Assert.Equal(["all", "user-unhandled"], c.AvailableExceptionFilters.Select(f => f.Filter));

        c.BreakOnAllExceptions = true;
        ui.PumpUntil(() => adapter.ExceptionFilterRequests().Count == 2 && !c.Busy, "the filters re-sent");
        Assert.Equal(["all", "user-unhandled"], adapter.ExceptionFilterRequests()[1]);
        c.BreakOnUnhandledExceptions = false;
        ui.PumpUntil(() => adapter.ExceptionFilterRequests().Count == 3 && !c.Busy, "the filters re-sent again");
        Assert.Equal(["all"], adapter.ExceptionFilterRequests()[2]);
        ui.PumpUntil(c.StopAsync(), "stop");
    }

    [Fact]
    public async Task An_exception_stop_reports_the_type_and_message()
    {
        var (c, host, ui, adapter) = NewController();
        await using var _ = adapter;
        string? reported = null;
        c.ExceptionStopped += text => reported = text;
        Launch(c, ui);
        adapter.Stop("exception");
        ui.PumpUntil(() => reported != null, "the exception");
        Assert.Equal("System.InvalidOperationException: kaboom", reported);
        Assert.Equal(reported, c.ExceptionText);
        Assert.Equal(DebugState.Paused, c.State);
        Assert.Equal(3, c.CurrentFrame!.Line);
        Assert.Contains("Exception thrown: System.InvalidOperationException: kaboom", host.Lines);
        ui.PumpUntil(c.StopAsync(), "stop");
    }

    [Fact]
    public void Arguments_split_like_a_command_line()
    {
        Assert.Equal(["a", "b c", "d\"e"], DebugController.SplitArguments("a \"b c\"  d\\\"e"));
        Assert.Empty(DebugController.SplitArguments("   "));
        Assert.Equal([""], DebugController.SplitArguments("\"\""));
    }

    // ---- Breakpoints follow edits; glyph states ----

    static (CodeDocument Doc, CodeEditor Editor, BreakpointStore Store, BreakpointDecorations Marks) Marks(string text)
    {
        var file = TempFile(text);
        var doc = new CodeDocument(file);
        var editor = ((CodeEditorAdapter)doc.Editor).Editor;
        var store = new BreakpointStore();
        return (doc, editor, store, new BreakpointDecorations(doc, editor, store));
    }

    static int LineStart(CodeEditor e, int line) => e.Snapshot.GetLineStart(line - 1);

    static int[] Lines(BreakpointStore store) => [.. store.All.Select(b => b.Line)];

    [Fact]
    public void Breakpoint_lines_follow_inserted_deleted_and_joined_lines()
    {
        var (doc, editor, store, _) = Marks(string.Join("\n", Enumerable.Range(1, 8).Select(i => $"line{i}")) + "\n");
        store.Add(doc.Path, 3);
        store.Add(doc.Path, 6);

        editor.ApplyEdits([new TextEdit(0, 0, "new\n")]);                             // a line above both
        Assert.Equal([4, 7], Lines(store));
        editor.ApplyEdits([new TextEdit(LineStart(editor, 4), 0, "x")]);              // typing on the line keeps it
        Assert.Equal([4, 7], Lines(store));
        editor.ApplyEdits([new TextEdit(LineStart(editor, 5), 0, "a\nb\n")]);         // two lines between them
        Assert.Equal([4, 9], Lines(store));
        editor.ApplyEdits([new TextEdit(0, LineStart(editor, 2), "")]);               // the first line deleted
        Assert.Equal([3, 8], Lines(store));
        int end2 = editor.Snapshot.GetLineEnd(1);
        editor.ApplyEdits([new TextEdit(end2, 1, "")]);                                // line 3 joined onto line 2
        Assert.Equal([2, 7], Lines(store));
        editor.Undo();                     // undo is an edit too: the line break comes back after line 2's start
        Assert.Equal([2, 8], Lines(store));

        // Deleting the lines from the first one up to the second puts both on one line: the moved one goes.
        editor.ApplyEdits([new TextEdit(LineStart(editor, 2), LineStart(editor, 8) - LineStart(editor, 2), "")]);
        Assert.Equal([2], Lines(store));

        // A reload from disk keeps the lines.
        doc.Editor.Text = "a\nb\nc\nd\n";
        Assert.Equal([2], Lines(store));
    }

    [Fact]
    public void Glyphs_show_enabled_disabled_and_unverified_breakpoints_and_the_current_statement()
    {
        var (doc, editor, store, marks) = Marks("class C\n{\n    void M()\n    {\n        int a = 1;\n    }\n}\n");
        var on = store.Add(doc.Path, 3);
        var off = store.Add(doc.Path, 4, enabled: false);
        var unbound = store.Add(doc.Path, 7);
        store.ApplyAdapterResult(doc.Path, [store.Get(on.Id)!, store.Get(unbound.Id)!],
            [new DapBreakpoint { Id = 1, Verified = true, Line = 3 }, new DapBreakpoint { Id = 2, Verified = false, Message = "No code" }]);

        Decoration GlyphOn(int line) => marks.GetDecorations(editor.Snapshot)!.Items
            .Single(d => d.Kind == DecorationKind.GutterGlyph && editor.Snapshot.GetLineFromOffset(d.Start) == line - 1 && d.Glyph != GutterGlyph.CurrentStatement);
        marks.Invalidate();
        Assert.Equal(GutterGlyph.Breakpoint, GlyphOn(3).Glyph);
        Assert.Null(GlyphOn(3).Color);
        Assert.Equal(GutterGlyph.BreakpointDisabled, GlyphOn(4).Glyph);
        Assert.Equal(GutterGlyph.Breakpoint, GlyphOn(7).Glyph);
        Assert.Equal((byte)96, GlyphOn(7).Color!.Value.A);                             // unverified: dimmed
        Assert.Contains("No code", GlyphOn(7).Message);

        marks.SetStatement(5);
        var items = marks.GetDecorations(editor.Snapshot)!.Items;
        var current = Assert.Single(items, d => d.Glyph == GutterGlyph.CurrentStatement);
        Assert.Equal(4, editor.Snapshot.GetLineFromOffset(current.Start));
        var highlight = Assert.Single(items, d => d.Kind == DecorationKind.Highlight);
        Assert.Equal("int a = 1;", editor.Snapshot.GetText(highlight.Start, highlight.Length));

        editor.ApplyEdits([new TextEdit(0, 0, "// header\n")]);                       // the statement moves with the text
        Assert.Equal(6, marks.StatementLine);
        marks.SetStatement(null);
        Assert.DoesNotContain(marks.GetDecorations(editor.Snapshot)!.Items, d => d.Kind == DecorationKind.Highlight);
        marks.Dispose();
        Assert.DoesNotContain((IDecorationSource)marks, editor.DecorationSources);
    }

    [Fact]
    public void Hover_expressions_take_the_member_access_chain()
    {
        const string text = "var x = this.calc.total + a1;";
        Assert.Equal((8, "this.calc"), DesignerForm.ExpressionAt(text, text.IndexOf("calc", StringComparison.Ordinal) + 1));
        Assert.Equal((8, "this.calc.total"), DesignerForm.ExpressionAt(text, text.IndexOf("total", StringComparison.Ordinal)));
        Assert.Equal((26, "a1"), DesignerForm.ExpressionAt(text, text.IndexOf("a1", StringComparison.Ordinal)));
        Assert.Null(DesignerForm.ExpressionAt(text, text.IndexOf('+')));
        Assert.Null(DesignerForm.ExpressionAt(text, 1));                                // a keyword
    }
}
