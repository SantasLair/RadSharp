using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using RadSharp.Design;
using RadSharp.Design.Code;
using RadSharp.Designer;

namespace RadSharp.Tests;

/// <summary>
/// The project's own controls (and its libraries') on the palette through a design session, and F9 off the UI thread,
/// driven on the real designer form with a host that queues what it posts, as a real UI thread does: the test thread
/// is the UI thread and runs the queue (<see cref="QueueHost.Pump"/>).
/// </summary>
[Collection("PlatformHost")]   // each test installs its own PlatformHost.Current (process-wide)
public class ProjectControlsTests
{
    /// <summary>A host whose <see cref="PostToUiThread"/> queues, and that records how long each queued action ran.</summary>
    sealed class QueueHost : IPlatformServices
    {
        readonly ConcurrentQueue<Action> _queue = new();
        public readonly List<Form> Shown = [];
        public readonly int UiThread = Environment.CurrentManagedThreadId;
        /// <summary>The longest a single queued action kept the UI thread busy.</summary>
        public TimeSpan Longest { get; private set; }

        public void ShowForm(Form form) { Shown.Add(form); form.PerformLayout(); }
        public void RunModal(Form form) { form.PerformLayout(); if (form.IsModal) form.Close(); }
        public void CloseForm(Form form) => Shown.Remove(form);
        public void ProcessEventsUntil(Func<bool> done) => Pump(done, TimeSpan.FromMinutes(5));
        public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) => done(null);
        public void PostToUiThread(Action action) => _queue.Enqueue(action);
        public void ShowModal(Form form, Action onClosed) { RunModal(form); onClosed(); }

        bool RunOne()
        {
            if (!_queue.TryDequeue(out var action)) return false;
            var watch = Stopwatch.StartNew();
            action();
            if (watch.Elapsed > Longest) Longest = watch.Elapsed;
            return true;
        }

        /// <summary>Runs the UI queue until <paramref name="done"/> (checked between actions).</summary>
        public void Pump(Func<bool> done, TimeSpan timeout, string? what = null)
        {
            Assert.Equal(UiThread, Environment.CurrentManagedThreadId);
            var watch = Stopwatch.StartNew();
            while (!done())
            {
                if (watch.Elapsed > timeout) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what ?? "the UI"}");
                if (!RunOne()) Thread.Sleep(5);
            }
        }

        public void Pump(Task task, string what) => Pump(() => task.IsCompleted, TimeSpan.FromMinutes(5), what);

        /// <summary>Runs the UI queue for <paramref name="time"/>.</summary>
        public void PumpFor(TimeSpan time)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < time) if (!RunOne()) Thread.Sleep(5);
        }
    }

    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-palette-").FullName;

    /// <summary>A fresh copy of tests/Fixtures/TallyLive whose RadSharp references are this test's own assemblies.</summary>
    static string CopyFixture()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TallyLive");
        var root = TempDir();
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), $"""
            <Project>
              <PropertyGroup>
                <RadSharpBin>{AppContext.BaseDirectory}</RadSharpBin>
              </PropertyGroup>
            </Project>
            """);
        return root;
    }

    static (DesignerForm Designer, QueueHost Host) Start(string? project, DesignerOptions options)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var host = new QueueHost();
        PlatformHost.Current = host;
        var designer = new DesignerForm(project, Path.Combine(TempDir(), "designer.json"), options);
        designer.Settings.Editor = EditorKind.Custom;
        designer.Settings.CustomEditorCommand = "dotnet --version";
        designer.SetBounds(0, 0, 1220, 780);
        designer.PerformLayout();
        return (designer, host);
    }

    static TreeView Palette(DesignerForm d) => (TreeView)d.FindControl("tvPalette")!;

    static IReadOnlyList<string> Category(DesignerForm d, string category) =>
        [.. Palette(d).Nodes.Where(n => n.Text == category).SelectMany(n => n.Nodes).Select(n => n.Text)];

    /// <summary>The load context of a palette entry's class (in a method of its own: no local keeps the class alive).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference ContextOf(DesignerForm d, string category, string name) =>
        new(AssemblyLoadContext.GetLoadContext(((ComponentClass)Palette(d).Nodes.Single(n => n.Text == category)
            .Nodes.Single(n => n.Text == name).Tag!).Type.Assembly));

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string ClassOf(DesignerForm d, string control) => d.Document!.Form.FindControl(control) switch
    {
        null => "(none)",
        UnknownControl u => "placeholder " + u.ClassName,
        var c => c.GetType().Name,
    };

    /// <summary>Drops a palette entry on the surface (a real drag, as the palette tests do), and sets a property in the inspector.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static string DropAndEdit(DesignerForm d, string category, string name, int x, int y, string property, string value)
    {
        var tv = Palette(d);
        var node = tv.Nodes.Single(n => n.Text == category).Nodes.Single(n => n.Text == name);
        d.Simulate.Press(tv.AbsoluteLeft + 5, tv.AbsoluteTop + 5);
        tv.Selected = node;
        d.BeginDrag(tv);
        var surface = d.ActiveForm!.Surface;
        var (sx, sy) = surface.DesignToScreen(x, y);
        d.Simulate.MoveTo(surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy);
        Assert.Equal(Cursor.Default, d.CurrentCursor);   // the surface takes it
        d.Simulate.Release(surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy);

        var created = d.Document!.Primary;
        Assert.Equal(name, created.GetType().Name);
        Assert.True(created.DesignMode);
        var props = (PropertyGrid)d.FindControl("gridProps")!;
        int row = props.IndexOf(property);
        Assert.True(row >= 0, $"no {property} row in the inspector");
        props.BeginEdit(row);
        d.Simulate.Type(value);
        d.Simulate.Key(Key.Enter);
        Assert.Equal(value, props.RowValue(row));

        // Its events, with a stub in the code-behind, as for a built-in control.
        var click = ComponentRegistry.Of(created).FindEvent("onClick")!;
        d.EditHandler(created, click);
        return created.Name;
    }

    /// <summary>F9 while the design session is loaded: the live form's controls are the live run's classes, not the designer's.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void RunAndClose(DesignerForm d, QueueHost host)
    {
        var live = d.Run();
        Assert.True(live != null, string.Join("\n", d.OutputLines));
        var counter = live.FindControl("cntTotal")!;
        var designed = d.Document!.Form.FindControl("cntTotal")!;
        Assert.Equal("TallyCounter", counter.GetType().Name);
        Assert.NotSame(designed.GetType(), counter.GetType());
        Assert.NotSame(AssemblyLoadContext.GetLoadContext(designed.GetType().Assembly), AssemblyLoadContext.GetLoadContext(counter.GetType().Assembly));
        Assert.False(counter.DesignMode);
        Assert.True(designed.DesignMode);
        live.Simulate.Click((Button)live.FindControl("btnTally")!);
        Assert.Equal("6 item(s)", ((Label)counter).Caption);
        live.Close();
        Assert.Empty(host.Shown);
        // The designer's TallyCounter is the registered one again.
        Assert.Same(designed.GetType(), ComponentRegistry.Find("TallyCounter")!.Type);
    }

    /// <summary>A completed task's result (outside the test method: xUnit1031 forbids blocking there, and this doesn't block).</summary>
    static T ResultOf<T>(Task<T> task)
    {
        Assert.True(task.IsCompleted);
        return task.Result;
    }

    static void Collect(WeakReference weak, QueueHost host)
    {
        for (int i = 0; i < 40 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            host.PumpFor(TimeSpan.FromMilliseconds(25));
        }
    }

    /// <summary>Restores Microsoft.Data.Sqlite like LiveDependencyTests (the local package cache when offline).</summary>
    [Fact]
    [Trait("Category", "NuGet")]
    public void The_projects_controls_are_on_the_palette_become_placeholders_while_it_is_broken_and_come_back()
    {
        var root = CopyFixture();
        var projectFile = Path.Combine(root, "TallyLive", "TallyLive.csproj");
        var (d, host) = Start(projectFile, new DesignerOptions { RefreshDelay = TimeSpan.FromMilliseconds(200) });
        var open = d.ActiveForm!;
        var doc = open.Document;
        // Opened before the project compiled: the project's control is a placeholder at first.
        Assert.Equal("placeholder TallyCounter", ClassOf(d, "cntTotal"));
        host.Pump(d.ProjectControlsTask, "the design session");

        // The palette: the project's control under Project, the project reference's under its name.
        Assert.True(d.DesignSession != null, string.Join("\n", d.OutputLines));
        Assert.Equal(["TallyCounter"], Category(d, PaletteCategories.Project));
        Assert.Equal(["MoneyLabel"], Category(d, "TallyLive.Lib"));
        Assert.Contains(d.OutputLines, l => l.StartsWith("Project controls: TallyCounter (Project), MoneyLabel (TallyLive.Lib)"));
        Assert.Equal(PaletteCategories.Project, Palette(d).Nodes[^2].Text);   // after RadSharp's own categories
        // The placeholder became the real control, without counting as a change.
        Assert.Equal("TallyCounter", ClassOf(d, "cntTotal"));
        Assert.False(doc.Modified);

        // Drop the library's control, edit a published property and give it a handler; save.
        var name = DropAndEdit(d, "TallyLive.Lib", "MoneyLabel", 200, 160, "currency", "USD");
        Assert.Contains($"void {name}_Click(Control sender)", File.ReadAllText(Path.Combine(root, "TallyLive", "MainForm.cs")));
        d.Save(open);
        var saved = File.ReadAllText(open.Path);
        Assert.Contains("\"class\": \"MoneyLabel\"", saved);
        Assert.Contains("\"currency\": \"USD\"", saved);
        Assert.Contains($"\"onClick\": \"{name}_Click\"", saved);
        // The stub changed a source file: the design session is rebuilt (after a short delay).
        host.PumpFor(TimeSpan.FromMilliseconds(600));
        host.Pump(d.ProjectControlsTask, "the rebuild after the stub");
        Assert.Equal("MoneyLabel", ClassOf(d, name));
        Assert.Equal(saved, doc.Save());

        // F9 meanwhile: the live run has classes of its own.
        RunAndClose(d, host);

        // Break the project's code: once the watcher has rebuilt, its controls are placeholders, and save as they were.
        var context = ContextOf(d, PaletteCategories.Project, "TallyCounter");
        var counterFile = Path.Combine(root, "TallyLive", "TallyCounter.cs");
        var good = File.ReadAllText(counterFile);
        File.WriteAllText(counterFile, good.Replace("int _count;", "int _count = ;"));
        host.Pump(() => ClassOf(d, "cntTotal") == "placeholder TallyCounter" && d.ProjectControlsTask.IsCompleted, TimeSpan.FromMinutes(3), "placeholders");
        Assert.Equal("placeholder MoneyLabel", ClassOf(d, name));
        Assert.Null(d.DesignSession);
        Assert.Empty(Category(d, PaletteCategories.Project));
        Assert.Contains(d.OutputLines, l => l.Contains("show as placeholders until it compiles"));
        Assert.Contains(d.OutputLines, l => l.Contains("TallyCounter.cs(") && l.Contains("error"));
        Assert.False(doc.Modified);
        Assert.Equal(saved, doc.Save());
        d.Save(open);
        Assert.Equal(saved, File.ReadAllText(open.Path));

        // The session's classes are unregistered, and its load context unloads.
        Assert.Null(ComponentRegistry.Find("TallyCounter"));
        Assert.Null(ComponentRegistry.Find("MoneyLabel"));
        Collect(context, host);
        Assert.False(context.IsAlive, "the design session's load context is still alive");

        // Fix it, and Project › Refresh Project Controls: real again.
        File.WriteAllText(counterFile, good);
        var menu = (MainMenu)d.FindControl("mnuMain")!;
        menu.Items.Single(m => m.Caption == "Project").Items.Single(m => m.Caption == "Refresh Project Controls").PerformClick();
        host.Pump(() => ClassOf(d, "cntTotal") == "TallyCounter" && d.ProjectControlsTask.IsCompleted, TimeSpan.FromMinutes(3), "real controls");
        Assert.Equal("MoneyLabel", ClassOf(d, name));
        Assert.Equal(["TallyCounter"], Category(d, PaletteCategories.Project));
        Assert.Equal(saved, doc.Save());
        Assert.False(doc.Modified);

        Assert.True(d.CloseProject());
        Assert.Null(d.DesignSession);
        Assert.Null(ComponentRegistry.Find("TallyCounter"));
    }

    [Fact]
    public void A_control_that_throws_at_design_time_stays_a_placeholder_and_DesignMode_tells_design_from_run_time()
    {
        var project = RadProject.Create(TempDir(), "Moody");
        File.WriteAllText(Path.Combine(project.Directory, "Controls.cs"), """
            using RadSharp;

            namespace Moody;

            /// <summary>Opens its "database" in the constructor, which only the running application can.</summary>
            public class Boom : Label
            {
                public Boom()
                {
                    if (DesignMode) throw new InvalidOperationException("no database at design time");
                    Caption = "connected";
                }
            }

            public class Shy : Label
            {
                public Shy() => Caption = DesignMode ? "design time" : "run time";
            }
            """);
        var formFile = project.FormFiles.Single();
        // The form as an earlier session left it: a Boom on it (unknown to this designer: a placeholder).
        var earlier = new DesignDocument();
        earlier.Load(File.ReadAllText(formFile));
        earlier.Paste("""[ { "class": "Boom", "name": "boom1", "left": 8, "top": 8 } ]""", earlier.Form, offset: 0);
        var text = earlier.Save();
        File.WriteAllText(formFile, text);
        // The project's packages aren't needed: compile against the designer's assemblies (as when MSBuild fails).
        var noPackages = new LiveRunOptions { Resolver = (_, _, _) => new DependencyResolution(null, "not resolved in this test", false) };
        var (d, host) = Start(project.ProjectFile, new DesignerOptions { WatchProject = false, LiveRun = noPackages });
        host.Pump(d.ProjectControlsTask, "the design session");

        Assert.Equal(["Shy"], Category(d, PaletteCategories.Project));
        Assert.Contains(d.OutputLines, l => l.Contains("Boom can't be created at design time (InvalidOperationException: no database at design time)"));
        Assert.Equal("placeholder Boom", ClassOf(d, "boom1"));
        Assert.Equal(text, d.Document!.Save());

        var shy = d.ActiveForm!.Surface.CreateAt(d.DesignSession!.Find("Shy")!, 40, 120)!;
        Assert.Equal("design time", ((Label)shy).Caption);

        // The running application constructs both, outside design mode.
        var live = d.Run();
        Assert.True(live != null, string.Join("\n", d.OutputLines));
        Assert.Equal("connected", ((Label)live.FindControl("boom1")!).Caption);
        Assert.Equal("run time", ((Label)live.FindControl(shy.Name)!).Caption);
        live.Close();
    }

    [Fact]
    public void F9_resolves_off_the_UI_thread_and_a_second_F9_drops_the_first()
    {
        var project = RadProject.Create(TempDir(), "Slow");
        using var release = new ManualResetEventSlim();
        int calls = 0, resolverThread = 0;
        var slow = new LiveRunOptions
        {
            Resolver = (_, _, progress) =>
            {
                Interlocked.Increment(ref calls);
                resolverThread = Environment.CurrentManagedThreadId;
                progress?.Invoke("Restoring (a slow test resolver)…");
                release.Wait(TimeSpan.FromMinutes(1));   // a cold restore
                return new DependencyResolution(null, "a test resolver", false);
            },
        };
        var (d, host) = Start(project.ProjectFile, new DesignerOptions { ProjectControls = false, LiveRun = slow });

        var watch = Stopwatch.StartNew();
        d.Simulate.Key(Key.F9);
        var first = d.LastRun!;
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"F9 kept the UI thread {watch.ElapsedMilliseconds} ms");
        host.Pump(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(30), "the resolver");
        host.PumpFor(TimeSpan.FromMilliseconds(300));
        Assert.False(first.IsCompleted);
        Assert.Contains(d.OutputLines, l => l == "Restoring (a slow test resolver)…");
        Assert.Equal("Restoring (a slow test resolver)…", ((StatusBar)d.FindControl("sbMain")!).SimpleText);

        // The designer stays usable while it resolves.
        var doc = d.Document!;
        doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 24, 24);

        // A second F9 before the first is done: the first is dropped.
        watch.Restart();
        d.Simulate.Key(Key.F9);
        var second = d.LastRun!;
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"F9 kept the UI thread {watch.ElapsedMilliseconds} ms");
        Assert.NotSame(first, second);
        release.Set();
        host.Pump(() => first.IsCompleted && second.IsCompleted, TimeSpan.FromMinutes(3), "both runs");
        Assert.Null(ResultOf(first));
        var live = ResultOf(second);
        Assert.True(live != null, string.Join("\n", d.OutputLines));
        Assert.Same(live, Assert.Single(host.Shown));
        Assert.NotNull(live.FindControl("Button1"));   // the unsaved state of the form
        Assert.Equal(2, calls);
        Assert.NotEqual(host.UiThread, resolverThread);
        Assert.True(host.Longest < TimeSpan.FromSeconds(2), $"a UI-thread step took {host.Longest.TotalMilliseconds:0} ms");
        live.Close();
    }

    [Fact]
    public void The_second_F9_of_a_project_MSBuild_cannot_resolve_does_not_run_MSBuild_again_until_refresh()
    {
        var project = RadProject.Create(TempDir(), "Broken");
        File.AppendAllText(project.ProjectFile, "<NotXml");   // MSBuild rejects it at once (no restore, no network)
        int runs = 0;
        void Started(string file) { if (string.Equals(file, project.ProjectFile, StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref runs); }
        ProjectDependencies.MSBuildStarting += Started;
        try
        {
            var (d, host) = Start(project.ProjectFile, new DesignerOptions { ProjectControls = false });
            Assert.NotNull(d.Run());
            Assert.Equal(1, runs);
            Assert.Contains(d.OutputLines, l => l.Contains(LiveSession.FallbackId) && !l.Contains("remembered"));

            Assert.NotNull(d.Run());
            Assert.Equal(1, runs);   // the failure was cached: no second restore
            Assert.Contains(d.OutputLines, l => l.Contains(LiveSession.FallbackId) && l.Contains("remembered from the last attempt"));

            // Project › Refresh forgets it.
            host.Pump(d.RefreshProject(), "the refresh");
            Assert.NotNull(d.Run());
            Assert.Equal(2, runs);

            // So does a change to the project file.
            Assert.NotNull(d.Run());
            Assert.Equal(2, runs);
            File.SetLastWriteTimeUtc(project.ProjectFile, DateTime.UtcNow.AddSeconds(5));
            Assert.NotNull(d.Run());
            Assert.Equal(3, runs);
        }
        finally { ProjectDependencies.MSBuildStarting -= Started; }
    }
}
