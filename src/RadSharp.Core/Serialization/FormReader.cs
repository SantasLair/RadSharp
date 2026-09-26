using System.Text.Json;

namespace RadSharp;

/// <summary>Maps form-file class names to constructors.</summary>
public sealed class ClassRegistry
{
    readonly Dictionary<string, Func<Control>> _factories = new(StringComparer.Ordinal);

    public static ClassRegistry CreateDefault()
    {
        var r = new ClassRegistry();
        r.Register("Form", () => new Form());
        r.Register("Panel", () => new Panel());
        r.Register("Label", () => new Label());
        r.Register("Button", () => new Button());
        r.Register("Splitter", () => new Splitter());
        return r;
    }

    public void Register(string className, Func<Control> factory) => _factories[className] = factory;

    public Control? Create(string className) => _factories.TryGetValue(className, out var f) ? f() : null;
}

/// <summary>The file isn't a form file this reader understands (wrong format or unknown major version).</summary>
public sealed class FormFileException(string message) : Exception(message);

public sealed record FormLoadResult(Form Form, IReadOnlyList<string> Warnings);

/// <summary>Reads form files (spec, Appendix A).</summary>
public static class FormReader
{
    public const string FormatId = "radform";
    public const int MajorVersion = 1;

    /// <summary>
    /// Loads a form and lays it out once at its design size, so anchors capture design-time distances.
    /// </summary>
    public static FormLoadResult Load(string json, ClassRegistry? registry = null)
    {
        registry ??= ClassRegistry.CreateDefault();
        var warnings = new List<string>();

        using var doc = ParseDocument(json);
        var top = doc.RootElement;
        if (top.ValueKind != JsonValueKind.Object) throw new FormFileException("A form file must be a JSON object.");

        var format = top.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        if (format != FormatId)
            throw new FormFileException($"Not a form file: \"format\" is {(format == null ? "missing" : $"\"{format}\"")}, expected \"{FormatId}\".");
        CheckVersion(top, warnings);

        if (!top.TryGetProperty("root", out var rootJson) || rootJson.ValueKind != JsonValueKind.Object)
            throw new FormFileException("The form file has no \"root\" object.");
        if (ReadNode(rootJson, registry, warnings, "root") is not Form form)
            throw new FormFileException("The root node must be a Form.");

        form.DesignWidth = form.Width;
        form.DesignHeight = form.Height;
        form.PerformLayout();
        return new FormLoadResult(form, warnings);
    }

    static JsonDocument ParseDocument(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException e) { throw new FormFileException($"The form file isn't valid JSON: {e.Message}"); }
    }

    static void CheckVersion(JsonElement top, List<string> warnings)
    {
        var version = top.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var parts = version.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out _))
            throw new FormFileException($"Invalid form file version \"{version}\"; expected \"MAJOR.MINOR\".");
        if (major != MajorVersion)
            throw new FormFileException($"Unsupported form file version {version}; this reader supports {MajorVersion}.x.");
    }

    static Control? ReadNode(JsonElement node, ClassRegistry registry, List<string> warnings, string path)
    {
        var className = node.TryGetProperty("class", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : null;
        if (className == null)
        {
            warnings.Add($"{path}: node has no \"class\"; skipped.");
            return null;
        }
        if (registry.Create(className) is not { } control)
        {
            warnings.Add($"{path}: unknown class \"{className}\"; node and its children skipped.");
            return null;
        }

        if (node.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            control.Name = n.GetString()!;
        var here = control.Name.Length > 0 ? control.Name : $"{path} ({className})";

        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Name is "class" or "name" or "children") continue;
            try
            {
                if (!control.ReadProperty(prop.Name, prop.Value))
                    warnings.Add($"{here}: {className} has no published property \"{prop.Name}\"; ignored.");
            }
            catch (FormatException e)
            {
                warnings.Add($"{here}.{prop.Name}: {e.Message}; ignored.");
            }
        }

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var childJson in children.EnumerateArray())
            {
                if (ReadNode(childJson, registry, warnings, $"{here}.children[{i++}]") is { } child)
                    control.Add(child);
            }
        }
        return control;
    }
}
