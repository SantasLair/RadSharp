using System.Diagnostics;
using RadSharp.CodeEditing;
using RadSharp.Design.Code;
using RadSharp.Designer;
using RadSharp.Drawing;
using Xunit.Abstractions;
using static RadSharp.Tests.PolishSupport;

namespace RadSharp.Tests;

/// <summary>
/// The I9 feel budgets as benchmarks. Each test measures, writes the numbers to the test output (and appends them to the file
/// named by <c>RADSHARP_I9_BUDGETS</c>), and asserts a generous ceiling so a slow CI machine does not flake while a real
/// regression (an order of magnitude) still fails. The plan's targets are in the comments and in the message.
/// </summary>
[Collection("Designer")]
public sealed class BudgetTests : IDisposable
{
    readonly ITestOutputHelper _out;
    readonly SynchronizationContext? _oldContext = SynchronizationContext.Current;

    public BudgetTests(ITestOutputHelper output)
    {
        _out = output;
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
    }

    public void Dispose()
    {
        LanguageHost.Enabled = false;
        LanguageHost.DefaultOptions = null;
        SynchronizationContext.SetSynchronizationContext(_oldContext);
    }

    void Report(string name, string numbers, string target)
    {
        var line = $"BUDGET {name}: {numbers} (plan target {target})";
        _out.WriteLine(line);
        Console.WriteLine(line);
        if (Environment.GetEnvironmentVariable("RADSHARP_I9_BUDGETS") is { Length: > 0 } file) File.AppendAllText(file, line + Environment.NewLine);
    }

    static double Ms(Stopwatch sw) => sw.Elapsed.TotalMilliseconds;

    static void Paint(DesignerForm d)
    {
        using var bitmap = FormRenderer.RenderToBitmap(d);
        Assert.True(bitmap.Width > 0);
    }

    [Fact]
    public void Cold_start_to_the_welcome_page()   // plan: < 1.5 s (process start to painted welcome page; this is the in-process part)
    {
        NewHost();
        var settings = Path.Combine(TempDir(), "designer.json");
        var s = new DesignerSettings();
        foreach (var i in new[] { 1, 2, 3 }) s.AddRecent(NewProject("W" + i).ProjectFile);
        s.Save(settings);

        var sw = Stopwatch.StartNew();
        var d = new DesignerForm(null, settings);
        d.SetBounds(0, 0, 1220, 780);
        d.PerformLayout();
        Paint(d);
        sw.Stop();
        Assert.True(d.WelcomeVisible);
        Report("cold start to welcome page (in-process, first form of the run)", $"{Ms(sw):F0} ms", "< 1500 ms");
        Assert.True(Ms(sw) < 5000, $"{Ms(sw):F0} ms");
    }

    [Fact]
    public void Open_a_sample_project_and_show_its_main_form()   // plan: < 2 s
    {
        NewHost();
        var sample = Path.Combine(RepoRoot, "samples", "Calculator", "Calculator.csproj");
        var settings = Path.Combine(TempDir(), "designer.json");
        var sw = Stopwatch.StartNew();
        var d = new DesignerForm(sample, settings);
        d.SetBounds(0, 0, 1220, 780);
        d.PerformLayout();
        Paint(d);
        sw.Stop();
        Assert.NotNull(d.ActiveForm);
        Report("open sample project and show main form", $"{Ms(sw):F0} ms", "< 2000 ms");
        Assert.True(Ms(sw) < 6000, $"{Ms(sw):F0} ms");
    }

    [Fact]
    public void F12_toggles_between_form_and_code_quickly()   // plan: < 100 ms
    {
        NewHost();
        var project = PolishLanguageTests.SampleCopy();
        var d = PolishSupport.Start(project.ProjectFile);
        Paint(d);
        d.ToggleFormCode();   // opens the code once; later toggles only switch tabs
        Paint(d);
        var times = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            var sw = Stopwatch.StartNew();
            d.ToggleFormCode();
            d.PerformLayout();
            Paint(d);
            sw.Stop();
            times.Add(Ms(sw));
        }
        times.Sort();
        Report("F12 form/code toggle (switch + layout + paint)", $"median {times[5]:F1} ms, max {times[^1]:F1} ms", "< 100 ms");
        Assert.True(times[5] < 1000, $"{times[5]:F0} ms");
    }

    [Fact]
    public void Warm_F9_compile_and_start_of_a_sample()   // plan: < 1.5 s
    {
        NewHost();
        var project = PolishLanguageTests.SampleCopy();
        var d = PolishSupport.Start(project.ProjectFile);
        var cold = Stopwatch.StartNew();
        Assert.NotNull(d.Run());
        cold.Stop();
        var sw = Stopwatch.StartNew();
        var form = d.Run();
        sw.Stop();
        Assert.NotNull(form);
        Report("F9 live run of a sample", $"first {Ms(cold):F0} ms, warm {Ms(sw):F0} ms", "warm < 1500 ms");
        Assert.True(Ms(sw) < 5000, $"{Ms(sw):F0} ms");
    }

    [Fact]
    public void Keystroke_to_paint_while_the_language_service_is_busy()   // plan: < 16 ms
    {
        var host = new PolishQueueHost();
        PlatformHost.Current = host;
        host.SetUiContext();
        LanguageHost.Enabled = true;
        LanguageHost.DefaultOptions = new RadSharp.Ide.Language.ProjectWorkspaceOptions { WatchFiles = false, Restore = RadSharp.Ide.Language.RestoreMode.Never };
        LanguageHost.DiagnosticsDelay = TimeSpan.FromMilliseconds(50);
        LanguageHost.PushDelay = TimeSpan.FromMilliseconds(30);   // push to the workspace while typing, so it really is busy
        try
        {
            var project = PolishLanguageTests.SampleCopy();
            var d = PolishSupport.Start(project.ProjectFile);
            var lang = Assert.Single(d.LanguageHosts);
            host.PumpUntil(lang.Ready, "the workspace");
            host.PumpUntil(() => lang.IsReady || lang.Error != null, "ready");
            var code = d.OpenCodeFile(Path.Combine(project.Directory, "MainForm.cs"))!;
            var editor = Assert.IsType<CodeEditor>(code.View);
            var language = lang.Find(code)!;
            host.PumpUntil(() => language.HasSemanticColours, "semantic colours");
            code.Focus();
            d.PerformLayout();
            editor.Select(editor.Text.IndexOf("ShowSettings();", StringComparison.Ordinal), 0);
            Paint(d);   // warm the line cache and fonts

            // Keep the service busy: references and formatting requests in flight while the user types.
            for (int i = 0; i < 6; i++)
            {
                _ = d.FindReferences();
                _ = d.Format(selection: false);
            }

            var times = new List<double>();
            foreach (var ch in "var x = 12345; // typing while busy")
            {
                var sw = Stopwatch.StartNew();
                d.Simulate.Type(ch.ToString());
                Paint(d);
                sw.Stop();
                times.Add(Ms(sw));
                host.PumpUntil(() => true, "pump");   // deliver whatever the service finished, as the UI loop would
            }
            times.Sort();
            double median = times[times.Count / 2], p95 = times[(int)(times.Count * 0.95)];
            Report("keystroke to paint, language service busy (whole designer window)", $"median {median:F1} ms, p95 {p95:F1} ms, max {times[^1]:F1} ms", "< 16 ms");
            Assert.True(median < 250, $"median {median:F0} ms");
        }
        finally { LanguageHost.PushDelay = TimeSpan.FromMilliseconds(120); LanguageHost.DiagnosticsDelay = TimeSpan.FromMilliseconds(300); }
    }
}
