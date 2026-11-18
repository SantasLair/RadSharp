using Microsoft.CodeAnalysis;

namespace RadSharp.Generators.Tests;

/// <summary>RAD1006: a control named like a member the form inherits is an error, and its field is not generated.</summary>
public partial class GeneratorTests
{
    static string OneControl(string name, string rootClass = "Form") => $$"""
        { "format": "radform", "version": "1.0", "root": { "class": "{{rootClass}}", "children": [
          { "class": "Button", "name": "{{name}}" } ] } }
        """;

    [Theory]
    [InlineData("Monitor", "Form.Monitor")]
    [InlineData("Icon", "Form.Icon")]
    [InlineData("Close", "Form.Close")]
    [InlineData("Caption", "Form.Caption")]
    [InlineData("ToString", "Object.ToString")]
    public void A_control_named_like_an_inherited_form_member_is_error_RAD1006_and_gets_no_field(string name, string member)
    {
        var (gen, compile, code) = Run("namespace App; public partial class Dlg : RadSharp.Form { }", ("Dlg.radform.json", OneControl(name)));
        var d = Assert.Single(gen);
        Assert.Equal("RAD1006", d.Id);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal($"Control name '{name}' hides the inherited member '{member}'; rename the control.", d.GetMessage());
        Assert.DoesNotContain($" {name} = null!;", code);
        Assert.DoesNotContain(compile, c => c.Id == "CS0108");   // the only error the user sees is RAD1006
        Assert.Empty(compile);
        // Located on the name value in the form file.
        Assert.Equal("Dlg.radform.json", d.Location.GetLineSpan().Path);
        Assert.Equal(1, d.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public void An_ordinary_control_name_gets_no_diagnostic_and_a_field()
    {
        var (gen, compile, code) = Run("namespace App; public partial class Dlg : RadSharp.Form { }", ("Dlg.radform.json", OneControl("btnMonitor")));
        Assert.Empty(gen);
        Assert.Empty(compile);
        Assert.Contains("internal global::RadSharp.Button btnMonitor = null!;", code);
    }

    [Fact]
    public void A_member_of_the_users_own_base_class_counts_too()
    {
        const string source = """
            namespace App;
            public class BaseForm : RadSharp.Form
            {
                public int Counter;
                protected void Refresh2() { }
                private int secret;
                internal string Tag2 => "";
            }
            public partial class Dlg : BaseForm { }
            """;
        foreach (var name in new[] { "Counter", "Refresh2", "Tag2" })
        {
            var (gen, compile, _) = Run(source, ("Dlg.radform.json", OneControl(name, "BaseForm")));
            Assert.Equal("RAD1006", Assert.Single(gen).Id);
            Assert.Equal($"Control name '{name}' hides the inherited member 'BaseForm.{name}'; rename the control.", gen[0].GetMessage());
            Assert.Empty(compile);
        }
        // A private member of the base class isn't inherited visibly: no clash.
        var (ok, okCompile, code) = Run(source, ("Dlg.radform.json", OneControl("secret", "BaseForm")));
        Assert.Empty(ok);
        Assert.Empty(okCompile);
        Assert.Contains("secret = null!;", code);
    }

    [Fact]
    public void A_named_menu_item_that_hides_a_member_is_refused_too()
    {
        const string json = """
            { "format": "radform", "version": "1.0", "root": { "class": "Form", "children": [
              { "class": "MainMenu", "name": "mnuMain", "items": [ { "name": "Close", "caption": "Close" } ] } ] } }
            """;
        var (gen, compile, _) = Run("namespace App; public partial class Dlg : RadSharp.Form { }", ("Dlg.radform.json", json));
        Assert.Equal("RAD1006", Assert.Single(gen).Id);
        Assert.Empty(compile);
    }
}
