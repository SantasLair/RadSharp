using System.Text;
using RadSharp.Design.Code;

namespace RadSharp.Design.Code.Tests;

/// <summary>Regression tests for the review fixes in RadSharp.Design.Code: encodings, file operations, git, NuGet and build arguments.</summary>
public class ReviewFixTests
{
    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-fix-").FullName;

    // ---- TextFileContent ----

    public static IEnumerable<object[]> Encodings() =>
    [
        [new byte[] { 0xEF, 0xBB, 0xBF }, "utf-8 bom"],
        [new byte[] { 0xFF, 0xFE }, "utf-16 le"],
        [new byte[] { 0xFE, 0xFF }, "utf-16 be"],
        [new byte[] { 0xFF, 0xFE, 0, 0 }, "utf-32 le"],
        [new byte[] { 0, 0, 0xFE, 0xFF }, "utf-32 be"],
        [Array.Empty<byte>(), "utf-8"],
    ];

    static Encoding EncodingOf(string label) => label switch
    {
        "utf-8 bom" or "utf-8" => new UTF8Encoding(false),
        "utf-16 le" => new UnicodeEncoding(false, false),
        "utf-16 be" => new UnicodeEncoding(true, false),
        "utf-32 le" => new UTF32Encoding(false, false),
        _ => new UTF32Encoding(true, false),
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    public void A_file_is_written_back_in_the_encoding_and_BOM_it_was_read_with(byte[] bom, string label)
    {
        var path = Path.Combine(TempDir(), "a.cs");
        var original = "class Ünï { }\n";
        File.WriteAllBytes(path, [.. bom, .. EncodingOf(label).GetBytes(original)]);

        var content = TextFileContent.Read(path);
        Assert.Equal(original, content.Text);
        Assert.False(content.IsLossy);
        Assert.Equal(bom.Length > 0, content.HasBom);

        content.Write(path, "class Changed { }\n");
        Assert.Equal([.. bom, .. EncodingOf(label).GetBytes("class Changed { }\n")], File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));   // no temp file left behind
    }

    [Fact]
    public void A_file_that_is_neither_valid_UTF8_nor_has_a_BOM_is_lossy()
    {
        var content = TextFileContent.Decode([0x63, 0x61, 0x66, 0xE9]);   // "café" in Latin-1
        Assert.True(content.IsLossy);
        Assert.Equal("café", content.Text);   // good enough to search
    }

    // ---- ProjectFileOps ----

    [Fact]
    public void Rewriting_sources_keeps_their_BOM_and_skips_files_that_are_not_UTF8()
    {
        var project = RadProject.Create(TempDir(), "Enc");
        var ops = new ProjectFileOps(project);
        var bom = Path.Combine(project.Directory, "Widget.cs");
        File.WriteAllBytes(bom, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("namespace Enc;\npublic class Widget { }\n")]);
        var latin = Path.Combine(project.Directory, "Latin.cs");
        byte[] latinBytes = [.. Encoding.ASCII.GetBytes("// caf"), 0xE9, .. Encoding.ASCII.GetBytes("\nnamespace Enc;\npublic class Other { Widget w; }\n")];
        File.WriteAllBytes(latin, latinBytes);
        var user = Path.Combine(project.Directory, "User.cs");
        File.WriteAllBytes(user, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("namespace Enc;\npublic class User { Widget w; }\n")]);

        ops.Rename(bom, "Gadget");

        var bytes = File.ReadAllBytes(Path.Combine(project.Directory, "Gadget.cs"));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Contains("class Gadget", Encoding.UTF8.GetString(bytes));
        var userBytes = File.ReadAllBytes(user);
        Assert.Equal([0xEF, 0xBB, 0xBF], userBytes[..3]);   // rewritten, BOM kept
        Assert.Contains("Gadget w", Encoding.UTF8.GetString(userBytes));
        Assert.Equal(latinBytes, File.ReadAllBytes(latin));   // not UTF-8: left byte for byte
    }

    [Fact]
    public void Rename_move_and_delete_refuse_the_project_folder_and_paths_outside_it()
    {
        var project = RadProject.Create(TempDir(), "Guard");
        var ops = new ProjectFileOps(project);
        var outside = Path.Combine(TempDir(), "Outside.cs");
        File.WriteAllText(outside, "class Outside { }\n");
        var sub = ops.CreateFolder(project.Directory, "Sub");

        Assert.Throws<IOException>(() => ops.Rename(project.Directory, "Elsewhere"));
        Assert.Throws<IOException>(() => ops.Delete(project.Directory));
        Assert.Throws<IOException>(() => ops.Move(project.Directory, sub, false));
        Assert.Throws<IOException>(() => ops.Rename(outside, "Renamed"));
        Assert.Throws<IOException>(() => ops.Delete(outside));
        Assert.Throws<IOException>(() => ops.Move(outside, sub, false));
        Assert.Throws<IOException>(() => ops.Move(sub, Path.GetDirectoryName(outside)!, false));   // target outside
        Assert.True(Directory.Exists(project.Directory));
        Assert.True(File.Exists(outside));
        // A sibling whose name merely starts with the project folder's name is outside, too.
        var sibling = project.Directory + "2";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "X.cs"), "class X { }\n");
        Assert.Throws<IOException>(() => ops.Delete(Path.Combine(sibling, "X.cs")));
    }

    [Theory]
    [InlineData("class")]
    [InlineData("int")]
    [InlineData("namespace")]
    public void C_sharp_keywords_are_not_type_names(string keyword)
    {
        var project = RadProject.Create(TempDir(), "Kw");
        var ops = new ProjectFileOps(project);
        var e = Assert.Throws<IOException>(() => ops.NewFile(project.Directory, keyword, FileTemplate.Class));
        Assert.Contains("keyword", e.Message);
        Assert.False(ProjectFileOps.IsValidIdentifier(keyword));
        Assert.True(ProjectFileOps.IsValidIdentifier("Class"));
    }

    [Fact]
    public void A_case_only_rename_works_for_files_forms_and_folders()
    {
        var project = RadProject.Create(TempDir(), "Case");
        var ops = new ProjectFileOps(project);
        var file = ops.NewFile(project.Directory, "widget", FileTemplate.Class);
        var changes = ops.Rename(file, "Widget");
        Assert.Contains(Directory.GetFiles(project.Directory), f => Path.GetFileName(f) == "Widget.cs");
        Assert.DoesNotContain(Directory.GetFiles(project.Directory), f => Path.GetFileName(f) == "widget.cs");
        Assert.Single(changes);
        Assert.Contains("class Widget", File.ReadAllText(Path.Combine(project.Directory, "Widget.cs")));

        var form = ops.NewFile(project.Directory, "aboutForm", FileTemplate.Form);
        ops.Rename(form, "AboutForm");
        Assert.Contains(Directory.GetFiles(project.Directory), f => Path.GetFileName(f) == "AboutForm.radform.json");
        Assert.Contains(Directory.GetFiles(project.Directory), f => Path.GetFileName(f) == "AboutForm.cs");

        var folder = ops.CreateFolder(project.Directory, "models");
        ops.Rename(folder, "Models");
        Assert.Contains(Directory.GetDirectories(project.Directory), d => Path.GetFileName(d) == "Models");
    }

    [Fact]
    public void Renaming_a_form_is_undone_when_its_code_behind_cannot_move()
    {
        if (!OperatingSystem.IsWindows()) return;   // relies on a sharing violation: an open file can't be moved
        var project = RadProject.Create(TempDir(), "Tx");
        var ops = new ProjectFileOps(project);
        var form = project.FormFileFor("MainForm")!;
        var code = RadProject.CodeFileFor(form);
        using (new FileStream(code, FileMode.Open, FileAccess.Read, FileShare.Read))   // blocks moving the code-behind
        {
            Assert.Throws<IOException>(() => ops.Rename(form, "ShellForm"));
        }
        Assert.True(File.Exists(form), "the form file was put back");
        Assert.True(File.Exists(code));
        Assert.False(File.Exists(Path.Combine(project.Directory, "ShellForm.radform.json")));
        Assert.Contains("class MainForm", File.ReadAllText(code));
    }

    [Fact]
    public void Moving_a_form_is_undone_when_its_code_behind_cannot_move()
    {
        if (!OperatingSystem.IsWindows()) return;
        var project = RadProject.Create(TempDir(), "TxMove");
        var ops = new ProjectFileOps(project);
        var sub = ops.CreateFolder(project.Directory, "Views");
        var form = project.FormFileFor("MainForm")!;
        var code = RadProject.CodeFileFor(form);
        using (new FileStream(code, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => ops.Move(form, sub, false));
        }
        Assert.True(File.Exists(form));
        Assert.Empty(Directory.GetFiles(sub));
    }

    // ---- Git ----

    [Fact]
    public void Folder_markers_use_a_separator_boundary()
    {
        var root = Path.Combine(TempDir(), "App");
        var sibling = root + "2";
        var files = new Dictionary<string, GitState>
        {
            [Path.Combine(root, "Sub", "a.cs")] = GitState.Modified,
            [Path.Combine(sibling, "Other", "b.cs")] = GitState.Modified,
        };
        var folders = GitRepo.Aggregate(files, root);
        Assert.Contains(Path.Combine(root, "Sub"), folders.Keys);
        Assert.Contains(root, folders.Keys);
        Assert.DoesNotContain(sibling, folders.Keys);
        Assert.DoesNotContain(Path.Combine(sibling, "Other"), folders.Keys);
    }

    [Fact]
    public void A_process_that_runs_too_long_is_killed_and_gives_null_without_blocking()
    {
        var dir = TempDir();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = OperatingSystem.IsWindows()
            ? ProcessCapture.Run("ping", ["-n", "30", "127.0.0.1"], dir, TimeSpan.FromMilliseconds(500))
            : ProcessCapture.Run("sleep", ["30"], dir, TimeSpan.FromMilliseconds(500));
        Assert.Null(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    [Fact]
    public void A_finished_process_returns_its_output_and_a_failing_one_returns_null()
    {
        var dir = TempDir();
        Assert.NotNull(ProcessCapture.Run("dotnet", ["--list-sdks"], dir, TimeSpan.FromSeconds(30)));
        Assert.Null(ProcessCapture.Run("dotnet", ["--no-such-switch-xyz"], dir, TimeSpan.FromSeconds(30)));   // non-zero exit
        Assert.Null(ProcessCapture.Run("no-such-program-xyz", [], dir, TimeSpan.FromSeconds(5)));            // can't start
    }

    // ---- NuGet and build arguments ----

    [Theory]
    [InlineData("--source")]
    [InlineData("-v")]
    [InlineData("Bad Id")]
    [InlineData("a;b")]
    [InlineData("")]
    public void Package_ids_that_could_be_options_or_garbage_are_refused(string id)
    {
        Assert.Throws<ArgumentException>(() => NuGetService.AddArguments("p.csproj", id));
        Assert.Throws<ArgumentException>(() => NuGetService.RemoveArguments("p.csproj", id));
    }

    [Theory]
    [InlineData("--prerelease")]
    [InlineData("-1.0")]
    [InlineData("1.0 --source x")]
    [InlineData("abc")]
    public void Versions_that_could_be_options_or_garbage_are_refused(string version) =>
        Assert.Throws<ArgumentException>(() => NuGetService.AddArguments("p.csproj", "Newtonsoft.Json", version));

    [Theory]
    [InlineData("13.0.3")]
    [InlineData("1.0")]
    [InlineData("2.0.0-beta.1")]
    [InlineData("1.0.0+build.5")]
    [InlineData("[1.0,2.0)")]
    [InlineData("1.*")]
    public void Normal_versions_and_ids_pass(string version)
    {
        var args = NuGetService.AddArguments("p.csproj", "Newtonsoft.Json", version);
        Assert.Equal(["add", "p.csproj", "package", "Newtonsoft.Json", "--version", version], args);
        Assert.Equal(["add", "p.csproj", "package", "_My.Pkg-1"], NuGetService.AddArguments("p.csproj", "_My.Pkg-1"));
    }

    [Fact]
    public void Extra_build_arguments_split_like_a_command_line()
    {
        Assert.Empty(ProjectBuild.SplitArguments(null));
        Assert.Equal(["-nodeReuse:false", "-p:UseSharedCompilation=false"], ProjectBuild.SplitArguments("-nodeReuse:false -p:UseSharedCompilation=false"));
        Assert.Equal(["-p:Name=a b", "x"], ProjectBuild.SplitArguments("\"-p:Name=a b\" x"));
        Assert.Equal(["say \"hi\""], ProjectBuild.SplitArguments("\"say \\\"hi\\\"\""));
    }

    [Fact]
    public async Task A_build_in_a_folder_with_spaces_runs()
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory("radsharp fix ").FullName, "my app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spacey.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "System.Console.WriteLine(1);");
        var project = new RadProject(Path.Combine(dir, "Spacey.csproj"));
        var lines = new List<string>();
        var result = await ProjectBuild.BuildAsync(project, l => { lock (lines) lines.Add(l); }, "Debug", "-nodeReuse:false -p:UseSharedCompilation=false");
        Assert.True(result.Succeeded, string.Join("\n", lines));
    }
}
