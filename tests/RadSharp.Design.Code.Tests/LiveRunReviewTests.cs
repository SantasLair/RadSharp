using System.Diagnostics;
using RadSharp.Design;
using RadSharp.Design.Code;

namespace RadSharp.Tests;

/// <summary>
/// Review of M16 (F9 with the project's dependencies, unknown classes): the dependency cache, the MSBuild process, and
/// the designer's documents while a live run's classes are registered. None of these restore NuGet packages.
/// </summary>
public class LiveRunReviewTests
{
    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-review-").FullName;

    /// <summary>A package-free SDK project (restores offline).</summary>
    static RadProject WriteProject(string dir, string name, string body = "")
    {
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name + ".csproj");
        File.WriteAllText(file, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            {body}
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Class1.cs"), $"namespace {name}; public class Class1 {{ }}\n");
        return new RadProject(file);
    }

    static ProjectDependencies Resolve(RadProject project, out bool fromCache)
    {
        var deps = ProjectDependencies.Resolve(project, TimeSpan.FromMinutes(2), out var error, out fromCache);
        Assert.True(deps != null, error);
        return deps!;
    }

    [Fact]
    public void A_Directory_Build_props_added_above_the_project_folder_invalidates_the_cache()
    {
        var root = TempDir();
        var project = WriteProject(Path.Combine(root, "App"), "App");
        var first = Resolve(project, out _);
        Assert.DoesNotContain("FROM_PARENT", first.DefineConstants);
        Resolve(project, out var cached);
        Assert.True(cached);

        // The usual place: the repository (solution) root, one level up.
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), """
            <Project>
              <PropertyGroup>
                <DefineConstants>$(DefineConstants);FROM_PARENT</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        var second = Resolve(project, out var fromCache);
        Assert.False(fromCache);
        Assert.Contains("FROM_PARENT", second.DefineConstants);
    }

    [Fact]
    public void A_project_in_a_folder_with_spaces_quotes_and_non_ASCII_characters_resolves()
    {
        var root = Path.Combine(TempDir(), "Prüfung 日本 'q' ;x");
        WriteProject(Path.Combine(root, "Lib"), "Lib");
        var project = WriteProject(Path.Combine(root, "App"), "App", """
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            """);
        var deps = Resolve(project, out _);
        Assert.Contains(deps.References, r => r.Equals(Path.Combine(root, "Lib", "bin", "Debug", "net10.0", "Lib.dll"), StringComparison.OrdinalIgnoreCase));
        Assert.All(deps.References, r => Assert.True(File.Exists(r), r));
        Resolve(project, out var cached);
        Assert.True(cached);
    }

    [Fact]
    public void A_file_the_build_ignores_in_a_project_reference_does_not_make_every_F9_run_MSBuild()
    {
        var root = TempDir();
        WriteProject(Path.Combine(root, "Lib"), "Lib");
        var project = WriteProject(Path.Combine(root, "App"), "App", """
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            """);
        Resolve(project, out _);
        Resolve(project, out var cached);
        Assert.True(cached);

        // An editor's settings in the reference's folder: newer than its output, but not something its build reads.
        var vscode = Directory.CreateDirectory(Path.Combine(root, "Lib", ".vscode")).FullName;
        File.WriteAllText(Path.Combine(vscode, "settings.json"), "{ }");
        Resolve(project, out _);   // may resolve once more (the build decides nothing changed)...
        Resolve(project, out var afterwards);
        Assert.True(afterwards, "every F9 runs MSBuild again");   // ...but not on every F9 from then on
    }

    [Fact]
    public void A_resolve_that_times_out_leaves_no_process_of_its_own_running()
    {
        var root = TempDir();
        var pidFile = Path.Combine(root, "App", "sleeper.pid");
        // A build step that outlives the timeout (a slow restore, a hung tool): a child process of MSBuild.
        var command = OperatingSystem.IsWindows()
            ? "powershell -NoProfile -NonInteractive -Command &quot;Set-Content -Path 'sleeper.pid' -Value $PID; Start-Sleep -Seconds 120&quot;"
            : "sh -c 'echo $$ &gt; sleeper.pid; exec sleep 120'";
        var project = WriteProject(Path.Combine(root, "App"), "App", $"""
              <Target Name="Sleeper" BeforeTargets="ResolveReferences">
                <Exec Command="{command}" WorkingDirectory="$(MSBuildProjectDirectory)" />
              </Target>
            """);
        var started = DateTime.Now;
        var deps = ProjectDependencies.Resolve(project, TimeSpan.FromSeconds(20), out var error, out _);
        Assert.Null(deps);
        Assert.Contains("longer than", error);

        Assert.True(File.Exists(pidFile), "the sleeper never started: " + error);
        int pid = int.Parse(File.ReadAllText(pidFile).Trim());
        Process? sleeper = null;
        try
        {
            try { sleeper = Process.GetProcessById(pid); } catch (ArgumentException) { }   // already gone: fine
            if (sleeper != null && sleeper.StartTime < started.AddSeconds(-1)) sleeper = null;   // a reused PID, not ours
            bool exited = sleeper == null || sleeper.WaitForExit(10_000);
            Assert.True(exited, $"MSBuild's child process {pid} is still running after the timeout");
        }
        finally
        {
            // Only the process this test started (through MSBuild), by its PID.
            if (sleeper is { HasExited: false }) sleeper.Kill();
            sleeper?.Dispose();
        }
    }

    [Fact]
    public void F9_compiles_the_sources_the_build_compiles()
    {
        var root = TempDir();
        // Linked in from outside the folder, and left out of it: both as the build sees them.
        Directory.CreateDirectory(Path.Combine(root, "Shared"));
        File.WriteAllText(Path.Combine(root, "Shared", "Shared.cs"), "namespace App; public static class Shared { public const int Answer = 42; }\n");
        var project = WriteProject(Path.Combine(root, "App"), "App", """
              <ItemGroup>
                <Compile Remove="Drafts\**" />
                <Compile Include="..\Shared\Shared.cs" />
              </ItemGroup>
            """);
        Directory.CreateDirectory(Path.Combine(project.Directory, "Drafts"));
        File.WriteAllText(Path.Combine(project.Directory, "Drafts", "Draft.cs"), "class Draft { int x = ; }\n");
        File.WriteAllText(Path.Combine(project.Directory, "UsesShared.cs"), "namespace App; static class UsesShared { const int A = Shared.Answer; }\n");

        using (var session = LiveSession.Compile(project))
        {
            Assert.NotNull(session.Dependencies);
            Assert.True(session.Success, string.Join("\n", session.Messages));
        }

        // A file added after the resolution (a new form's code, say) is compiled without running MSBuild again.
        File.WriteAllText(Path.Combine(project.Directory, "Added.cs"), "namespace App; static class Added { public const int B = 1; }\n");
        File.WriteAllText(Path.Combine(project.Directory, "UsesAdded.cs"), "namespace App; static class UsesAdded { const int A = Added.B; }\n");
        using var again = LiveSession.Compile(project);
        Assert.True(again.DependenciesFromCache);
        Assert.True(again.Success, string.Join("\n", again.Messages));
    }

    [Fact]
    public void A_reference_deleted_after_it_was_resolved_falls_back_instead_of_throwing()
    {
        var root = TempDir();
        var vendor = Directory.CreateDirectory(Path.Combine(root, "vendor")).FullName;
        var dll = Path.Combine(vendor, "Vendor.dll");
        // A library of its own (not Roslyn: this test assembly would then reference it, and so share it).
        var builder = new System.Reflection.Emit.PersistedAssemblyBuilder(new System.Reflection.AssemblyName("Vendor"), typeof(object).Assembly);
        builder.DefineDynamicModule("Vendor").DefineType("Vendor.V", System.Reflection.TypeAttributes.Public).CreateType();
        builder.Save(dll);
        var project = WriteProject(Path.Combine(root, "App"), "App", """
              <ItemGroup>
                <Reference Include="..\vendor\Vendor.dll" />
              </ItemGroup>
            """);
        using (var first = LiveSession.Compile(project))
        {
            Assert.True(first.Success, string.Join("\n", first.Messages));
            Assert.True(first.Dependencies!.References.Any(r => r.Equals(dll, StringComparison.OrdinalIgnoreCase)),
                dll + " not in:\n" + string.Join("\n", first.Dependencies.References.Where(r => !r.Contains(@"\packs\"))));
        }

        // Gone while the designer stays open (a cleaned package cache, a moved vendor folder).
        File.Delete(dll);
        using var second = LiveSession.Compile(project);
        Assert.True(second.Success, string.Join("\n", second.Messages));
        Assert.False(second.DependenciesFromCache);
    }

    /// <summary>
    /// The designer's own Roslyn (RadSharp.Design.Code's) isn't one of the assemblies a live run shares: an app that
    /// uses Roslyn itself compiles and runs with its own version. Restores Microsoft.CodeAnalysis.CSharp 4.14.0 (which
    /// building RadSharp.Generators caches).
    /// </summary>
    [Fact]
    [Trait("Category", "NuGet")]
    public void An_app_that_uses_Roslyn_gets_its_own_version_not_the_designers()
    {
        var project = WriteProject(TempDir(), "Scripting", """
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.14.0" />
              </ItemGroup>
            """);
        File.WriteAllText(Path.Combine(project.Directory, "Probe.cs"), """
            namespace Scripting;
            public static class Probe
            {
                public static string RoslynVersion() => typeof(Microsoft.CodeAnalysis.SyntaxTree).Assembly.GetName().Version!.ToString();
            }
            """);
        using var session = LiveSession.Compile(project);
        Assert.NotNull(session.Dependencies);
        Assert.True(session.Success, string.Join("\n", session.Messages));
        Assert.Contains(session.Dependencies!.References, r => r.Contains(Path.Combine("microsoft.codeanalysis.common", "4.14.0"), StringComparison.OrdinalIgnoreCase));
        var version = (string)session.Assembly!.GetType("Scripting.Probe")!.GetMethod("RoslynVersion")!.Invoke(null, null)!;
        Assert.Equal("4.14.0.0", version);
    }

    [Fact]
    public void Undo_while_a_live_run_has_the_project_classes_registered_still_saves_them_after_it_ends()
    {
        var project = RadProject.Create(TempDir(), "Gauges");
        File.WriteAllText(Path.Combine(project.Directory, "Gauge.cs"), """
            using RadSharp;

            namespace Gauges;

            public class Gauge : Label
            {
                [Published(Category = "Behavior")]
                public int Value { get; set; }
            }
            """);
        var formFile = project.FormFiles.Single();

        // The form as the designer has it: a Gauge (unknown to the designer: a placeholder) and a button.
        var doc = new DesignDocument();
        doc.Load(File.ReadAllText(formFile));
        doc.Paste("""
            [
              { "class": "Gauge", "name": "ggSpeed", "left": 8, "top": 8, "value": 7 }
            ]
            """, doc.Form, offset: 0);
        File.WriteAllText(formFile, doc.Save());
        doc.Load(File.ReadAllText(formFile));
        Assert.IsType<UnknownControl>(doc.Form.FindControl("ggSpeed"));
        var saved = doc.Save();

        // F9: the live assembly registers Gauge while its form runs (modeless; the designer stays usable).
        using (var session = LiveSession.Compile(project, options: new LiveRunOptions { UseProjectDependencies = false }))
        {
            Assert.True(session.Success, string.Join("\n", session.Messages));
            session.CreateForm("MainForm");
            Assert.NotNull(ComponentRegistry.Find("Gauge"));

            // Meanwhile, in the designer: an edit, then undo (which reloads the form from its JSON snapshot).
            doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 100, 100);
            Assert.True(doc.Undo());
        }
        // The live window closed: its classes are gone. Saving must still write the Gauge as it was.
        Assert.Null(ComponentRegistry.Find("Gauge"));
        Assert.Equal(saved, doc.Save());
        Assert.IsType<UnknownControl>(doc.Form.FindControl("ggSpeed"));
    }
}
