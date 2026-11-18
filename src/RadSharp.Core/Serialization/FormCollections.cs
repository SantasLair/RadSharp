using System.Text.Json;

namespace RadSharp;

/// <summary>
/// A control with list-valued members saved as their own form-file arrays after its other members (a grid's or list
/// view's <c>"columns"</c>, a tree's <c>"nodes"</c>), and members that depend on them (<c>"sortColumn"</c>).
/// </summary>
internal interface IFormCollections
{
    /// <summary>Members the reader applies after all the others, in this order, wherever they are in the file.</summary>
    IReadOnlyList<string> DeferredMembers { get; }

    /// <summary>Applies one deferred member; <paramref name="here"/> names the control in warnings.</summary>
    void ReadDeferred(string name, JsonElement value, List<string> warnings, string here);

    /// <summary>Writes the collections (nothing for an empty one), after the other members.</summary>
    void WriteCollections(FormJsonWriter w);
}

/// <summary>Shared form-file code for <see cref="IFormCollections"/> controls.</summary>
internal static class FormCollections
{
    /// <summary>Writes <c>"columns": [...]</c> (nothing when there are none), one object per column holding only its non-default values.</summary>
    public static void WriteColumns(FormJsonWriter w, GridColumns columns)
    {
        if (columns.Count == 0) return;
        w.Key("columns");
        w.BeginArray();
        foreach (var column in columns) { w.NextElement(); column.Write(w); }
        w.EndArray();
    }

    /// <summary>
    /// Reads a <c>"columns"</c> array of column objects. With <paramref name="allowSpecs"/> an element may also be a
    /// <c>"Title:width"</c> string, the form a list view's columns were saved in before they became objects.
    /// </summary>
    public static List<GridColumn> ReadColumns(JsonElement json, List<string> warnings, string path, bool allowSpecs)
    {
        var columns = new List<GridColumn>();
        if (json.ValueKind != JsonValueKind.Array) { warnings.Add($"{path}: expected an array; ignored."); return columns; }
        int i = 0;
        foreach (var item in json.EnumerateArray())
        {
            columns.Add(allowSpecs && item.ValueKind == JsonValueKind.String
                ? GridColumn.Parse(item.GetString()!)
                : GridColumn.Read(item, warnings, $"{path}[{i}]"));
            i++;
        }
        return columns;
    }

    /// <summary>Sets a published member from JSON; a bad value becomes a warning and false.</summary>
    public static bool TrySetMember(Control control, string name, JsonElement value, List<string> warnings, string here)
    {
        try
        {
            FormReader.SetProperty(control, name, value);
            return true;
        }
        catch (FormatException e)
        {
            warnings.Add($"{here}.{name}: {e.Message}; ignored.");
            return false;
        }
    }
}
