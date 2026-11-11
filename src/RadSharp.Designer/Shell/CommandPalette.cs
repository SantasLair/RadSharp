using System.Text.Json;
using System.Text.Json.Nodes;

namespace RadSharp.Designer;

/// <summary>The command ids the user ran from the palette most recently, newest first; kept in the IDE settings folder.</summary>
public sealed class RecentCommands
{
    public const int Capacity = 30;
    readonly List<string> _ids = [];

    public IReadOnlyList<string> Ids => _ids;

    public void Touch(string id)
    {
        _ids.Remove(id);
        _ids.Insert(0, id);
        if (_ids.Count > Capacity) _ids.RemoveRange(Capacity, _ids.Count - Capacity);
    }

    public static RecentCommands Load(string path)
    {
        var r = new RecentCommands();
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root && root["recent"] is JsonArray ids)
                foreach (var id in ids.Select(n => n?.GetValue<string>()).OfType<string>().Take(Capacity))
                    if (!r._ids.Contains(id)) r._ids.Add(id);
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or FormatException) { }
        return r;
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var root = new JsonObject { ["recent"] = new JsonArray([.. _ids.Select(i => (JsonNode)JsonValue.Create(i)!)]) };
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>The rows of the command palette (Ctrl+Shift+P): every command of the registry with its title, category, shortcut and enabled state.</summary>
public static class CommandPalette
{
    /// <summary>
    /// One item per command that makes sense without a parameter (<see cref="Command.HiddenFromPalette"/> leaves the others out). Recently used
    /// commands come first, in the order they were used, then the rest in registry order; the picker's stable ranking keeps that order for equal scores.
    /// </summary>
    public static List<QuickPickItem> Build(CommandRegistry registry, RecentCommands recent)
    {
        var items = new List<QuickPickItem>();
        var shown = registry.All.Where(c => !c.HiddenFromPalette && c.Title.Length > 0).ToList();
        foreach (var id in recent.Ids)
            if (shown.FirstOrDefault(c => c.Id == id) is { } c) items.Add(ItemOf(c));
        foreach (var c in shown)
            if (!recent.Ids.Contains(c.Id)) items.Add(ItemOf(c));
        return items;
    }

    static QuickPickItem ItemOf(Command c) =>
        new(c.Title, c.Category, ShortcutText(c), c.Enabled, c);

    /// <summary>The shortcut as the menus print it, or empty.</summary>
    public static string ShortcutText(Command c) =>
        c.Shortcut.Length > 0 && Shortcut.TryParse(c.Shortcut, out var s) ? s.ToString() : "";
}
