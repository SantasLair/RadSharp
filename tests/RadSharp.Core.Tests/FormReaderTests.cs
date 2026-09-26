namespace RadSharp.Tests;

public class FormReaderTests
{
    const string Header = "\"format\": \"radform\", \"version\": \"1.0\"";

    [Fact]
    public void Unknown_class_is_skipped_and_siblings_still_load()
    {
        var (form, warnings) = FormReader.Load($$"""
            { {{Header}}, "root": { "class": "Form", "name": "f", "children": [
                { "class": "Gizmo", "name": "g", "children": [ { "class": "Button", "name": "inner" } ] },
                { "class": "Button", "name": "b" } ] } }
            """);
        Assert.Null(form.FindControl("g"));
        Assert.Null(form.FindControl("inner"));
        Assert.NotNull(form.FindControl("b"));
        Assert.Contains(warnings, w => w.Contains("Gizmo"));
    }

    [Fact]
    public void Unknown_property_and_wrong_type_warn_but_load()
    {
        var (form, warnings) = FormReader.Load($$"""
            { {{Header}}, "root": { "class": "Form", "children": [
                { "class": "Button", "name": "b", "colour": "red", "width": "wide", "left": 5 } ] } }
            """);
        var b = form.FindControl("b")!;
        Assert.Equal(5, b.Left);
        Assert.Equal(80, b.Width);
        Assert.Equal(2, warnings.Count);
    }

    [Theory]
    [InlineData("{ \"format\": \"gmforms\", \"version\": \"1.0\", \"root\": { \"class\": \"Form\" } }")]
    [InlineData("{ \"format\": \"radform\", \"version\": \"2.0\", \"root\": { \"class\": \"Form\" } }")]
    [InlineData("{ \"format\": \"radform\", \"version\": 1, \"root\": { \"class\": \"Form\" } }")]
    [InlineData("{ \"format\": \"radform\", \"version\": \"1.0\", \"root\": { \"class\": \"Button\" } }")]
    [InlineData("not json")]
    public void Wrong_format_version_or_root_is_rejected(string json)
    {
        Assert.Throws<FormFileException>(() => FormReader.Load(json));
    }

    [Fact]
    public void Newer_minor_version_loads()
    {
        var (form, _) = FormReader.Load("{ \"format\": \"radform\", \"version\": \"1.7\", \"root\": { \"class\": \"Form\" } }");
        Assert.NotNull(form);
    }

    [Fact]
    public void Event_handler_names_are_kept()
    {
        var (form, warnings) = FormReader.Load($$"""
            { {{Header}}, "root": { "class": "Form", "children": [
                { "class": "Button", "name": "btnSave", "onClick": "btnSave_Click" } ] } }
            """);
        Assert.Empty(warnings);
        Assert.Equal("btnSave_Click", form.FindControl("btnSave")!.EventHandlers["onClick"]);
    }

    [Fact]
    public void Design_size_is_recorded()
    {
        var (form, _) = FormReader.Load($$"""{ {{Header}}, "root": { "class": "Form", "width": 800, "height": 600 } }""");
        form.Resize(1000, 700);
        Assert.Equal((800, 600), (form.DesignWidth, form.DesignHeight));
    }
}
