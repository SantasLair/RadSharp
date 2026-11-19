using System.Runtime.InteropServices;
using RadSharp.Design;
using RadSharp.Design.Code;

namespace RadSharp.Tests;

/// <summary>
/// The designer's design session (the project's controls on the palette) and what it needs from dependency
/// resolution: cached failures, a resolve that runs on, and a class library's package run-time assets.
/// </summary>
public class DesignSessionTests
{
    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-session-").FullName;

    /// <summary>A package-free SDK project (restores offline).</summary>
    static RadProject WriteProject(string dir, string name, string body = "", string code = "")
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
        File.WriteAllText(Path.Combine(dir, "Class1.cs"), code.Length > 0 ? code : $"namespace {name}; public class Class1 {{ }}\n");
        return new RadProject(file);
    }

    /// <summary>Counts MSBuild runs for one project file while <paramref name="body"/> runs.</summary>
    static void CountingMSBuild(RadProject project, Action<Func<int>> body)
    {
        int runs = 0;
        void Started(string file) { if (string.Equals(file, project.ProjectFile, StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref runs); }
        ProjectDependencies.MSBuildStarting += Started;
        try { body(() => Volatile.Read(ref runs)); }
        finally { ProjectDependencies.MSBuildStarting -= Started; }
    }

    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    [Fact]
    public void A_failed_resolve_is_remembered_until_what_it_read_changes_or_it_is_invalidated()
    {
        var root = TempDir();
        // A project reference that doesn't compile: MSBuild builds it while it resolves, and fails.
        var libCode = Path.Combine(root, "Lib", "Class1.cs");
        WriteProject(Path.Combine(root, "Lib"), "Lib", code: "namespace Lib; public class Class1 { int x = ; }\n");
        var project = WriteProject(Path.Combine(root, "App"), "App", """
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            """);
        CountingMSBuild(project, runs =>
        {
            var first = ProjectDependencies.Resolve(project, Timeout);
            Assert.Null(first.Dependencies);
            Assert.False(first.FromCache);
            Assert.Contains("CS1525", first.Error);
            Assert.Equal(1, runs());

            var second = ProjectDependencies.Resolve(project, Timeout);
            Assert.Null(second.Dependencies);
            Assert.True(second.FromCache);
            Assert.Equal(first.Error, second.Error);
            Assert.Equal(1, runs());   // no second restore for the same inputs

            // The reference's code fixed: resolved again, and it works.
            File.WriteAllText(libCode, "namespace Lib; public class Class1 { }\n");
            var fixedOne = ProjectDependencies.Resolve(project, Timeout);
            Assert.True(fixedOne.Dependencies != null, fixedOne.Error);
            Assert.Equal(2, runs());
            Assert.True(ProjectDependencies.Resolve(project, Timeout).FromCache);

            // A broken project file fails at once; it is remembered too, until Invalidate (Project › Refresh).
            File.AppendAllText(project.ProjectFile, "<NotXml");
            Assert.Null(ProjectDependencies.Resolve(project, Timeout).Dependencies);
            Assert.Equal(3, runs());
            Assert.True(ProjectDependencies.Resolve(project, Timeout).FromCache);
            Assert.Equal(3, runs());
            ProjectDependencies.Invalidate(project);
            Assert.False(ProjectDependencies.Resolve(project, Timeout).FromCache);
            Assert.Equal(4, runs());
        });
    }

    [Fact]
    public void A_failed_resolve_is_forgotten_when_a_reference_of_a_reference_is_fixed()
    {
        var root = TempDir();
        // App -> Mid -> Leaf, and Leaf doesn't compile: MSBuild builds both references while it resolves App, and fails.
        var leafCode = Path.Combine(root, "Leaf", "Class1.cs");
        WriteProject(Path.Combine(root, "Leaf"), "Leaf", code: "namespace Leaf; public class Class1 { int x = ; }\n");
        WriteProject(Path.Combine(root, "Mid"), "Mid", """
              <ItemGroup>
                <ProjectReference Include="..\Leaf\Leaf.csproj" />
              </ItemGroup>
            """);
        var project = WriteProject(Path.Combine(root, "App"), "App", """
              <ItemGroup>
                <ProjectReference Include="..\Mid\Mid.csproj" />
              </ItemGroup>
            """);
        CountingMSBuild(project, runs =>
        {
            var first = ProjectDependencies.Resolve(project, Timeout);
            Assert.Null(first.Dependencies);
            Assert.Contains("CS1525", first.Error);
            Assert.True(ProjectDependencies.Resolve(project, Timeout).FromCache);
            Assert.Equal(1, runs());

            // Leaf's code fixed: what the failed resolve read has changed, so it is resolved again.
            File.WriteAllText(leafCode, "namespace Leaf; public class Class1 { }\n");
            File.SetLastWriteTimeUtc(leafCode, DateTime.UtcNow.AddSeconds(5));
            var fixedOne = ProjectDependencies.Resolve(project, Timeout);
            Assert.True(fixedOne.Dependencies != null, fixedOne.Error);
            Assert.Equal(2, runs());
        });
    }

    [Theory]
    [InlineData("""<ProjectReference Include='..\Lib\Lib.csproj' />""")]
    [InlineData("""<ProjectReference Condition="'$(Configuration)' != ''" Include="..\Lib\Lib.csproj" />""")]
    public void A_failed_resolve_is_forgotten_when_a_reference_written_another_way_is_fixed(string reference)
    {
        var root = TempDir();
        var libCode = Path.Combine(root, "Lib", "Class1.cs");
        WriteProject(Path.Combine(root, "Lib"), "Lib", code: "namespace Lib; public class Class1 { int x = ; }\n");
        var project = WriteProject(Path.Combine(root, "App"), "App", $"""
              <ItemGroup>
                {reference}
              </ItemGroup>
            """);
        CountingMSBuild(project, runs =>
        {
            Assert.Contains("CS1525", ProjectDependencies.Resolve(project, Timeout).Error);
            Assert.True(ProjectDependencies.Resolve(project, Timeout).FromCache);
            File.WriteAllText(libCode, "namespace Lib; public class Class1 { }\n");
            File.SetLastWriteTimeUtc(libCode, DateTime.UtcNow.AddSeconds(5));
            var fixedOne = ProjectDependencies.Resolve(project, Timeout);
            Assert.True(fixedOne.Dependencies != null, fixedOne.Error);
            Assert.Equal(2, runs());
        });
    }

    [Fact]
    public async Task Cancelling_ResolveAsync_stops_the_wait_not_the_resolve_and_the_next_caller_takes_its_result()
    {
        var project = WriteProject(TempDir(), "Waits");
        int runs = 0;
        void Started(string file) { if (string.Equals(file, project.ProjectFile, StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref runs); }
        ProjectDependencies.MSBuildStarting += Started;
        try
        {
            using var cancel = new CancellationTokenSource();
            var task = ProjectDependencies.ResolveAsync(project, Timeout, cancel: cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            // MSBuild ran on; this caller waits for it rather than starting another.
            var next = await ProjectDependencies.ResolveAsync(project, Timeout);
            Assert.True(next.Dependencies != null, next.Error);
            Assert.Equal(1, Volatile.Read(ref runs));
        }
        finally { ProjectDependencies.MSBuildStarting -= Started; }
    }

    /// <summary>
    /// A class library's build copies no package assets locally (ReferenceCopyLocalPaths is empty), yet its controls
    /// use them at design time. Restores Microsoft.Data.Sqlite (from the local package cache when offline).
    /// </summary>
    [Fact]
    [Trait("Category", "NuGet")]
    public void A_class_librarys_control_runs_with_its_package_assets_at_design_time()
    {
        var root = TempDir();
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), $"""
            <Project>
              <PropertyGroup>
                <RadSharpBin>{AppContext.BaseDirectory}</RadSharpBin>
              </PropertyGroup>
            </Project>
            """);
        var project = WriteProject(Path.Combine(root, "SqlControls"), "SqlControls", """
              <PropertyGroup>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.13" />
                <Reference Include="$(RadSharpBin)RadSharp.Core.dll" />
                <Reference Include="$(RadSharpBin)SkiaSharp.dll" />
                <Reference Include="$(RadSharpBin)HarfBuzzSharp.dll" />
                <Analyzer Include="$(RadSharpBin)RadSharp.Generators.dll" />
              </ItemGroup>
            """, """
            using Microsoft.Data.Sqlite;
            using RadSharp;

            namespace SqlControls;

            /// <summary>Shows the SQLite version: managed package code and the native e_sqlite3, at design time.</summary>
            public class SqliteVersionLabel : Label
            {
                public SqliteVersionLabel()
                {
                    using var db = new SqliteConnection("Data Source=:memory:");
                    db.Open();
                    using var command = db.CreateCommand();
                    command.CommandText = "SELECT sqlite_version()";
                    Caption = "SQLite " + command.ExecuteScalar();
                }
            }
            """);

        using var session = DesignSession.Compile(project);
        Assert.True(session.Success, string.Join("\n", session.Messages));
        var deps = session.Dependencies!;
        Assert.Contains("Microsoft.Data.Sqlite", deps.RuntimeAssemblies.Keys);
        Assert.Contains(deps.NativeLibraries, n => n.Contains(Path.Combine("runtimes", RuntimeInformation.RuntimeIdentifier, "native")));

        var problems = session.Activate();
        Assert.Empty(problems);
        var cls = Assert.Single(session.Classes);
        Assert.Equal("SqliteVersionLabel", cls.Name);
        Assert.Equal(PaletteCategories.Project, session.CategoryOf(cls));
        Assert.StartsWith("SQLite 3.", ((Label)cls.Prototype).Caption);
        Assert.True(cls.Prototype.DesignMode);

        session.Dispose();
        Assert.Null(ComponentRegistry.Find("SqliteVersionLabel"));
    }

    [Fact]
    public void The_projects_design_time_setup_runs_before_its_controls_are_created_and_the_designers_theme_is_kept()
    {
        var root = TempDir();
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), $"""
            <Project>
              <PropertyGroup>
                <RadSharpBin>{AppContext.BaseDirectory}</RadSharpBin>
              </PropertyGroup>
            </Project>
            """);
        var project = WriteProject(Path.Combine(root, "SetupControls"), "SetupControls", """
              <PropertyGroup>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="$(RadSharpBin)RadSharp.Core.dll" />
                <Reference Include="$(RadSharpBin)SkiaSharp.dll" />
                <Reference Include="$(RadSharpBin)HarfBuzzSharp.dll" />
                <Analyzer Include="$(RadSharpBin)RadSharp.Generators.dll" />
              </ItemGroup>
            """, """
            using RadSharp;
            using RadSharp.Drawing;

            [assembly: DesignTimeSetup(typeof(SetupControls.Palette), nameof(SetupControls.Palette.Init))]

            namespace SetupControls;

            /// <summary>What the application's Main sets up before any form opens.</summary>
            public static class Palette
            {
                public static string? Accent;

                public static void Init(bool dark = true)
                {
                    Accent = "teal";
                    Theme.Current = dark ? Theme.Dark : Theme.Default;   // a side effect the designer undoes
                }
            }

            public class AccentLabel : Label
            {
                public AccentLabel() => Caption = Palette.Accent ?? throw new InvalidOperationException("Palette.Init didn't run");
            }
            """);

        using var session = DesignSession.Compile(project, new LiveRunOptions { UseProjectDependencies = false });
        Assert.True(session.Success, string.Join("\n", session.Messages));
        var theme = RadSharp.Drawing.Theme.Current;
        Assert.Empty(session.Activate());
        var cls = Assert.Single(session.Classes);
        Assert.Equal("teal", ((Label)cls.Prototype).Caption);
        Assert.Same(theme, RadSharp.Drawing.Theme.Current);
    }

    [Fact]
    public void A_generator_set_is_not_unloaded_under_a_compile_that_holds_it_and_goes_when_the_last_one_lets_go()
    {
        var project = WriteProject(Path.Combine(TempDir(), "Gen"), "Gen");
        var deps = ProjectDependencies.Resolve(project, Timeout).Dependencies!;
        Assert.NotNull(deps);

        var set = deps.AcquireGenerators();          // a compile (F9) is running with it...
        var other = deps.AcquireGenerators();        // ...and so is the design-session rebuild
        Assert.Same(set, other);
        ProjectDependencies.Invalidate(project);      // Project > Refresh evicts the resolution meanwhile
        Assert.False(set.IsUnloaded);
        deps.ReleaseGenerators();
        Assert.False(set.IsUnloaded);
        deps.ReleaseGenerators();
        Assert.True(set.IsUnloaded);

        // A compile that starts on already evicted dependencies gets a set that goes with it, never one that lingers.
        var late = deps.AcquireGenerators();
        Assert.False(late.IsUnloaded);
        deps.ReleaseGenerators();
        Assert.True(late.IsUnloaded);

        // Nothing leased at eviction: unloaded at once.
        var fresh = ProjectDependencies.Resolve(project, Timeout).Dependencies!;
        var idle = fresh.AcquireGenerators();
        fresh.ReleaseGenerators();
        Assert.False(idle.IsUnloaded);               // still cached: kept for the next compile
        ProjectDependencies.Invalidate(project);
        Assert.True(idle.IsUnloaded);
    }
}
