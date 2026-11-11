using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RadSharp.Design.Code;

/// <summary>The project properties the IDE edits (Project › Properties). Null/empty means "not set in the .csproj".</summary>
public sealed class ProjectProperties
{
    public string? AssemblyName { get; set; }
    public string? Version { get; set; }
    public string? ApplicationIcon { get; set; }
    public string? RootNamespace { get; set; }
    /// <summary>Semicolon-separated runtime identifiers (<c>win-x64;linux-x64</c>).</summary>
    public string? RuntimeIdentifiers { get; set; }
    /// <summary>True/false when <c>PublishAot</c> is set, null when it isn't.</summary>
    public bool? PublishAot { get; set; }
}

/// <summary>
/// Reads and edits a <c>.csproj</c> as XML, keeping what it doesn't touch: unknown elements, comments, conditions
/// and indentation survive (it loads with <see cref="LoadOptions.PreserveWhitespace"/>), and a file with no changes is
/// never rewritten. Known limits of <see cref="XDocument"/>: an empty element written <c>&lt;X/&gt;</c> is saved as
/// <c>&lt;X /&gt;</c> and single-quoted attributes as double-quoted, and only when something was actually changed.
/// Properties are read from and written to the first <c>PropertyGroup</c> without a <c>Condition</c>.
/// </summary>
public sealed class CsprojEditor
{
    readonly XDocument _doc;
    readonly string _original;
    readonly string _newline;
    readonly bool _bom;
    bool _changed;

    CsprojEditor(string xml, bool bom)
    {
        _original = xml;
        _bom = bom;
        _newline = xml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        _doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
    }

    public string? Path { get; private set; }

    public static CsprojEditor Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var editor = new CsprojEditor(Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom) { Path = path };
        return editor;
    }

    public static CsprojEditor Parse(string xml) => new(xml, false);

    public bool Changed => _changed;

    XElement Root => _doc.Root ?? throw new InvalidDataException("The project file is empty.");

    IEnumerable<XElement> PropertyGroups => Root.Elements().Where(e => e.Name.LocalName == "PropertyGroup");

    XElement? DefaultGroup => PropertyGroups.FirstOrDefault(g => g.Attribute("Condition") == null);

    /// <summary>The value of a property in an unconditional PropertyGroup, or null.</summary>
    public string? Get(string name)
    {
        foreach (var g in PropertyGroups.Where(g => g.Attribute("Condition") == null))
            if (g.Elements().FirstOrDefault(e => e.Name.LocalName == name && e.Attribute("Condition") == null) is { } e)
                return e.Value.Trim();
        return null;
    }

    /// <summary>Sets a property (adding it to the first unconditional PropertyGroup); null or empty removes it.</summary>
    public void Set(string name, string? value)
    {
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var existing = PropertyGroups.Where(g => g.Attribute("Condition") == null)
            .SelectMany(g => g.Elements()).FirstOrDefault(e => e.Name.LocalName == name && e.Attribute("Condition") == null);
        if (existing != null)
        {
            if (value == null) { RemoveWithIndent(existing); _changed = true; }
            else if (existing.Value != value) { existing.Value = value; _changed = true; }
            return;
        }
        if (value == null) return;
        var group = DefaultGroup ?? AddGroup();
        var ns = Root.Name.Namespace;
        var element = new XElement(ns + name, value);
        var last = group.Elements().LastOrDefault();
        if (last != null)
        {
            // Copy the indentation of the previous sibling.
            var indent = (last.PreviousNode as XText)?.Value ?? _newline + "    ";
            last.AddAfterSelf(new XText(indent.Contains('\n') ? indent : _newline + indent), element);
            // AddAfterSelf(text, element) puts the text between: [last][text][element]
        }
        else
        {
            var groupIndent = (group.PreviousNode as XText)?.Value is { } t ? t[(t.LastIndexOf('\n') + 1)..] : "  ";
            group.Add(new XText(_newline + groupIndent + "  "), element, new XText(_newline + groupIndent));
        }
        _changed = true;
    }

    XElement AddGroup()
    {
        var group = new XElement(Root.Name.Namespace + "PropertyGroup");
        var first = Root.Elements().FirstOrDefault();
        if (first != null) first.AddBeforeSelf(group, new XText(_newline + _newline + "  "));
        else Root.Add(new XText(_newline + "  "), group, new XText(_newline));
        return group;
    }

    static void RemoveWithIndent(XElement e)
    {
        if (e.PreviousNode is XText t && t.Value.Trim().Length == 0) t.Remove();
        e.Remove();
    }

    public ProjectProperties Read() => new()
    {
        AssemblyName = Get("AssemblyName"),
        Version = Get("Version"),
        ApplicationIcon = Get("ApplicationIcon"),
        RootNamespace = Get("RootNamespace"),
        RuntimeIdentifiers = Get("RuntimeIdentifiers"),
        PublishAot = Get("PublishAot") is { } v ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : null,
    };

    public void Apply(ProjectProperties p)
    {
        Set("AssemblyName", p.AssemblyName);
        Set("Version", p.Version);
        Set("ApplicationIcon", p.ApplicationIcon);
        Set("RootNamespace", p.RootNamespace);
        Set("RuntimeIdentifiers", p.RuntimeIdentifiers);
        Set("PublishAot", p.PublishAot is { } b ? (b ? "true" : "false") : null);
    }

    /// <summary>The project file's text: the original, untouched, if nothing changed.</summary>
    public string ToXml()
    {
        if (!_changed) return _original;
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = _doc.Declaration == null,
            Indent = false,
            NewLineChars = _newline,
            NewLineHandling = NewLineHandling.Replace,
            Encoding = new UTF8Encoding(false),
        };
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings)) _doc.Save(writer);
        var text = Encoding.UTF8.GetString(ms.ToArray());
        if (_original.EndsWith('\n') && !text.EndsWith('\n')) text += _newline;
        return text;
    }

    public void Save(string? path = null)
    {
        path ??= Path ?? throw new InvalidOperationException("No path.");
        if (!_changed && path == Path) return;
        File.WriteAllBytes(path, [.. (_bom ? new byte[] { 0xEF, 0xBB, 0xBF } : []), .. new UTF8Encoding(false).GetBytes(ToXml())]);
        Path = path;
    }
}
