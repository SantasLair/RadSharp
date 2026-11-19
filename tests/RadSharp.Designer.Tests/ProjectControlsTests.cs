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
public class ProjectControlsTests : IDisposable
{
    /// <summary>The designers a test started: closed after it, so no design session stays registered for the next test.</summary>
    readonly List<DesignerForm> _started = [];

    public void Dispose()
    {
        foreach (var d in _started)
        {
            foreach (var f in d.OpenForms) f.Document.MarkSaved();
            d.CloseProject();
        }
    }

    /// <summary>A host whose <see cref="PostToUiThread"/> queues, and that records how long each queued action ran.</summary>
    sealed class QueueHost : IPlatformServices
    {
        readonly ConcurrentQueue<Action> _queue = new();
        public readonly List<Form> Shown = [];
        public readonly int UiThread = Environment.CurrentManagedThreadId;
        /// <summary>The longest a single queued action kept the UI thread busy.</summary>
        public TimeSpan Longest { get; private set; }

        /// <summary>What the "user" does while a modal form is open (its nested loop runs the UI queue, as SDL's does).</summary>
        public Action<Form>? OnModal;

        public void ShowForm(Form form) { Shown.Add(form); form.PerformLayout(); }
        public void RunModal(Form form) { form.PerformLayout(); OnModal?.Invoke(form); if (form.IsModal) form.Close(); }
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

    (DesignerForm Designer, QueueHost Host) Start(string? project, DesignerOptions options)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var host = new QueueHost();
        PlatformHost.Current = host;
        var designer = new DesignerForm(project, Path.Combine(TempDir(), "designer.json"), options);
        designer.Settings.Editor = EditorKind.Custom;
        designer.Settings.CustomEditorCommand = "dotnet --version";
        designer.SetBounds(0, 0, 1220, 780);
        designer.PerformLayout();
        _started.Add(designer);
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

    // ---- Review: what a swap must not lose ----

    /// <summary>Compiles against the designer's own assemblies (no MSBuild, no network).</summary>
    static readonly LiveRunOptions NoPackages = new() { Resolver = (_, _, _) => new DependencyResolution(null, "not resolved in this test", false) };

    /// <summary>A new project with <paramref name="controls"/> as Controls.cs and <paramref name="children"/> (JSON nodes) on its form.</summary>
    static (RadProject Project, string ControlsFile) ProjectWith(string name, string controls, params string[] children)
    {
        var project = RadProject.Create(TempDir(), name);
        var controlsFile = Path.Combine(project.Directory, "Controls.cs");
        File.WriteAllText(controlsFile, controls);
        var formFile = project.FormFiles.Single();
        var form = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(formFile))!;
        var root = form["root"]!.AsObject();
        if (root["children"] is not System.Text.Json.Nodes.JsonArray list) root["children"] = list = [];
        foreach (var child in children) list.Add(System.Text.Json.Nodes.JsonNode.Parse(child));
        File.WriteAllText(formFile, form.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return (project, controlsFile);
    }

    [Fact]
    public void A_value_of_a_property_the_projects_class_stops_publishing_is_kept_not_dropped()
    {
        var (project, controlsFile) = ProjectWith("Gauges", """
            using RadSharp;

            namespace Gauges;

            public class Gauge : Label
            {
                [Published(Category = "Behavior")] public int Level { get; set; }
            }
            """, """{ "class": "Gauge", "name": "gauge1", "left": 8, "top": 8, "level": 5 }""");
        var (d, host) = Start(project.ProjectFile, new DesignerOptions { WatchProject = false, LiveRun = NoPackages });
        host.Pump(d.ProjectControlsTask, "the design session");
        var doc = d.Document!;
        Assert.Equal("Gauge", ClassOf(d, "gauge1"));
        Assert.Contains("\"level\": 5", doc.Save());

        // Halfway through renaming the property (an editor's autosave): the project still compiles, without "level".
        var good = File.ReadAllText(controlsFile);
        File.WriteAllText(controlsFile, good.Replace("int Level", "int Lev"));
        host.Pump(d.RefreshProjectControls(), "the rebuild");
        Assert.Contains("\"level\": 5", doc.Save());

        // The rename is undone: the value is still there.
        File.WriteAllText(controlsFile, good);
        host.Pump(d.RefreshProjectControls(), "the rebuild");
        Assert.Equal("Gauge", ClassOf(d, "gauge1"));
        Assert.Contains("\"level\": 5", doc.Save());
        Assert.False(doc.Modified);
    }

    [Fact]
    public void A_project_control_named_like_a_RadSharp_one_keeps_its_own_properties_before_it_loads_and_while_the_project_is_broken()
    {
        var (project, controlsFile) = ProjectWith("Shadow", """
            using RadSharp;

            namespace Shadow;

            /// <summary>The application's own Button: form files can't tell it from RadSharp's.</summary>
            public class Button : RadSharp.Button
            {
                [Published(Category = "Behavior")] public int Extra { get; set; }
            }
            """, """{ "class": "Button", "name": "btnMine", "caption": "Mine", "left": 8, "top": 8, "extra": 3 }""");
        var (d, host) = Start(project.ProjectFile, new DesignerOptions { WatchProject = false, LiveRun = NoPackages });
        var doc = d.Document!;
        Assert.Contains("\"extra\": 3", doc.Save());   // before the project's controls are loaded
        host.Pump(d.ProjectControlsTask, "the design session");
        Assert.True(doc.Form.FindControl("btnMine")!.GetType().Assembly.IsCollectible, "the project's Button");
        Assert.Contains("\"extra\": 3", doc.Save());

        var good = File.ReadAllText(controlsFile);
        File.WriteAllText(controlsFile, good.Replace("{ get; set; }", "{ get; set; } = ;"));
        host.Pump(d.RefreshProjectControls(), "the broken rebuild");
        Assert.Null(d.DesignSession);
        Assert.Contains("\"extra\": 3", doc.Save());
        Assert.False(doc.Modified);
    }

    /// <summary>Waits until the rebuild is installed, or is waiting for the designer to be idle.</summary>
    static void PumpUntilRebuiltOrWaiting(DesignerForm d, QueueHost host, Task rebuild) =>
        host.Pump(() => rebuild.IsCompleted || d.OutputLines.Any(l => l.Contains("controls load once")), TimeSpan.FromMinutes(1), "the rebuild");

    [Fact]
    public void A_rebuild_that_finishes_while_an_editor_dialog_is_open_does_not_lose_its_edits()
    {
        var (project, _) = ProjectWith("Grids", "namespace Grids; public class Nothing { }");
        var (d, host) = Start(project.ProjectFile, new DesignerOptions { WatchProject = false, LiveRun = NoPackages });
        host.Pump(d.ProjectControlsTask, "the design session");
        var doc = d.Document!;
        doc.Create(ComponentRegistry.Find("StringGrid")!, doc.Form, 16, 16);
        var props = (PropertyGrid)d.FindControl("gridProps")!;
        Task? rebuild = null;
        host.OnModal = editor =>
        {
            // The watcher saw a source change: the rebuild finishes while the Columns editor is open.
            rebuild = d.RefreshProjectControls();
            PumpUntilRebuiltOrWaiting(d, host, rebuild);
            editor.Simulate.Click(editor.FindControl("btnAdd")!);
        };
        props.BeginEdit(props.IndexOf("columns"));
        host.OnModal = null;
        Assert.NotNull(rebuild);
        host.Pump(rebuild, "the rebuild");
        Assert.Contains("\"title\": \"Column\"", doc.Save());
    }

    [Fact]
    public void A_rebuild_that_finishes_during_a_drag_does_not_lose_the_move()
    {
        var (project, _) = ProjectWith("Drags", "namespace Drags; public class Nothing { }");
        var (d, host) = Start(project.ProjectFile, new DesignerOptions { WatchProject = false, LiveRun = NoPackages });
        host.Pump(d.ProjectControlsTask, "the design session");
        var doc = d.Document!;
        var button = doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 24, 24);
        var surface = d.ActiveForm!.Surface;
        (int X, int Y) At(int x, int y) { var (sx, sy) = surface.DesignToScreen(x, y); return (surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy); }

        var (px, py) = At(30, 30);
        d.Simulate.Press(px, py);
        var (mx, my) = At(70, 30);
        d.Simulate.MoveTo(mx, my);
        var rebuild = d.RefreshProjectControls();
        PumpUntilRebuiltOrWaiting(d, host, rebuild);
        var (ex, ey) = At(110, 70);
        d.Simulate.MoveTo(ex, ey);
        d.Simulate.Release(ex, ey);
        host.Pump(rebuild, "the rebuild");
        var moved = doc.Form.FindControl(button.Name)!;
        Assert.Equal((104, 64), (moved.Left, moved.Top));
    }

    [Fact]
    public void A_name_being_typed_survives_a_rebuild()
    {
        var (project, _) = ProjectWith("Names", "namespace Names; public class Nothing { }");
        var (d, host) = Start(project.ProjectFile, new DesignerOptions { WatchProject = false, LiveRun = NoPackages });
        host.Pump(d.ProjectControlsTask, "the design session");
        var doc = d.Document!;
        var button = doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 24, 24);
        var edtName = (Edit)d.FindControl("edtName")!;
        d.Simulate.Click(edtName);
        edtName.Text = "btnOk";   // typed, not applied yet (Enter or leaving the box applies it)
        var rebuild = d.RefreshProjectControls();
        PumpUntilRebuiltOrWaiting(d, host, rebuild);
        d.Simulate.Key(Key.Enter);
        host.Pump(rebuild, "the rebuild");
        Assert.NotNull(doc.Form.FindControl("btnOk"));
    }

    [Fact]
    public void A_project_control_that_throws_while_painted_at_design_time_does_not_stop_the_designer_painting()
    {
        var (project, _) = ProjectWith("Fussy", """
            using RadSharp;
            using RadSharp.Drawing;

            namespace Fussy;

            public class Chart : Label
            {
                protected override void Paint(Canvas canvas)
                {
                    if (DesignMode) throw new InvalidOperationException("no data at design time");
                    base.Paint(canvas);
                }
            }
            """, """{ "class": "Chart", "name": "chart1", "left": 8, "top": 8 }""");
        var (d, host) = Start(project.ProjectFile, new DesignerOptions { WatchProject = false, LiveRun = NoPackages });
        host.Pump(d.ProjectControlsTask, "the design session");
        Assert.Equal("Chart", ClassOf(d, "chart1"));
        // What the host does every frame (SdlHost's loop doesn't catch it: the designer would close, unsaved work and all).
        using var frame = RadSharp.Drawing.FormRenderer.RenderToBitmap(d);
    }

    [Fact]
    public void A_project_control_that_throws_while_laid_out_at_design_time_leaves_the_form_open_and_the_swap_whole()
    {
        var (project, controlsFile) = ProjectWith("Layouts", """
            using RadSharp;

            namespace Layouts;

            /// <summary>Fine on its own; throws once it is on a form (it looks for the application's data there).</summary>
            public class Sidebar : Panel
            {
                protected override void OnResize()
                {
                    base.OnResize();
                    if (DesignMode && Parent is Form) throw new InvalidOperationException("no data at design time");
                }
            }
            """, """{ "class": "Sidebar", "name": "side1", "align": "client" }""",
            """{ "class": "Label", "name": "lblHello", "caption": "Hello" }""");
        var (d, host) = Start(project.ProjectFile, new DesignerOptions { WatchProject = false, LiveRun = NoPackages });
        var doc = d.Document!;
        var text = doc.Save();
        host.Pump(d.ProjectControlsTask, "the design session");
        Assert.True(d.DesignSession != null, string.Join("\n", d.OutputLines));
        Assert.Equal(text, doc.Save());
        Assert.NotNull(doc.Form.FindControl("lblHello"));
        Assert.Contains(d.OutputLines, l => l.Contains("no data at design time"));
        // The swap went through: the palette shows the new session's class.
        Assert.Equal(["Sidebar"], Category(d, PaletteCategories.Project));

        // Undo and redo still work on it.
        doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 8, 8);
        Assert.True(doc.Undo());
        Assert.Equal(text, doc.Save());
        Assert.True(doc.Redo());
        Assert.NotNull(doc.Form.FindControl("Button1"));

        // Placing one from the palette, then aligning it: its layout throws from then on; the designer still paints.
        var surface = d.ActiveForm!.Surface;
        var (sx, sy) = surface.DesignToScreen(200, 100);
        var placed = surface.CreateAt(d.DesignSession!.Find("Sidebar")!, sx, sy)!;
        placed.Align = Align.Client;
        using var frame = RadSharp.Drawing.FormRenderer.RenderToBitmap(d);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Handle(DesignerForm d, QueueHost host, string name)
    {
        var doc = d.Document!;
        var surface = d.ActiveForm!.Surface;
        var dial = doc.Form.FindControl(name)!;
        var (sx, sy) = surface.DesignToScreen(dial.Left + 4, dial.Top + 4);
        d.Simulate.MoveTo(surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy);   // hover
        d.Simulate.Click(surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy);   // select
        Assert.Same(dial, doc.Primary);
        var props = (PropertyGrid)d.FindControl("gridProps")!;
        int row = props.IndexOf("value");
        props.BeginEdit(row);
        d.Simulate.Type("7");
        d.Simulate.Key(Key.Enter);
        Assert.True(doc.Undo());
        Assert.True(doc.Redo());
        d.ActiveForm!.Surface.CopySelection();
        var events = (PropertyGrid)d.FindControl("gridEvents")!;
        Assert.Same(doc.Primary, events.Targets.Single());
        host.PumpFor(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void A_replaced_session_unloads_after_its_control_was_hovered_selected_edited_undone_and_copied()
    {
        var (project, controlsFile) = ProjectWith("Dials", """
            using RadSharp;

            namespace Dials;

            public class Dial : Label
            {
                [Published(Category = "Behavior")] public int Value { get; set; }
            }
            """, """{ "class": "Dial", "name": "dial1", "left": 40, "top": 40, "width": 120, "height": 40 }""");
        var (d, host) = Start(project.ProjectFile, new DesignerOptions { WatchProject = false, LiveRun = NoPackages });
        host.Pump(d.ProjectControlsTask, "the design session");
        Assert.Equal("Dial", ClassOf(d, "dial1"));
        var context = ContextOf(d, PaletteCategories.Project, "Dial");
        Handle(d, host, "dial1");

        // A rebuild (the project changed, and still compiles): the old session's context must go.
        File.AppendAllText(controlsFile, "\npublic class Other { }\n");
        host.Pump(d.RefreshProjectControls(), "the rebuild");
        Assert.Equal("Dial", ClassOf(d, "dial1"));
        Assert.Contains("\"value\": 7", d.Document!.Save());
        Collect(context, host);
        Assert.False(context.IsAlive, "the replaced design session's load context is still alive");
    }

    [Fact]
    public void Closing_the_designer_unloads_the_projects_controls()
    {
        var (project, _) = ProjectWith("Closing", "using RadSharp; namespace Closing; public class Knob : Label { }");
        var (d, host) = Start(project.ProjectFile, new DesignerOptions { WatchProject = false, LiveRun = NoPackages });
        host.Pump(d.ProjectControlsTask, "the design session");
        Assert.NotNull(ComponentRegistry.Find("Knob"));
        Assert.True(d.Close());
        Assert.Null(ComponentRegistry.Find("Knob"));
    }

    [Fact]
    public async Task A_designer_without_a_platform_host_still_finishes_loading_the_project_controls()
    {
        var (project, _) = ProjectWith("Hostless", "namespace Hostless; public class Nothing { }");
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        PlatformHost.Current = null;
        var d = new DesignerForm(project.ProjectFile, Path.Combine(TempDir(), "designer.json"), new DesignerOptions { WatchProject = false, LiveRun = NoPackages });
        _started.Add(d);
        // No host to post to: the callback runs where it is (it used to be dropped: the task never completed).
        var finished = await Task.WhenAny(d.ProjectControlsTask, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(d.ProjectControlsTask, finished);
        Assert.NotNull(d.DesignSession);
    }

    [Fact]
    public void A_document_that_cannot_be_reloaded_with_the_new_classes_falls_back_to_placeholders_and_the_swap_still_completes()
    {
        var (project, _) = ProjectWith("Strict", """
            using RadSharp;

            namespace Strict;

            /// <summary>Fails the way a form file reader does (an exception the reader never absorbs) once it is on a form.</summary>
            public class Sidebar : Panel
            {
                protected override void OnResize()
                {
                    base.OnResize();
                    if (DesignMode && Parent is Form) throw new FormFileException("sidebar refuses");
                }
            }
            """, """{ "class": "Sidebar", "name": "side1", "align": "client" }""",
            """{ "class": "Label", "name": "lblHello", "caption": "Hello" }""");
        var (d, host) = Start(project.ProjectFile, new DesignerOptions { WatchProject = false, LiveRun = NoPackages });
        var doc = d.Document!;
        var text = doc.Save();
        host.Pump(d.ProjectControlsTask, "the design session");
        Assert.True(d.DesignSession != null, string.Join("\n", d.OutputLines));
        Assert.Equal(text, doc.Save());
        Assert.Equal("placeholder Sidebar", ClassOf(d, "side1"));
        Assert.Contains(d.OutputLines, l => l.Contains("sidebar refuses"));
        Assert.Equal(["Sidebar"], Category(d, PaletteCategories.Project));
        Assert.Contains("up to date", ((StatusBar)d.FindControl("sbMain")!).SimpleText);
    }

    const string GaugeCode = """
        using RadSharp;

        namespace Gauges;

        public class Gauge : Label { }
        """;
    const string GaugeNode = """{ "class": "Gauge", "name": "gauge1", "left": 8, "top": 8 }""";

    [Fact]
    public void A_transient_failure_while_rebuilding_is_retried_and_keeps_the_last_good_session_meanwhile()
    {
        var (project, _) = ProjectWith("Gauges", GaugeCode, GaugeNode);
        int calls = 0, failFrom = int.MaxValue, failUntil = 0;
        var resolver = new LiveRunOptions
        {
            // What File.ReadAllText does right after a watcher event: the editor still holds the file.
            Resolver = (_, _, _) => ++calls >= failFrom && calls <= failUntil
                ? throw new IOException("The process cannot access the file")
                : new DependencyResolution(null, "not resolved in this test", false),
        };
        var (d, host) = Start(project.ProjectFile, new DesignerOptions
        {
            WatchProject = false, LiveRun = resolver, TransientRetries = 2, TransientRetryDelay = TimeSpan.FromMilliseconds(10),
        });
        host.Pump(d.ProjectControlsTask, "the design session");
        var first = d.DesignSession;
        Assert.NotNull(first);
        Assert.Equal("Gauge", ClassOf(d, "gauge1"));

        // One failure: retried, and the new session installs.
        failFrom = calls + 1; failUntil = calls + 1;
        host.Pump(d.RefreshProjectControls(), "the retried rebuild");
        Assert.NotNull(d.DesignSession);
        Assert.NotSame(first, d.DesignSession);
        Assert.Equal("Gauge", ClassOf(d, "gauge1"));
        Assert.Contains(d.OutputLines, l => l.Contains("trying again"));

        // Failing every time: after the retries the last good session is kept, its controls stay real.
        var second = d.DesignSession;
        failFrom = calls + 1; failUntil = int.MaxValue;
        var rebuild = d.RefreshProjectControls();
        host.Pump(rebuild, "the failed rebuild");
        Assert.Same(second, d.DesignSession);
        Assert.Equal("Gauge", ClassOf(d, "gauge1"));
        Assert.Equal(["Gauge"], Category(d, PaletteCategories.Project));
        Assert.Contains(d.OutputLines, l => l.Contains("couldn't be loaded") && l.Contains("The process cannot access the file"));
    }

    [Fact]
    public void Build_output_is_ignored_below_the_project_folder_only()
    {
        // A project that lives under a folder called "bin" is still watched; its own bin and obj are not.
        var root = Path.Combine(Path.GetTempPath(), "bin", "src", "App");
        Assert.False(DesignerForm.IsBuildOutput(root, Path.Combine(root, "MainForm.cs")));
        Assert.False(DesignerForm.IsBuildOutput(root, Path.Combine(root, "Forms", "MainForm.cs")));
        Assert.True(DesignerForm.IsBuildOutput(root, Path.Combine(root, "obj", "Debug", "App.AssemblyInfo.cs")));
        Assert.True(DesignerForm.IsBuildOutput(root, Path.Combine(root, "bin", "Debug", "net10.0", "Gen.cs")));
        Assert.True(DesignerForm.IsBuildOutput(root, Path.Combine(root, ".git", "hooks", "x.cs")));
        Assert.False(DesignerForm.IsBuildOutput(root, Path.Combine(root, "bin.cs")));   // a file, not a folder
    }
}
