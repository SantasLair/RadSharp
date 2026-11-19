using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>An application's control that builds a child of its own in its constructor (Delphi's subcomponent).</summary>
public class SearchBar : Panel
{
    public readonly Edit Search;

    public SearchBar()
    {
        SetDefaultSize(300, 30);
        Search = new Edit { Name = "edtSearch", Align = Align.Right, Width = 120 };
        Search.Add(new Label { Caption = "deeper" });   // its own children belong to the bar too
        Add(Search);
    }

    [Published(Category = "Appearance")] public string Placeholder { get; set; } = "";
}

/// <summary>A control whose painting needs what only the running application has.</summary>
public class PaintBomb : Control
{
    public string Reason { get; set; } = "no data source";

    protected override void Paint(Canvas canvas) => throw new InvalidOperationException(Reason);
}

/// <summary>
/// Children a control's constructor adds are its own (not saved, not the designer's to select), and a control that
/// throws while it paints at design time is reported once.
/// </summary>
public class OwnChildrenTests
{
    const string FormText = """
        {
          "format": "radform",
          "version": "1.0",
          "root": {
            "class": "Form",
            "name": "frmMain",
            "height": 200,
            "width": 300,
            "children": [
              {
                "class": "SearchBar",
                "name": "bar1",
                "placeholder": "find"
              }
            ]
          }
        }
        """;

    [Fact]
    public void A_controls_constructor_children_are_not_saved_so_reading_the_file_again_does_not_double_them()
    {
        var form = FormReader.Load(FormText).Form;
        var bar = Assert.IsType<SearchBar>(form.FindControl("bar1"));
        Assert.True(bar.Search.IsTransient);
        Assert.True(bar.Search.Children[0].IsTransient);
        Assert.False(bar.IsTransient);

        var saved = FormWriter.Save(form);
        Assert.DoesNotContain("edtSearch", saved);
        var again = FormReader.Load(saved).Form;
        Assert.Single(again.FindControl("bar1")!.Children);
        Assert.Equal(saved, FormWriter.Save(again));
    }

    [Fact]
    public void RadSharps_own_controls_create_no_children_in_their_constructors()
    {
        // So marking constructor children changes nothing for them: it is for an application's composite controls.
        var withChildren = ComponentRegistry.All
            .Where(c => c.CanCreate && c.Type.Assembly == typeof(Control).Assembly)
            .Where(c => c.Create().Children.Count > 0)
            .Select(c => c.Name).Order().ToList();
        Assert.Empty(withChildren);
    }

    [Fact]
    public void Children_added_after_creation_are_saved_as_usual()
    {
        var bar = (SearchBar)ComponentRegistry.Find("SearchBar")!.Create();
        var button = new Button { Name = "btnGo", Caption = "Go" };
        bar.Add(button);
        Assert.False(button.IsTransient);
        var form = new Form { Name = "frmMain" };
        bar.Name = "bar1";
        form.Add(bar);
        var saved = FormWriter.Save(form);
        Assert.Contains("btnGo", saved);
        Assert.DoesNotContain("edtSearch", saved);
    }

    [Fact]
    public void A_design_time_paint_error_is_drawn_as_a_box_and_reported_once_per_message()
    {
        Form form;
        PaintBomb bomb;
        using (Control.EnterDesignMode())
        {
            form = new Form { Name = "frmMain" };
            form.SetBounds(0, 0, 200, 100);
            bomb = new PaintBomb { Name = "bomb1" };
            bomb.SetBounds(0, 0, 100, 40);
            form.Add(bomb);
        }
        var reports = new List<string>();
        void OnError(Control c, Exception e) => reports.Add($"{c.Name}: {e.Message}");
        FormRenderer.PaintError += OnError;
        try
        {
            using (FormRenderer.RenderToBitmap(form)) { }
            using (var again = FormRenderer.RenderToBitmap(form))
                Assert.Equal(new SkiaSharp.SKColor(255, 236, 236), again.GetPixel(50, 30));   // the error box
            Assert.Equal(["bomb1: no data source"], reports);
            bomb.Reason = "still broken";
            using (FormRenderer.RenderToBitmap(form)) { }
            Assert.Equal(["bomb1: no data source", "bomb1: still broken"], reports);
        }
        finally { FormRenderer.PaintError -= OnError; }

        // In the running application the exception isn't swallowed.
        var running = new Form { Name = "frmRun" };
        running.SetBounds(0, 0, 200, 100);
        running.Add(new PaintBomb { Width = 50, Height = 20 });
        Assert.Throws<InvalidOperationException>(() => FormRenderer.RenderToBitmap(running).Dispose());
    }
}
