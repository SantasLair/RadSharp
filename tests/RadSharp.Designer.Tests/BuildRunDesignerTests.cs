using RadSharp.Design.Code;
using RadSharp.Designer;
using RadSharp.Diagnostics;

namespace RadSharp.Tests;

/// <summary>
/// Milestone I4 on the real designer form: Build (Ctrl+Shift+B) and the Problems window, Run Without Debugging
/// (Ctrl+F5) and Stop (Shift+F5) as a separate process, exceptions of live runs, run configurations.
/// </summary>
[Collection("Designer")]   // sets PlatformHost.Current
public class BuildRunDesignerTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);
    const string NoReuse = "-nodeReuse:false -p:UseSharedCompilation=false";

    sealed class FakeHost : IPlatformServices
    {
        public readonly List<Form> Shown = [];
        public void ShowForm(Form form) { Shown.Add(form); form.PerformLayout(); }
        public void RunModal(Form form)
        {
            form.PerformLayout();
            if (form is RunConfigurationDialog) Answer?.Invoke(form);
            if (form.IsModal) form.Close();
        }
        public Action<Form>? Answer;
        public void CloseForm(Form form) => Shown.Remove(form);
        public void ProcessEventsUntil(Func<bool> done) { }
        public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) => done(null);
        public void PostToUiThread(Action action) => action();
        public void ShowModal(Form form, Action onClosed) { RunModal(form); onClosed(); }
    }

    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-i4-").FullName;

    static (DesignerForm Designer, FakeHost Host) Start(RadProject project)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var host = new FakeHost();
        PlatformHost.Current = host;
        var d = new DesignerForm(project.ProjectFile, Path.Combine(TempDir(), "designer.json"));
        d.Settings.Editor = EditorKind.Custom;
        d.Settings.CustomEditorCommand = "dotnet --version";
        d.BuildExtraArguments = NoReuse;
        d.SetBounds(0, 0, 1220, 780);
        d.PerformLayout();
        return (d, host);
    }

    /// <summary>A console app without packages (builds offline, exits at once unless given --wait).</summary>
    static RadProject ConsoleProject(string name)
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, name + ".csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), """
            Console.WriteLine("hello " + string.Join(",", args));
            Console.Error.WriteLine("started " + Environment.GetEnvironmentVariable("RAD_TEST"));
            if (args.Contains("--wait")) Thread.Sleep(Timeout.Infinite);
            """);
        return new RadProject(Path.Combine(dir, name + ".csproj"));
    }

    static async Task WaitFor(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(50);
        }
    }

    static (int X, int Y) RowCenter(ListBox list, int index)
    {
        var r = list.ItemRect(index);
        return (list.AbsoluteLeft + r.X + r.Width / 2, list.AbsoluteTop + r.Y + r.Height / 2);
    }

    // ---- Build and Problems ----

    [Fact]
    public async Task Build_puts_an_injected_error_in_Problems_and_double_click_opens_the_code_there()
    {
        var project = ConsoleProject("Inject");
        var oops = Path.Combine(project.Directory, "Oops.cs");
        File.WriteAllText(oops, "class Oops\n{\n    void M()\n    {\n        int x = \"text\";\n    }\n}\n");
        var (d, _) = Start(project);

        var result = await d.BuildProjectAsync();
        Assert.False(result!.Succeeded);
        var problem = Assert.Single(d.Problems.All, p => p.Message.Id == "CS0029");
        Assert.Equal(DesignerForm.ProblemSources.Build, problem.Source);
        Assert.Equal(oops, problem.Message.File, ignoreCase: true);
        Assert.Equal(5, problem.Message.Line);
        Assert.Contains(d.OutputLines, l => l.Contains("CS0029"));
        Assert.Contains(d.OutputLines, l => l.StartsWith("Build failed"));
        Assert.Contains("1 error", d.ProblemCounts);

        // The Problems window lists it with its file and line, and double-clicking opens the code document there.
        d.Dock.Show(d.FindToolWindow(DesignerForm.ToolWindows.Problems));
        d.PerformLayout();
        var view = d.ProblemsView;
        var row = view.Rows.ToList().FindIndex(p => p.Message.Id == "CS0029");
        Assert.True(row >= 0);
        Assert.Contains("Oops.cs(5)", view.List.Items[row]);
        var (x, y) = RowCenter(view.List, row);
        d.Simulate.DoubleClick(x, y);
        var doc = Assert.Single(d.OpenCodeDocuments);
        Assert.Equal(oops, doc.Path, ignoreCase: true);
        Assert.Equal(5, doc.Editor.Caret.Line);
        Assert.Same(doc, d.ActiveDocument);

        // Filters: errors off hides it; "current document only" follows the active document.
        view.ShowErrors = false;
        Assert.DoesNotContain(view.Rows, p => p.Message.Id == "CS0029");
        view.ShowErrors = true;
        view.CurrentDocumentOnly = true;
        Assert.Contains(view.Rows, p => p.Message.Id == "CS0029");
        d.OpenCodeFile(Path.Combine(project.Directory, "Program.cs"));
        Assert.DoesNotContain(view.Rows, p => p.Message.Id == "CS0029");
        view.CurrentDocumentOnly = false;

        // Fixing it and building again clears the build's problems.
        d.CloseAllDocuments();
        File.WriteAllText(oops, "class Oops { }\n");
        result = await d.BuildProjectAsync();
        Assert.True(result!.Succeeded, string.Join("\n", result.Messages));
        Assert.Empty(d.Problems.All);
        Assert.Equal("No problems", d.ProblemCounts);
    }

    [Fact]
    public async Task Ctrl_Shift_B_builds_saves_first_and_Cancel_Build_stops_it()
    {
        var project = ConsoleProject("Shortcut");
        var (d, _) = Start(project);
        var program = d.OpenCodeFile(Path.Combine(project.Directory, "Program.cs"))!;
        program.Text += "\nint oops = \"x\";\n";
        Assert.True(program.IsDirty);

        d.Simulate.Key("Ctrl+Shift+B");
        Assert.True(d.IsBuilding);
        Assert.True(d.Commands["Run.CancelBuild"].Enabled);
        Assert.False(d.Commands["Run.Build"].Enabled);
        Assert.False(program.IsDirty);                          // Save All came first
        Assert.Contains("int oops", File.ReadAllText(program.Path));
        d.Commands.Execute("Run.CancelBuild");
        await WaitFor(() => !d.IsBuilding, "the cancelled build to end");
        Assert.Contains(d.OutputLines, l => l == "Build cancelled.");
        Assert.True(d.Commands["Run.Build"].Enabled);

        var result = await d.BuildProjectAsync();               // the cancelled build left nothing behind
        Assert.Contains(result!.Messages, m => m.Id == "CS0029" || m.Id == "CS8803");
    }

    [Fact]
    public void Menus_name_the_commands_Build_Publish_and_Run_Without_Debugging()
    {
        var project = ConsoleProject("Menus");
        var (d, _) = Start(project);
        Assert.Equal("Build", d.Commands["Run.Build"].Title);
        Assert.Equal("Ctrl+Shift+B", d.Commands["Run.Build"].Shortcut);
        Assert.Equal("Publish (NativeAOT)", d.Commands["Run.Publish"].Title);
        Assert.Equal("Ctrl+F5", d.Commands["Run.RunWithoutDebugging"].Shortcut);
        Assert.Equal("Shift+F5", d.Commands["Run.Stop"].Shortcut);
        Assert.Equal("F5", d.Commands["Run.StartDebugging"].Shortcut);
        Assert.Equal("F9", d.Commands["Run.Run"].Shortcut);
        Assert.False(d.Commands["Run.Stop"].Enabled);
        Assert.False(d.Commands["Run.CancelBuild"].Enabled);
    }

    // ---- Run Without Debugging ----

    [Fact]
    public async Task Ctrl_F5_runs_a_separate_process_with_the_run_configuration_and_streams_its_output()
    {
        var project = ConsoleProject("RunOut");
        LaunchSettings.Save(project, new RunConfiguration { Arguments = "alpha beta", Environment = { ["RAD_TEST"] = "cfg" } });
        var (d, _) = Start(project);

        Assert.True(await d.RunWithoutDebuggingAsync());
        await d.AppExited.WaitAsync(Timeout);
        Assert.Contains("hello alpha,beta", d.OutputLines);
        Assert.Contains("started cfg", d.OutputLines);
        Assert.Contains(d.OutputLines, l => l == "The app exited with code 0.");
        Assert.False(d.AppRunning);
    }

    [Fact]
    public async Task Stop_kills_the_app_started_with_Ctrl_F5()
    {
        var project = ConsoleProject("StopMe");
        LaunchSettings.Save(project, new RunConfiguration { Arguments = "--wait" });
        var (d, _) = Start(project);

        d.Simulate.Key("Ctrl+F5");                               // (F5 is the debugger now: DebuggerIntegrationTests)
        await WaitFor(() => d.AppRunning && d.OutputLines.Any(l => l.StartsWith("started")), "the app to start");
        Assert.True(d.Commands["Run.Stop"].Enabled);
        Assert.False(d.Commands["Run.StartDebugging"].Enabled);
        Assert.False(d.Commands["Run.RunWithoutDebugging"].Enabled);

        d.Simulate.Key("Shift+F5");
        await d.AppExited.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(d.AppRunning);
        Assert.Contains(d.OutputLines, l => l == "The app was stopped.");
        Assert.False(d.Commands["Run.Stop"].Enabled);
    }

    [Fact]
    public async Task A_failed_build_does_not_start_the_app()
    {
        var project = ConsoleProject("NoStart");
        File.WriteAllText(Path.Combine(project.Directory, "Bad.cs"), "class Bad { int x = \"s\"; }\n");
        var (d, _) = Start(project);
        Assert.False(await d.RunWithoutDebuggingAsync());
        Assert.False(d.AppRunning);
        Assert.Contains(d.OutputLines, l => l.StartsWith("The app was not started"));
    }

    // ---- Run configuration dialog ----

    [Fact]
    public void The_run_configuration_dialog_writes_launchSettings_json()
    {
        var project = ConsoleProject("Dialog");
        var (d, host) = Start(project);
        host.Answer = form =>
        {
            ((Edit)form.FindControl("edtArguments")!).Text = "--verbose";
            ((Edit)form.FindControl("edtWorkingDirectory")!).Text = "bin";
            ((Memo)form.FindControl("memEnvironment")!).Text = "A=1\nB=2";
            form.Simulate.Click(form.FindControl("btnOk")!);
        };
        Assert.True(d.Commands.Execute("Project.RunConfiguration"));
        var saved = LaunchSettings.Load(project);
        Assert.Equal("--verbose", saved.Arguments);
        Assert.Equal("bin", saved.WorkingDirectory);
        Assert.Equal(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" }, saved.Environment);
        Assert.True(File.Exists(Path.Combine(project.Directory, "Properties", "launchSettings.json")));
    }

    // ---- Exceptions in live runs ----

    [Fact]
    public void A_live_handler_that_throws_gives_a_clickable_stack_frame_on_the_throwing_line()
    {
        var project = RadProject.Create(TempDir(), "LiveThrow");
        var host = new FakeHost();
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        PlatformHost.Current = host;
        var d = new DesignerForm(project.ProjectFile, Path.Combine(TempDir(), "designer.json"));
        d.SetBounds(0, 0, 1220, 780);
        d.PerformLayout();
        var doc = d.Document!;
        var button = doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 24, 24);
        d.EditHandler(button, CodeIntegration.DefaultEvent(button)!);

        var code = Path.Combine(project.Directory, "MainForm.cs");
        var source = File.ReadAllText(code).Replace("\r\n", "\n");
        const string body = "    void Button1_Click(Control sender)\n    {\n        \n    }";
        Assert.Contains(body, source);
        File.WriteAllText(code, source.Replace(body, "    void Button1_Click(Control sender)\n    {\n        throw new InvalidOperationException(\"kaboom\");\n    }"));
        int throwLine = File.ReadAllLines(code).ToList().FindIndex(l => l.Contains("kaboom")) + 1;

        d.Simulate.Key(Key.F9);
        var live = Assert.Single(host.Shown);
        var liveButton = (Button)live.FindControl("Button1")!;
        Guard.Run(() => live.Simulate.Click(liveButton));        // what the host does around every dispatch

        Assert.Contains(d.OutputLines, l => l.Contains("Unhandled exception in the running application"));
        Assert.Contains(d.OutputLines, l => l.StartsWith("System.InvalidOperationException: kaboom"));
        var frameRow = d.OutputLines.ToList().FindIndex(l => l.Contains("Button1_Click") && l.Contains($"MainForm.cs:line {throwLine}"));
        Assert.True(frameRow >= 0, string.Join("\n", d.OutputLines));

        // Double-clicking the frame in Output shows the throwing line in the code editor.
        d.Dock.Show(d.FindToolWindow(DesignerForm.ToolWindows.Output));
        d.PerformLayout();
        var output = (ListBox)d.FindControl("lstOutput")!;
        output.ScrollIntoView(frameRow);
        var (x, y) = RowCenter(output, frameRow);
        d.Simulate.DoubleClick(x, y);
        var open = Assert.Single(d.OpenCodeDocuments);
        Assert.Equal(code, open.Path, ignoreCase: true);
        Assert.Equal(throwLine, open.Editor.Caret.Line);
        Assert.Empty(d.Problems.All);                  // the compile succeeded, so nothing in Problems
    }

    [Fact]
    public void A_compile_error_of_F9_lands_in_Problems_under_its_own_source()
    {
        var project = RadProject.Create(TempDir(), "LiveBroken");
        var host = new FakeHost();
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        PlatformHost.Current = host;
        var d = new DesignerForm(project.ProjectFile, Path.Combine(TempDir(), "designer.json"));
        d.SetBounds(0, 0, 1220, 780);
        d.PerformLayout();
        File.WriteAllText(Path.Combine(project.Directory, "Bad.cs"), "class Bad { int x = \"s\"; }\n");
        d.Problems.Set("lint", [new BuildMessage("X.cs", 1, 1, "warning", "L1", "style")]);

        Assert.Null(d.Run());
        Assert.Contains(d.Problems.All, p => p.Source == DesignerForm.ProblemSources.LiveCompile && p.Message.Id == "CS0029");
        Assert.Contains(d.Problems.All, p => p.Source == "lint");     // another source is untouched
        d.CloseProject();
        Assert.DoesNotContain(d.Problems.All, p => p.Source == DesignerForm.ProblemSources.LiveCompile);
    }
}
