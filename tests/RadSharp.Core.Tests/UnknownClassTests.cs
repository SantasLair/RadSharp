using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>Keeping nodes of unknown classes as placeholders (<see cref="FormLoadOptions.KeepUnknownClasses"/>).</summary>
public class UnknownClassTests
{
    static readonly FormLoadOptions Keep = new() { KeepUnknownClasses = true };

    static string Vector() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "serialization", "placeholders", "unknown-classes.radform.json"));

    [Fact]
    public void A_file_with_unknown_classes_round_trips_byte_for_byte()
    {
        var text = Vector();
        var (form, warnings) = FormReader.Load(text, Keep);
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("kept as a placeholder", w));
        Assert.Equal(text, FormWriter.Save(form));
        // Again from what was written, and with the text's line endings changed (a checkout with CRLF).
        Assert.Equal(text, FormWriter.Save(FormReader.Load(FormWriter.Save(form), Keep).Form));
        Assert.Equal(text, FormWriter.Save(FormReader.Load(text.Replace("\n", "\r\n"), Keep).Form));
    }

    [Fact]
    public void Without_the_option_an_unknown_class_is_still_skipped()
    {
        var (form, warnings) = FormReader.Load(Vector());
        Assert.Null(form.FindControl("cntTotal"));
        Assert.Null(form.FindControl("chtSales"));
        Assert.NotNull(form.FindControl("btnOk"));
        Assert.Contains(warnings, w => w.Contains("\"TallyCounter\"") && w.Contains("skipped"));
    }

    [Fact]
    public void A_placeholder_takes_its_name_and_layout_from_the_node_and_keeps_the_rest_raw()
    {
        var (form, _) = FormReader.Load(Vector(), Keep);
        var counter = Assert.IsType<UnknownControl>(form.FindControl("cntTotal"));
        Assert.Equal("TallyCounter", counter.ClassName);
        Assert.Equal(new Rect(8, 8, UnknownControl.DefaultWidth, UnknownControl.DefaultHeight), counter.Bounds);
        Assert.True(counter.Font is { Bold: true, Size: 16 });           // a member every control has: applied
        Assert.Contains(counter.RawMembers, m => m.Key == "count" && m.Value == "3");
        Assert.Contains(counter.RawMembers, m => m.Key == "onCountChanged");

        var chart = Assert.IsType<UnknownControl>(form.FindControl("chtSales"));
        Assert.Equal(Align.Client, chart.Align);
        Assert.Equal(new Rect(0, 60, 400, 240), chart.Bounds);           // laid out like any client-aligned control
        Assert.Empty(chart.Children);                                    // children stay in the raw node...
        Assert.Null(form.FindControl("btnZoom"));
        Assert.Equal(["btnZoom"], chart.ContainedNames);                 // ...but their names are known
    }

    [Fact]
    public void Moving_and_resizing_a_placeholder_rewrites_only_its_bounds()
    {
        var text = Vector();
        var (form, _) = FormReader.Load(text, Keep);
        var counter = (UnknownControl)form.FindControl("cntTotal")!;
        counter.SetBounds(20, 8, 150, UnknownControl.DefaultHeight);
        var saved = FormWriter.Save(form);
        // "left" changes in place; "width" (not in the file: the class default was used) goes in key order.
        var expected = text.Replace("""
                        "left": 8,
                        "onCountChanged": "cntTotal_CountChanged",
                        "top": 8
            """, """
                        "left": 20,
                        "onCountChanged": "cntTotal_CountChanged",
                        "top": 8,
                        "width": 150
            """);
        Assert.NotEqual(text, expected);
        Assert.Equal(expected, saved);
        Assert.Equal(saved, FormWriter.Save(FormReader.Load(saved, Keep).Form));

        // A client-aligned placeholder doesn't write the bounds its align computes, even after a resize.
        form.Resize(500, 300);
        form.PerformLayout();
        Assert.Equal(500, form.FindControl("chtSales")!.Width);
        Assert.Equal(expected, FormWriter.Save(form));
    }

    [Fact]
    public void Renaming_a_placeholder_and_its_handlers_rewrites_those_members()
    {
        var (form, _) = FormReader.Load($$"""
            {
              "format": "radform",
              "version": "1.0",
              "root": {
                "class": "Form",
                "children": [
                  { "class": "Gauge", "value": 7 }
                ]
              }
            }
            """, Keep);
        var gauge = (UnknownControl)form.Children[0];
        gauge.Name = "ggSpeed";
        gauge.EventHandlers["onClick"] = "ggSpeed_Click";
        gauge.Align = Align.Top;
        var saved = FormWriter.Save(form);
        Assert.Contains("""
                  {
                    "class": "Gauge",
                    "name": "ggSpeed",
                    "align": "top",
                    "onClick": "ggSpeed_Click",
                    "value": 7
                  }
            """, saved);
        var again = (UnknownControl)FormReader.Load(saved, Keep).Form.FindControl("ggSpeed")!;
        Assert.Equal("ggSpeed_Click", again.EventHandlers["onClick"]);

        // Clearing the handler removes the member again.
        again.EventHandlers.Remove("onClick");
        Assert.DoesNotContain("onClick", FormWriter.Save(again.OwningForm!));
    }

    [Fact]
    public void A_copied_placeholder_is_reindented_where_it_is_pasted()
    {
        var (form, _) = FormReader.Load(Vector(), Keep);
        var chart = form.FindControl("chtSales")!;
        var fragment = FormWriter.SaveFragment([chart]);
        Assert.StartsWith("""
            [
              {
                "class": "ChartView",
                "name": "chtSales",
                "align": "client",
                "series": [
                  {
                    "title": "2026",
            """, fragment);
        var (pasted, warnings) = FormReader.LoadFragment(fragment, Keep);
        Assert.Single(warnings);
        Assert.Equal(fragment, FormWriter.SaveFragment(pasted));

        // Into a deeper container: the raw children follow the new depth.
        var (target, _) = FormReader.Load(Vector(), Keep);
        var panel = target.FindControl("pnlTop")!;
        var copy = pasted[0];
        copy.Name = "chtCopy";
        panel.Add(copy);
        var saved = FormWriter.Save(target);
        Assert.Contains(new string(' ', 16) + "\"name\": \"btnZoom\"", saved);   // 4 levels deeper than in chtSales
        var reloaded = FormReader.Load(saved, Keep).Form;
        var nested = Assert.IsType<UnknownControl>(reloaded.FindControl("chtCopy"));
        Assert.Equal(((UnknownControl)chart).ToJson().Replace("chtSales", "chtCopy"), nested.ToJson());
        Assert.Equal(saved, FormWriter.Save(reloaded));
    }

    [Fact]
    public void A_placeholder_paints_as_a_marked_box()
    {
        var (form, _) = FormReader.Load("""
            { "format": "radform", "version": "1.0", "root": { "class": "Form", "width": 200, "height": 100,
              "children": [ { "class": "Gizmo", "name": "g", "left": 10, "top": 10, "width": 150, "height": 60 } ] } }
            """, Keep);
        using var bmp = FormRenderer.RenderToBitmap(form);
        var background = bmp.GetPixel(5, 5);
        var inside = bmp.GetPixel(100, 22);   // tinted fill, away from the text and the diagonal
        Assert.NotEqual(background, inside);
        // The dashed outline uses the warning colour somewhere along the top edge.
        Assert.Contains(Enumerable.Range(10, 150).Select(x => bmp.GetPixel(x, 10)), p => p.Red > p.Blue + 60);
    }
}
