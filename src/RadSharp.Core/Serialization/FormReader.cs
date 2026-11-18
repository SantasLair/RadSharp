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

    /// <summary>
    /// Keeps a node whose class isn't registered as an <see cref="UnknownControl"/> placeholder (still with a warning)
    /// instead of skipping it, so saving the form writes it back unchanged. The designer sets this; an application
    /// leaves it off, so a control it can't create is skipped as before.
    /// </summary>
    public bool KeepUnknownClasses { get; init; }

    /// <summary>
    /// Which registered classes the reader may create (null: all); a node of any other class is read as if its class
    /// were unknown. The designer accepts only classes that stay registered: a live run's (F9) classes are unregistered
    /// when it ends, and a control of one left in a designed form would then be saved as its base class.
    /// </summary>
    public Func<ComponentClass, bool>? AcceptClass { get; init; }
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

    /// <summary>
    /// Reads controls written by <see cref="FormWriter.SaveFragment"/> (a JSON array of nodes). Of the options only
    /// <see cref="FormLoadOptions.KeepUnknownClasses"/> applies (a fragment isn't scaled).
    /// </summary>
    public static (List<Control> Controls, List<string> Warnings) LoadFragment(string json, FormLoadOptions? options = null)
    {
        options ??= new();
        var warnings = new List<string>();
        var controls = new List<Control>();
        using var doc = ParseDocument(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new FormFileException("A fragment must be a JSON array of controls.");
        int i = 0;
        foreach (var node in doc.RootElement.EnumerateArray())
            if (ReadNode(node, warnings, $"[{i++}]", options) is { } c) controls.Add(c);
        return (controls, warnings);
    }

    /// <summary>Receives warnings from <c>LoadInto</c> (unknown classes, properties...).</summary>
    public static Action<string>? Warning { get; set; } = w => System.Diagnostics.Debug.WriteLine("RadSharp: " + w);

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
            ReadInto(target, rootJson, warnings, "root", options);
            form = target;
        }
        else if (ReadNode(rootJson, warnings, "root", options) is Form created) form = created;
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

    static Control? ReadNode(JsonElement node, List<string> warnings, string path, FormLoadOptions options)
    {
        var className = node.TryGetProperty("class", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : null;
        if (className == null)
        {
            warnings.Add($"{path}: node has no \"class\"; skipped.");
            return null;
        }
        if (ComponentRegistry.Find(className) is not { CanCreate: true } cls || options.AcceptClass?.Invoke(cls) == false)
        {
            if (options.KeepUnknownClasses)
            {
                warnings.Add($"{path}: unknown class \"{className}\"; kept as a placeholder (written back unchanged).");
                return UnknownControl.Read(node, className);
            }
            warnings.Add($"{path}: unknown class \"{className}\"; node and its children skipped.");
            return null;
        }
        var control = cls.Create();
        ReadInto(control, node, warnings, path, options);
        return control;
    }

    /// <summary>Applies a node's name, properties, event handler names and children to a control.</summary>
    static void ReadInto(Control control, JsonElement node, List<string> warnings, string path, FormLoadOptions options)
    {
        var cls = ComponentRegistry.Of(control);
        if (node.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            control.Name = n.GetString()!;
        var here = control.Name.Length > 0 ? control.Name : $"{path} ({cls.Name})";

        // Some members depend on what the others set (a grid's "columns" and "sortColumn" on colCount and fixedCols), so
        // they are applied after all of them, in the control's order: property order in the file never matters (spec, "Grids").
        var collections = control as IFormCollections;
        Dictionary<string, JsonElement>? deferred = null;
        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Name is "class" or "name" or "children") continue;
            if (collections != null && collections.DeferredMembers.Contains(prop.Name))
            {
                (deferred ??= [])[prop.Name] = prop.Value;
                continue;
            }
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

        if (collections != null && deferred != null)
            foreach (var name in collections.DeferredMembers)
                if (deferred.TryGetValue(name, out var value)) collections.ReadDeferred(name, value, warnings, here);

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var childJson in children.EnumerateArray())
            {
                if (ReadNode(childJson, warnings, $"{here}.children[{i++}]", options) is { } child)
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
