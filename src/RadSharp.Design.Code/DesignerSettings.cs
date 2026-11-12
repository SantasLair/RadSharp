using System.Text.Json;

namespace RadSharp.Design.Code;

/// <summary>The designer's preferences and recent projects, kept in the user's application-data folder.</summary>
public sealed class DesignerSettings
{
    public const int MaxRecent = 10;

    public List<string> RecentProjects { get; } = [];
    public EditorKind Editor { get; set; } = EditorKind.VSCode;
    /// <summary>For <see cref="EditorKind.Custom"/>: a command with {file}, {line}, {folder}, {solution}.</summary>
    public string CustomEditorCommand { get; set; } = "";
    /// <summary>Where New Application puts projects by default.</summary>
    /// <remarks>The folder keeps the original "RADSharp" spelling so existing projects and settings stay where they are.</remarks>
    public string ProjectsFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RADSharp Projects");
    /// <summary>A local folder of RadSharp packages for new projects; empty = auto-detect, then nuget.org.</summary>
    public string PackageSource { get; set; } = "";
    /// <summary>Theme preference: "light" or "dark".</summary>
    public string Theme { get; set; } = "light";

    /// <summary>The settings file. The folder keeps the original "RADSharp" spelling (see <see cref="ProjectsFolder"/>).</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RADSharp", "designer.json");

    /// <summary>Moves (or adds) a project to the top of the recent list.</summary>
    public void AddRecent(string projectFile)
    {
        var full = Path.GetFullPath(projectFile);
        RecentProjects.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, full);
        if (RecentProjects.Count > MaxRecent) RecentProjects.RemoveRange(MaxRecent, RecentProjects.Count - MaxRecent);
    }

    public void RemoveMissing() => RecentProjects.RemoveAll(p => !File.Exists(p));

    public static DesignerSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        var s = new DesignerSettings();
        if (!File.Exists(path)) return s;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("recentProjects", out var recent))
                foreach (var p in recent.EnumerateArray()) if (p.GetString() is { } str) s.RecentProjects.Add(str);
            if (root.TryGetProperty("editor", out var e) && Enum.TryParse<EditorKind>(e.GetString(), true, out var kind)) s.Editor = kind;
            if (root.TryGetProperty("customEditorCommand", out var c)) s.CustomEditorCommand = c.GetString() ?? "";
            if (root.TryGetProperty("projectsFolder", out var f) && f.GetString() is { Length: > 0 } folder) s.ProjectsFolder = folder;
            if (root.TryGetProperty("packageSource", out var ps)) s.PackageSource = ps.GetString() ?? "";
            if (root.TryGetProperty("theme", out var t) && t.GetString() is { Length: > 0 } theme) s.Theme = theme;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { }
        return s;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteStartArray("recentProjects");
        foreach (var p in RecentProjects) w.WriteStringValue(p);
        w.WriteEndArray();
        w.WriteString("editor", Editor.ToString());
        w.WriteString("customEditorCommand", CustomEditorCommand);
        w.WriteString("projectsFolder", ProjectsFolder);
        w.WriteString("packageSource", PackageSource);
        w.WriteString("theme", Theme);
        w.WriteEndObject();
    }

    /// <summary>The package source new projects use: the setting, or the auto-detected local folder.</summary>
    public string? EffectivePackageSource =>
        PackageSource.Length > 0 && Directory.Exists(PackageSource) ? PackageSource : RadSharpPackages.FindLocalSource();
}
