using RadSharp.RefApp.Recipes;

[assembly: CollectionBehavior(DisableTestParallelization = true)]   // the designer, PlatformHost and DefaultScale are process-wide

namespace RadSharp.RefApp;

/// <summary>
/// The guard of docs/reference-app-scope.md §4.2 step 4: the recipes, run on the real designer in a fresh project, produce
/// exactly the committed refapp/Tally form files. Agents never edit form JSON by hand; to change a form, change its recipe
/// and run with <c>RADSHARP_REFAPP_WRITE=1</c>, which copies the output over (see <see cref="RefApp.WriteBack"/>).
/// Also writes each form's design-time render at 100 % and 150 % to TestResults/refapp.
/// </summary>
public class RebuildReferenceAppTests
{
    [Fact]
    public void Recipes_rebuild_every_committed_form_byte_for_byte()
    {
        // File > New Application refuses a package folder that doesn't exist, and a fresh clone has no artifacts/packages
        // (git-ignored, made by pack). The recipe only writes the path into nuget.config (never restores), so an empty
        // folder at the real path is enough and keeps the committed relative path.
        Directory.CreateDirectory(RefApp.Packages);
        var temp = RefApp.TempDir("radsharp-refapp-");
        var d = DesignerDriver.Start(temp);
        var folder = Path.Combine(RefApp.RecipeLocation, TallyRecipe.AppName);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        var project = TallyRecipe.Build(d, RefApp.RecipeLocation, RefApp.Packages);

        if (RefApp.Write)
            foreach (var line in RefApp.WriteBack(project, RefApp.Tally)) Console.WriteLine("refapp: " + line);
        Assert.True(Directory.Exists(RefApp.Tally), "refapp/Tally doesn't exist yet: run once with RADSHARP_REFAPP_WRITE=1.");

        var produced = RefApp.Files(project.Directory, "*.radform.json");
        Assert.Equal(produced, RefApp.Files(RefApp.Tally, "*.radform.json"));
        foreach (var rel in produced)
        {
            var expected = File.ReadAllBytes(Path.Combine(RefApp.Tally, rel));
            var actual = File.ReadAllBytes(Path.Combine(project.Directory, rel));
            Assert.True(expected.AsSpan().SequenceEqual(actual),
                $"{rel} differs from its recipe's output ({RefApp.FirstDifference(System.Text.Encoding.UTF8.GetString(expected), System.Text.Encoding.UTF8.GetString(actual))}). " +
                "Forms change only through recipes: rerun with RADSHARP_REFAPP_WRITE=1.");
        }

        foreach (var rel in RefApp.DesignerOwned)
            Assert.True(File.ReadAllText(Path.Combine(project.Directory, rel)) == File.ReadAllText(Path.Combine(RefApp.Tally, rel)),
                $"{rel} differs from what File › New Application writes: rerun with RADSHARP_REFAPP_WRITE=1.");

        // Every handler a form names was written into the code-behind by the designer (double-click, the menu editor), and
        // the committed code has a method of that name taking the same parameter types.
        foreach (var rel in produced)
        {
            var className = Path.GetFileName(rel)[..^".radform.json".Length];
            var (form, _) = FormReader.Load(File.ReadAllText(Path.Combine(project.Directory, rel)));
            var designed = RefApp.MethodSignatures(File.ReadAllText(Path.Combine(project.Directory, className + ".cs")), className);
            var committed = RefApp.MethodSignatures(File.ReadAllText(Path.Combine(RefApp.Tally, className + ".cs")), className);
            foreach (var (owner, handler, types) in RefApp.Handlers(form))
            {
                Assert.True(designed.TryGetValue(handler, out var stub) && stub == types,
                    $"{className}: the designer wrote no stub {handler}({types}) for {owner}.");
                Assert.True(committed.TryGetValue(handler, out var code) && code == types,
                    $"{className}.cs has no {handler}({types}) for {owner}: rerun with RADSHARP_REFAPP_WRITE=1 to add the stub.");
            }
        }

        foreach (var rel in produced)
        {
            var text = File.ReadAllText(Path.Combine(project.Directory, rel));
            var name = Path.GetFileName(rel)[..^".radform.json".Length];
            foreach (var (scale, label) in new[] { (1.0, "100%"), (1.5, "150%") })
            {
                var (form, warnings) = FormReader.Load(text, new FormLoadOptions { Scale = scale });
                Assert.Empty(warnings);
                RefApp.Render(form, $"{name}@{label}");
            }
        }
    }
}
