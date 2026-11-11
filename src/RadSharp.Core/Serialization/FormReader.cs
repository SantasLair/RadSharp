using System.Reflection;
using System.Text.Json;

namespace RadSharp;

/// <summary>The file isn't a form file this reader understands (wrong format or unknown major version).</summary>
public sealed class FormFileException(string message) : Exception(message);

public sealed record FormLoadResult(Form Form, IReadOnlyList<string> Warnings);

public sealed record FormLoadOptions
{
    /// <summary>UI scale factor; stored lengths are multiplied by it and rounded before the first layout.</summary>
    public double Scale { get; init; } = 1;
}

/// <summary>Reads form files (spec, Appendix A), driven by the generated component metadata.</summary>
public static class FormReader
{
    public const string FormatId = "radform";
    public const int MajorVersion = 1;

    /// <summary>
    /// Loads a form into a new instance of its root class and lays it out once at its design size,
    /// so anchors capture design-time distances.
    /// </summary>
    public static FormLoadResult Load(string json, FormLoadOptions? options = null) => LoadCore(json, null, options ?? new());

    /// <summary>
    /// Loads a form file into an existing form instance: what a generated <c>InitializeComponent()</c> does.
    /// Uses <see cref="Form.DefaultScale"/> unless options say otherwise; warnings go to <see cref="Warning"/>.
    /// </summary>
    public static void LoadInto(Form form, string json, FormLoadOptions? options = null)
    {
        var result = LoadCore(json, form, options ?? new() { Scale = Form.DefaultScale });
        foreach (var w in result.Warnings) Warning?.Invoke(w);
    }

    /// <summary>Loads an embedded form resource into an existing form instance.</summary>
    public static void LoadInto(Form form, Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FormFileException($"The form resource \"{resourceName}\" isn't embedded in {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        LoadInto(form, reader.ReadToEnd());
    }

    /// <summary>Reads controls written by <see cref="FormWriter.SaveFragment"/> (a JSON array of nodes).</summary>
    public static (List<Control> Controls, List<string> Warnings) LoadFragment(string json)
    {
        var warnings = new List<string>();
        var controls = new List<Control>();
        using var doc = ParseDocument(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new FormFileException("A fragment must be a JSON array of controls.");
        int i = 0;
        foreach (var node in doc.RootElement.EnumerateArray())
            if (ReadNode(node, warnings, $"[{i++}]") is { } c) controls.Add(c);
        return (controls, warnings);
    }

    /// <summary>Receives warnings from <c>LoadInto</c> (unknown classes, properties...).</summary>
    public static Action<string>? Warning { get; set; } = w => System.Diagnostics.Debug.WriteLine("RADSharp: " + w);

    static FormLoadResult LoadCore(string json, Form? target, FormLoadOptions options)
    {
        if (options.Scale <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Scale must be positive.");
        var warnings = new List<string>();

        using var doc = ParseDocument(json);
        var top = doc.RootElement;
        if (top.ValueKind != JsonValueKind.Object) throw new FormFileException("A form file must be a JSON object.");

        var format = top.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        if (format != FormatId)
            throw new FormFileException($"Not a form file: \"format\" is {(format == null ? "missing" : $"\"{format}\"")}, expected \"{FormatId}\".");
        CheckVersion(top);

        if (!top.TryGetProperty("root", out var rootJson) || rootJson.ValueKind != JsonValueKind.Object)
            throw new FormFileException("The form file has no \"root\" object.");

        Form form;
        if (target != null)
        {
            if (ClassOf(rootJson) is { } cls && !cls.Type.IsInstanceOfType(target))
                throw new FormFileException($"The form file's root is a {cls.Name}, but it is being loaded into a {target.GetType().Name}.");
            ReadInto(target, rootJson, warnings, "root");
            form = target;
        }
        else if (ReadNode(rootJson, warnings, "root") is Form created) form = created;
        else throw new FormFileException("The root node must be a Form.");

        form.ApplyScaleCore(options.Scale);
        form.PerformLayout();
        return new FormLoadResult(form, warnings);
    }

    static JsonDocument ParseDocument(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException e) { throw new FormFileException($"The form file isn't valid JSON: {e.Message}"); }
    }

    static void CheckVersion(JsonElement top)
    {
        var version = top.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var parts = version.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out _))
            throw new FormFileException($"Invalid form file version \"{version}\"; expected \"MAJOR.MINOR\".");
        if (major != MajorVersion)
            throw new FormFileException($"Unsupported form file version {version}; this reader supports {MajorVersion}.x.");
    }

    static ComponentClass? ClassOf(JsonElement node) =>
        node.TryGetProperty("class", out var c) && c.ValueKind == JsonValueKind.String ? ComponentRegistry.Find(c.GetString()!) : null;

    static Control? ReadNode(JsonElement node, List<string> warnings, string path)
    {
        var className = node.TryGetProperty("class", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : null;
        if (className == null)
        {
            warnings.Add($"{path}: node has no \"class\"; skipped.");
            return null;
        }
        if (ComponentRegistry.Find(className) is not { CanCreate: true } cls)
        {
            warnings.Add($"{path}: unknown class \"{className}\"; node and its children skipped.");
            return null;
        }
        var control = cls.Create();
        ReadInto(control, node, warnings, path);
        return control;
    }

    /// <summary>Applies a node's name, properties, event handler names and children to a control.</summary>
    static void ReadInto(Control control, JsonElement node, List<string> warnings, string path)
    {
        var cls = ComponentRegistry.Of(control);
        if (node.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            control.Name = n.GetString()!;
        var here = control.Name.Length > 0 ? control.Name : $"{path} ({cls.Name})";

        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Name is "class" or "name" or "children") continue;
            if (prop.Name == "items" && control is IMenuOwner owner)
            {
                owner.Items.Clear();
                if (prop.Value.ValueKind != JsonValueKind.Array) { warnings.Add($"{here}.items: expected an array; ignored."); continue; }
                int i = 0;
                foreach (var item in prop.Value.EnumerateArray()) owner.Items.Add(MenuItem.Read(item, warnings, $"{here}.items[{i++}]"));
                continue;
            }
            try
            {
                if (!SetProperty(control, cls, prop.Name, prop.Value))
                    warnings.Add($"{here}: {cls.Name} has no published property \"{prop.Name}\"; ignored.");
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
                if (ReadNode(childJson, warnings, $"{here}.children[{i++}]") is { } child)
                    control.Add(child);
            }
        }
    }

    /// <summary>
    /// Sets a published property or event handler name from form-file JSON. Returns false if the class
    /// publishes nothing by that name; throws <see cref="FormatException"/> for a wrong value.
    /// </summary>
    public static bool SetProperty(Control control, string name, JsonElement value) =>
        SetProperty(control, ComponentRegistry.Of(control), name, value);

    static bool SetProperty(Control control, ComponentClass cls, string name, JsonElement value)
    {
        if (cls.FindProperty(name) is { } p)
        {
            p.Read(control, value);
            return true;
        }
        if (cls.FindEvent(name) != null)
        {
            if (value.ValueKind != JsonValueKind.String) throw new FormatException($"expected a handler name, got {value.GetRawText()}");
            control.EventHandlers[name] = value.GetString()!;
            return true;
        }
        return false;
    }
}
