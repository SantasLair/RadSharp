using System.Diagnostics;
using RadSharp.CodeEditing;
using RadSharp.Designer;
using RadSharp.Ide.Debug.Acquisition;
using RadSharp.Threading;

namespace RadSharp.Tests;

/// <summary>
/// Skipped unless netcoredbg is already available without a download (the NETCOREDBG variable, PATH, or the tool
/// cache): the same check as the debugger engine's own integration tests.
/// </summary>
public sealed class NetCoreDbgDesignerFactAttribute : FactAttribute
{
    public NetCoreDbgDesignerFactAttribute()
    {
        string? found;
        try { found = new NetCoreDbgAcquisition().FindExisting(); }
        catch (Exception) { found = null; }
        if (found is null) Skip = "netcoredbg is not available (set NETCOREDBG, put it on PATH, or install it into the tool cache).";
    }
}

/// <summary>
/// Milestone I6 end to end in the designer with the real netcoredbg: a breakpoint set by a gutter click in a button
/// handler, F5 (save, build, launch), the paused state at the right line with a local's value, F10, and Shift+F5
/// ending in Design with the debuggee gone.
/// </summary>
/// <remarks>
/// The debuggee is a console app whose top-level code calls the handler (<c>Button1_Click</c>) directly. A RADSharp
/// window app would need a display and a simulated click to reach it, and its RADSharp packages restored; calling the
/// handler from <c>Main</c> reaches the breakpoint deterministically, with no UI and nothing to restore.
/// </remarks>
[Collection("Designer")]   // sets PlatformHost.Current
public sealed class DebuggerIntegrationTests : IDisposable
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);
    readonly SynchronizationContext? _oldContext = SynchronizationContext.Current;
    readonly UiQueue _ui = new() { Timeout = Timeout };

    public DebuggerIntegrationTests()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        PlatformHost.Current = new QueuePlatform(_ui);
    }

    public void Dispose() => SynchronizationContext.SetSynchronizationContext(_oldContext);

    const string Program = """
        System.Console.WriteLine("pid " + System.Environment.ProcessId);
        var calculator = new Calculator();
        calculator.Button1_Click(null);
        System.Console.WriteLine("done");

        class Calculator
        {
            public void Button1_Click(object? sender)
            {
                int total = 40;
                total += 2;
                System.Console.WriteLine("total " + total);
            }
        }

        """;

    [NetCoreDbgDesignerFact]
    public void F5_hits_a_gutter_breakpoint_in_a_handler_reads_a_local_steps_and_Shift_F5_kills_the_debuggee()
    {
        var project = DebuggerDesignerTests.ConsoleProject("Debuggee", Program);
        SynchronizationContext.SetSynchronizationContext(new UiSynchronizationContext(_ui.Post));
        var d = new DesignerForm(project.ProjectFile, Path.Combine(DebuggerDesignerTests.TempDir(), "designer.json"))
        {
            BuildExtraArguments = "-nodeReuse:false -p:UseSharedCompilation=false",
        };
        d.SetBounds(0, 0, 1280, 820);
        d.PerformLayout();
        int pid = 0;
        try
        {
            var doc = d.OpenCodeFile(Path.Combine(project.Directory, "Program.cs"))!;
            var editor = DebuggerDesignerTests.EditorOf(doc);
            DebuggerDesignerTests.ClickGutter(d, editor, 11);                 // total += 2;
            Assert.Equal(11, Assert.Single(d.Breakpoints.All).Line);

            d.Simulate.Key(Key.F5);
            Assert.True(d.Debugger.IsActive);
            _ui.PumpUntil(() => d.Debugger.IsPaused && d.LocalsView.ValueOf("total") != null
                || (d.Debugger.State == DebugState.Design && d.OutputLines.Any(l => l.Contains("not started") || l.Contains("could not"))), "the breakpoint");
            Assert.True(d.Debugger.IsPaused, string.Join("\n", d.OutputLines));
            _ui.PumpUntil(() => d.OutputLines.Any(l => l.StartsWith("pid ")), "the debuggee's pid in Output");
            pid = int.Parse(d.OutputLines.First(l => l.StartsWith("pid "))[4..]);

            // Paused at the breakpoint: the editor shows the current statement there, Locals has the value.
            Assert.Equal("breakpoint", d.Debugger.StopReason);
            Assert.Same(doc, d.ActiveDocument);
            var marks = d.DebugMarksFor(doc)!;
            Assert.Equal(11, marks.StatementLine);
            var glyph = Assert.Single(marks.GetDecorations(editor.Snapshot)!.Items, i => i.Glyph == GutterGlyph.CurrentStatement);
            Assert.Equal(10, editor.Snapshot.GetLineFromOffset(glyph.Start));
            Assert.Equal("40", d.LocalsView.ValueOf("total"));
            Assert.True(d.Breakpoints.All[0].Verified);
            Assert.Equal(1, d.Breakpoints.All[0].TimesHit);
            Assert.Contains(d.CallStackView.Frames, f => f.Name.Contains("Button1_Click"));
            Assert.EndsWith("[Paused]", d.Caption);

            // F10: the next line, and the local updated.
            d.Simulate.Key(Key.F10);
            _ui.PumpUntil(() => d.Debugger.IsPaused && marks.StatementLine == 12 && d.LocalsView.ValueOf("total") == "42", "the step over");

            // Shift+F5: Design again, and the debuggee is gone.
            d.Simulate.Key("Shift+F5");
            Assert.Equal(DebugState.Design, d.Debugger.State);
            _ui.PumpUntil(d.Debugger.Ending, "the adapter to go");
            Assert.Null(marks.StatementLine);
            Assert.False(d.Commands["Run.Stop"].Enabled);
            Assert.True(ProcessGone(pid, TimeSpan.FromSeconds(20)), $"The debuggee (pid {pid}) is still running.");
            Assert.DoesNotContain("done", d.OutputLines);
        }
        finally
        {
            if (pid != 0 && !ProcessGone(pid, TimeSpan.Zero))
                try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch (Exception) { }
            _ui.PumpUntil(d.Debugger.DisposeAsync().AsTask(), "the debugger to be disposed");   // pumped, not awaited: see the UI context
        }
    }

    static bool ProcessGone(int pid, TimeSpan wait)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.WaitForExit(wait);
        }
        catch (ArgumentException) { return true; }        // no such process
        catch (InvalidOperationException) { return true; }
    }
}
