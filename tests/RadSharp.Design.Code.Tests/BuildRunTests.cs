using System.Runtime.CompilerServices;
using RadSharp.Design.Code;

namespace RadSharp.Tests;

/// <summary>Milestone I4: MSBuild output parsing, Problems sources, launchSettings.json, exception reports, <c>dotnet build</c> and a separate-process run.</summary>
public class BuildRunTests : IClassFixture<BuildRunTests.ConsoleProject>
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    /// <summary>A tiny console app (no packages, so it builds offline) built once for the process tests.</summary>
    public sealed class ConsoleProject : IAsyncLifetime
    {
        public RadProject Project { get; private set; } = null!;
        public BuildResult Result { get; private set; } = null!;

        public static RadProject Create(string name = "Console1")
        {
            var dir = Directory.CreateTempSubdirectory("radsharp-build-").FullName;
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
                Console.WriteLine("env=" + Environment.GetEnvironmentVariable("RAD_TEST"));
                Console.WriteLine("cwd=" + Directory.GetCurrentDirectory());
                Console.Error.WriteLine("to-stderr");
                if (args.Contains("--wait")) Thread.Sleep(Timeout.Infinite);
                """);
            return new RadProject(Path.Combine(dir, name + ".csproj"));
        }

        public async Task InitializeAsync()
        {
            Project = Create();
            using var cts = new CancellationTokenSource(Timeout);
            Result = await ProjectBuild.BuildAsync(Project, _ => { }, extraArguments: "-nodeReuse:false -p:UseSharedCompilation=false", cancel: cts.Token);
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    readonly ConsoleProject _console;
    public BuildRunTests(ConsoleProject console) => _console = console;

    // ---- Parsing ----

    [Fact]
    public void MSBuild_diagnostic_lines_are_parsed()
    {
        var m = MsBuildOutput.TryParse(@"C:\p\Broken.cs(3,13): error CS0029: Cannot implicitly convert type 'string' to 'int' [C:\p\App.csproj]")!;
        Assert.Equal(@"C:\p\Broken.cs", m.File);
        Assert.Equal((3, 13, "error", "CS0029"), (m.Line, m.Column, m.Severity, m.Id));
        Assert.Equal("Cannot implicitly convert type 'string' to 'int'", m.Message);

        var w = MsBuildOutput.TryParse("Program.cs(7,1,7,9): warning CS0168: The variable 'x' is declared but never used [/tmp/App.csproj]", "/tmp/app")!;
        Assert.Equal("warning", w.Severity);
        Assert.Equal(7, w.Line);
        Assert.True(Path.IsPathRooted(w.File));

        var noFile = MsBuildOutput.TryParse("MSBUILD : error MSB1009: Project file does not exist.")!;
        Assert.Equal(("", 0, "MSB1009"), (noFile.File, noFile.Line, noFile.Id));
        Assert.Equal("NETSDK1138", MsBuildOutput.TryParse("/p/App.csproj : warning NETSDK1138: The target framework is out of support.")!.Id);

        Assert.Null(MsBuildOutput.TryParse("  App -> C:\\p\\bin\\Debug\\net10.0\\App.dll"));
        Assert.Null(MsBuildOutput.TryParse("Build succeeded."));
    }

    // ---- Problem sources ----

    sealed class FakeSource(string id) : IProblemSource
    {
        public string Id => id;
        public IReadOnlyList<BuildMessage> Problems { get; private set; } = [];
        public event Action? Changed;
        public void Set(params BuildMessage[] m) { Problems = m; Changed?.Invoke(); }
    }

    [Fact]
    public void Problem_sources_coexist_and_are_replaced_independently()
    {
        var store = new ProblemStore();
        int changes = 0;
        store.Changed += () => changes++;
        var live = new FakeSource("live");
        store.Add(live);
        store.Set("build", [new BuildMessage("A.cs", 1, 1, "error", "CS1", "x"), new BuildMessage("A.cs", 2, 1, "warning", "CS2", "y")]);
        live.Set(new BuildMessage("B.cs", 5, 2, "info", "IDE1", "z"), new BuildMessage("B.cs", 6, 1, "error", "CS3", "w"));
        Assert.Equal(4, store.All.Count);
        Assert.Equal(2, store.Count(ProblemSeverity.Error));
        Assert.Equal(1, store.Count(ProblemSeverity.Warning));
        Assert.Equal(["live", "live", "build", "build"], store.All.Select(p => p.Source));

        store.Set("build", []);   // a new build replaces only its own
        Assert.Equal(2, store.All.Count);
        Assert.All(store.All, p => Assert.Equal("live", p.Source));
        live.Set();
        Assert.Empty(store.All);
        Assert.True(changes >= 4);
    }

    // ---- launchSettings.json ----

    [Fact]
    public void Launch_settings_round_trip_and_keep_other_profiles()
    {
        var project = ConsoleProject.Create("Rt");
        Assert.Equal("", LaunchSettings.Load(project).Arguments);   // no file: defaults

        var config = new RunConfiguration { Arguments = "--fast \"a b\"", WorkingDirectory = "work", Environment = { ["RAD_TEST"] = "1", ["X"] = "y z" } };
        LaunchSettings.Save(project, config);
        var path = LaunchSettings.PathFor(project);
        Assert.EndsWith(Path.Combine("Properties", "launchSettings.json"), path);
        var text = File.ReadAllText(path);
        Assert.Contains("\"commandLineArgs\"", text);
        Assert.Contains("\"Rt\"", text);

        var loaded = LaunchSettings.Load(project);
        Assert.Equal(config.Arguments, loaded.Arguments);
        Assert.Equal("work", loaded.WorkingDirectory);
        Assert.Equal(config.Environment, loaded.Environment);
        Assert.Equal(Path.Combine(project.Directory, "work"), loaded.ResolveWorkingDirectory(project));

        // A hand-written file with another profile: it survives a save, and clearing a setting removes it.
        File.WriteAllText(path, """{ "profiles": { "Other": { "commandName": "Executable", "executablePath": "x.exe" }, "Rt": { "commandName": "Project", "dotnetRunMessages": true, "commandLineArgs": "-a" } } }""");
        var edited = LaunchSettings.Load(project);
        Assert.Equal("-a", edited.Arguments);
        edited.Arguments = "";
        LaunchSettings.Save(project, edited);
        var after = File.ReadAllText(path);
        Assert.Contains("executablePath", after);
        Assert.Contains("dotnetRunMessages", after);
        Assert.DoesNotContain("commandLineArgs", after);
        Assert.Equal(new RunConfiguration().ResolveWorkingDirectory(project), project.Directory);
    }

    [Fact]
    public void Environment_text_parses_and_formats()
    {
        var env = LaunchSettings.ParseEnvironment("A=1\n\n bad line\nB = two words \r\n=skip");
        Assert.Equal(new Dictionary<string, string> { ["A"] = "1", ["B"] = "two words" }, env);
        Assert.Equal("A=1\nB=two words", LaunchSettings.FormatEnvironment(env));
    }

    // ---- Exception reports ----

    static int Here([CallerLineNumber] int n = 0) => n;

    [Fact]
    public void Exception_report_links_frames_to_source_lines()
    {
        Exception ex; int line = 0;
        try { line = Here(); throw new InvalidOperationException("boom\nsecond"); } catch (Exception e) { ex = new AggregateException("outer", e); }
        var report = ExceptionReport.Describe(ex);
        Assert.Contains(report, l => l.Text.StartsWith("System.AggregateException: outer"));
        Assert.Contains(report, l => l.Text.StartsWith("--- inner: System.InvalidOperationException: boom"));
        var frame = Assert.Single(report, l => l.Location != null && l.Location.File.EndsWith("BuildRunTests.cs"));
        Assert.Equal(line, frame.Location!.Line);
        Assert.Contains("Exception_report_links_frames_to_source_lines", frame.Text);
        Assert.True(ExceptionReport.InvolvesAssembly(ex, typeof(BuildRunTests).Assembly));
        Assert.False(ExceptionReport.InvolvesAssembly(ex, typeof(RadProject).Assembly));
    }

    // ---- dotnet build and a separate-process run ----

    [Fact]
    public void Build_succeeds_for_the_console_project()
    {
        Assert.True(_console.Result.Succeeded, string.Join("\n", _console.Result.Messages));
        Assert.EndsWith("Console1.dll", _console.Result.OutputFile);
        Assert.True(File.Exists(_console.Result.OutputFile));
    }

    [Fact]
    public async Task Build_reports_an_injected_error_with_file_and_line()
    {
        var project = ConsoleProject.Create("Broken");
        var broken = Path.Combine(project.Directory, "Oops.cs");
        File.WriteAllText(broken, "class Oops\n{\n    int x = \"text\";\n}\n");
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(Timeout);
        var result = await ProjectBuild.BuildAsync(project, l => { lock (lines) lines.Add(l); }, extraArguments: "-nodeReuse:false -p:UseSharedCompilation=false", cancel: cts.Token);
        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Messages, m => m.Id == "CS0029");
        Assert.Equal(broken, error.File, ignoreCase: true);
        Assert.Equal(3, error.Line);
        Assert.Equal("error", error.Severity);
        Assert.Null(result.OutputFile);
        Assert.Contains(lines, l => l.Contains("CS0029"));   // the output streamed too
    }

    [Fact]
    public async Task Build_can_be_cancelled()
    {
        var project = ConsoleProject.Create("Cancel");
        using var cts = new CancellationTokenSource();
        var first = new TaskCompletionSource();
        cts.CancelAfter(50);   // long before a build finishes
        var result = await ProjectBuild.BuildAsync(project, _ => first.TrySetResult(), extraArguments: "-nodeReuse:false", cancel: cts.Token).WaitAsync(Timeout);
        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_separate_process_streams_output_and_honours_the_run_configuration()
    {
        var project = _console.Project;
        var config = new RunConfiguration { Arguments = "one two", WorkingDirectory = Path.GetTempPath(), Environment = { ["RAD_TEST"] = "from-config" } };
        var lines = new List<string>();
        using var app = AppProcess.StartProject(project, _console.Result.OutputFile!, config, l => { lock (lines) lines.Add(l); });
        Assert.Equal(0, await app.Exited.WaitAsync(Timeout));
        lock (lines)
        {
            Assert.Contains("hello one,two", lines);
            Assert.Contains("env=from-config", lines);
            Assert.Contains("to-stderr", lines);
            Assert.Contains(lines, l => l.StartsWith("cwd=") && Path.TrimEndingDirectorySeparator(l[4..]) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())));
        }
    }

    [Fact]
    public async Task Stop_kills_a_running_process()
    {
        var started = new TaskCompletionSource();
        using var app = AppProcess.StartProject(_console.Project, _console.Result.OutputFile!, new RunConfiguration { Arguments = "--wait" },
            l => { if (l.StartsWith("to-stderr")) started.TrySetResult(); });
        await started.Task.WaitAsync(Timeout);
        Assert.False(app.HasExited);
        app.Stop();
        await app.Exited.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(app.Stopped);
        Assert.True(app.HasExited);
    }
}
