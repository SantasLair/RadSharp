using System.Diagnostics;
using System.Runtime.InteropServices;
using RadSharp.Design.Code;
using Xunit.Abstractions;

namespace RadSharp.Tests;

/// <summary>
/// F9 with the project's real dependencies (M16/1.0): NuGet packages with native assets, the targeting pack's
/// System.Text.Json generator, project references and compile settings, resolved by MSBuild and cached.
/// </summary>
public class LiveDependencyTests(ITestOutputHelper output)
{
    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-live-").FullName;

    /// <summary>
    /// A fresh copy of tests/Fixtures/TallyLive whose RadSharp references point at this test's output folder (the
    /// same assemblies the "designer" — this process — runs on).
    /// </summary>
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

    /// <summary>
    /// Restores Microsoft.Data.Sqlite (from nuget.org the first time, the local package cache after that). Filter it out
    /// with <c>dotnet test --filter "Category!=NuGet"</c>.
    /// </summary>
    [Fact]
    [Trait("Category", "NuGet")]
    public void F9_compiles_against_packages_generators_and_project_references_and_runs_the_handler()
    {
        var root = CopyFixture();
        var project = new RadProject(Path.Combine(root, "TallyLive", "TallyLive.csproj"));

        var watch = Stopwatch.StartNew();
        using (var cold = LiveSession.Compile(project))
        {
            var coldTime = watch.Elapsed;
            Assert.True(cold.Success, string.Join("\n", cold.Messages));
            Assert.Empty(cold.Messages);
            Assert.False(cold.DependenciesFromCache);
            var deps = cold.Dependencies!;
            Assert.Contains(deps.References, r => r.EndsWith("Microsoft.Data.Sqlite.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(deps.Analyzers, a => a.EndsWith("System.Text.Json.SourceGeneration.dll", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(deps.Analyzers, a => a.EndsWith("RadSharp.Generators.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("TALLY_LIVE", deps.DefineConstants);
            Assert.Equal("Tally", deps.RootNamespace);
            // RadSharp is compiled against, and shared with, this process's copy: one Form type on both sides.
            Assert.Contains(typeof(Form).Assembly.Location, deps.References);
            // Native SQLite for this platform, from its runtimes/<rid>/native folder.
            Assert.Contains(deps.NativeLibraries, n => n.Contains(Path.Combine("runtimes", RuntimeInformation.RuntimeIdentifier, "native")));

            // Run the handler: in-memory SQLite, the project reference's code, and the generated JsonSerializerContext.
            var form = cold.CreateForm("MainForm");
            Assert.IsAssignableFrom<Form>(form);
            form.Simulate.Click((Button)form.FindControl("btnTally")!);
            Assert.Equal("""{"Label":"Total: 6","Total":6}""", ((Label)form.FindControl("lblResult")!).Caption);
            var counter = (Label)form.FindControl("cntTotal")!;
            Assert.Equal("TallyCounter", counter.GetType().Name);
            Assert.Equal("6 item(s)", counter.Caption);
            Assert.NotSame(typeof(Form).Assembly, form.GetType().Assembly);

            // The control class the project defines, for the palette.
            var classes = cold.GetComponentClasses();
            var tally = Assert.Single(classes);
            Assert.Equal("TallyCounter", tally.Name);
            Assert.Contains(tally.Properties, p => p.Name == "count");
            Assert.Same(tally, ComponentRegistry.Find("TallyCounter"));
            Assert.IsType<Label>(tally.Create(), exactMatch: false);
            // With the libraries it references (here the project reference, which defines a control too).
            Assert.Equal(["MoneyLabel", "TallyCounter"], cold.GetComponentClasses(includeLibraries: true).Select(c => c.Name));
            Assert.Equal("EUR", ComponentRegistry.Find("MoneyLabel")!.FindProperty("currency")!.FormatValue(ComponentRegistry.Find("MoneyLabel")!.Create()));
            output.WriteLine($"cold F9: {coldTime.TotalMilliseconds:0} ms (resolve {cold.ResolveTime.TotalMilliseconds:0} ms, MSBuild {deps.ResolveTime.TotalMilliseconds:0} ms, compile {cold.CompileTime.TotalMilliseconds:0} ms)");
        }
        Assert.Null(ComponentRegistry.Find("TallyCounter"));   // unregistered with the session, the library's classes too
        Assert.Null(ComponentRegistry.Find("MoneyLabel"));

        // Again: the resolution comes from the cache.
        watch.Restart();
        using (var warm = LiveSession.Compile(project))
        {
            var warmTime = watch.Elapsed;
            Assert.True(warm.Success, string.Join("\n", warm.Messages));
            Assert.True(warm.DependenciesFromCache);
            output.WriteLine($"warm F9: {warmTime.TotalMilliseconds:0} ms (resolve {warm.ResolveTime.TotalMilliseconds:0} ms, compile {warm.CompileTime.TotalMilliseconds:0} ms)");
        }

        // A change in the project reference's sources: resolved (and so rebuilt) again.
        var money = Path.Combine(root, "TallyLive.Lib", "Money.cs");
        File.WriteAllText(money, File.ReadAllText(money).Replace("Total: ", "Sum: "));
        File.SetLastWriteTimeUtc(money, DateTime.UtcNow.AddSeconds(5));
        watch.Restart();
        using (var rebuilt = LiveSession.Compile(project))
        {
            Assert.True(rebuilt.Success, string.Join("\n", rebuilt.Messages));
            Assert.False(rebuilt.DependenciesFromCache);
            var form = rebuilt.CreateForm("MainForm");
            form.Simulate.Click((Button)form.FindControl("btnTally")!);
            Assert.Equal("""{"Label":"Sum: 6","Total":6}""", ((Label)form.FindControl("lblResult")!).Caption);
            output.WriteLine($"F9 after a project-reference change: {watch.Elapsed.TotalMilliseconds:0} ms");
        }
    }

    [Fact]
    public void When_MSBuild_cannot_resolve_the_project_F9_falls_back_with_a_warning()
    {
        var project = RadProject.Create(TempDir(), "Unresolvable");
        File.AppendAllText(project.ProjectFile, "<NotXml");   // MSBuild rejects it at once (no restore, no network)
        using var session = LiveSession.Compile(project);
        Assert.True(session.Success, string.Join("\n", session.Messages));   // compiled against the designer's assemblies
        Assert.Null(session.Dependencies);
        var notice = Assert.Single(session.Messages);
        Assert.Equal(LiveSession.FallbackId, notice.Id);
        Assert.Equal("warning", notice.Severity);
        Assert.False(notice.IsError);
        Assert.Contains("MSB", notice.Message);
        Assert.StartsWith("warning RAD9001: Live run without the project's packages", notice.ToString());
        Assert.NotNull(session.CreateForm("MainForm"));
    }

    [Fact]
    public void The_designer_only_mode_compiles_without_MSBuild()
    {
        var project = RadProject.Create(TempDir(), "DesignerOnly");
        var watch = Stopwatch.StartNew();
        using var first = LiveSession.Compile(project, options: new LiveRunOptions { UseProjectDependencies = false });
        var firstTime = watch.Elapsed;
        Assert.True(first.Success, string.Join("\n", first.Messages));
        Assert.Empty(first.Messages);
        watch.Restart();
        using var second = LiveSession.Compile(project, options: new LiveRunOptions { UseProjectDependencies = false });
        Assert.True(second.Success);
        output.WriteLine($"designer-only F9: first {firstTime.TotalMilliseconds:0} ms, second {watch.Elapsed.TotalMilliseconds:0} ms");
    }
}
