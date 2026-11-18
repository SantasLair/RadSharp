using RadSharp.Design;

namespace RadSharp.Tests;

/// <summary>Pasting keeps every name in the form file unique, the names inside placeholders' raw children included.</summary>
public class UnknownControlPasteTests
{
    const string FormText = """
        {
          "format": "radform",
          "version": "1.0",
          "generator": "RADSharp",
          "root": {
            "class": "Form",
            "name": "frmMain",
            "height": 320,
            "width": 480,
            "children": [
              {
                "class": "TallyCounter",
                "name": "cntTotal",
                "left": 24,
                "top": 24,
                "children": [
                  {
                    "class": "Button",
                    "name": "btnReset",
                    "caption": "Reset",
                    "children": [
                      {
                        "class": "Label",
                        "name": "lblInner"
                      }
                    ]
                  }
                ]
              },
              {
                "class": "Button",
                "name": "Button1",
                "left": 24,
                "top": 200
              },
              {
                "class": "Button",
                "name": "Button2",
                "left": 120,
                "top": 200
              }
            ]
          }
        }

        """;

    /// <summary>Every <c>"name"</c> of a control node in a saved form (placeholders' raw children included).</summary>
    static List<string> NamesInFile(string json)
    {
        var names = new List<string>();
        void Walk(System.Text.Json.JsonElement node)
        {
            if (node.TryGetProperty("name", out var n)) names.Add(n.GetString()!);
            if (node.TryGetProperty("children", out var children))
                foreach (var c in children.EnumerateArray()) Walk(c);
        }
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Walk(doc.RootElement.GetProperty("root"));
        return names;
    }

    static void AssertUnique(List<string> names) =>
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

    [Fact]
    public void Pasting_a_copy_of_a_placeholder_renames_the_controls_inside_its_raw_children()
    {
        var doc = new DesignDocument();
        doc.Load(FormText);
        doc.Select(doc.Form.FindControl("cntTotal")!);
        var pasted = Assert.Single(doc.Paste(doc.CopySelection(), doc.Form));

        var saved = doc.Save();
        var names = NamesInFile(saved);
        AssertUnique(names);
        var copy = Assert.IsType<UnknownControl>(pasted);
        Assert.Equal(2, copy.ContainedNames.Count);
        Assert.DoesNotContain("btnReset", copy.ContainedNames);
        Assert.DoesNotContain("lblInner", copy.ContainedNames);
        // The renamed copy reloads with the names it was saved with, and the original is untouched.
        var reloaded = FormReader.Load(saved, DesignDocument.LoadOptions).Form;
        Assert.Equal(["btnReset", "lblInner"], ((UnknownControl)reloaded.FindControl("cntTotal")!).ContainedNames);
        Assert.Equal(copy.ContainedNames, ((UnknownControl)reloaded.FindControl(copy.Name)!).ContainedNames);
        Assert.Contains("\"caption\": \"Reset\"", copy.ToJson());   // only the names changed
    }

    [Fact]
    public void A_pasted_control_whose_name_is_taken_inside_a_placeholder_is_renamed()
    {
        var doc = new DesignDocument();
        doc.Load(FormText);
        var fragment = """
            [
              { "class": "Button", "name": "btnReset", "caption": "Other" }
            ]
            """;
        var pasted = Assert.Single(doc.Paste(fragment, doc.Form));
        Assert.NotEqual("btnReset", pasted.Name);
        AssertUnique(NamesInFile(doc.Save()));
    }

    [Fact]
    public void Renaming_a_contained_control_changes_only_that_name_value()
    {
        var (form, _) = FormReader.Load("""
            {
              "format": "radform",
              "version": "1.0",
              "root": {
                "class": "Form",
                "children": [
                  { "class": "ChartView", "name": "cht", "series": [ { "name": "btnA" } ],
                    "children": [ { "class": "Button", "name": "btnA", "caption": "btnA", "children": [ { "class": "Label", "name": "btnA" } ] } ] }
                ]
              }
            }
            """, DesignDocument.LoadOptions);
        var chart = (UnknownControl)form.Children[0];
        Assert.Equal(["btnA", "btnA"], chart.ContainedNames);
        Assert.True(chart.RenameContained("btnA", "btnB"));
        Assert.False(chart.RenameContained("nope", "x"));
        Assert.Equal(["btnB", "btnB"], chart.ContainedNames);
        Assert.Equal(["Button", "Label"], chart.ContainedClassNames);
        var json = chart.ToJson();
        Assert.Contains("\"series\": [ { \"name\": \"btnA\" } ]", json);   // not a control: untouched
        Assert.Contains("\"caption\": \"btnA\"", json);
        Assert.DoesNotContain("\\u0041", json);
    }

    [Fact]
    public void Controls_pasted_together_get_distinct_new_names()
    {
        var doc = new DesignDocument();
        doc.Load(FormText);
        // A panel holding two buttons named like the form's: both clash, and must not get the same new name.
        var fragment = """
            [
              { "class": "Panel", "name": "pnlCopy", "children": [
                { "class": "Button", "name": "Button1" },
                { "class": "Button", "name": "Button2" }
              ] }
            ]
            """;
        doc.Paste(fragment, doc.Form);
        AssertUnique(NamesInFile(doc.Save()));
    }
}
