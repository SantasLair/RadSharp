namespace RadSharp.Designer;

/// <summary>
/// A coarse grouping for the component palette (Delphi's Standard/Additional/Dialogs/System tool-palette
/// tabs). <c>ComponentClass</c> has no category metadata of its own the way <c>PropertyMeta.Category</c>
/// does for the property grid — adding one would mean a new attribute across every control class plus a
/// source-generator change, more than this designer-polish item is worth. This is a small, hand-maintained
/// lookup instead; an unlisted class (a new control nobody has categorized yet) falls into "Additional"
/// rather than being hidden or crashing.
/// </summary>
static class PaletteCategories
{
    const string Standard = "Standard", Additional = "Additional", Dialogs = "Dialogs", SystemCategory = "System";

    static readonly Dictionary<string, string> ByName = new(StringComparer.Ordinal)
    {
        ["Button"] = Standard, ["CheckBox"] = Standard, ["Edit"] = Standard, ["GroupBox"] = Standard,
        ["Label"] = Standard, ["Memo"] = Standard, ["RadioButton"] = Standard, ["Panel"] = Standard,
        ["MainMenu"] = Standard, ["PopupMenu"] = Standard,

        ["OpenDialog"] = Dialogs, ["SaveDialog"] = Dialogs, ["FolderDialog"] = Dialogs,

        ["Timer"] = SystemCategory, ["ImageList"] = SystemCategory,
    };

    /// <summary>The palette category for a component class named <paramref name="componentClassName"/>.</summary>
    public static string Of(string componentClassName) => ByName.GetValueOrDefault(componentClassName, Additional);
}
