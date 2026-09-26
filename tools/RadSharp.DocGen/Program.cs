using System.Text;
using RadSharp;

// The control reference, generated from the same metadata the form reader, writer and inspector use.
var output = args.Length > 0 ? args[0] : Path.Combine("docs", "controls.md");
var control = ComponentRegistry.Find(typeof(Control))!;
var classes = ComponentRegistry.All
    .Where(c => c.Type.Assembly == typeof(Control).Assembly && c.CanCreate && c.Type != typeof(Control))
    .OrderBy(c => c.Name, StringComparer.Ordinal)
    .ToList();

var sb = new StringBuilder();
sb.AppendLine("# Control reference");
sb.AppendLine();
sb.AppendLine("Generated from the component metadata by `tools/RadSharp.DocGen`; do not edit by hand.");
sb.AppendLine("Every control also has the [common properties and events](#common-to-every-control) listed at the end.");
sb.AppendLine("Defaults are the class defaults at 100 % scale; only values that differ are written to form files.");
sb.AppendLine();
foreach (var c in classes)
    sb.AppendLine($"- [{c.Name}](#{c.Name.ToLowerInvariant()}){(typeof(Component).IsAssignableFrom(c.Type) ? " (non-visual)" : "")}");
sb.AppendLine();

void Table(ComponentClass cls, IEnumerable<PropertyMeta> props, IEnumerable<EventMeta> events)
{
    var p = props.ToList();
    if (p.Count > 0)
    {
        sb.AppendLine("| Property | Type | Default | Category |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var m in p)
        {
            var type = m.Type == PropertyType.Enum ? "enum: " + string.Join(", ", m.Values!) : m.Type.ToString().ToLowerInvariant();
            var def = m.FormatValue(cls.Prototype).Replace("\n", "\\n").Replace("|", "\\|");
            sb.AppendLine($"| `{m.Name}` | {type} | {(def.Length == 0 ? "\"\"" : $"`{def}`")} | {m.Category} |");
        }
        sb.AppendLine();
    }
    var e = events.ToList();
    if (e.Count > 0)
    {
        sb.AppendLine("| Event | Handler parameters |");
        sb.AppendLine("|---|---|");
        foreach (var m in e) sb.AppendLine($"| `{m.Name}` | `({m.Parameters})` |");
        sb.AppendLine();
    }
}

var common = control.Properties.Select(p => p.Name).Concat(control.Events.Select(e => e.Name)).ToHashSet();
foreach (var c in classes)
{
    var baseName = c.Base?.Name ?? "";
    sb.AppendLine($"## {c.Name}");
    sb.AppendLine();
    if (baseName.Length > 0 && baseName != "Control") sb.AppendLine($"Inherits from [{baseName}](#{baseName.ToLowerInvariant()}).");
    else if (typeof(Component).IsAssignableFrom(c.Type)) sb.AppendLine("A non-visual component (shown in the designer's component tray).");
    sb.AppendLine();
    // Own and redeclared members; common Control members are listed once at the end.
    Table(c,
        c.Properties.Where(p => !common.Contains(p.Name) || control.FindProperty(p.Name) != p),
        c.Events.Where(e => !common.Contains(e.Name) || control.FindEvent(e.Name) != e));
}
sb.AppendLine("## Common to every control");
sb.AppendLine();
var sample = ComponentRegistry.Find("Panel")!;
Table(sample, control.Properties, control.Events);

File.WriteAllText(output, sb.ToString().Replace("\r\n", "\n"));
Console.WriteLine($"Wrote {output} ({classes.Count} classes)");
