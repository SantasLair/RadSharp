using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RadSharp.Designer;

/// <summary>One unsaved buffer kept for crash recovery: a code document's text or a form's JSON.</summary>
public sealed record RecoveryItem(string Kind, string Path, string Text, DateTime Saved);

/// <summary>
/// Crash recovery: <see cref="Write"/> keeps the unsaved buffers in a folder under the settings directory, and a
/// <c>running</c> marker says whether the last session ended cleanly. <see cref="BeginSession"/> reports an abnormal
/// exit (a marker left by a process that no longer runs); <see cref="EndSession"/> removes the marker and the files.
/// </summary>
public sealed class RecoveryStore
{
    string Marker => System.IO.Path.Combine(directory, "running");
    string Manifest => System.IO.Path.Combine(directory, "manifest.json");

    readonly string directory;

    public RecoveryStore(string directory) => this.directory = Directory = directory;

    public string Directory { get; }

    /// <summary>Marks the session as running. True if the previous one never ended cleanly.</summary>
    public bool BeginSession()
    {
        bool crashed = false;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            if (File.Exists(Marker) && int.TryParse(File.ReadAllText(Marker).Trim(), out var pid) && pid != Environment.ProcessId && !IsRunning(pid))
                crashed = true;
            File.WriteAllText(Marker, Environment.ProcessId.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return crashed;
    }

    static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>A clean exit: the marker and every recovery file go.</summary>
    public void EndSession()
    {
        Clear();
        try { File.Delete(Marker); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Replaces the recovery files with <paramref name="items"/> (what is unsaved now); an empty set removes them.</summary>
    public void Write(IReadOnlyList<RecoveryItem> items)
    {
        try
        {
            if (items.Count == 0) { Clear(); return; }
            System.IO.Directory.CreateDirectory(directory);
            var entries = new JsonArray();
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json", "running" };
            foreach (var item in items)
            {
                var file = FileFor(item.Path);
                File.WriteAllText(System.IO.Path.Combine(directory, file), item.Text, Encoding.UTF8);
                keep.Add(file);
                entries.Add(new JsonObject { ["kind"] = item.Kind, ["path"] = item.Path, ["file"] = file, ["saved"] = item.Saved.ToString("o") });
            }
            File.WriteAllText(Manifest, new JsonObject { ["items"] = entries }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            foreach (var f in System.IO.Directory.GetFiles(directory))
                if (!keep.Contains(System.IO.Path.GetFileName(f))) File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The buffers a crashed session left.</summary>
    public IReadOnlyList<RecoveryItem> List()
    {
        var list = new List<RecoveryItem>();
        try
        {
            if (!File.Exists(Manifest) || JsonNode.Parse(File.ReadAllText(Manifest)) is not JsonObject root || root["items"] is not JsonArray items) return list;
            foreach (var o in items.OfType<JsonObject>())
            {
                var file = System.IO.Path.Combine(directory, o["file"]?.GetValue<string>() ?? "");
                if (o["kind"]?.GetValue<string>() is { } kind && o["path"]?.GetValue<string>() is { } path && File.Exists(file))
                    list.Add(new RecoveryItem(kind, path, File.ReadAllText(file, Encoding.UTF8),
                        DateTime.TryParse(o["saved"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : default));
            }
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>Deletes the recovery files (not the running marker).</summary>
    public void Clear()
    {
        try
        {
            if (!System.IO.Directory.Exists(directory)) return;
            foreach (var f in System.IO.Directory.GetFiles(directory))
                if (System.IO.Path.GetFileName(f) != "running") File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static string FileFor(string path) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToLowerInvariant())))[..16] + ".txt";
}
