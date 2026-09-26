using System.Runtime.CompilerServices;

namespace RadSharp.Tests;

/// <summary>Deterministic form files (spec, Appendix A and D.5).</summary>
public class SerializationTests
{
    static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "vectors", "serialization");

    public static TheoryData<string> CanonicalFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Dir, "*.radform.json").Order(StringComparer.Ordinal)) data.Add(Path.GetFileName(f));
        return data;
    }

    public static TheoryData<string> InputFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Dir, "*.input.json").Order(StringComparer.Ordinal)) data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(CanonicalFiles))]
    public void Canonical_files_round_trip_byte_for_byte(string file)
    {
        var text = File.ReadAllText(Path.Combine(Dir, file));
        var (form, warnings) = FormReader.Load(text);
        Assert.Empty(warnings);
        Assert.Equal(text, FormWriter.Save(form));
    }

    [Theory]
    [MemberData(nameof(InputFiles))]
    public void Inputs_normalize_to_their_canonical_file(string file)
    {
        var (form, _) = FormReader.Load(File.ReadAllText(Path.Combine(Dir, file)));
        var saved = FormWriter.Save(form);
        var canonicalName = file.Replace(".input.json", ".radform.json");
        UpdateOrCompare(canonicalName, saved);

        // Saving what was saved changes nothing.
        Assert.Equal(saved, FormWriter.Save(FormReader.Load(saved).Form));
    }

    static void UpdateOrCompare(string canonicalName, string saved, [CallerFilePath] string testFile = "")
    {
        if (Environment.GetEnvironmentVariable("RADSHARP_UPDATE_GOLDEN") == "1")
        {
            var specDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "spec", "vectors", "serialization"));
            File.WriteAllText(Path.Combine(specDir, canonicalName), saved);
            return;
        }
        Assert.Equal(File.ReadAllText(Path.Combine(Dir, canonicalName)), saved);
    }

    [Fact]
    public void Anchored_controls_are_saved_at_design_size_even_after_a_resize()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "d2-anchors.json"));
        var formJson = System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("form").GetRawText();
        var form = FormReader.Load(formJson).Form;
        var before = FormWriter.Save(form);

        form.Resize(1400, 900);
        Assert.Equal(new Rect(854, 46, 130, 28), form.FindControl("btnTR")!.Bounds);
        Assert.Equal(before, FormWriter.Save(form));
        Assert.Equal(new Rect(854, 46, 130, 28), form.FindControl("btnTR")!.Bounds);  // size restored afterwards
        Assert.Contains("\"left\": 478", before);
    }

    [Fact]
    public void Forms_loaded_at_another_scale_cannot_be_saved()
    {
        var form = FormReader.Load("""{ "format": "radform", "version": "1.0", "root": { "class": "Form" } }""",
            new FormLoadOptions { Scale = 1.5 }).Form;
        Assert.Throws<InvalidOperationException>(() => FormWriter.Save(form));
    }

    [Theory]
    [InlineData(700.0, "700")]
    [InlineData(-3.0, "-3")]
    [InlineData(0.1, "0.1")]
    [InlineData(1.0 / 3, "0.3333333333333333")]
    [InlineData(1e-7, "0.0000001")]
    public void Reals_use_shortest_round_trip_without_exponent(double value, string expected)
    {
        Assert.Equal(expected, FormJsonWriter.FormatReal(value));
    }

    [Fact]
    public void Metadata_lists_published_properties_with_class_defaults()
    {
        var button = ComponentRegistry.Find("Button")!;
        Assert.True(button.CanCreate);
        Assert.Equal(80, button.FindProperty("width")!.GetValue(button.Prototype));
        Assert.Equal(PropertyType.Enum, button.FindProperty("align")!.Type);
        Assert.Contains("client", button.FindProperty("align")!.Values!);
        Assert.Equal("Control sender", button.FindEvent("onClick")!.Parameters);
        Assert.Equal("Control sender, MouseEventArgs e", button.FindEvent("onMouseDown")!.Parameters);
        Assert.NotNull(button.FindEvent("onFocus"));

        var toggle = ComponentRegistry.Find("CheckBox")!;
        Assert.NotNull(toggle.FindProperty("checked"));   // inherited from the abstract ToggleControl
        Assert.NotNull(toggle.FindEvent("onChange"));
        Assert.False(ComponentRegistry.Find("ToggleControl")!.CanCreate);
    }
}
