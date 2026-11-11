using RadSharp.Design.Code;

namespace RadSharp.Tests;

/// <summary>I8: file operations, NuGet parsing and command building, .csproj editing, git parsing and solutions (no UI, no network).</summary>
public class ProjectToolsTests
{
    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-i8-").FullName;

    sealed class FakeRunner : IPackageRunner
    {
        public readonly List<string[]> Calls = [];
        public int ExitCode;
        public Task<int> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, Action<string> onLine, CancellationToken cancel = default)
        {
            Calls.Add([.. arguments]);
            onLine("fake: " + string.Join(' ', arguments));
            if (ExitCode == 0 && arguments[0] == "add")
            {
                var proj = arguments[1];
                var id = arguments[3];
                var version = arguments.Count > 5 ? arguments[5] : "1.2.3";
                var text = File.ReadAllText(proj);
                text = System.Text.RegularExpressions.Regex.Replace(text, $"<PackageReference Include=\"{id}\"[^>]*/>\\s*", "");
                text = text.Replace("</ItemGroup>", $"  <PackageReference Include=\"{id}\" Version=\"{version}\" />\n  </ItemGroup>");
                File.WriteAllText(proj, text);
            }
            else if (ExitCode == 0 && arguments[0] == "remove")
            {
                var text = File.ReadAllText(arguments[1]);
                File.WriteAllText(arguments[1], System.Text.RegularExpressions.Regex.Replace(text, $"[ \\t]*<PackageReference Include=\"{arguments[3]}\"[^>]*/>\\r?\\n?", ""));
            }
            return Task.FromResult(ExitCode);
        }
    }

    // ---- Scripted workflow ----

    [Fact]
    public async Task Scripted_project_folder_class_move_namespace_and_package()
    {
        var project = RadProject.Create(TempDir(), "Scripted");
        var ops = new ProjectFileOps(project);

        var folder = ops.CreateFolder(project.Directory, "Models");
        Assert.True(Directory.Exists(folder));
        var cls = ops.NewFile(project.Directory, "Customer", FileTemplate.Class);
        Assert.Equal("Scripted", ProjectFileOps.DeclaredNamespace(File.ReadAllText(cls)));

        var changes = ops.Move(cls, folder, fixNamespace: true);
        var moved = Path.Combine(folder, "Customer.cs");
        Assert.Equal([new FileChange(cls, moved)], changes);
        Assert.False(File.Exists(cls));
        Assert.Equal("Scripted.Models", ProjectFileOps.DeclaredNamespace(File.ReadAllText(moved)));
        Assert.Contains("public class Customer", File.ReadAllText(moved));

        var runner = new FakeRunner();
        var nuget = new NuGetService(runner, new HttpClient());
        var lines = new List<string>();
        Assert.Equal(0, await nuget.AddAsync(project, "Newtonsoft.Json", "13.0.3", lines.Add));
        Assert.Contains(NuGetService.InstalledPackages(project.ProjectFile), p => p.Id == "Newtonsoft.Json" && p.Version == "13.0.3");
        Assert.Equal(["add", project.ProjectFile, "package", "Newtonsoft.Json", "--version", "13.0.3"], runner.Calls[0]);
        Assert.Contains(lines, l => l.StartsWith("fake:"));
        Assert.Equal(0, await nuget.RemoveAsync(project, "Newtonsoft.Json", lines.Add));
        Assert.DoesNotContain(NuGetService.InstalledPackages(project.ProjectFile), p => p.Id == "Newtonsoft.Json");

        // The result still parses as a project with its form.
        Assert.Contains(project.SourceFiles, f => f == moved);
        Assert.Single(project.FormFiles);
    }

    [Fact]
    public void Form_files_travel_together_and_class_renames_follow()
    {
        var project = RadProject.Create(TempDir(), "Forms");
        var ops = new ProjectFileOps(project);
        var form = project.FormFileFor("MainForm")!;

        var changes = ops.Rename(form, "ShellForm");
        Assert.Equal(2, changes.Count);
        Assert.True(File.Exists(Path.Combine(project.Directory, "ShellForm.radform.json")));
        Assert.True(File.Exists(Path.Combine(project.Directory, "ShellForm.cs")));
        Assert.False(File.Exists(Path.Combine(project.Directory, "MainForm.cs")));
        Assert.Contains("class ShellForm", File.ReadAllText(Path.Combine(project.Directory, "ShellForm.cs")));
        Assert.Contains("new ShellForm()", File.ReadAllText(Path.Combine(project.Directory, "Program.cs")));

        var sub = ops.CreateFolder(project.Directory, "Views");
        var moved = ops.Move(Path.Combine(project.Directory, "ShellForm.cs"), sub, fixNamespace: true);   // the code-behind moves with its form
        Assert.Equal(2, moved.Count);
        Assert.True(File.Exists(Path.Combine(sub, "ShellForm.radform.json")));
        Assert.Contains("namespace Forms.Views;", File.ReadAllText(Path.Combine(sub, "ShellForm.cs")));

        var removed = ops.Delete(Path.Combine(sub, "ShellForm.radform.json"));
        Assert.Equal(2, removed.Count);
        Assert.Empty(Directory.GetFiles(sub));
    }

    [Fact]
    public void New_form_interface_and_validation()
    {
        var project = RadProject.Create(TempDir(), "Tpl");
        var ops = new ProjectFileOps(project);
        var dir = ops.CreateFolder(project.Directory, "Ui");
        var form = ops.NewFile(dir, "AboutForm", FileTemplate.Form);
        Assert.EndsWith("AboutForm.radform.json", form);
        Assert.Contains("namespace Tpl.Ui;", File.ReadAllText(RadProject.CodeFileFor(form)));
        var itf = ops.NewFile(dir, "IThing", FileTemplate.Interface);
        Assert.Contains("public interface IThing", File.ReadAllText(itf));
        Assert.Throws<IOException>(() => ops.NewFile(dir, "IThing", FileTemplate.Interface));
        Assert.Throws<IOException>(() => ops.NewFile(dir, "not valid", FileTemplate.Class));
        Assert.Throws<IOException>(() => ops.CreateFolder(dir, "a/b"));
    }

    [Fact]
    public void Plain_class_rename_renames_the_type_and_namespace_rewrite_handles_block_syntax()
    {
        var project = RadProject.Create(TempDir(), "Ren");
        var ops = new ProjectFileOps(project);
        var file = ops.NewFile(project.Directory, "Old", FileTemplate.Class);
        File.WriteAllText(Path.Combine(project.Directory, "Use.cs"), "namespace Ren; class Use { Old o; }\n");
        ops.Rename(file, "New");
        Assert.Contains("class New", File.ReadAllText(Path.Combine(project.Directory, "New.cs")));
        Assert.Contains("New o;", File.ReadAllText(Path.Combine(project.Directory, "Use.cs")));
        Assert.Equal("namespace A.B {\n class X {} }", ProjectFileOps.RewriteNamespace("namespace Old.Ns {\n class X {} }", "A.B"));
        Assert.Equal("class Y {}", ProjectFileOps.RewriteNamespace("class Y {}", "A"));
    }

    [Fact]
    public void Folder_move_and_rename_report_each_file()
    {
        var project = RadProject.Create(TempDir(), "Fold");
        var ops = new ProjectFileOps(project);
        var a = ops.CreateFolder(project.Directory, "A");
        var b = ops.CreateFolder(project.Directory, "B");
        ops.NewFile(a, "One", FileTemplate.Class);
        var changes = ops.Move(a, b, fixNamespace: true);
        Assert.Equal(Path.Combine(b, "A", "One.cs"), Assert.Single(changes).NewPath);
        Assert.Equal("Fold.B.A", ProjectFileOps.DeclaredNamespace(File.ReadAllText(changes[0].NewPath)));
        Assert.Throws<IOException>(() => ops.Move(b, Path.Combine(b, "A"), false));
        var renamed = ops.Rename(Path.Combine(b, "A"), "C");
        Assert.EndsWith(Path.Combine("B", "C", "One.cs"), renamed[0].NewPath);
        Assert.True(ops.NamespaceMismatch(renamed[0].NewPath));   // the folder was renamed; the offer to fix the namespace follows
        ops.FixNamespaces([renamed[0].NewPath]);
        Assert.False(ops.NamespaceMismatch(renamed[0].NewPath));
    }

    // ---- NuGet ----

    [Fact]
    public void Installed_packages_read_attribute_and_element_versions()
    {
        var list = NuGetService.InstalledPackagesFromXml("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="A" Version="1.0.0" />
                <PackageReference Include="B"><Version>2.0.0-beta</Version></PackageReference>
                <PackageReference Include="C" />
              </ItemGroup>
            </Project>
            """);
        Assert.Equal([new PackageRef("A", "1.0.0"), new PackageRef("B", "2.0.0-beta"), new PackageRef("C", null)], list);
    }

    [Fact]
    public void Search_response_service_index_and_urls_parse_and_build()
    {
        var results = NuGetService.ParseSearchResults("""
            {"totalHits":2,"data":[
              {"id":"Serilog","version":"4.0.0","description":"Logging","totalDownloads":123456789,"versions":[{"version":"3.0.0"},{"version":"4.0.0"}]},
              {"id":"Other","version":"1.0.0"} ]}
            """);
        Assert.Equal(2, results.Count);
        Assert.Equal("Serilog", results[0].Id);
        Assert.Equal(123456789, results[0].TotalDownloads);
        Assert.Equal(["3.0.0", "4.0.0"], results[0].Versions);
        Assert.Empty(results[1].Versions);
        Assert.Empty(NuGetService.ParseSearchResults("{}"));

        var endpoint = NuGetService.ParseServiceIndex("""
            {"version":"3.0.0","resources":[
              {"@id":"https://x/a","@type":"SearchGalleryQueryService/3.0.0-rc"},
              {"@id":"https://azuresearch-usnc.nuget.org/query","@type":"SearchQueryService"} ]}
            """);
        Assert.Equal("https://azuresearch-usnc.nuget.org/query", endpoint);
        Assert.Null(NuGetService.ParseServiceIndex("""{"resources":[]}"""));
        Assert.Equal("https://s/query?q=a%20b&prerelease=true&skip=5&take=10&semVerLevel=2.0.0", NuGetService.BuildSearchUrl("https://s/query", "a b", true, 5, 10));
    }

    [Fact]
    public void Dotnet_commands_are_built_for_add_update_remove_and_the_client_is_generic()
    {
        Assert.Equal(["add", "p.csproj", "package", "X"], NuGetService.AddArguments("p.csproj", "X"));
        Assert.Equal(["add", "p.csproj", "package", "X", "--prerelease"], NuGetService.AddArguments("p.csproj", "X", prerelease: true));
        Assert.Equal(["add", "p.csproj", "package", "X", "--version", "1.2.3"], NuGetService.AddArguments("p.csproj", "X", " 1.2.3 "));
        Assert.Equal(["remove", "p.csproj", "package", "X"], NuGetService.RemoveArguments("p.csproj", "X"));
        using var http = NuGetService.CreateHttpClient();
        Assert.Equal("RADSharp-IDE", http.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public async Task Failed_runs_report_the_exit_code()
    {
        var project = RadProject.Create(TempDir(), "Fail");
        var runner = new FakeRunner { ExitCode = 1 };
        Assert.Equal(1, await new NuGetService(runner, new HttpClient()).UpdateAsync(project, "X", _ => { }));
        Assert.Equal(["add", project.ProjectFile, "package", "X"], runner.Calls[0]);
    }

    [Fact]
    public async Task Real_dotnet_add_and_remove_package_offline_from_the_local_cache()
    {
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", "newtonsoft.json");
        if (!Directory.Exists(cache)) return;
        var version = Directory.GetDirectories(cache).Select(Path.GetFileName).FirstOrDefault(v => v != null && !v.Contains('-'));
        if (version == null) return;   // nothing cached: the offline run is skipped, the fake-runner tests cover the logic
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "Real.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        var project = new RadProject(Path.Combine(dir, "Real.csproj"));
        var nuget = new NuGetService();
        var lines = new List<string>();
        int exit;
        try { exit = await nuget.AddAsync(project, "Newtonsoft.Json", version, lines.Add, source: Path.GetDirectoryName(cache)); }
        catch (System.ComponentModel.Win32Exception) { return; }   // no dotnet on the PATH
        Assert.True(exit == 0, string.Join("\n", lines));
        Assert.Contains(NuGetService.InstalledPackages(project.ProjectFile), p => p.Id == "Newtonsoft.Json" && p.Version == version);
        Assert.Equal(0, await nuget.RemoveAsync(project, "Newtonsoft.Json", lines.Add));
        Assert.Empty(NuGetService.InstalledPackages(project.ProjectFile));
    }

    // ---- Project properties ----

    const string SampleProject = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n  <!-- keep me -->\r\n  <PropertyGroup>\r\n    <OutputType>WinExe</OutputType>\r\n    <TargetFramework>net10.0</TargetFramework>\r\n    <RootNamespace>App</RootNamespace>\r\n    <PublishAot>true</PublishAot>\r\n    <MyCustom Foo=\"bar\">x</MyCustom>\r\n  </PropertyGroup>\r\n\r\n  <PropertyGroup Condition=\"'$(Configuration)'=='Release'\">\r\n    <Optimize>true</Optimize>\r\n  </PropertyGroup>\r\n\r\n  <ItemGroup>\r\n    <PackageReference Include=\"X\" Version=\"1.0\" />\r\n  </ItemGroup>\r\n\r\n</Project>\r\n";

    [Fact]
    public void Csproj_without_changes_round_trips_byte_for_byte()
    {
        var path = Path.Combine(TempDir(), "A.csproj");
        File.WriteAllText(path, SampleProject);
        var ed = CsprojEditor.Load(path);
        Assert.Equal("App", ed.Read().RootNamespace);
        Assert.True(ed.Read().PublishAot);
        ed.Apply(ed.Read());   // applying what is already there changes nothing
        Assert.False(ed.Changed);
        ed.Save();
        Assert.Equal(SampleProject, File.ReadAllText(path));
    }

    [Fact]
    public void Csproj_edits_touch_only_what_changed()
    {
        var path = Path.Combine(TempDir(), "A.csproj");
        File.WriteAllText(path, SampleProject);
        var ed = CsprojEditor.Load(path);
        var p = ed.Read();
        p.Version = "2.1.0";
        p.ApplicationIcon = "app.ico";
        p.PublishAot = false;
        p.RuntimeIdentifiers = "win-x64;linux-x64";
        p.AssemblyName = "My App";
        ed.Apply(p);
        ed.Save();
        var text = File.ReadAllText(path);
        Assert.Contains("<!-- keep me -->", text);
        Assert.Contains("<MyCustom Foo=\"bar\">x</MyCustom>", text);
        Assert.Contains("<Optimize>true</Optimize>", text);
        Assert.Contains("<PackageReference Include=\"X\" Version=\"1.0\" />", text);
        Assert.Contains("<PublishAot>false</PublishAot>", text);
        Assert.Contains("    <Version>2.1.0</Version>\r\n", text);   // new elements take the neighbours' indentation and line endings
        Assert.DoesNotContain("\r\n\r\n\r\n", text);
        var again = CsprojEditor.Parse(text).Read();
        Assert.Equal("2.1.0", again.Version);
        Assert.Equal("app.ico", again.ApplicationIcon);
        Assert.Equal("win-x64;linux-x64", again.RuntimeIdentifiers);
        Assert.False(again.PublishAot);

        // Everything outside the edited lines is identical to the original.
        var original = SampleProject.Split("\r\n");
        var edited = text.Split("\r\n");
        foreach (var line in original.Where(l => !l.Contains("PublishAot"))) Assert.Contains(line, edited);

        // Clearing the added values removes the elements and their indentation lines again.
        var ed2 = CsprojEditor.Parse(text);
        ed2.Set("Version", "");
        ed2.Set("ApplicationIcon", null);
        ed2.Set("AssemblyName", null);
        ed2.Set("RuntimeIdentifiers", null);
        ed2.Set("PublishAot", "true");
        Assert.Equal(SampleProject, ed2.ToXml());
    }

    [Fact]
    public void Csproj_keeps_bom_declaration_and_lf_endings()
    {
        var path = Path.Combine(TempDir(), "B.csproj");
        var original = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n";
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(original)]);
        var ed = CsprojEditor.Load(path);
        ed.Set("Version", "1.0.0");
        ed.Save();
        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = System.Text.Encoding.UTF8.GetString(bytes[3..]);
        Assert.Equal(original.Replace("  </PropertyGroup>", "    <Version>1.0.0</Version>\n  </PropertyGroup>"), text);
    }

    // ---- Git ----

    [Fact]
    public void Porcelain_z_output_parses_states_renames_and_spaces()
    {
        var entries = GitStatusParser.Parse(" M src/a.cs\0A  new file.cs\0?? untracked.cs\0R  new.cs\0old.cs\0 D gone.cs\0UU both.cs\0!! ignored.log\0MM both-staged.cs\0");
        Assert.Equal(
            [GitState.Modified, GitState.Added, GitState.Untracked, GitState.Renamed, GitState.Deleted, GitState.Conflicted, GitState.Modified],
            entries.Select(e => e.State));
        Assert.Equal("new file.cs", entries[1].Path);
        Assert.Equal("old.cs", entries[3].OriginalPath);
        Assert.Equal("new.cs", entries[3].Path);
        Assert.Empty(GitStatusParser.Parse(""));
    }

    [Fact]
    public void Unified_diff_marks_changes_with_context()
    {
        Assert.Equal("", TextDiff.Unified("a\nb\n", "a\r\nb\r\n", "x", "y"));
        var diff = TextDiff.Unified("1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n", "1\n2\n3\n4\nfive\n6\n7\n8\n9\n10\n", "a/f.cs", "b/f.cs");
        Assert.StartsWith("--- a/f.cs\n+++ b/f.cs\n@@ -2,7 +2,7 @@\n", diff);
        Assert.Contains("\n-5\n+five\n", diff);
        Assert.DoesNotContain("\n 1\n", diff);
        var added = TextDiff.Unified("", "new\n", "/dev/null", "b/n.cs");
        Assert.Contains("@@ -0,0 +1,1 @@", added);
        Assert.Contains("+new", added);
    }

    [Fact]
    public void Real_git_repository_reports_markers_and_head_content()
    {
        if (!GitAvailable()) return;   // no git here: the feature is off, nothing to test
        var dir = TempDir();
        Git(dir, "init", "-q");
        Git(dir, "config", "user.email", "t@example.com");
        Git(dir, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(dir, "a.cs"), "one\n");
        File.WriteAllText(Path.Combine(dir, "b.cs"), "bee\n");
        Git(dir, "add", ".");
        Git(dir, "commit", "-q", "-m", "init");
        File.WriteAllText(Path.Combine(dir, "a.cs"), "two\n");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "c.cs"), "new\n");
        File.WriteAllText(Path.Combine(dir, "d.cs"), "staged\n");
        Git(dir, "add", "d.cs");

        var repo = GitRepo.Find(Path.Combine(dir, "sub"))!;
        var status = repo.Status();
        Assert.Equal(GitState.Modified, status[Path.Combine(dir, "a.cs")]);
        Assert.Equal(GitState.Untracked, status[Path.Combine(dir, "sub", "c.cs")]);
        Assert.Equal(GitState.Added, status[Path.Combine(dir, "d.cs")]);
        Assert.False(status.ContainsKey(Path.Combine(dir, "b.cs")));
        Assert.Equal("one\n", repo.ShowHead(Path.Combine(dir, "a.cs")));
        Assert.Null(repo.ShowHead(Path.Combine(dir, "d.cs")));
        Assert.True(GitRepo.Aggregate(status, dir).ContainsKey(Path.Combine(dir, "sub")));
    }

    internal static bool GitAvailable()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            p!.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    internal static void Git(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    // ---- Solutions ----

    [Fact]
    public void Solutions_list_their_csharp_projects()
    {
        var dir = TempDir();
        var app = RadProject.Create(Path.Combine(dir, "App"), "App");
        var lib = Path.Combine(dir, "Lib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var slnx = Path.Combine(dir, "All.slnx");
        File.WriteAllText(slnx, "<Solution><Project Path=\"App/App.csproj\" /><Project Path=\"Lib\\Lib.csproj\" /><Project Path=\"Missing/M.csproj\" /></Solution>");
        Assert.Equal([app.ProjectFile, Path.Combine(lib, "Lib.csproj")], SolutionFile.ProjectFiles(slnx));
        var sln = Path.Combine(dir, "All.sln");
        File.WriteAllText(sln, "Microsoft Visual Studio Solution File, Format Version 12.00\r\nProject(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"Lib\", \"Lib\\Lib.csproj\", \"{AAAAAAAA-0000-0000-0000-000000000000}\"\r\nEndProject\r\nProject(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"Folder\", \"Folder\", \"{BBBBBBBB-0000-0000-0000-000000000000}\"\r\nEndProject\r\n");
        Assert.Equal([Path.Combine(lib, "Lib.csproj")], SolutionFile.ProjectFiles(sln));
        Assert.True(SolutionFile.IsSolution("x.SLNX"));
        Assert.False(SolutionFile.IsSolution("x.csproj"));
    }
}
