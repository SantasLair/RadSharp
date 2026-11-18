using RadSharp.Design.Code;

namespace RadSharp.RefApp.Recipes;

/// <summary>
/// Tally, built the way a person builds it: File › New Application, then one recipe per form. Recipes only call
/// <see cref="DesignerDriver"/> verbs, so every form file in refapp/Tally is the designer's own output
/// (RebuildReferenceAppTests checks that byte for byte). The code lives in refapp/Tally's .cs files.
/// </summary>
public static class TallyRecipe
{
    public const string AppName = "Tally";

    /// <summary>Creates the project in <c>location/Tally</c> and designs every form.</summary>
    public static RadProject Build(DesignerDriver d, string location, string? packages)
    {
        var project = d.NewApplication(AppName, location, packages);
        MainFormRecipe.Build(d);
        d.Save();
        InvoiceFormRecipe.Build(d);
        d.Save();
        return project;
    }
}
