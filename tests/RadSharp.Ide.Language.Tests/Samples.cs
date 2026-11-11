using RadSharp.Design.Code;

namespace RadSharp.Ide.Language.Tests;

/// <summary>The repo's samples as fixtures. Tests never write to them: every change goes through the workspace in memory.</summary>
static class Samples
{
    public static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public static string Dir(string sample) => Path.Combine(Root, "samples", sample);
    public static string File(string sample, string file) => Path.Combine(Dir(sample), file);
    public static RadProject Project(string sample) => new(Path.Combine(Dir(sample), sample + ".csproj"));

    /// <summary>Opens a sample without the file watcher and without restoring (the build restored it).</summary>
    public static async Task<(ProjectWorkspace Workspace, LanguageService Language)> OpenAsync(string sample)
    {
        var workspace = await ProjectWorkspace.OpenAsync(Project(sample), new ProjectWorkspaceOptions { WatchFiles = false, Restore = RestoreMode.Never });
        return (workspace, new LanguageService(workspace));
    }

    /// <summary>The offset just after the first occurrence of <paramref name="marker"/> (or of the part before '|' in it).</summary>
    public static int After(string text, string marker)
    {
        int bar = marker.IndexOf('|');
        var find = bar >= 0 ? marker.Replace("|", "") : marker;
        int i = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{find}' not found");
        return i + (bar >= 0 ? bar : find.Length);
    }

    /// <summary>Applies edits (from last to first) to a text.</summary>
    public static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        foreach (var e in edits.OrderByDescending(e => e.Start)) text = text[..e.Start] + e.NewText + text[(e.Start + e.Length)..];
        return text;
    }
}
