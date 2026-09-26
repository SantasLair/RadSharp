namespace RadSharp;

/// <summary>
/// Writes form files deterministically (spec, Appendix A), so save → load → save is byte-identical:
/// only non-default published values, keys in canonical order, bounds by the align rules, and anchored
/// controls at the form's design size.
/// </summary>
public static class FormWriter
{
    /// <summary>The <c>generator</c> value written into form files.</summary>
    public const string Generator = "RADSharp";

    public static string Save(Form form)
    {
        if (form.Scale != 1)
            throw new InvalidOperationException("Only forms loaded at 100 % can be saved; lengths at other scales are rounded.");

        int w = form.Width, h = form.Height;
        int designW = form.DesignWidth > 0 ? form.DesignWidth : w;
        int designH = form.DesignHeight > 0 ? form.DesignHeight : h;
        bool resized = designW != w || designH != h;
        if (resized) form.Resize(designW, designH);  // anchored controls are written at design size
        form.PerformLayout();
        try
        {
            var writer = new FormJsonWriter();
            writer.BeginObject();
            writer.Key("format"); writer.WriteString(FormReader.FormatId);
            writer.Key("version"); writer.WriteString($"{FormReader.MajorVersion}.0");
            writer.Key("generator"); writer.WriteString(Generator);
            writer.Key("root"); WriteNode(writer, form, isRoot: true);
            writer.EndObject();
            return writer + "\n";
        }
        finally
        {
            if (resized) form.Resize(w, h);
        }
    }

    static readonly string[] BoundsKeys = ["left", "top", "width", "height"];

    /// <summary>Which bounds a control saves: aligned controls save only the size their align doesn't compute.</summary>
    static bool IsSavedBound(Control c, string key, bool isRoot)
    {
        if (isRoot) return key is "width" or "height";
        return c.Align switch
        {
            Align.Top or Align.Bottom => key == "height",
            Align.Left or Align.Right => key == "width",
            Align.Client => false,
            _ => true,
        };
    }

    static void WriteNode(FormJsonWriter w, Control control, bool isRoot)
    {
        var cls = isRoot && control is Form { DesignedClass: { } designed } && ComponentRegistry.Find(designed) is { } designedClass
            ? designedClass
            : ComponentRegistry.Of(control);
        var prototype = cls.Prototype;

        // Members other than class/name/caption/text/items/children, in ordinal key order.
        var rest = new SortedDictionary<string, Action>(StringComparer.Ordinal);
        Action? caption = null, text = null;

        foreach (var p in cls.Properties)
        {
            bool isBound = Array.IndexOf(BoundsKeys, p.Name) >= 0;
            if (isBound && (isRoot || !IsSavedBound(control, p.Name, isRoot))) continue;  // root size is written below
            if (p.HasDefaultValue(control, prototype)) continue;
            var prop = p;
            Action write = () => { w.Key(prop.Name); prop.Write(w, control); };
            if (p.Name == "caption") caption = write;
            else if (p.Name == "text") text = write;
            else rest[p.Name] = write;
        }
        if (isRoot)
        {
            // The root always records its design size.
            rest["width"] = () => { w.Key("width"); w.WriteInt(control.Width); };
            rest["height"] = () => { w.Key("height"); w.WriteInt(control.Height); };
        }
        foreach (var (name, handler) in control.EventHandlers)
        {
            if (string.IsNullOrEmpty(handler)) continue;
            rest[name] = () => { w.Key(name); w.WriteString(handler); };
        }

        w.BeginObject();
        w.Key("class"); w.WriteString(cls.Name);
        if (control.Name.Length > 0) { w.Key("name"); w.WriteString(control.Name); }
        caption?.Invoke();
        text?.Invoke();
        foreach (var write in rest.Values) write();
        if (control is IMenuOwner menu) MenuItem.WriteItems(w, menu.Items);
        if (control.Children.Count > 0)
        {
            w.Key("children");
            w.BeginArray();
            foreach (var child in control.Children)
            {
                w.NextElement();
                WriteNode(w, child, isRoot: false);
            }
            w.EndArray();
        }
        w.EndObject();
    }
}
