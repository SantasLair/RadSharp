using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>Review of the grid "columns" form-file integration: round trips, malformed input, SortColumn edge cases.</summary>
public class GridColumnsReviewTests
{
    static string Wrap(string members, string cls = "StringGrid") =>
        "{ \"format\": \"radform\", \"version\": \"1.0\", \"root\": { \"class\": \"Form\", \"name\": \"f\", \"width\": 400, \"height\": 300, \"children\": [ { \"class\": \""
        + cls + "\", \"name\": \"g\", " + members + " } ] } }";

    static CustomGrid Load(string members, out IReadOnlyList<string> warnings, string cls = "StringGrid")
    {
        var result = FormReader.Load(Wrap(members, cls));
        warnings = result.Warnings;
        return (CustomGrid)result.Form.FindControl("g")!;
    }

    [Theory]
    [InlineData("StringGrid")]
    [InlineData("DrawGrid")]
    public void Every_field_round_trips_including_escaped_and_non_ascii_titles(string cls)
    {
        var form = new Form { Name = "f" };
        form.SetBounds(0, 0, 400, 300);
        CustomGrid g = cls == "StringGrid" ? new StringGrid { Name = "g" } : new DrawGrid { Name = "g" };
        g.Columns.Add("café 日本 \U0001F600", 11, TextAlign.Center).MinWidth = 5;
        g.Columns.Add("tab\there\nnl \\ \"\u0001 </script>", 0, TextAlign.Right);
        form.Add(g);
        var json = FormWriter.Save(form);
        var back = (CustomGrid)FormReader.Load(json).Form.FindControl("g")!;
        Assert.Equal(g.Columns.Select(c => (c.Title, c.Width, c.MinWidth, c.Alignment)), back.Columns.Select(c => (c.Title, c.Width, c.MinWidth, c.Alignment)));
        Assert.Equal(json, FormWriter.Save(FormReader.Load(json).Form));
    }

    [Theory]
    [InlineData("\"columns\": [ {\"width\": 99999999999} ]")]
    [InlineData("\"columns\": [ {\"width\": 1.5} ]")]
    [InlineData("\"columns\": [ {\"width\": null} ]")]
    [InlineData("\"columns\": [ {\"minWidth\": \"x\"} ]")]
    [InlineData("\"columns\": [ {\"title\": 5} ]")]
    [InlineData("\"columns\": [ {\"title\": null} ]")]
    [InlineData("\"columns\": [ {\"alignment\": \"bogus\"} ]")]
    [InlineData("\"columns\": [ {\"alignment\": 1} ]")]
    [InlineData("\"columns\": [ null, [], \"s\", true ]")]
    [InlineData("\"columns\": null")]
    [InlineData("\"columns\": {}")]
    [InlineData("\"sortColumn\": \"x\"")]
    [InlineData("\"sortColumn\": 99999999999")]
    [InlineData("\"sortColumn\": 1.5")]
    [InlineData("\"sortColumn\": null")]
    public void Malformed_input_warns_and_never_throws(string members)
    {
        var g = Load(members, out var warnings);
        Assert.NotEmpty(warnings);
        Assert.NotNull(g);
    }

    [Fact]
    public void Negative_and_zero_widths_load_as_default()
    {
        var g = Load("\"columns\": [ {\"width\": -5, \"minWidth\": -1}, {\"width\": 0} ]", out var w);
        Assert.Empty(w);
        Assert.Equal([0, 0], g.Columns.Select(c => c.Width));
    }

    [Fact]
    public void Old_files_without_columns_still_load_with_their_col_count()
    {
        var g = Load("\"colCount\": 4, \"sortColumn\": 2", out var w);
        Assert.Empty(w);
        Assert.Equal((4, 2, 0), (g.ColCount, g.SortColumn, g.Columns.Count));
    }
}
