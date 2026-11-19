using System.Reflection;
using RadSharp.Design;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using RadSharp.Drawing;
using SkiaSharp;

namespace RadSharp.Tests;

/// <summary>Loading a project's classes into the (process-wide) registry: these tests run on their own.</summary>
[CollectionDefinition("ProjectAssembly", DisableParallelization = true)]
public sealed class ProjectAssemblyCollection;

/// <summary>
/// The designer loading a project's prebuilt output (the SampleControls fixture, built by the normal SDK with RadSharp's
/// generator) as the Visual Studio design host does, from a copy of its output folder so a test can "rebuild" it.
/// </summary>
[Collection("ProjectAssembly")]
public sealed class ProjectAssemblyTests : IDisposable
{
    readonly List<ProjectAssembly> _loaded = [];

    public void Dispose()
    {
        foreach (var p in _loaded) p.Dispose();
    }

    /// <summary>A fresh copy of the fixture's output folder; returns the path of SampleControls.dll in it.</summary>
    internal static string CopyOfSampleControls(string? into = null)
    {
        var built = typeof(ProjectAssemblyTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "SampleControls").Value!;
        var dir = into ?? Directory.CreateTempSubdirectory("radsharp-project-").FullName;
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(built)!))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
        return Path.Combine(dir, Path.GetFileName(built));
    }

    ProjectAssembly Load(string? path = null)
    {
        var project = ProjectAssembly.Load(path ?? CopyOfSampleControls());
        _loaded.Add(project);
        return project;
    }

    static string Form(params string[] children) => $$"""
        {
          "format": "radform",
          "version": "1.0",
          "generator": "RADSharp",
          "root": {
            "class": "Form",
            "name": "frmTest",
            "height": 300,
            "width": 400,
            "children": [{{string.Join(",", children)}}
            ]
          }
        }

        """;

    static DesignDocument Open(ProjectAssembly project, string json)
    {
        var doc = new DesignDocument { ProjectClasses = project.ClassSet.Find };
        doc.Load(json);
        return doc;
    }

    /// <summary>Rewrites the RadSharp.Core reference of an assembly to another major version.</summary>
    static void PointAtCore(string path, byte major)
    {
        var bytes = File.ReadAllBytes(path);
        using (var pe = new PEReader(new MemoryStream(bytes)))
        {
            var md = pe.GetMetadataReader();
            int rowSize = md.GetTableRowSize(TableIndex.AssemblyRef);
            int start = pe.PEHeaders.MetadataStartOffset + md.GetTableMetadataOffset(TableIndex.AssemblyRef);
            int row = 0;
            foreach (var h in md.AssemblyReferences)
            {
                row++;
                if (md.GetString(md.GetAssemblyReference(h).Name) == "RadSharp.Core")
                    bytes[start + (row - 1) * rowSize] = major;   // the version's major number, as a little-endian ushort
            }
        }
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void A_project_built_against_another_RadSharp_Core_says_so()
    {
        var path = CopyOfSampleControls();
        Assert.Equal(ProjectAssembly.DesignerCoreVersion.Major, ProjectAssembly.BuiltAgainstCore(path)!.Major);
        PointAtCore(path, 99);
        Assert.Equal(99, ProjectAssembly.BuiltAgainstCore(path)!.Major);
        var project = Load(path);
        var lines = project.Error != null ? [project.Error] : project.Warnings;
        var note = Assert.Single(lines, l => l.Contains("was built against RadSharp.Core 99."));
        Assert.Contains($"the designer has {ProjectAssembly.DesignerCoreVersion}", note);
        Assert.Contains("show as placeholders or errors", note);
    }

    [Fact]
    public void A_project_built_against_the_same_RadSharp_Core_gets_no_version_note()
    {
        var project = Load();
        Assert.DoesNotContain(project.Warnings, w => w.Contains("built against"));
    }

    [Fact]
    public void A_missing_member_error_gets_the_version_hint_and_other_errors_do_not()
    {
        const string hint = "built against RadSharp.Core 2.0";
        Assert.EndsWith(". " + hint, ProjectClassSet.Describe(new MissingMethodException("no such method"), hint));
        Assert.EndsWith(". " + hint, ProjectClassSet.Describe(new TypeLoadException("no such type"), hint));
        Assert.EndsWith(". " + hint, ProjectClassSet.Describe(new TargetInvocationException(new MissingFieldException("f")), hint));
        Assert.DoesNotContain(hint, ProjectClassSet.Describe(new InvalidOperationException("other"), hint));
        Assert.DoesNotContain(hint, ProjectClassSet.Describe(new MissingMethodException("no such method")));
    }

    [Fact]
    public void A_locked_symbols_file_does_not_fail_the_load()
    {
        var path = CopyOfSampleControls();
        var pdb = Path.ChangeExtension(path, ".pdb");
        Assert.True(File.Exists(pdb), "the fixture has symbols");
        using var lockedFile = new FileStream(pdb, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var project = Load(path);
        Assert.Null(project.Error);
        Assert.NotNull(project.ClassSet.Find("Gauge"));
    }

    [Fact]
    public void Loading_offers_the_projects_and_its_librarys_controls_but_not_its_forms_or_one_that_throws()
    {
        var theme = Theme.Current;
        var project = Load();
        Assert.Null(project.Error);
        Assert.Equal(["BadPainter", "ChattyPainter", "Fragile", "Gauge", "TitleBar", "Swatch"], project.Classes.Select(c => c.Name));
        Assert.Equal(PaletteCategories.Project, project.ClassSet.CategoryOf(project.ClassSet.Find("Gauge")!));
        Assert.Equal("SampleData", project.ClassSet.CategoryOf(project.ClassSet.Find("Swatch")!));
        Assert.Null(project.ClassSet.Find("SampleForm"));
        Assert.Contains(project.Warnings, w => w.StartsWith("Exploding can't be created at design time") && w.Contains("no budget here"));
        // The setup ran before the prototypes (Gauge's constructor checks), and its dark theme was undone.
        Assert.Same(theme, Theme.Current);
        // Not RadSharp's own palette: that stays the same for documents of other projects.
        Assert.DoesNotContain(ComponentPalette.Classes(), c => c.Name == "Gauge");
    }

    [Fact]
    public void The_palette_lists_the_projects_controls_with_RadSharps_under_the_shared_categories()
    {
        var set = Load().ClassSet;
        var tree = new TreeView();
        ComponentPalette.Fill(tree, set.PaletteClasses(ComponentPalette.Classes()), "", set.PaletteCategory);
        var categories = tree.Nodes.Select(n => n.Text).ToList();
        Assert.Equal([PaletteCategories.Project, "SampleData"], categories.Skip(categories.Count - 2));
        Assert.Equal(["BadPainter", "ChattyPainter", "Fragile", "Gauge", "TitleBar"], tree.Nodes.Single(n => n.Text == PaletteCategories.Project).Nodes.Select(n => n.Text));

        // The Visual Studio toolbox lists them first, as Visual Studio lists a project's own components.
        ComponentPalette.Fill(tree, set.PaletteClasses(ComponentPalette.Classes()), "", set.PaletteCategory, projectFirst: true);
        Assert.Equal([PaletteCategories.Project, "SampleData"], tree.Nodes.Take(2).Select(n => n.Text));
    }

    [Fact]
    public void Disposing_forgets_the_classes()
    {
        var project = Load();
        var gauge = project.ClassSet.Find("Gauge")!;
        project.Dispose();
        Assert.Null(ComponentRegistry.Find(gauge.Type));
        Assert.Empty(project.Classes);
        Assert.Null(project.Assembly);
    }

    [Fact]
    public void The_output_folder_stays_writable_and_a_rebuild_is_noticed()
    {
        var path = CopyOfSampleControls();
        var project = Load(path);
        Assert.False(project.IsStale());
        // Gauge paints with a colour from SampleData.dll, which is loaded from the project's folder.
        var doc = Open(project, Form("""{ "class": "Gauge", "name": "gauge1", "left": 8, "top": 8 }"""));
        Assert.Equal("SampleControls.Gauge", doc.Form.FindControl("gauge1")!.GetType().FullName);
        using (var bitmap = FormRenderer.RenderToBitmap(doc.Form))
            Assert.Equal(new SKColor(0x33, 0x66, 0xCC), bitmap.GetPixel(20, 20));
        // Not locked: a build can overwrite both files while they are loaded.
        var data = Path.Combine(Path.GetDirectoryName(path)!, "SampleData.dll");
        File.WriteAllBytes(path, File.ReadAllBytes(path));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.True(project.IsStale());
        var again = Load(path);
        Assert.False(again.IsStale());
        File.WriteAllBytes(data, File.ReadAllBytes(data));
        File.SetLastWriteTimeUtc(data, DateTime.UtcNow.AddSeconds(10));
        Assert.True(again.IsStale());
    }

    [Fact]
    public void A_missing_assembly_is_an_error_until_it_is_built()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("radsharp-project-").FullName, "Nope.dll");
        var project = Load(path);
        Assert.Contains("isn't built yet", project.Error);
        Assert.Empty(project.Classes);
        Assert.False(project.IsStale());
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.True(project.IsStale());
        var broken = Load(path);
        Assert.Contains("Can't load Nope.dll", broken.Error);
        Assert.False(broken.IsStale());
    }

    [Fact]
    public void A_controls_own_children_are_not_saved_and_clicking_one_selects_the_control()
    {
        var project = Load();
        var json = Form("""

                  {
                    "class": "TitleBar",
                    "name": "title1",
                    "left": 10,
                    "top": 10
                  }
            """);
        var doc = Open(project, json);
        var title = doc.Form.FindControl("title1")!;
        var search = Assert.Single(title.Children);
        Assert.True(search.IsTransient);
        Assert.Equal(json, doc.Save());

        var host = new Form();
        host.SetBounds(0, 0, 800, 600);
        var surface = new DesignSurface { Document = doc, Align = Align.Client };
        host.Add(surface);
        host.PerformLayout();
        var (sx, sy) = surface.DesignToScreen(search.AbsoluteLeft + 4, search.AbsoluteTop + 4);
        host.Simulate.Click(surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy);
        Assert.Same(title, Assert.Single(doc.Selection));
    }

    [Fact]
    public void A_class_from_an_unbuilt_project_is_a_placeholder_until_the_project_loads_then_the_real_control()
    {
        var json = Form("""

                  {
                    "class": "Gauge",
                    "name": "gauge1",
                    "left": 8,
                    "top": 8,
                    "value": 7
                  }
            """);
        var doc = new DesignDocument();
        doc.Load(json);
        Assert.IsType<UnknownControl>(doc.Form.FindControl("gauge1"));
        Assert.Equal(json, doc.Save());
        doc.Select(doc.Form.FindControl("gauge1"));

        var saved = doc.Save();
        var project = Load();
        var (_, problem) = doc.ReloadWith(saved, project.ClassSet.Find);
        Assert.Null(problem);
        var gauge = doc.Form.FindControl("gauge1")!;
        Assert.Equal("SampleControls.Gauge", gauge.GetType().FullName);
        Assert.Equal(7, (int)gauge.GetType().GetProperty("Value")!.GetValue(gauge)!);
        Assert.Same(gauge, doc.Primary);
        Assert.False(doc.Modified);
        Assert.Equal(json, doc.Save());
    }

    [Fact]
    public void Reloading_with_a_rebuilt_project_keeps_unsaved_changes_and_undo()
    {
        var path = CopyOfSampleControls();
        var first = Load(path);
        var doc = Open(first, Form("""{ "class": "Gauge", "name": "gauge1", "left": 8, "top": 8 }"""));
        doc.Snapshot();
        doc.Form.FindControl("gauge1")!.SetBounds(50, 60, 120, 40);
        doc.NotifyChanged();
        Assert.True(doc.Modified);

        var json = doc.Save();
        var second = Load(path);
        doc.ReloadWith(json, second.ClassSet.Find);
        first.Dispose();
        var gauge = doc.Form.FindControl("gauge1")!;
        Assert.Same(second.Assembly, gauge.GetType().Assembly);
        Assert.True(doc.Modified);
        Assert.Equal(50, gauge.Left);
        Assert.True(doc.Undo());
        Assert.Equal(8, doc.Form.FindControl("gauge1")!.Left);
        Assert.False(doc.Modified);
    }

    [Fact]
    public void A_control_that_fails_to_paint_draws_an_error_box_and_the_rest_of_the_form_paints()
    {
        var project = Load();
        var doc = Open(project, Form("""{ "class": "BadPainter", "name": "bad1", "left": 0, "top": 0, "width": 100, "height": 40 }""",
            """{ "class": "Gauge", "name": "gauge1", "left": 200, "top": 0 }"""));
        var errors = new List<string>();
        void OnError(Control c, Exception e) => errors.Add($"{c.Name}: {e.Message}");
        FormRenderer.PaintError += OnError;
        try
        {
            using var bitmap = FormRenderer.RenderToBitmap(doc.Form);
            Assert.Equal(["bad1: paint broke"], errors);
            Assert.Equal(new SKColor(0x33, 0x66, 0xCC), bitmap.GetPixel(220, 20));
        }
        finally { FormRenderer.PaintError -= OnError; }
    }
}
