namespace RadSharp.Tests;

/// <summary>Placeholders (<see cref="UnknownControl"/>) whose node limits the control's size.</summary>
public class UnknownControlReviewTests
{
    static readonly FormLoadOptions Keep = new() { KeepUnknownClasses = true };

    static string Wrap(string child) => $$"""
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
        {{child}}
            ]
          }
        }

        """;

    /// <summary>
    /// The file has no width or height because the class's own default is used (say a chart 150 high, with minHeight
    /// 90): the placeholder's stand-in size, kept within the limits by the layout, isn't a change to save.
    /// </summary>
    [Theory]
    [InlineData("\"maxWidth\": 60")]
    [InlineData("\"minHeight\": 90")]
    [InlineData("\"maxHeight\": 20,\n        \"minWidth\": 200")]
    public void A_placeholder_with_size_limits_round_trips_byte_for_byte(string limits)
    {
        var text = Wrap($$"""
                  {
                    "class": "Gizmo",
                    "name": "g",
                    {{limits}}
                  }
            """);
        var (form, _) = FormReader.Load(text, Keep);
        Assert.Equal(text, FormWriter.Save(form));
        form.PerformLayout();
        Assert.Equal(text, FormWriter.Save(form));
    }
    /// <summary>A hand-edited (or merged) node with a key twice: the reader takes the last; a move must not be undone by it.</summary>
    [Fact]
    public void Moving_a_placeholder_whose_node_has_a_duplicate_key_keeps_the_move()
    {
        var text = Wrap("""
                  {
                    "class": "Gizmo",
                    "name": "g",
                    "left": 8,
                    "top": 8,
                    "left": 16
                  }
            """);
        var (form, _) = FormReader.Load(text, Keep);
        var g = form.FindControl("g")!;
        Assert.Equal(16, g.Left);
        Assert.Equal(text, FormWriter.Save(form));   // unchanged: verbatim, duplicate included
        g.Left = 40;
        var saved = FormWriter.Save(form);
        Assert.Equal(40, FormReader.Load(saved, Keep).Form.FindControl("g")!.Left);
    }
}
