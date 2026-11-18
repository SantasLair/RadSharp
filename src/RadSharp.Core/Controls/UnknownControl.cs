using System.Text.Json;
using RadSharp.Drawing;

namespace RadSharp;

/// <summary>
/// A placeholder for a form-file node whose class isn't registered: a control the application defines in its own
/// code (not compiled into the designer yet), or one from a library the designer doesn't reference. The reader
/// keeps it, instead of dropping the node, when <see cref="FormLoadOptions.KeepUnknownClasses"/> is set (the
/// designer does), and the writer writes it back verbatim, so loading and saving such a file is byte-identical.
/// </summary>
/// <remarks>
/// The node's members are kept as their raw JSON text, children included (they aren't loaded as controls). Only the
/// members every control has — those <see cref="Control"/> publishes, such as <c>left</c>, <c>top</c>,
/// <c>width</c>, <c>height</c>, <c>align</c>, <c>anchors</c> — and its <c>name</c> are read into the placeholder;
/// when one of them changes (the designer moved or resized it) the writer writes the new value in its place, and
/// every other member stays exactly as it was in the file.
/// </remarks>
public sealed class UnknownControl : Control
{
    /// <summary>The size a placeholder gets when the file doesn't give one (the class default isn't known).</summary>
    public const int DefaultWidth = 120, DefaultHeight = 40;

    static readonly string[] BoundsKeys = ["left", "top", "width", "height"];

    /// <summary>One member of the node in file order: its raw JSON text, and whether the writer keeps it last.</summary>
    readonly record struct Member(string Key, string Raw, bool IsTail);

    readonly List<Member> _members = [];
    readonly HashSet<string> _unreadable = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _baseline = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _baselineHandlers = new(StringComparer.Ordinal);
    readonly List<string> _containedNames = [];
    string _baselineName = "";

    UnknownControl(string className)
    {
        ClassName = className;
        SetDefaultSize(DefaultWidth, DefaultHeight);
    }

    /// <summary>The class name the form file gives (the one the designer doesn't know).</summary>
    public string ClassName { get; }

    /// <summary>The node's members other than <c>class</c>, in file order, as raw JSON text (as last read).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> RawMembers => [.. _members.Select(m => KeyValuePair.Create(m.Key, m.Raw))];

    /// <summary>The names of the controls inside this node's <c>children</c> (all levels), which stay in the file too.</summary>
    public IReadOnlyList<string> ContainedNames => _containedNames;

    /// <summary>The node as the writer writes it now (a one-node fragment's element).</summary>
    public string ToJson()
    {
        var w = new FormJsonWriter();
        Write(w);
        return w.ToString();
    }

    /// <summary>Builds a placeholder from a node whose class is unknown.</summary>
    internal static UnknownControl Read(JsonElement node, string className)
    {
        var control = new UnknownControl(className);
        var cls = ComponentRegistry.Of(control);
        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Name == "class") continue;
            bool isTail = prop.Name == "children"
                || (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object));
            control._members.Add(new Member(prop.Name, Normalize(prop.Value.GetRawText()), isTail));

            if (prop.Name == "name")
            {
                if (prop.Value.ValueKind == JsonValueKind.String) control.Name = prop.Value.GetString()!;
                else control._unreadable.Add("name");
            }
            else if (prop.Name == "children") CollectNames(prop.Value, control._containedNames);
            else if (cls.FindProperty(prop.Name) is { } p)
            {
                try { p.Read(control, prop.Value); }
                catch (FormatException)
                {
                    // The unknown class may publish this name with another type: keep the text, never rewrite it.
                    control._unreadable.Add(prop.Name);
                }
            }
            else if (cls.FindEvent(prop.Name) != null)
            {
                if (prop.Value.ValueKind == JsonValueKind.String) control.EventHandlers[prop.Name] = prop.Value.GetString()!;
                else control._unreadable.Add(prop.Name);
            }
        }
        control.MarkClean();
        return control;
    }

    /// <summary>Records the current values as the ones the file holds (nothing to rewrite).</summary>
    void MarkClean()
    {
        var cls = ComponentRegistry.Of(this);
        _baseline.Clear();
        foreach (var p in cls.Properties) _baseline[p.Name] = p.FormatValue(this);
        _baselineHandlers.Clear();
        foreach (var e in cls.Events)
            if (EventHandlers.TryGetValue(e.Name, out var h)) _baselineHandlers[e.Name] = h;
        _baselineName = Name;
    }

    static void CollectNames(JsonElement children, List<string> names)
    {
        if (children.ValueKind != JsonValueKind.Array) return;
        foreach (var child in children.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.Object) continue;
            if (child.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name)
                names.Add(name);
            if (child.TryGetProperty("children", out var grandChildren)) CollectNames(grandChildren, names);
        }
    }

    /// <summary>Line endings as the writer writes them (a value can't hold a raw line break inside a string).</summary>
    static string Normalize(string raw) => raw.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// Writes the node: <c>class</c>, then the members in file order, each verbatim unless the placeholder changed it
    /// (then its new value); a changed member the file didn't have goes where a writer would put it (after
    /// <c>class</c> for <c>name</c>, otherwise in ordinal order before the collections and <c>children</c>).
    /// </summary>
    internal void Write(FormJsonWriter w)
    {
        var cls = ComponentRegistry.Of(this);
        // Key → what to write; null removes a member the file had.
        var changed = new SortedDictionary<string, Action?>(StringComparer.Ordinal);

        if (Name != _baselineName && !_unreadable.Contains("name"))
        {
            var name = Name;
            changed["name"] = name.Length == 0 ? null : () => { w.Key("name"); w.WriteString(name); };
        }

        bool alignChanged = _baseline.TryGetValue("align", out var align0) && cls.FindProperty("align") is { } alignProp
            && alignProp.FormatValue(this) != align0;
        foreach (var p in cls.Properties)
        {
            if (_unreadable.Contains(p.Name)) continue;
            if (Array.IndexOf(BoundsKeys, p.Name) >= 0 && !FormWriter.IsSavedBound(this, p.Name, isRoot: false))
            {
                // A bound the layout computes (it is aligned): not saved, and dropped if a new align made it so.
                if (alignChanged) changed[p.Name] = null;
                continue;
            }
            if (_baseline.TryGetValue(p.Name, out var was) && p.FormatValue(this) == was) continue;
            var prop = p;
            changed[p.Name] = () => { w.Key(prop.Name); prop.Write(w, this); };
        }
        foreach (var e in cls.Events)
        {
            if (_unreadable.Contains(e.Name)) continue;
            var now = EventHandlers.GetValueOrDefault(e.Name, "");
            if (now == _baselineHandlers.GetValueOrDefault(e.Name, "")) continue;
            var key = e.Name;
            changed[key] = now.Length == 0 ? null : () => { w.Key(key); w.WriteString(now); };
        }

        // Changed members the file didn't have, in the order they are inserted.
        var present = _members.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
        var inserted = changed.Where(c => c.Value != null && !present.Contains(c.Key)).Select(c => c.Key).ToList();

        w.BeginObject();
        w.Key("class"); w.WriteString(ClassName);
        if (inserted.Remove("name")) changed["name"]!();
        int next = 0;
        foreach (var m in _members)
        {
            if (m.Key is not ("name" or "caption" or "text"))
                while (next < inserted.Count && (m.IsTail || string.CompareOrdinal(m.Key, inserted[next]) > 0))
                    changed[inserted[next++]]!();
            if (changed.TryGetValue(m.Key, out var write))
            {
                write?.Invoke();
                changed.Remove(m.Key);   // a duplicate key in the file is rewritten once
                continue;
            }
            w.Key(m.Key);
            w.WriteRaw(m.Raw);
        }
        while (next < inserted.Count) changed[inserted[next++]]!();
        w.EndObject();
    }

    protected override void Paint(Canvas canvas)
    {
        var r = new Rect(0, 0, Width, Height);
        var theme = canvas.Theme;
        var warn = Color.FromHex("#D97706");
        canvas.FillRect(r, theme.PanelBackground.Blend(warn, 0.12));
        // A dashed outline and a diagonal: clearly not a real control.
        float inset = canvas.Px(1) / 2f, right = Width - inset, bottom = Height - inset;
        float[] dash = [canvas.Px(4), canvas.Px(3)];
        canvas.DrawPolyline([new(inset, inset), new(right, inset), new(right, bottom), new(inset, bottom), new(inset, inset)],
            warn, canvas.Px(1), dash: dash);
        canvas.DrawLine(new(inset, inset), new(right, bottom), warn.Blend(theme.PanelBackground, 0.6), canvas.Px(1));
        var font = EffectiveFont;
        int line = LayoutMath.Round(font.Size * canvas.Scale * 1.4);
        int top = (Height - 2 * line) / 2;
        if (Height >= 2 * line)
        {
            canvas.DrawText(ClassName, font with { Bold = true }, theme.Text, new Rect(0, top, Width, line), TextAlign.Center, VerticalAlign.Center, ellipsis: true);
            canvas.DrawText("unknown class", font, theme.TextMuted, new Rect(0, top + line, Width, line), TextAlign.Center, VerticalAlign.Center, ellipsis: true);
        }
        else canvas.DrawText(ClassName + "?", font with { Bold = true }, theme.Text, r, TextAlign.Center, VerticalAlign.Center, ellipsis: true);
    }
}
