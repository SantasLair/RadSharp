using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RadSharp.Design.Code;

/// <summary>How the project's app is started: arguments, working directory and environment variables.</summary>
public sealed class RunConfiguration
{
    /// <summary>The command line (already quoted as the app wants it).</summary>
    public string Arguments { get; set; } = "";

    /// <summary>Absolute, or relative to the project folder; empty means the project folder.</summary>
    public string WorkingDirectory { get; set; } = "";

    public Dictionary<string, string> Environment { get; set; } = [];

    public string ResolveWorkingDirectory(RadProject project) =>
        WorkingDirectory.Trim().Length == 0 ? project.Directory : Path.GetFullPath(Path.Combine(project.Directory, WorkingDirectory.Trim()));
}

/// <summary>
/// Reads and writes the run configuration in <c>Properties/launchSettings.json</c>, the file <c>dotnet run</c>,
/// Visual Studio and Rider read too. The profile is named after the project; other profiles and any other
/// properties of the file are left alone.
/// </summary>
public static class LaunchSettings
{
    public static string PathFor(RadProject project) => Path.Combine(project.Directory, "Properties", "launchSettings.json");

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    static JsonObject? ReadRoot(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>The project's profile, or the first <c>Project</c> profile, or defaults.</summary>
    public static RunConfiguration Load(RadProject project)
    {
        var config = new RunConfiguration();
        if (ReadRoot(PathFor(project))?["profiles"] is not JsonObject profiles) return config;
        var profile = profiles[project.Name] as JsonObject
            ?? profiles.Select(p => p.Value as JsonObject).FirstOrDefault(p => p?["commandName"]?.GetValue<string>() == "Project");
        if (profile == null) return config;
        config.Arguments = profile["commandLineArgs"]?.GetValue<string>() ?? "";
        config.WorkingDirectory = profile["workingDirectory"]?.GetValue<string>() ?? "";
        if (profile["environmentVariables"] is JsonObject env)
            foreach (var (k, v) in env) if (v != null) config.Environment[k] = v.ToString();
        return config;
    }

    /// <summary>Writes the profile named after the project (creating the file and the folder if needed).</summary>
    public static void Save(RadProject project, RunConfiguration config)
    {
        var path = PathFor(project);
        var root = ReadRoot(path) ?? new JsonObject { ["$schema"] = "https://json.schemastore.org/launchsettings.json" };
        if (root["profiles"] is not JsonObject profiles) root["profiles"] = profiles = [];
        if (profiles[project.Name] is not JsonObject profile) profiles[project.Name] = profile = new JsonObject { ["commandName"] = "Project" };
        profile["commandName"] ??= "Project";
        Set(profile, "commandLineArgs", config.Arguments.Trim().Length > 0 ? config.Arguments : null);
        Set(profile, "workingDirectory", config.WorkingDirectory.Trim().Length > 0 ? config.WorkingDirectory : null);
        if (config.Environment.Count > 0)
        {
            var env = new JsonObject();
            foreach (var (k, v) in config.Environment) env[k] = v;
            profile["environmentVariables"] = env;
        }
        else profile.Remove("environmentVariables");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(Indented) + "\n");
    }

    static void Set(JsonObject o, string key, string? value)
    {
        if (value == null) o.Remove(key);
        else o[key] = value;
    }

    /// <summary>Splits <c>KEY=value</c> lines (one per line) into a dictionary; blank and malformed lines are skipped.</summary>
    public static Dictionary<string, string> ParseEnvironment(string text)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq > 0) env[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return env;
    }

    public static string FormatEnvironment(IReadOnlyDictionary<string, string> env) =>
        string.Join("\n", env.Select(p => $"{p.Key}={p.Value}"));
}

/// <summary>A stack trace as lines for the Output window; frames with a source file and line carry a location to navigate to.</summary>
public sealed record ReportLine(string Text, BuildMessage? Location);

public static class ExceptionReport
{
    /// <summary>The exception (and its inner exceptions) as lines. A frame whose file exists (per
    /// <paramref name="fileExists"/>, default <see cref="File.Exists"/>) gets a <see cref="ReportLine.Location"/>.</summary>
    public static IReadOnlyList<ReportLine> Describe(Exception exception, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var lines = new List<ReportLine>();
        Add(exception, "", lines, fileExists, 0);
        return lines;
    }

    static void Add(Exception ex, string prefix, List<ReportLine> lines, Func<string, bool> fileExists, int depth)
    {
        if (depth > 8) return;
        foreach (var l in $"{prefix}{ex.GetType().FullName}: {ex.Message}".Split('\n')) lines.Add(new ReportLine(l.TrimEnd('\r'), null));
        foreach (var frame in new StackTrace(ex, fNeedFileInfo: true).GetFrames())
        {
            var method = frame.GetMethod();
            if (method == null) continue;
            var name = $"{method.DeclaringType?.FullName}.{method.Name}";
            var file = frame.GetFileName();
            int line = frame.GetFileLineNumber(), col = frame.GetFileColumnNumber();
            if (file != null && line > 0)
            {
                var text = $"   at {name} in {file}:line {line}";
                lines.Add(new ReportLine(text, fileExists(file) ? new BuildMessage(file, line, Math.Max(1, col), "frame", "", text.Trim()) : null));
            }
            else lines.Add(new ReportLine($"   at {name}", null));
        }
        if (ex is AggregateException agg) foreach (var inner in agg.InnerExceptions) Add(inner, "--- inner: ", lines, fileExists, depth + 1);
        else if (ex.InnerException is { } i) Add(i, "--- inner: ", lines, fileExists, depth + 1);
    }

    /// <summary>True if any frame of the exception (or its inner exceptions) runs in <paramref name="assembly"/>.</summary>
    public static bool InvolvesAssembly(Exception ex, System.Reflection.Assembly assembly)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (new StackTrace(e, false).GetFrames().Any(f => f.GetMethod()?.DeclaringType?.Assembly == assembly)) return true;
            if (e is AggregateException agg && agg.InnerExceptions.Any(i => InvolvesAssembly(i, assembly))) return true;
        }
        return false;
    }
}
