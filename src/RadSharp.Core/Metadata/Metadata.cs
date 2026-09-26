using System.Text.Json;

namespace RadSharp;

/// <summary>
/// Marks a property or event as published: saved in form files and shown in the designer's Object
/// Inspector. The source generator turns these into <see cref="PropertyMeta"/> and <see cref="EventMeta"/>
/// entries, so no runtime reflection is needed.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Event, Inherited = true)]
public sealed class PublishedAttribute(string? name = null) : Attribute
{
    /// <summary>The name in form files; defaults to the camelCased property name, or "on" + event name.</summary>
    public string? Name { get; } = name;

    /// <summary>The Object Inspector category (e.g. "Layout", "Appearance", "Behavior").</summary>
    public string? Category { get; set; }
}

/// <summary>The kinds of published property (spec, Appendix A: metadata types).</summary>
public enum PropertyType { String, Int, Real, Bool, Enum, Color, Anchors, Struct, Strings }

/// <summary>A published property: its form-file name, type and accessors.</summary>
public abstract class PropertyMeta(string name, string? category)
{
    public string Name { get; } = name;
    public string? Category { get; } = category;
    public abstract PropertyType Type { get; }
    /// <summary>Allowed values for enum properties, as written in form files.</summary>
    public abstract IReadOnlyList<string>? Values { get; }
    public abstract Type ValueType { get; }

    public abstract object? GetValue(Control control);
    public abstract void SetValue(Control control, object? value);
    /// <summary>Sets the property from its form-file JSON. Throws <see cref="FormatException"/> for a wrong value.</summary>
    public abstract void Read(Control control, JsonElement json);
    public abstract void Write(FormJsonWriter writer, Control control);
    /// <summary>True if the control's value equals the prototype's (the class default).</summary>
    public abstract bool HasDefaultValue(Control control, Control prototype);
    /// <summary>The value as the Object Inspector shows it.</summary>
    public abstract string FormatValue(Control control);
    /// <summary>Sets the value from Object Inspector text; returns false if the text isn't valid.</summary>
    public abstract bool TryParseValue(Control control, string text);
}

public sealed class PropertyMeta<TOwner, TValue>(
    string name, ValueConverter<TValue> converter, Func<TOwner, TValue> get, Action<TOwner, TValue> set, string? category)
    : PropertyMeta(name, category) where TOwner : Control
{
    public override PropertyType Type => converter.Type;
    public override IReadOnlyList<string>? Values => converter.Values;
    public override Type ValueType => typeof(TValue);

    public TValue Get(Control c) => get((TOwner)c);
    public void Set(Control c, TValue v) => set((TOwner)c, v);

    public override object? GetValue(Control control) => Get(control);
    public override void SetValue(Control control, object? value) => Set(control, (TValue)value!);
    public override void Read(Control control, JsonElement json) => Set(control, converter.Read(json));
    public override void Write(FormJsonWriter writer, Control control) => converter.Write(writer, Get(control));
    public override bool HasDefaultValue(Control control, Control prototype) => converter.AreEqual(Get(control), Get(prototype));
    public override string FormatValue(Control control) => converter.Format(Get(control));

    public override bool TryParseValue(Control control, string text)
    {
        if (!converter.TryParse(text, out var v)) return false;
        Set(control, v);
        return true;
    }
}

/// <summary>A published event: its form-file name (e.g. "onClick"), C# event name and handler parameters.</summary>
public sealed class EventMeta(string name, string clrName, string parameters)
{
    public string Name { get; } = name;
    public string ClrName { get; } = clrName;
    /// <summary>The handler's parameter list, e.g. <c>Control sender, MouseEventArgs e</c>.</summary>
    public string Parameters { get; } = parameters;
}

/// <summary>A control class as known to form files and the designer.</summary>
public sealed class ComponentClass(
    string name, Type type, Type? baseType, Func<Control>? factory,
    IReadOnlyList<PropertyMeta> ownProperties, IReadOnlyList<EventMeta> ownEvents)
{
    IReadOnlyList<PropertyMeta>? _properties;
    IReadOnlyList<EventMeta>? _events;
    Control? _prototype;

    /// <summary>The class name used in form files.</summary>
    public string Name { get; } = name;
    public Type Type { get; } = type;
    public Type? BaseType { get; } = baseType;
    public bool CanCreate => factory != null;
    public IReadOnlyList<PropertyMeta> OwnProperties { get; } = ownProperties;
    public IReadOnlyList<EventMeta> OwnEvents { get; } = ownEvents;

    public ComponentClass? Base => BaseType == null ? null : ComponentRegistry.Find(BaseType);

    public Control Create() => factory?.Invoke() ?? throw new InvalidOperationException($"{Name} can't be created from a form file.");

    /// <summary>All published properties, inherited first, sorted by name; a redeclared name replaces the inherited one.</summary>
    public IReadOnlyList<PropertyMeta> Properties => _properties ??= Merge(Base?.Properties, OwnProperties, p => p.Name);

    public IReadOnlyList<EventMeta> Events => _events ??= Merge(Base?.Events, OwnEvents, e => e.Name);

    /// <summary>A fresh instance holding the class defaults, for "write only non-default values".</summary>
    public Control Prototype => _prototype ??= Create();

    public PropertyMeta? FindProperty(string name)
    {
        foreach (var p in Properties) if (p.Name == name) return p;
        return null;
    }

    public EventMeta? FindEvent(string name)
    {
        foreach (var e in Events) if (e.Name == name) return e;
        return null;
    }

    static IReadOnlyList<T> Merge<T>(IReadOnlyList<T>? inherited, IReadOnlyList<T> own, Func<T, string> key)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        if (inherited != null) foreach (var x in inherited) map[key(x)] = x;
        foreach (var x in own) map[key(x)] = x;
        return map.Values.OrderBy(key, StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// Every control class registered by generated code (one module initializer per assembly), by
/// form-file name and by type.
/// </summary>
public static class ComponentRegistry
{
    static readonly Dictionary<string, ComponentClass> ByName = new(StringComparer.Ordinal);
    static readonly Dictionary<Type, ComponentClass> ByType = [];
    static readonly object Lock = new();

    public static void Register(ComponentClass component)
    {
        lock (Lock)
        {
            ByType[component.Type] = component;
            // A creatable class wins the name over an abstract one of the same name.
            if (!ByName.TryGetValue(component.Name, out var existing) || !existing.CanCreate || component.CanCreate)
                ByName[component.Name] = component;
        }
    }

    public static ComponentClass? Find(string name)
    {
        lock (Lock) return ByName.GetValueOrDefault(name);
    }

    public static ComponentClass? Find(Type type)
    {
        lock (Lock) return ByType.GetValueOrDefault(type);
    }

    /// <summary>The metadata for a control: its own class, or the nearest registered base class.</summary>
    public static ComponentClass Of(Control control)
    {
        for (var t = control.GetType(); t != null; t = t.BaseType)
            if (Find(t) is { } c) return c;
        throw new InvalidOperationException($"No component metadata for {control.GetType().Name}.");
    }

    public static IReadOnlyList<ComponentClass> All
    {
        get { lock (Lock) return [.. ByType.Values]; }
    }
}
