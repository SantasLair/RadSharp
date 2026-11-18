using RadSharp.Design.Code;

namespace RadSharp.Tests;

/// <summary>RadProject.EnsureSolution in a repository that keeps its solution above the project folder.</summary>
public class SolutionLookupTests
{
    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-sln-").FullName;

    /// <summary>repo/.git, repo/dotnet/All.slnx (optionally listing the app), repo/dotnet/src/App/App.csproj.</summary>
    static (string Repo, RadProject Project) Repo(string? solutionText)
    {
        string repo = TempDir();
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        string app = Path.Combine(repo, "dotnet", "src", "App");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        if (solutionText != null) File.WriteAllText(Path.Combine(repo, "dotnet", "All.slnx"), solutionText);
        return (repo, new RadProject(Path.Combine(app, "App.csproj")));
    }

    [Theory]
    [InlineData("<Solution><Project Path=\"src/App/App.csproj\" /></Solution>")]
    [InlineData("Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"App\", \"src\\App\\App.csproj\", \"{1}\"\nEndProject")]
    public void A_solution_above_that_lists_the_project_is_used_and_none_is_written(string text)
    {
        var (repo, p) = Repo(text);
        Assert.Equal(Path.Combine(repo, "dotnet", "All.slnx"), p.EnsureSolution());
        Assert.Empty(Directory.EnumerateFiles(p.Directory, "*.sln*"));
    }

    [Fact]
    public void A_solution_above_for_other_projects_is_ignored_and_one_is_written_next_to_the_project()
    {
        var (_, p) = Repo("<Solution><Project Path=\"src/Other/Other.csproj\" /></Solution>");
        Assert.Null(p.FindListingSolution());
        Assert.Equal(Path.Combine(p.Directory, "App.sln"), p.EnsureSolution());
        Assert.True(File.Exists(Path.Combine(p.Directory, "App.sln")));
    }

    [Fact]
    public void The_search_stops_at_the_repository_root()
    {
        var (repo, p) = Repo(null);
        // a solution outside the repository that happens to list the path is not this project's
        string outside = Path.Combine(Path.GetDirectoryName(repo)!, "Outside.slnx");
        string rel = Path.GetRelativePath(Path.GetDirectoryName(outside)!, p.ProjectFile).Replace('\\', '/');
        File.WriteAllText(outside, "<Solution><Project Path=\"" + rel + "\" /></Solution>");
        try { Assert.Null(p.FindListingSolution()); }
        finally { File.Delete(outside); }
    }
}
