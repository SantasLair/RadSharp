using System.Text.Json.Nodes;
using RadSharp.Design.Code;

namespace RadSharp.Ide.Language.Tests;

public class WorkspaceTests
{
    /// <summary>
    /// The workspace and the live run must see the same program: same errors for each sample, as is and with
    /// an injected missing handler (the comparison skips the entry point file, which only the workspace compiles).
    /// </summary>
    [Theory]
    [InlineData("TileEditor", "MainForm.radform.json", "\"onClick\": \"mnuNew_Click\"")]
    [InlineData("SettingsDialog", "MainForm.radform.json", "\"onClick\": \"btnSettings_Click\"")]
    public async Task Workspace_errors_match_the_live_run(string sample, string form, string binding)
    {
        var (workspace, language) = await Samples.OpenAsync(sample);
        using var _ = workspace;
        var project = Samples.Project(sample);

        await Compare(null);

        var formPath = Samples.File(sample, form);
        var broken = File.ReadAllText(formPath).Replace(binding, binding.Replace("_Click", "_Missing"));
        workspace.UpdateFormText(formPath, broken);
        var errors = await Compare(new Dictionary<string, string> { [formPath] = broken });
        Assert.Contains("RAD1001", errors);

        async Task<HashSet<string>> Compare(Dictionary<string, string>? overrides)
        {
            using var live = LiveSession.Compile(project, overrides);
            var expected = live.Messages.Select(Key).ToHashSet();
            var diagnostics = (await language.GetProjectDiagnosticsAsync()).Value;
            var entryPoints = diagnostics.Where(d => d.IsError && Path.GetFileName(d.File) == "Program.cs").ToList();
            Assert.True(entryPoints.Count == 0, string.Join("\n", entryPoints));   // Program.cs resolves (RadSharp.Platform.Sdl from the project reference)
            var actual = diagnostics.Where(d => d.IsError).Select(d => Key(d.ToBuildMessage())).ToHashSet();
            Assert.Equal(expected.Order(), actual.Order());
            return [.. live.Messages.Select(m => m.Id)];
        }

        static string Key(BuildMessage m) => $"{Path.GetFileName(m.File)}({m.Line},{m.Column}) {m.Id}";
    }

    [Fact]
    public async Task Generated_form_code_is_in_the_workspace()
    {
        var (workspace, _) = await Samples.OpenAsync("SettingsDialog");
        using var __ = workspace;
        var generated = await workspace.GetGeneratedFilesAsync();
        var settings = generated.Single(g => g.HintName == "SettingsForm.Form.g.cs");
        Assert.Contains("edtUser", settings.Text);
        Assert.Contains("private void InitializeComponent()", settings.Text);
    }

    [Fact]
    public async Task References_include_project_reference_outputs()
    {
        var references = await ProjectReferences.ResolveAsync(Samples.Project("TileEditor"), RestoreMode.Never);
        Assert.Contains(references.Paths, p => Path.GetFileName(p) == "RadSharp.Platform.Sdl.dll");
        Assert.Contains(references.Paths, p => Path.GetFileName(p) == "RadSharp.Core.dll");
        // Nothing the host already provides is added again: RadSharp.Core is the host's copy.
        var host = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Where(File.Exists).ToList();   // what LiveSession compiles against
        var added = references.Paths.Skip(host.Count).ToList();
        Assert.Contains(added, p => Path.GetFileName(p) == "RadSharp.Platform.Sdl.dll");
        Assert.DoesNotContain(added, p => host.Any(h => Path.GetFileNameWithoutExtension(h).Equals(Path.GetFileNameWithoutExtension(p), StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Package compile assets come from project.assets.json; placeholders and missing files are skipped with a note.</summary>
    [Fact]
    public async Task References_read_package_compile_assets()
    {
        var dir = Directory.CreateTempSubdirectory("radsharp-ide-").FullName;
        var packages = Path.Combine(dir, "packages");
        var lib = Path.Combine(packages, "fake.package", "1.2.3", "lib", "net10.0");
        Directory.CreateDirectory(lib);
        File.WriteAllBytes(Path.Combine(lib, "Fake.Package.dll"), [0]);
        File.WriteAllText(Path.Combine(dir, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="Fake.Package" Version="1.2.3" /></ItemGroup>
            </Project>
            """);
        Directory.CreateDirectory(Path.Combine(dir, "obj"));
        File.WriteAllText(Path.Combine(dir, "obj", "project.assets.json"), $$"""
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Fake.Package/1.2.3": { "type": "package", "compile": { "lib/net10.0/Fake.Package.dll": {} } },
                  "Meta.Package/1.0.0": { "type": "package", "compile": { "lib/net10.0/_._": {} } },
                  "Gone.Package/1.0.0": { "type": "package", "compile": { "lib/net10.0/Gone.dll": {} } }
                },
                "net10.0/win-x64": {
                  "Runtime.Only/1.0.0": { "type": "package", "compile": { "lib/net10.0/Runtime.Only.dll": {} } }
                }
              },
              "libraries": {
                "Fake.Package/1.2.3": { "type": "package", "path": "fake.package/1.2.3" },
                "Meta.Package/1.0.0": { "type": "package", "path": "meta.package/1.0.0" },
                "Gone.Package/1.0.0": { "type": "package", "path": "gone.package/1.0.0" },
                "Runtime.Only/1.0.0": { "type": "package", "path": "runtime.only/1.0.0" }
              },
              "packageFolders": { {{System.Text.Json.JsonSerializer.Serialize(packages + Path.DirectorySeparatorChar)}}: {} }
            }
            """);
        // The assets file is newer than the project: no restore is needed.
        File.SetLastWriteTimeUtc(Path.Combine(dir, "App.csproj"), DateTime.UtcNow.AddMinutes(-5));

        var references = await ProjectReferences.ResolveAsync(new RadProject(Path.Combine(dir, "App.csproj")));
        Assert.Contains(Path.Combine(lib, "Fake.Package.dll"), references.Paths);
        Assert.DoesNotContain(references.Paths, p => p.Contains("Runtime.Only") || p.EndsWith("_._"));
        Assert.Contains(references.Notes, n => n.Contains("Gone.Package"));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Every_update_bumps_the_version_and_makes_older_results_stale()
    {
        var (workspace, language) = await Samples.OpenAsync("SettingsDialog");
        using var _ = workspace;
        var file = Samples.File("SettingsDialog", "SettingsForm.cs");
        var before = await language.ClassifySyntaxAsync(file);
        Assert.True(workspace.IsCurrent(before.Version));
        long docVersion = workspace.GetDocumentVersion(file);

        var version = workspace.UpdateDocumentText(file, File.ReadAllText(file) + "\n// edited\n");
        Assert.True(version > before.Version);
        Assert.False(workspace.IsCurrent(before.Version));
        Assert.Equal(docVersion + 1, workspace.GetDocumentVersion(file));
        Assert.Equal(version, (await language.ClassifySyntaxAsync(file)).Version);

        // The same text again is not a change.
        Assert.Equal(version, workspace.UpdateDocumentText(file, File.ReadAllText(file) + "\n// edited\n"));
    }

    [Fact]
    public async Task Requests_are_cancellable()
    {
        var (workspace, language) = await Samples.OpenAsync("TileEditor");
        using var _ = workspace;
        var file = Samples.File("TileEditor", "MainForm.cs");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => language.GetCompletionsAsync(file, 100, cancel: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => language.GetProjectDiagnosticsAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => language.ClassifyAsync(file, cancel: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => language.FindReferencesAsync(file, 100, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => language.RenameAsync(file, 100, "x", cancelled.Token));

        // Cancelled mid-flight, on a cold workspace (the first compilation takes a while).
        var (cold, coldLanguage) = await Samples.OpenAsync("TileEditor");
        using var __ = cold;
        using var soon = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coldLanguage.GetProjectDiagnosticsAsync(soon.Token));
    }

    [Fact]
    public async Task File_watcher_follows_disk_but_not_open_buffers()
    {
        var dir = Directory.CreateTempSubdirectory("radsharp-ide-").FullName;
        File.WriteAllText(Path.Combine(dir, "Watched.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>Watched</RootNamespace></PropertyGroup>
            </Project>
            """);
        var a = Path.Combine(dir, "A.cs");
        var b = Path.Combine(dir, "B.cs");
        File.WriteAllText(a, "namespace Watched; class A { }");
        File.WriteAllText(b, "namespace Watched; class B { }");
        using var workspace = await ProjectWorkspace.OpenAsync(new RadProject(Path.Combine(dir, "Watched.csproj")));

        workspace.UpdateDocumentText(b, "namespace Watched; class B { int unsaved; }");
        var changed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.Changed += c => { if (c.Kind == WorkspaceChangeKind.DocumentAdded) changed.TrySetResult(c.Path); };

        File.WriteAllText(a, "namespace Watched; class A { int fromDisk; }");
        File.WriteAllText(b, "namespace Watched; class B { int alsoFromDisk; }");
        var c = Path.Combine(dir, "C.cs");
        File.WriteAllText(c, "namespace Watched; class C { }");

        Assert.Equal(c, await changed.Task.WaitAsync(TimeSpan.FromSeconds(10)), ignoreCase: true);
        for (int i = 0; i < 100 && !workspace.GetText(a)!.Contains("fromDisk"); i++) await Task.Delay(50);
        Assert.Contains("fromDisk", workspace.GetText(a));
        Assert.Contains("unsaved", workspace.GetText(b));         // open in an editor: left alone

        workspace.CloseDocument(b);
        Assert.Contains("alsoFromDisk", workspace.GetText(b));    // closed: back to disk
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>A form JSON with one more button: what the designer hands over after a drop, before any save.</summary>
    internal static string WithButton(string json, string name)
    {
        var doc = JsonNode.Parse(json)!;
        doc["root"]!["children"]!.AsArray().Add(new JsonObject
        {
            ["class"] = "Button", ["name"] = name, ["caption"] = "Apply", ["left"] = 140, ["top"] = 286, ["width"] = 80, ["height"] = 30,
        });
        return doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}
