using System.Diagnostics;
using System.Text;
using RadSharp.Design.Code;
using RadSharp.Designer;

namespace RadSharp.Tests;

/// <summary>Regression tests for the review fixes in the designer: stale debugger starts, encodings, async git, shutdown.</summary>
[Collection("Designer")]   // sets PlatformHost.Current
public class ReviewFixDesignerTests
{
    sealed class InlineHost : IPlatformServices
    {
        public void ShowForm(Form form) => form.PerformLayout();
        public void RunModal(Form form) { form.PerformLayout(); if (form.IsModal) form.Close(); }
        public void CloseForm(Form form) { }
        public void ProcessEventsUntil(Func<bool> done) { }
        public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) => done(null);
        public void PostToUiThread(Action action) => action();
        public void ShowModal(Form form, Action onClosed) { RunModal(form); onClosed(); }
    }

    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-rf-").FullName;

    static DesignerForm Start(RadProject project, string? buildArgs = null)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        PlatformHost.Current = new InlineHost();
        var d = new DesignerForm(project.ProjectFile, Path.Combine(TempDir(), "designer.json"));
        d.Settings.Editor = EditorKind.Custom;
        d.Settings.CustomEditorCommand = "dotnet --version";
        d.BuildExtraArguments = buildArgs;
        d.SetBounds(0, 0, 1220, 780);
        d.PerformLayout();
        return d;
    }

    // ---- 7: a stale start must not abort a newer one ----

    [Fact]
    public void A_stopped_start_finishing_late_does_not_reset_the_next_start()
    {
        var ui = new UiQueue();
        var host = new RecordingDebugHost();
        var adapter = new ScriptedAdapter(Path.Combine(TempDir(), "Calc.cs"));
        var c = new DebugController(host, null, ui.Post) { SessionFactory = adapter.CreateSession };
        var firstBuild = new TaskCompletionSource<BuildResult?>();
        host.BuildQueue.Enqueue(firstBuild.Task);

        var first = c.StartAsync();
        Assert.Equal(DebugState.Starting, c.State);
        ui.PumpUntil(c.StopAsync(), "stop while starting");
        Assert.Equal(DebugState.Design, c.State);

        var gate = new TaskCompletionSource<string>();
        c.AdapterResolver = (_, ct) => gate.Task.WaitAsync(ct);
        var second = c.StartAsync();   // F5 again: builds (Build is a success), then waits for the adapter
        Assert.Equal(DebugState.Starting, c.State);

        firstBuild.SetResult(new BuildResult(0, false, [], "C:\\app\\App.dll"));   // the first start completes late
        Assert.False(ui.Run(first, "the stale start"));
        Assert.Equal(DebugState.Starting, c.State);   // the second start is untouched

        gate.SetResult("netcoredbg");
        Assert.True(ui.Run(second, "the second start"));
        Assert.Equal(DebugState.Running, c.State);
        ui.PumpUntil(c.StopAsync(), "stop");
        ui.PumpUntil(adapter.DisposeAsync().AsTask(), "adapter");
    }

    // ---- 4 and 14: Find in Files keeps encodings and never rewrites what is not UTF-8 ----

    [Fact]
    public void Find_in_files_reads_UTF16_and_writes_it_back_unchanged_in_encoding()
    {
        var file = Path.Combine(TempDir(), "u16.txt");
        File.WriteAllBytes(file, [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("hello wörld\n")]);
        Assert.Equal("hello wörld\n", FindInFiles.ReadText(file));   // zero bytes are not "binary" with a BOM

        FindInFiles.WriteText(file, "hello there\n");
        Assert.Equal([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("hello there\n")], File.ReadAllBytes(file));
    }

    [Fact]
    public void Find_in_files_will_not_rewrite_a_file_that_is_not_UTF8()
    {
        var file = Path.Combine(TempDir(), "latin.txt");
        byte[] bytes = [.. Encoding.ASCII.GetBytes("caf"), 0xE9, .. Encoding.ASCII.GetBytes(" cafe\n")];
        File.WriteAllBytes(file, bytes);
        Assert.True(FindInFiles.ReadContent(file)!.IsLossy);
        Assert.Contains("cafe", FindInFiles.ReadText(file));   // still searchable
        Assert.Throws<IOException>(() => FindInFiles.WriteText(file, "changed"));
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Replace_in_files_skips_and_reports_non_UTF8_files_and_keeps_BOMs()
    {
        var project = RadProject.Create(TempDir(), "Repl");
        var bomFile = Path.Combine(project.Directory, "Bom.txt");
        File.WriteAllBytes(bomFile, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("needle here\n")]);
        var latin = Path.Combine(project.Directory, "Latin.txt");
        byte[] latinBytes = [.. Encoding.ASCII.GetBytes("needle caf"), 0xE9, (byte)'\n'];
        File.WriteAllBytes(latin, latinBytes);
        var d = Start(project);
        PlatformHost.Current = new ConfirmingHost();

        await d.ReplaceInFilesAsync(new FindOptions("needle", FileFilter: "*.txt"), "thread").WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("thread here\n")], File.ReadAllBytes(bomFile));
        Assert.Equal(latinBytes, File.ReadAllBytes(latin));
        Assert.Contains(d.OutputLines, l => l.StartsWith("Skipped") && l.Contains("Latin.txt"));
    }

    sealed class ConfirmingHost : IPlatformServices
    {
        public void ShowForm(Form form) => form.PerformLayout();
        public void RunModal(Form form)
        {
            form.PerformLayout();
            if (form.FindControl("btnYes") is Button yes) form.Simulate.Click(yes);
            if (form.IsModal) form.Close();
        }
        public void CloseForm(Form form) { }
        public void ProcessEventsUntil(Func<bool> done) { }
        public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) => done(null);
        public void PostToUiThread(Action action) => action();
        public void ShowModal(Form form, Action onClosed) { RunModal(form); onClosed(); }
    }

    // ---- 5: the code editor's documents keep the file's encoding ----

    [Fact]
    public void A_code_document_saves_with_its_BOM_and_a_non_UTF8_file_opens_read_only()
    {
        PlatformHost.Current = new InlineHost();
        var dir = TempDir();
        var bom = Path.Combine(dir, "Bom.cs");
        File.WriteAllBytes(bom, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("class A { }\n")]);
        var doc = new CodeDocument(bom);
        doc.ApplyText("class B { }\n");
        Assert.True(doc.Save());
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("class B { }\n")], File.ReadAllBytes(bom));
        Assert.Single(Directory.GetFiles(dir));   // the temp file of the atomic write is gone

        var latin = Path.Combine(dir, "Latin.cs");
        byte[] bytes = [.. Encoding.ASCII.GetBytes("// caf"), 0xE9, (byte)'\n'];
        File.WriteAllBytes(latin, bytes);
        var ro = new CodeDocument(latin);
        Assert.True(ro.ReadOnly);
        Assert.NotNull(ro.LoadNotice);
        Assert.False(ro.Save());
        Assert.Equal(bytes, File.ReadAllBytes(latin));
    }

    // ---- 3: git runs off the UI thread ----

    [Fact]
    public async Task Git_status_refreshes_asynchronously()
    {
        if (!ProjectToolsTests.GitAvailable()) return;
        var project = RadProject.Create(TempDir(), "GitAsync");
        ProjectToolsTests.Git(project.Directory, "init", "-q");
        ProjectToolsTests.Git(project.Directory, "config", "user.email", "t@example.com");
        ProjectToolsTests.Git(project.Directory, "config", "user.name", "t");
        ProjectToolsTests.Git(project.Directory, "add", ".");
        ProjectToolsTests.Git(project.Directory, "commit", "-q", "-m", "init");
        File.AppendAllText(Path.Combine(project.Directory, "Program.cs"), "// changed\n");
        var d = Start(project);

        d.RefreshGit();   // returns at once; the result arrives through the UI queue
        await d.GitRefreshed.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(GitState.Modified, d.GitStateOf(Path.Combine(project.Directory, "Program.cs")));
    }

    // ---- 1: closing the IDE ends the app started with Ctrl+F5 ----

    [Fact]
    public async Task Closing_the_IDE_kills_the_app_started_with_Ctrl_F5()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "Waiter.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "Console.WriteLine(\"pid \" + Environment.ProcessId); Thread.Sleep(Timeout.Infinite);");
        var d = Start(new RadProject(Path.Combine(dir, "Waiter.csproj")), "-nodeReuse:false -p:UseSharedCompilation=false");

        Assert.True(await d.RunWithoutDebuggingAsync());
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (!d.OutputLines.Any(l => l.StartsWith("pid ")) && DateTime.UtcNow < until) await Task.Delay(50);
        int pid = int.Parse(d.OutputLines.First(l => l.StartsWith("pid "))[4..]);
        Assert.True(d.AppRunning);

        Assert.True(d.Close());

        using var process = TryGet(pid);
        Assert.True(process == null || process.WaitForExit(10000), "the app outlived the IDE");
    }

    static Process? TryGet(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
    }
}
