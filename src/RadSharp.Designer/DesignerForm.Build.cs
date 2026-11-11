using RadSharp.Design.Code;
using RadSharp.Drawing;

namespace RadSharp.Designer;

/// <summary>
/// Build, run modes and the Problems window (milestone I4): Ctrl+Shift+B builds with <c>dotnet build</c>, Ctrl+F5 runs the
/// built app as a separate process (Shift+F5 stops it), F5 will start the debugger, F9 stays the in-process live run
/// (whose unhandled exceptions are reported here too), Publish is the NativeAOT build, and Project › Run Configuration
/// edits <c>Properties/launchSettings.json</c>.
/// </summary>
public partial class DesignerForm
{
    /// <summary>The ids of the problem sources the designer itself registers.</summary>
    public static class ProblemSources
    {
        public const string Build = "build", LiveCompile = "compile";
    }

    readonly ProblemStore _problemStore = new();
    readonly object _outputLock = new();
    ProblemsWindow _problemsWindow = null!;
    Label _lblProblemCounts = null!;
    CancellationTokenSource? _buildCts;
    AppProcess? _app;

    /// <summary>All problems of all sources (the build's, the live compile's, later the language service's).</summary>
    public ProblemStore Problems => _problemStore;
    public ProblemsWindow ProblemsView => _problemsWindow;
    public bool IsBuilding => _building;
    public bool AppRunning => _app is { HasExited: false };

    /// <summary>Extra <c>dotnet build</c> arguments (tests pass <c>-nodeReuse:false</c> and the like).</summary>
    public string? BuildExtraArguments { get; set; }

    void InitBuildSupport()
    {
        _problemsWindow = new ProblemsWindow(_problemStore)
        {
            Align = Align.Client,
            Navigate = m => NavigateToCode(m.File, Math.Max(1, m.Line), Math.Max(1, m.Column)),
            CurrentFile = () => _docs.Active?.Path,
        };
        lstProblems.Visible = false;
        pnlProblems.Add(_problemsWindow);
        _docs.ActiveChanged += _ => { if (_problemsWindow.CurrentDocumentOnly) _problemsWindow.Refresh(); };

        _lblProblemCounts = new Label { Name = "lblProblemCounts", Align = Align.Right, Alignment = TextAlign.Right, Caption = "No problems" };
        _lblProblemCounts.SetBounds(0, 0, 230, 24);
        _lblProblemCounts.Click += _ => _dock.Show(FindToolWindow(ToolWindows.Problems));
        sbMain.Add(_lblProblemCounts);
        _problemStore.Changed += UpdateProblemCounts;
    }

    /// <summary>The status bar text: error and warning counts of every problem source.</summary>
    public string ProblemCounts => _lblProblemCounts.Caption;

    void UpdateProblemCounts()
    {
        int e = _problemStore.Count(ProblemSeverity.Error), w = _problemStore.Count(ProblemSeverity.Warning);
        _lblProblemCounts.Caption = e + w == 0 ? "No problems" : $"{e} error{(e == 1 ? "" : "s")}, {w} warning{(w == 1 ? "" : "s")}";
    }

    void RegisterBuildCommands()
    {
        var c = _commands;
        void Reg(string id, string title, string category, Action run, string shortcut = "", Func<bool>? enabled = null) =>
            c.Register(new Command(id, title, category, run, shortcut, enabled));
        Reg("Run.Build", "Build", "Run", () => _ = BuildProjectAsync(), "Ctrl+Shift+B", () => HasProject && !_building);
        Reg("Run.CancelBuild", "Cancel Build", "Run", CancelBuild, "", () => _building);
        Reg("Run.Publish", "Publish (NativeAOT)", "Run", () => _ = PublishAsync(), "Ctrl+F9", () => HasProject && !_building);
        Reg("Run.RunWithoutDebugging", "Run Without Debugging", "Run", () => _ = RunWithoutDebuggingAsync(), "Ctrl+F5", () => HasProject && !_building && !AppRunning && !DebugActive);
        Reg("Run.StartDebugging", "Start Debugging", "Run", () => _ = StartDebuggingAsync(), "F5",
            () => _debugger.CanContinue || (HasProject && !_building && !AppRunning && _debugger.CanStart));   // F5 also continues when paused
        Reg("Run.Stop", "Stop", "Run", StopApp, "Shift+F5", () => AppRunning || DebugActive);
        Reg("Project.RunConfiguration", "Run Configuration…", "Project", ShowRunConfiguration, "", () => HasProject);
    }

    // ---- Output with locations ----

    /// <summary>Appends a line to Output from any thread (a worker reading a process, say).</summary>
    void PostOutput(string line, BuildMessage? location = null) =>
        Application.Post(() => { lock (_outputLock) Output(line, location); });

    /// <summary>An exception and its stack trace in Output; every frame with a source file and line is clickable.</summary>
    void ReportException(string title, Exception exception)
    {
        Output(title);
        foreach (var line in ExceptionReport.Describe(exception)) Output(line.Text, line.Location);
        sbMain.SimpleText = title;
    }

    // ---- Build (dotnet build, Debug) ----

    /// <summary>
    /// Build (Ctrl+Shift+B): saves everything, runs <c>dotnet build</c> in Debug streaming its output to Output, and
    /// puts the diagnostics in Problems (source <see cref="ProblemSources.Build"/>). Null if there is no project or a
    /// build is running. Cancel with <see cref="CancelBuild"/>.
    /// </summary>
    public async Task<BuildResult?> BuildProjectAsync()
    {
        if (_project is not { } project || _building) return null;
        SaveAll();
        _building = true;
        var cts = _buildCts = new CancellationTokenSource();
        RefreshCommandState();
        ClearOutput();
        _problemStore.Clear(ProblemSources.Build);
        Output($"Building {project.Name} (Debug)…");
        BuildResult? result = null;
        try
        {
            result = await ProjectBuild.BuildAsync(project, line => PostOutput(line, MsBuildOutput.TryParse(line, project.Directory)),
                "Debug", BuildExtraArguments, null, cts.Token);
            Application.Post(() =>
            {
                _problemStore.Set(ProblemSources.Build, result.Messages);
                Output(result.Cancelled ? "Build cancelled."
                    : result.Succeeded ? $"Build succeeded: {result.Warnings} warning(s), 0 error(s)."
                    : $"Build failed: {result.Errors} error(s), {result.Warnings} warning(s) (exit code {result.ExitCode}).");
                sbMain.SimpleText = result.Cancelled ? "Build cancelled" : result.Succeeded ? "Build succeeded" : "Build failed";
            });
            if (result is { Succeeded: false, Cancelled: false, Errors: > 0 }) Application.Post(() => _dock.Show(FindToolWindow(ToolWindows.Problems)));   // show what to fix
            return result;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Application.Post(() => ReportMissingDotnet(e.Message));
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Application.Post(() => { Output("The build could not run: " + e.Message); sbMain.SimpleText = "The build could not run"; });
            return null;
        }
        finally
        {
            _building = false;
            _buildCts = null;
            cts.Dispose();
            Application.Post(RefreshCommandState);
        }
    }

    /// <summary>Cancel Build: kills the running build or publish.</summary>
    public void CancelBuild() => _buildCts?.Cancel();

    // ---- Publish (NativeAOT) ----

    /// <summary>Publish: <c>dotnet publish</c> with NativeAOT for this OS (the former "Build"). Cancellable.</summary>
    public async Task<bool> PublishAsync()
    {
        if (_project is not { } project || _building) return false;
        SaveAll();
        _building = true;
        var cts = _buildCts = new CancellationTokenSource();
        RefreshCommandState();
        ClearOutput();
        _problemStore.Clear(ProblemSources.Build);
        Output($"Publishing {project.Name} (NativeAOT, {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier})…");
        var messages = new List<BuildMessage>();
        try
        {
            var (exit, dir) = await ProjectBuilder.PublishAsync(project, line =>
            {
                var m = MsBuildOutput.TryParse(line, project.Directory);
                if (m != null) lock (messages) { if (!messages.Contains(m)) messages.Add(m); }
                PostOutput(line, m);
            }, cancel: cts.Token);
            Application.Post(() =>
            {
                lock (messages) _problemStore.Set(ProblemSources.Build, messages);
                Output(exit == 0 ? $"Publish succeeded: {dir}" : $"Publish failed (exit code {exit}).");
                sbMain.SimpleText = exit == 0 ? "Publish succeeded" : "Publish failed";
            });
            return exit == 0;
        }
        catch (OperationCanceledException)
        {
            Application.Post(() => { Output("Publish cancelled."); sbMain.SimpleText = "Publish cancelled"; });
            return false;
        }
        finally
        {
            _building = false;
            _buildCts = null;
            cts.Dispose();
            Application.Post(RefreshCommandState);
        }
    }

    // ---- Run Without Debugging (Ctrl+F5) ----

    /// <summary>
    /// Run Without Debugging (Ctrl+F5): builds in Debug and starts the app as a separate process using the run
    /// configuration (<see cref="LaunchSettings"/>); its stdout and stderr stream to Output. Completes when the
    /// process has started (not when it ends: see <see cref="AppExited"/>). False if the build failed.
    /// </summary>
    public async Task<bool> RunWithoutDebuggingAsync()
    {
        if (_project is not { } project || _building || AppRunning) return false;
        var result = await BuildProjectAsync();
        if (result is not { Succeeded: true, OutputFile: { } file })
        {
            if (result is { Cancelled: false }) Application.Post(() => Output("The app was not started: the build failed."));
            return false;
        }
        var config = LaunchSettings.Load(project);
        try
        {
            Application.Post(() => Output($"Starting {Path.GetFileName(file)}{(config.Arguments.Length > 0 ? " " + config.Arguments : "")}…"));
            var app = AppProcess.StartProject(project, file, config, line => PostOutput(line));
            _app = app;
            _appExited = app.Exited.ContinueWith(t => Application.Post(() =>
            {
                Output(app.Stopped ? "The app was stopped." : t.IsCompletedSuccessfully ? $"The app exited with code {t.Result}." : "The app ended unexpectedly.");
                if (_app == app) _app = null;
                app.Dispose();   // it has exited: releases the process handle
                RefreshCommandState();
            }), TaskScheduler.Default);
            Application.Post(RefreshCommandState);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Application.Post(() => Output("Could not start the app: " + e.Message));
            return false;
        }
    }

    Task _appExited = Task.CompletedTask;

    /// <summary>Completes after the separate-process app's exit was reported in Output (for tests).</summary>
    public Task AppExited => _appExited;

    /// <summary>Stop (Shift+F5): ends the debugging session (killing the debuggee), or kills the separate-process app and what it started.</summary>
    public void StopApp()
    {
        if (DebugActive) { _ = _debugger.StopAsync(); return; }
        _app?.Stop();
    }

    /// <summary>Start Debugging (F5): builds and starts the app under the debugger, or continues when paused (see DesignerForm.Debug.cs).</summary>
    public Task<bool> StartDebuggingAsync() => StartOrContinueDebuggingAsync();

    // ---- Run configuration ----

    void ShowRunConfiguration()
    {
        if (_project is not { } project) return;
        var dialog = new RunConfigurationDialog(LaunchSettings.Load(project), project, this);
        if (dialog.ShowModal() != ModalResult.Ok) return;
        try
        {
            LaunchSettings.Save(project, dialog.Result);
            Output($"Saved {Path.GetRelativePath(project.Directory, LaunchSettings.PathFor(project))}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dialogs.MessageDlg($"Could not save the run configuration:\n{e.Message}", MessageType.Error, owner: this);
        }
    }

    // ---- Live-run exceptions ----

    /// <summary>
    /// The live form's event handlers are dispatched by the host inside <c>Guard.Run</c>, which reports an exception through
    /// <see cref="Application.ThreadException"/>: the designer listens for the ones that came from the live assembly, shows
    /// them in Output with a clickable stack and marks them handled (so no error dialog covers the designer).
    /// </summary>
    void AttachLiveHooks() => Application.ThreadException += OnLiveException;

    void DetachLiveHooks() => Application.ThreadException -= OnLiveException;

    void OnLiveException(object? sender, ThreadExceptionEventArgs e)
    {
        if (_live?.Assembly is not { } assembly || !ExceptionReport.InvolvesAssembly(e.Exception, assembly)) return;
        e.Handled = true;
        ReportException("Unhandled exception in the running application", e.Exception);
    }

    /// <summary>Called when the project closes: stops the app, forgets the build's problems.</summary>
    void ResetBuildState()
    {
        ResetDebugState();
        StopApp();
        _buildCts?.Cancel();
        _problemStore.Clear(ProblemSources.Build);
        _problemStore.Clear(ProblemSources.LiveCompile);
    }
}
