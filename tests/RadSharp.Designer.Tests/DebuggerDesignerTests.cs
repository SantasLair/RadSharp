using RadSharp.CodeEditing;
using RadSharp.Design.Code;
using RadSharp.Designer;
using RadSharp.Ide.Debug;
using RadSharp.Threading;

namespace RadSharp.Tests;

/// <summary>
/// Milestone I6 in the real designer form, against the in-memory adapter: gutter clicks and F8, the debug tool bar and
/// title, the paused view (current statement, Call Stack, Locals, Watch, hover), F10 and Shift+F5, exception hints,
/// the Breakpoints window, and breakpoints persisted per project in the session file.
/// </summary>
[Collection("Designer")]   // sets PlatformHost.Current
public sealed class DebuggerDesignerTests : IDisposable
{
    readonly SynchronizationContext? _oldContext = SynchronizationContext.Current;
    readonly UiQueue _ui = new();
    readonly QueuePlatform _platform;

    public DebuggerDesignerTests()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        _platform = new QueuePlatform(_ui);
        PlatformHost.Current = _platform;
    }

    public void Dispose() => SynchronizationContext.SetSynchronizationContext(_oldContext);

    internal static string TempDir() => Directory.CreateTempSubdirectory("radsharp-i6d-").FullName;

    internal static RadProject ConsoleProject(string name, string program, string? dir = null)
    {
        dir ??= TempDir();
        File.WriteAllText(Path.Combine(dir, name + ".csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <DebugType>portable</DebugType>
                <Optimize>false</Optimize>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), program);
        return new RadProject(Path.Combine(dir, name + ".csproj"));
    }

    DesignerForm Start(string? projectFile, string settings)
    {
        // As on the real UI thread: awaits in the designer come back through the UI queue (set in the test body,
        // since xunit installs its own context after the constructor).
        SynchronizationContext.SetSynchronizationContext(new UiSynchronizationContext(_ui.Post));
        var d = new DesignerForm(projectFile, settings);
        d.SetBounds(0, 0, 1280, 820);
        d.PerformLayout();
        return d;
    }

    internal static CodeEditor EditorOf(CodeDocument doc) => ((CodeEditorAdapter)doc.Editor).Editor;

    /// <summary>Clicks the glyph lane of the gutter beside a 1-based line.</summary>
    internal static void ClickGutter(Form form, CodeEditor editor, int line)
    {
        form.PerformLayout();
        int y = editor.AbsoluteTop + editor.TextArea.Y + (line - 1) * editor.LineHeight - editor.ScrollY + editor.LineHeight / 2;
        form.Simulate.Click(editor.AbsoluteLeft + 4, y);
    }

    const string CalcSource = """
        class Calc
        {
            public int Add(int a)
            {
                var total = a + 22;
                return total;
            }
        }

        static class Program
        {
            static void Main() => new Calc().Add(20);
        }

        """;

    [Fact]
    public void A_stop_shows_the_current_statement_call_stack_locals_watch_and_hover_and_Shift_F5_ends_it()
    {
        // Synchronous on purpose: the UI context only runs posted work while the test pumps, so nothing may await.
        var project = ConsoleProject("Paused", "");
        File.Delete(Path.Combine(project.Directory, "Program.cs"));
        var calc = Path.Combine(project.Directory, "Calc.cs");
        File.WriteAllText(calc, CalcSource);
        var d = Start(project.ProjectFile, Path.Combine(TempDir(), "designer.json"));
        var adapter = new ScriptedAdapter(calc) { Line = 5, ExitOnContinue = false };
        try { PausedScenario(d, adapter, project); }
        finally
        {
            _ui.PumpUntil(d.Debugger.DisposeAsync().AsTask(), "the debugger to be disposed");
            _ui.PumpUntil(adapter.DisposeAsync().AsTask(), "the adapter to be disposed");
        }
    }

    void PausedScenario(DesignerForm d, ScriptedAdapter adapter, RadProject project)
    {
        var calc = adapter.File;
        d.Debugger.SessionFactory = adapter.CreateSession;

        // A breakpoint by clicking the gutter's glyph lane; F8 toggles one at the caret.
        var doc = d.OpenCodeFile(calc)!;
        var editor = EditorOf(doc);
        ClickGutter(d, editor, 5);
        var bp = Assert.Single(d.Breakpoints.All);
        Assert.Equal(5, bp.Line);
        Assert.Equal(calc, bp.File, ignoreCase: true);
        Assert.Contains(d.DebugMarksFor(doc)!.GetDecorations(editor.Snapshot)!.Items, i => i.Glyph == GutterGlyph.Breakpoint);
        doc.GoTo(6, 1);
        d.Simulate.Key(Key.F8);
        Assert.Equal([5, 6], d.Breakpoints.All.Select(b => b.Line));
        d.Simulate.Key(Key.F8);
        Assert.Single(d.Breakpoints.All);
        Assert.Equal("F8", d.Commands["Debug.ToggleBreakpoint"].Shortcut);
        Assert.False(d.FindControl("btnDebugStop")!.Visible);    // the debug tool bar only shows while debugging

        // Running: the title says so, the debug tool bar shows, Stop is enabled, the step commands aren't.
        Assert.True(_ui.Run(d.Debugger.LaunchAsync(new DebugLaunchOptions { Program = "dotnet", Args = ["Paused.dll"] }), "the launch"));
        Assert.EndsWith("[Running]", d.Caption);
        Assert.True(d.FindControl("btnDebugStop")!.Visible);
        Assert.True(d.Commands["Run.Stop"].Enabled);
        Assert.False(d.Commands["Debug.StepOver"].Enabled);
        Assert.False(d.Commands["Run.StartDebugging"].Enabled);
        Assert.False(d.Commands["Run.RunWithoutDebugging"].Enabled);
        Assert.True(d.Commands["Debug.Pause"].Enabled);

        // The stop: the file at the current statement, the editor focused, Locals and Call Stack filled.
        d.OpenCodeFile(Path.Combine(project.Directory, Path.GetFileName(project.ProjectFile)));   // something else in front
        adapter.Stop("breakpoint");
        _ui.PumpUntil(() => d.Debugger.IsPaused && d.LocalsView.ValueOf("a") == "20", "the paused view");
        Assert.Same(doc, d.ActiveDocument);
        Assert.True(editor.Focused);
        var marks = d.DebugMarksFor(doc)!;
        Assert.Equal(5, marks.StatementLine);
        Assert.True(marks.StatementIsTopFrame);
        var current = Assert.Single(marks.GetDecorations(editor.Snapshot)!.Items, i => i.Glyph == GutterGlyph.CurrentStatement);
        Assert.Equal(4, editor.Snapshot.GetLineFromOffset(current.Start));
        Assert.Equal(5, doc.Editor.Caret.Line);
        Assert.EndsWith("[Paused]", d.Caption);
        Assert.True(d.Commands["Debug.StepOver"].Enabled);
        Assert.True(d.Commands["Run.StartDebugging"].Enabled);   // F5 continues
        Assert.Equal(["Calc.Add  Calc.cs:5", "Program.Main  Calc.cs:12", "[External Code]  [external code]"], d.CallStackView.List.Items);
        Assert.Equal(0, d.CallStackView.CurrentIndex);
        Assert.True(d.FindToolWindow(DesignerForm.DebugToolWindows.Locals).IsShown);

        // Locals: structured values expand on demand.
        var calcNode = d.LocalsView.Tree.Find("calc")!;
        Assert.Equal("calc = {Calc}  (Calc)", calcNode.Text);
        _ui.PumpUntil(d.LocalsView.Tree.ExpandAsync(calcNode), "calc's members");
        Assert.Equal("total = 42  (int)", d.LocalsView.Tree.Find("calc", "total")!.Text);

        // Hover evaluates the identifier under the pointer.
        int aOffset = doc.Text.IndexOf("a + 22", StringComparison.Ordinal);
        Assert.Equal((aOffset, "a = 20"), _ui.Run(d.DebugHoverAsync(doc, aOffset + 1), "the hover value"));
        Assert.Null(_ui.Run(d.DebugHoverAsync(doc, doc.Text.IndexOf("total", StringComparison.Ordinal)), "an unknown name"));

        // Watch: expressions evaluated in the frame; errors show as such.
        d.WatchView.Add("a + 1");
        d.Dock.Show(d.FindToolWindow(DesignerForm.DebugToolWindows.Watch));
        d.PerformLayout();
        d.WatchView.Input.Text = "nope";
        d.WatchView.Input.Focus();
        d.Simulate.Key(Key.Enter);
        Assert.Equal(["a + 1", "nope"], d.WatchView.Expressions);
        _ui.PumpUntil(() => d.WatchView.ValueOf(0) == "21" && d.WatchView.ValueOf(1)?.StartsWith('<') == true, "the watches");

        // Another frame: its line is marked (not as the current statement) and Locals follows.
        d.CallStackView.Activate(1);
        _ui.PumpUntil(() => d.Debugger.CurrentFrameIndex == 1 && marks.StatementLine == 12, "frame 1");
        Assert.False(marks.StatementIsTopFrame);
        Assert.Equal("{string[0]}", d.LocalsView.ValueOf("args"));
        Assert.DoesNotContain(marks.GetDecorations(editor.Snapshot)!.Items, i => i.Glyph == GutterGlyph.CurrentStatement);
        d.CallStackView.Activate(0);
        _ui.PumpUntil(() => d.Debugger.CurrentFrameIndex == 0 && marks.StatementLine == 5, "frame 0");

        // F10 steps over: the marker moves a line.
        d.Simulate.Key(Key.F10);
        _ui.PumpUntil(() => d.Debugger.IsPaused && marks.StatementLine == 6, "the step");

        // An exception stop shows the exception at the line.
        adapter.Stop("exception");
        _ui.PumpUntil(() => editor.HintText?.Contains("kaboom") == true, "the exception hint");
        Assert.Equal("Exception: System.InvalidOperationException: kaboom", editor.HintText);
        Assert.Contains("Exception thrown: System.InvalidOperationException: kaboom", d.OutputLines);

        // Shift+F5: back to Design, marks and windows cleared, the tool bar hidden.
        d.Simulate.Key("Shift+F5");
        _ui.PumpUntil(() => d.Debugger.State == DebugState.Design && d.Debugger.Ending.IsCompleted, "the session to end");
        Assert.Null(marks.StatementLine);
        Assert.Empty(d.LocalsView.Tree.Nodes);
        Assert.Empty(d.CallStackView.Frames);
        Assert.DoesNotContain("[", d.Caption);
        Assert.False(d.FindControl("btnDebugStop")!.Visible);
        Assert.False(d.Commands["Run.Stop"].Enabled);
        Assert.True(Assert.Single(adapter.Fake.ArgsOf("disconnect")).GetProperty("terminateDebuggee").GetBoolean());
    }

    [Fact]
    public void Breakpoints_persist_per_project_in_the_session_and_the_window_edits_them()
    {
        var settings = Path.Combine(TempDir(), "designer.json");
        var a = ConsoleProject("Alpha", "var x = 1;\nvar y = x + 1;\nConsole.WriteLine(y);\n");
        var b = ConsoleProject("Beta", "Console.WriteLine(2);\n");
        var program = Path.Combine(a.Directory, "Program.cs");

        var d = Start(a.ProjectFile, settings);
        var doc = d.OpenCodeFile(program)!;
        ClickGutter(d, EditorOf(doc), 2);
        doc.GoTo(3, 1);
        d.Simulate.Key(Key.F8);
        Assert.Equal([2, 3], d.Breakpoints.All.Select(x => x.Line));

        // The Breakpoints window: rows, the enabled box, the condition dialog.
        d.Dock.Show(d.FindToolWindow(DesignerForm.DebugToolWindows.Breakpoints));
        d.PerformLayout();
        var window = d.BreakpointsView;
        _ui.Drain();
        Assert.Equal(2, window.Rows.Count);
        Assert.Contains("Program.cs:2", window.List.Items[0]);
        window.ToggleEnabled(1);
        Assert.False(d.Breakpoints.All[1].Enabled);
        _platform.Answer = form =>
        {
            ((Edit)form.FindControl("edtCondition")!).Text = "x > 0";
            ((Edit)form.FindControl("edtHitCondition")!).Text = ">=2";
            return "btnOk";
        };
        window.EditCondition(0);
        Assert.Equal("x > 0", d.Breakpoints.All[0].Condition);
        Assert.Equal(">=2", d.Breakpoints.All[0].HitCondition);
        Assert.Contains("when x > 0", window.List.Items[0]);
        _ui.Drain();
        Assert.Contains("\"breakpoints\"", File.ReadAllText(d.Paths.Session));

        // Restart: the project comes back with its breakpoints.
        Assert.True(d.Close());
        var again = Start(null, settings);
        Assert.Equal(a.ProjectFile, again.Project!.ProjectFile);
        var restored = again.Breakpoints.All;
        Assert.Equal([2, 3], restored.Select(x => x.Line));
        Assert.Equal(("x > 0", ">=2", true), (restored[0].Condition, restored[0].HitCondition, restored[0].Enabled));
        Assert.False(restored[1].Enabled);
        Assert.Equal(program, restored[0].File, ignoreCase: true);

        // Another project has its own (none); switching back restores the first project's.
        Assert.True(again.OpenProject(b.ProjectFile));
        Assert.Empty(again.Breakpoints.All);
        again.ToggleBreakpoint(Path.Combine(b.Directory, "Program.cs"), 1);
        _ui.Drain();
        Assert.True(again.OpenProject(a.ProjectFile));
        Assert.Equal([2, 3], again.Breakpoints.All.Select(x => x.Line));
        Assert.True(again.OpenProject(b.ProjectFile));
        Assert.Equal([1], again.Breakpoints.All.Select(x => x.Line));

        // Delete and Disable All from the window.
        window = again.BreakpointsView;
        window.SelectedIndex = 0;
        window.DeleteSelected();
        Assert.Empty(again.Breakpoints.All);
        Assert.True(again.Close());
    }

    [Fact]
    public void The_Debug_menu_and_shortcuts_come_from_the_registry()
    {
        var project = ConsoleProject("Menu", "Console.WriteLine(1);\n");
        var d = Start(project.ProjectFile, Path.Combine(TempDir(), "designer.json"));
        var shortcuts = new Dictionary<string, string>
        {
            ["Run.StartDebugging"] = "F5", ["Run.Stop"] = "Shift+F5", ["Debug.Restart"] = "Ctrl+Shift+F5", ["Debug.StepOver"] = "F10",
            ["Debug.StepInto"] = "F11", ["Debug.StepOut"] = "Shift+F11", ["Debug.Pause"] = "Ctrl+Alt+P", ["Debug.ToggleBreakpoint"] = "F8",
            ["Run.Run"] = "F9", ["Run.Publish"] = "Ctrl+F9",
        };
        foreach (var (id, key) in shortcuts) Assert.Equal(key, d.Commands[id].Shortcut);
        // No two commands share a key.
        var bound = d.Commands.All.Where(c => c.Shortcut.Length > 0).GroupBy(c => Shortcut.Parse(c.Shortcut)).Where(g => g.Count() > 1).Select(g => string.Join(", ", g));
        Assert.Empty(bound);

        var menu = Assert.Single(((MainMenu)d.FindControl("mnuMain")!).Items, i => i.Caption == "Debug");
        IEnumerable<MenuItem> Leaves(IEnumerable<MenuItem> items) => items.SelectMany(i => i.HasSubmenu ? Leaves(i.Items) : i.Separator ? [] : [i]);
        Assert.All(Leaves(menu.Items), i => Assert.NotNull(d.Commands.CommandOf(i)));
        Assert.Contains(Leaves(menu.Items), i => d.Commands.CommandOf(i) == "View.ToolWindow.callStack");
        Assert.True(d.Commands["Debug.BreakOnUnhandledExceptions"].Checked);
        d.Commands.Execute("Debug.BreakOnAllExceptions");
        Assert.True(d.Debugger.BreakOnAllExceptions);

        // F10 opens the menu bar while nothing is paused (the step command is disabled, so it doesn't take the key).
        Assert.False(d.Commands["Debug.StepOver"].Enabled);
        foreach (var id in new[] { DesignerForm.DebugToolWindows.CallStack, DesignerForm.DebugToolWindows.Locals, DesignerForm.DebugToolWindows.Watch, DesignerForm.DebugToolWindows.Breakpoints })
            Assert.True(d.FindToolWindow(id).IsShown);
        Assert.Equal(DockSide.Right, d.FindToolWindow(DesignerForm.DebugToolWindows.CallStack).Side);
        Assert.Equal(DockSide.Bottom, d.FindToolWindow(DesignerForm.DebugToolWindows.Locals).Side);
        Assert.Same(d.FindToolWindow(DesignerForm.ToolWindows.Output), d.Dock.Region(DockSide.Bottom).ActiveWindow);
    }
}
