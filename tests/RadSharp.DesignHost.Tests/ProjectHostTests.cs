using System.Reflection;
using RadSharp.Design;
using RadSharp.Drawing;
using P = RadSharp.DesignHost.Protocol;

namespace RadSharp.DesignHost.Tests;

/// <summary>
/// SetProjectAssembly (protocol 4): the host shows a project's own controls (the SampleControls fixture, loaded from a
/// copy of its build output as Visual Studio's TargetPath) and follows its rebuilds.
/// </summary>
[Collection("DesignHost")]
public class ProjectHostTests
{
    const int Doc = 1;

    const string FormText = """
        {
          "format": "radform",
          "version": "1.0",
          "generator": "RADSharp",
          "root": {
            "class": "Form",
            "name": "frmProject",
            "height": 300,
            "width": 400,
            "children": [
              {
                "class": "Gauge",
                "name": "gauge1",
                "left": 8,
                "top": 8,
                "value": 7
              }
            ]
          }
        }

        """;

    static string BuiltDirectory => Path.GetDirectoryName(typeof(ProjectHostTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "SampleControls").Value!)!;

    /// <summary>A copy of the fixture's output folder (so a test can rebuild it), and a form file using its Gauge.</summary>
    static (string Dll, string Form) Project(bool built = true)
    {
        var dir = TestFiles.TempDir();
        if (built) CopyBuild(dir);
        var form = Path.Combine(dir, "ProjectForm.radform.json");
        File.WriteAllText(form, FormText);
        return (Path.Combine(dir, "SampleControls.dll"), form);
    }

    static void CopyBuild(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(BuiltDirectory))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
    }

    static HostClient Connect(bool fastReload = false)
    {
        var client = new HostClient();
        client.Send(new P.Hello { ProtocolVersion = P.ProtocolInfo.Version, ClientName = "tests" });
        client.Expect<P.HelloAck>();
        if (fastReload)
            client.Ui(() =>
            {
                client.Server!.ProjectSettleTime = TimeSpan.FromMilliseconds(100);
                client.Server!.ProjectCheckInterval = TimeSpan.FromMilliseconds(50);
            });
        return client;
    }

    static Control Gauge(HostClient client) => client.Ui(() => client.Server!.Document(Doc)!.Doc.Form.FindControl("gauge1")!);

    /// <summary>The host's document: only inside <see cref="HostClient.Ui{T}"/> (on the host's UI thread).</summary>
    static HostDocument Document(HostClient client) => client.Server!.Document(Doc)!;

    static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what + ".");
            Thread.Sleep(50);
        }
    }

    /// <summary>Rewrites a file as a build would (it isn't locked), with a newer time stamp.</summary>
    static void Rebuild(string file, int seconds = 5)
    {
        File.WriteAllBytes(file, File.ReadAllBytes(file));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(seconds));
    }

    [Fact]
    public void A_form_opened_after_its_project_assembly_shows_the_projects_controls_first_on_the_palette()
    {
        var (dll, form) = Project();
        using var client = Connect();
        var theme = client.Ui(() => Theme.Current);
        client.Send(new P.SetProjectAssembly { DocId = Doc, Path = dll });
        var log = client.Expect<P.Log>(l => l.Text.StartsWith("Project controls from SampleControls.dll"));
        Assert.Equal(1, log.Level);
        Assert.Contains("Gauge (Project)", log.Text);
        Assert.Contains("Swatch (SampleData)", log.Text);
        var warning = client.Expect<P.Log>(l => l.Text.Contains("Exploding can't be created at design time"));
        Assert.Equal(2, warning.Level);
        var panes = client.Open(Doc, form);
        client.Show(panes.Surface, 800, 600);
        client.Show(panes.Palette, 240, 600);
        client.Settle();

        var gauge = Gauge(client);
        Assert.Equal("SampleControls.Gauge", gauge.GetType().FullName);
        Assert.Equal(7, (int)gauge.GetType().GetProperty("Value")!.GetValue(gauge)!);
        Assert.True(gauge.DesignMode);
        Assert.Same(theme, client.Ui(() => Theme.Current));   // the project's setup set a dark theme
        var palette = client.Ui(() => Document(client).PaletteTree!.Nodes.Select(n => n.Text).ToList());
        Assert.Equal([PaletteCategories.Project, "SampleData"], palette.Take(2));
        Assert.False(client.Ui(() => Document(client).Doc.Modified));
    }

    [Fact]
    public void A_project_control_picked_on_the_palette_is_placed_on_the_form_and_saved()
    {
        var (dll, form) = Project();
        using var client = Connect();
        client.Send(new P.SetProjectAssembly { DocId = Doc, Path = dll });
        var panes = client.Open(Doc, form);
        client.Show(panes.Surface, 800, 600);
        client.Show(panes.Palette, 240, 600);
        client.Settle();

        var (px, py) = client.Ui(() =>
        {
            var tree = Document(client).PaletteTree!;
            int i = tree.VisibleNodes().FindIndex(n => n.Tag is ComponentClass { Name: "TitleBar" });
            Assert.True(i >= 0, "TitleBar is on the palette");
            return (tree.AbsoluteLeft + tree.Width / 2, tree.AbsoluteTop + 1 + i * 24 + 12);
        });
        client.Click(panes.Palette, px, py);
        client.Settle();
        Assert.Equal("TitleBar", client.Ui(() => Document(client).Surface!.PendingClass?.Name));

        var (sx, sy) = client.DesignPoint(Doc, 40, 120);
        client.Click(panes.Surface, sx, sy);
        client.Expect<P.DocumentState>(s => s.Dirty && s.HasSelection);
        var title = client.Ui(() => Document(client).Doc.Form.FindControl("TitleBar1"));
        Assert.Equal("SampleControls.TitleBar", title?.GetType().FullName);

        client.Send(new P.SaveDocument { DocId = Doc, Path = "" });
        Assert.True(client.Expect<P.DocumentSaved>().Ok);
        var saved = File.ReadAllText(form);
        Assert.Contains("\"class\": \"TitleBar\"", saved);
        Assert.DoesNotContain("\"class\": \"Edit\"", saved);   // its own search box isn't saved
    }

    [Fact]
    public void A_rebuilt_project_is_reloaded_and_the_form_keeps_its_unsaved_changes_and_undo()
    {
        var (dll, form) = Project();
        using var client = Connect(fastReload: true);
        client.Send(new P.SetProjectAssembly { DocId = Doc, Path = dll });
        var panes = client.Open(Doc, form);
        client.Show(panes.Surface, 800, 600);
        client.Settle();
        var before = Gauge(client).GetType();
        var (x, y) = client.ControlPoint(Doc, "gauge1");
        client.Drag(panes.Surface, x, y, x + 40, y + 24);
        client.Settle();
        int left = Gauge(client).Left;
        Assert.NotEqual(8, left);

        Rebuild(dll);
        WaitFor(() => Gauge(client).GetType() != before, "the reloaded Gauge");

        var after = Gauge(client);
        Assert.Equal("SampleControls.Gauge", after.GetType().FullName);
        Assert.NotSame(before.Assembly, after.GetType().Assembly);
        Assert.Equal(left, after.Left);
        Assert.True(client.Ui(() => Document(client).Doc.Modified));
        Assert.True(client.Ui(() => Document(client).Doc.CanUndo));
        Assert.Single(client.Ui(() => client.Server!.Projects.Keys.ToList()));
        // The old code is gone from the registry; the new one is what the palette offers.
        Assert.Null(client.Ui(() => ComponentRegistry.Find(before)));
        var tagged = client.Ui(() => Document(client).PaletteTree!.Nodes.SelectMany(n => n.Nodes).Select(n => n.Tag).OfType<ComponentClass>()
            .Single(c => c.Name == "Gauge").Type);
        Assert.Same(after.GetType(), tagged);
    }

    [Fact]
    public void A_rebuild_waits_for_a_drag_in_progress_on_the_form()
    {
        var (dll, form) = Project();
        using var client = Connect(fastReload: true);
        client.Send(new P.SetProjectAssembly { DocId = Doc, Path = dll });
        var panes = client.Open(Doc, form);
        client.Show(panes.Surface, 800, 600);
        client.Settle();
        var before = Gauge(client).GetType();
        var (x, y) = client.ControlPoint(Doc, "gauge1");
        client.Move(panes.Surface, x, y);
        client.Send(new P.MouseDown { WindowId = panes.Surface, X = x, Y = y, Clicks = 1 });
        client.Move(panes.Surface, x + 20, y + 10);
        client.Settle();
        Assert.True(client.Ui(() => Document(client).Surface!.IsTracking));

        Rebuild(dll);
        client.Expect<P.Log>(l => l.Text.Contains("is reloaded once the drag on the form is done"), timeoutMs: 15000);
        Assert.Same(before, Gauge(client).GetType());

        client.Send(new P.MouseUp { WindowId = panes.Surface, X = x + 40, Y = y + 24 });
        WaitFor(() => Gauge(client).GetType() != before, "the reloaded Gauge after the drag");
        Assert.NotEqual(8, Gauge(client).Left);   // the drag's move survived the reload
    }

    [Fact]
    public void Before_the_project_is_built_its_controls_are_placeholders_that_save_unchanged_then_become_real()
    {
        var (dll, form) = Project(built: false);
        using var client = Connect(fastReload: true);
        client.Send(new P.SetProjectAssembly { DocId = Doc, Path = dll });
        var log = client.Expect<P.Log>(l => l.Text.Contains("isn't built yet"));
        Assert.Equal(2, log.Level);
        var panes = client.Open(Doc, form);
        client.Show(panes.Surface, 800, 600);
        client.Settle();
        Assert.IsType<UnknownControl>(Gauge(client));

        client.Send(new P.SaveDocument { DocId = Doc, Path = "" });
        Assert.True(client.Expect<P.DocumentSaved>().Ok);
        Assert.Equal(FormText, File.ReadAllText(form));

        // The build finishes: the real control replaces the placeholder, and the form is still unmodified.
        CopyBuild(Path.GetDirectoryName(dll)!);
        WaitFor(() => Gauge(client) is not UnknownControl, "the built Gauge");
        Assert.Equal("SampleControls.Gauge", Gauge(client).GetType().FullName);
        Assert.False(client.Ui(() => Document(client).Doc.Modified));
    }

    [Fact]
    public void A_control_that_fails_to_paint_is_reported_once_to_the_client()
    {
        var (dll, form) = Project();
        File.WriteAllText(form, FormText.Replace("\"class\": \"Gauge\"", "\"class\": \"BadPainter\"").Replace("\"value\": 7", "\"width\": 90"));
        using var client = Connect();
        client.Send(new P.SetProjectAssembly { DocId = Doc, Path = dll });
        var panes = client.Open(Doc, form);
        client.Send(new P.AttachWindow { WindowId = panes.Surface });
        client.Send(new P.ResizeWindow { WindowId = panes.Surface, Width = 800, Height = 600, Scale = 1 });
        var log = client.Expect<P.Log>(l => l.Text.Contains("can't paint at design time"));
        Assert.Equal("gauge1 (BadPainter) can't paint at design time: InvalidOperationException: paint broke", log.Text);
        client.Ui(() => Document(client).Doc.Form.InvalidateAll());
        client.Settle();
        client.Ui(() => Document(client).Doc.Form.InvalidateAll());
        Assert.Throws<TimeoutException>(() => client.Expect<P.Log>(l => l.Text.Contains("can't paint at design time"), timeoutMs: 500));
    }

    [Fact]
    public void Closing_the_last_document_of_a_project_unloads_it_and_two_documents_share_one_load()
    {
        var (dll, form) = Project();
        var second = Path.Combine(Path.GetDirectoryName(form)!, "Second.radform.json");
        File.WriteAllText(second, FormText.Replace("frmProject", "frmSecond"));
        using var client = Connect();
        client.Send(new P.SetProjectAssembly { DocId = Doc, Path = dll });
        client.Open(Doc, form);
        client.Send(new P.SetProjectAssembly { DocId = 2, Path = dll });
        client.Open(2, second);
        client.Settle();
        Assert.Single(client.Ui(() => client.Server!.Projects.Keys.ToList()));
        var gauge = Gauge(client).GetType();
        Assert.Same(gauge, client.Ui(() => client.Server!.Document(2)!.Doc.Form.FindControl("gauge1")!.GetType()));

        client.Send(new P.CloseDocument { DocId = Doc });
        client.Settle();
        Assert.Single(client.Ui(() => client.Server!.Projects.Keys.ToList()));
        client.Send(new P.CloseDocument { DocId = 2 });
        client.Settle();
        Assert.Empty(client.Ui(() => client.Server!.Projects.Keys.ToList()));
        Assert.Null(client.Ui(() => ComponentRegistry.Find(gauge)));
    }

    [Fact]
    public void A_document_without_a_project_assembly_shows_RadSharps_controls_only()
    {
        var (dll, form) = Project();
        var other = TestFiles.HelloForm();
        using var client = Connect();
        client.Send(new P.SetProjectAssembly { DocId = 2, Path = dll });
        client.Open(2, form);
        var panes = client.Open(Doc, other);
        client.Show(panes.Palette, 240, 600);
        client.Settle();
        var names = client.Ui(() => Document(client).PaletteTree!.Nodes.SelectMany(n => n.Nodes).Select(n => n.Text).ToList());
        Assert.Contains("Button", names);
        Assert.DoesNotContain("Gauge", names);
        Assert.DoesNotContain(PaletteCategories.Project, client.Ui(() => Document(client).PaletteTree!.Nodes.Select(n => n.Text).ToList()));

        // An empty path takes the project away from a document: its controls become placeholders that keep their text.
        client.Send(new P.SetProjectAssembly { DocId = 2, Path = "" });
        client.Settle();
        Assert.IsType<UnknownControl>(client.Ui(() => client.Server!.Document(2)!.Doc.Form.FindControl("gauge1")));
        Assert.Empty(client.Ui(() => client.Server!.Projects.Keys.ToList()));
    }

    /// <summary>Takes the rebuild check away from the host's loop: the test calls it with its own clock.</summary>
    static void ManualChecks(HostClient client) => client.Ui(() =>
    {
        client.Server!.AutoCheckProjects = false;
        client.Server.ProjectCheckInterval = TimeSpan.Zero;
        client.Server.ProjectSettleTime = TimeSpan.FromMilliseconds(750);
    });

    static void Check(HostClient client, long now) => client.Ui(() => client.Server!.CheckProjects(now));

    static ProjectAssembly Loaded(HostClient client) => client.Ui(() => client.Server!.Projects.Values.Single());

    [Fact]
    public void A_rebuild_that_keeps_changing_files_is_reloaded_only_once_it_has_settled()
    {
        var (dll, form) = Project();
        using var client = Connect();
        ManualChecks(client);
        client.Send(new P.SetProjectAssembly { DocId = Doc, Path = dll });
        client.Open(Doc, form);
        client.Settle();
        var first = Loaded(client);

        Rebuild(dll, 5);
        Check(client, 1000);                 // the rebuild is seen
        Rebuild(dll, 6);                     // ... and the copy goes on
        Check(client, 1600);                 // changed again: the wait starts over (it would be 600 ms since 1000)
        Check(client, 1800);                 // 800 ms since the first sighting, but only 200 since the last change
        Assert.Same(first, Loaded(client));
        Check(client, 2400);                 // unchanged for 800 ms
        Assert.NotSame(first, Loaded(client));
        Assert.Null(Loaded(client).Error);
    }

    [Fact]
    public void A_load_that_fails_right_after_a_rebuild_is_tried_once_more_when_it_has_settled()
    {
        var (dll, form) = Project();
        var good = File.ReadAllBytes(dll);
        using var client = Connect();
        ManualChecks(client);
        client.Send(new P.SetProjectAssembly { DocId = Doc, Path = dll });
        client.Open(Doc, form);
        client.Settle();

        // A half-written file...
        var stamp = DateTime.UtcNow.AddSeconds(5);
        File.WriteAllBytes(dll, good.AsSpan(0, good.Length / 2).ToArray());
        File.SetLastWriteTimeUtc(dll, stamp);
        Check(client, 1000);
        Check(client, 2000);
        Assert.NotNull(Loaded(client).Error);

        // ... which is complete a moment later, with the time stamp the failed read already saw.
        File.WriteAllBytes(dll, good);
        File.SetLastWriteTimeUtc(dll, stamp);
        Check(client, 2500);                 // not yet: a retry waits as long as a rebuild does
        Assert.NotNull(Loaded(client).Error);
        Check(client, 2800);
        Assert.Null(Loaded(client).Error);
        Assert.Equal("SampleControls.Gauge", Gauge(client).GetType().FullName);
    }

    [Fact]
    public void A_document_that_fails_to_open_does_not_keep_its_project_loaded()
    {
        var (dll, _) = Project();
        using var client = Connect();
        client.Send(new P.SetProjectAssembly { DocId = Doc, Path = dll });
        client.Send(new P.OpenDocument { DocId = Doc, Path = Path.Combine(Path.GetDirectoryName(dll)!, "missing.radform.json") });
        client.Expect<P.Error>(e => e.Text.Contains("Could not open"));
        client.Settle();
        Assert.Empty(client.Ui(() => client.Server!.Projects.Keys.ToList()));
    }

    [Fact]
    public void A_project_set_for_a_document_that_is_never_opened_is_unloaded_when_it_is_closed()
    {
        var (dll, _) = Project();
        using var client = Connect();
        client.Send(new P.SetProjectAssembly { DocId = Doc, Path = dll });
        client.Settle();
        Assert.Single(client.Ui(() => client.Server!.Projects.Keys.ToList()));
        client.Send(new P.CloseDocument { DocId = Doc });
        client.Settle();
        Assert.Empty(client.Ui(() => client.Server!.Projects.Keys.ToList()));
    }
}
