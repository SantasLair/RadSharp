namespace RadSharp.Generators.Tests;

/// <summary>[Component("Name")] (a form-file class name apart from the C# name) and [Published(References = ...)].</summary>
public partial class GeneratorTests
{
    [Fact]
    public void Class_Action_in_a_form_file_is_the_ActionItem_type()
    {
        const string json = """
            { "format": "radform", "version": "1.0", "root": { "class": "Form", "children": [
              { "class": "Action", "name": "actSave", "caption": "Save", "onExecute": "actSave_Execute", "onUpdate": "actSave_Update" } ] } }
            """;
        const string cs = """
            namespace App;
            public partial class Doc : RadSharp.Form
            {
                void actSave_Execute(RadSharp.Control sender) { }
                void actSave_Update(RadSharp.Control sender) { RadSharp.ActionItem a = actSave; a.Enabled = false; }
            }
            """;
        var (gen, compile, code) = Run(cs, ("Doc.radform.json", json));
        Assert.Empty(gen);
        Assert.Empty(compile);
        Assert.Contains("internal global::RadSharp.ActionItem actSave = null!;", code);
        Assert.Contains("actSave.Executed += actSave_Execute;", code);
    }

    [Fact]
    public void The_CSharp_name_of_a_renamed_component_is_not_a_form_file_class()
    {
        const string json = """{ "format": "radform", "version": "1.0", "root": { "class": "Form", "children": [ { "class": "ActionItem", "name": "a" } ] } }""";
        var (gen, _, code) = Run("namespace App; public partial class Doc2 : RadSharp.Form { }", ("Doc2.radform.json", json));
        Assert.Contains(gen, d => d.Id == "RAD1002");
        Assert.Contains("internal global::RadSharp.Control a = null!;", code);
    }

    const string GadgetCs = """
        using RadSharp;
        namespace Lib;
        [Component("Gadget")]
        public class GadgetControl : Control
        {
            [Published(Category = "Behavior", References = typeof(GadgetControl))] public string Partner { get; set; } = "";
            [Published] public string Plain { get; set; } = "";
        }
        """;

    [Fact]
    public void Component_attribute_renames_the_registry_entry_and_References_is_emitted()
    {
        var (gen, compile, code) = Run(GadgetCs);
        Assert.Empty(gen);
        Assert.Empty(compile);
        Assert.Contains("ComponentClass(\n                \"Gadget\", typeof(global::Lib.GadgetControl)".Replace("\n", Environment.NewLine), code);
        Assert.Contains("\"partner\", global::RadSharp.Converters.String, static o => o.Partner, static (o, v) => o.Partner = v, \"Behavior\", typeof(global::Lib.GadgetControl))", code);
        Assert.Contains("\"plain\", global::RadSharp.Converters.String, static o => o.Plain, static (o, v) => o.Plain = v, null)", code);
    }

    [Fact]
    public void A_form_file_resolves_a_project_class_by_its_component_name()
    {
        const string json = """{ "format": "radform", "version": "1.0", "root": { "class": "Form", "children": [ { "class": "Gadget", "name": "gad" } ] } }""";
        var (gen, compile, code) = Run(GadgetCs + "\npublic partial class Doc3 : RadSharp.Form { }", ("Doc3.radform.json", json));
        Assert.Empty(gen);
        Assert.Empty(compile);
        Assert.Contains("internal global::Lib.GadgetControl gad = null!;", code);
    }

    [Fact]
    public void References_needs_a_string_property()
    {
        const string cs = """
            using RadSharp;
            public class Bad : Control { [Published(References = typeof(Bad))] public int Partner { get; set; } }
            """;
        var (gen, _, _) = Run(cs);
        Assert.Contains(gen, d => d.Id == "RAD0002" && d.GetMessage().Contains("References"));
    }
}
