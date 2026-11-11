using System.Text.Json;
using System.Text.Json.Nodes;
using RadSharp.Design.Code;

namespace RadSharp.Designer;

/// <summary>Where handler double-clicks and Output entries take you.</summary>
public enum CodeNavigation { Embedded, External }

/// <summary>
/// The IDE's own files, kept next to <see cref="DesignerSettings"/> (which is shared with the Visual Studio track
/// and so stays as it is): <c>ide.json</c> (IDE preferences), <c>keymap.json</c> (shortcut overrides) and
/// <c>session.json</c> (what was open, and the dock layout).
/// </summary>
public sealed class IdePaths(string? designerSettingsPath)
{
    public string Directory { get; } = Path.GetDirectoryName(Path.GetFullPath(designerSettingsPath ?? DesignerSettings.DefaultPath))!;
    public string Settings => Path.Combine(Directory, "ide.json");
    public string Keymap => Path.Combine(Directory, "keymap.json");
    public string Session => Path.Combine(Directory, "session.json");
}

/// <summary>IDE preferences that aren't in <see cref="DesignerSettings"/>.</summary>
public sealed partial class IdeSettings
{
    /// <summary>Open code in the built-in editor (the default) or in the external IDE of <see cref="DesignerSettings.Editor"/>.</summary>
    public CodeNavigation CodeNavigation { get; set; } = CodeNavigation.Embedded;

    public static IdeSettings Load(string path)
    {
        var s = new IdeSettings();
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root
                && root["codeNavigation"]?.GetValue<string>() is { } nav && Enum.TryParse<CodeNavigation>(nav, true, out var n))
                s.CodeNavigation = n;
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException) { }
        s.LoadExtras(path);
        return s;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = new JsonObject { ["codeNavigation"] = CodeNavigation.ToString() };
        SaveExtras(root);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// What the IDE restores on start: the last project, its open documents in tab order (with each document's own
/// state: carets, scroll), the active one, split view, and the dock layout.
/// </summary>
public sealed class IdeSession
{
    public sealed record Document(string Kind, string Path, bool Preview, JsonObject? State);

    public string? Project { get; set; }
    public List<Document> Documents { get; } = [];
    public string? Active { get; set; }
    public bool Split { get; set; }
    public DockLayout? Layout { get; set; }

    public static IdeSession Load(string path)
    {
        var s = new IdeSession();
        if (!File.Exists(path)) return s;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return s;
            s.Project = root["project"]?.GetValue<string>();
            s.Active = root["active"]?.GetValue<string>();
            s.Split = root["split"]?.GetValue<bool>() ?? false;
            s.Layout = DockLayout.FromJson(root["layout"]);
            if (root["documents"] is JsonArray docs)
                foreach (var d in docs.OfType<JsonObject>())
                    if (d["kind"]?.GetValue<string>() is { } kind && d["path"]?.GetValue<string>() is { } p)
                        s.Documents.Add(new Document(kind, p, d["preview"]?.GetValue<bool>() ?? false, d["state"]?.DeepClone() as JsonObject));
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException) { return new IdeSession(); }
        return s;
    }

    public void Save(string path)
    {
        var docs = new JsonArray();
        foreach (var d in Documents)
        {
            var o = new JsonObject { ["kind"] = d.Kind, ["path"] = d.Path };
            if (d.Preview) o["preview"] = true;
            if (d.State != null) o["state"] = d.State.DeepClone();
            docs.Add(o);
        }
        var root = new JsonObject
        {
            ["project"] = Project,
            ["documents"] = docs,
            ["active"] = Active,
            ["split"] = Split,
            ["layout"] = Layout?.ToJson(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
