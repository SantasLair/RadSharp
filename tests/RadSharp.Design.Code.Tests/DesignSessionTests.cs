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
}
