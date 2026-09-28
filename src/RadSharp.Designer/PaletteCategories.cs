using RadSharp.Drawing;

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

    /// <summary>A fixed accent per category (Delphi-style tool-palette colour coding), used for the palette's
    /// toolbox icons (<see cref="PaletteIcons"/>). Not theme-derived on purpose: four fixed, distinguishable
    /// hues read as a stable colour code across sessions, the way a real toolbox's tab colours do.</summary>
    static readonly Dictionary<string, Color> Colors = new(StringComparer.Ordinal)
    {
        [Standard] = Color.FromHex("#3B82C4"),
        [Additional] = Color.FromHex("#2F9E6B"),
        [Dialogs] = Color.FromHex("#C97A1E"),
        [SystemCategory] = Color.FromHex("#8B5FBF"),
    };

    public static Color ColorOf(string category) => Colors.GetValueOrDefault(category, Colors[Additional]);
}
