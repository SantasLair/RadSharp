using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>Scaling code-built forms: <see cref="Form.ApplyScale"/>, <see cref="Control.ScaleLengths"/>, <see cref="Control.Px"/>.</summary>
public class ScaleTests
{
    [Fact]
    public void ApplyScale_scales_the_whole_tree_once_like_a_loaded_form()
    {
        var f = new Form { MinWidth = 100 };
        f.SetBounds(0, 0, 400, 300);
        var panel = new Panel { Margin = new Thickness(2, 4, 6, 8), Padding = new Thickness(5, 5, 5, 5) };
        panel.SetBounds(10, 20, 200, 100);
        var button = new Button { MinWidth = 40 };
        button.SetBounds(7, 9, 80, 28);
        panel.Add(button);
        f.Add(panel);
        var splitter = new Splitter { MinSize = 30 };
        f.Add(splitter);

        f.ApplyScale(1.5);

        Assert.Equal(1.5, f.Scale);
        Assert.Equal((400, 300), (f.DesignWidth, f.DesignHeight));
        Assert.Equal(new Rect(0, 0, 600, 450), f.Bounds);
        Assert.Equal(150, f.MinWidth);
        Assert.Equal(new Rect(15, 30, 300, 150), panel.Bounds);
        Assert.Equal(new Thickness(3, 6, 9, 12), panel.Margin);
        Assert.Equal(new Rect(11, 14, 120, 42), button.Bounds);   // 10.5 rounds away from zero
        Assert.Equal(60, button.MinWidth);
        Assert.Equal(45, splitter.MinSize);
        Assert.Equal(2, button.Px(1));   // Px follows the form's scale: 1.5 rounds to 2

        Assert.Throws<InvalidOperationException>(() => f.ApplyScale(1.5));
    }

    [Fact]
    public void ApplyScale_without_an_argument_uses_the_default_scale_and_a_scaled_form_renders()
    {
        // Form.DefaultScale is process-wide state other tests read, so don't change it here.
        var f = new Form();
        f.SetBounds(0, 0, 100, 60);
        var b = new Button { Caption = "OK" };
        b.SetBounds(10, 10, 50, 20);
        f.Add(b);
        f.ApplyScale();
        Assert.Equal(Form.DefaultScale, f.Scale);

        var g = new Form();
        g.SetBounds(0, 0, 100, 60);
        var c = new Button { Caption = "OK" };
        c.SetBounds(10, 10, 50, 20);
        g.Add(c);
        g.ApplyScale(2);
        Assert.Equal(new Rect(20, 20, 100, 40), c.Bounds);
        using var bitmap = FormRenderer.RenderToBitmap(g);
        Assert.Equal((200, 120), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public void ApplyScale_rejects_a_non_positive_scale()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Form().ApplyScale(-1));
    }

    [Fact]
    public void ScaleLengths_scales_a_subtree_built_after_the_form_was_scaled()
    {
        var f = new Form();
        f.SetBounds(0, 0, 100, 100);
        f.ApplyScale(2);
        var panel = new Panel();
        panel.SetBounds(5, 5, 20, 20);
        panel.ScaleLengths(f.Scale);
        f.Add(panel);
        Assert.Equal(new Rect(10, 10, 40, 40), panel.Bounds);
    }
    const string DesignedDialog = """
        { "format": "radform", "version": "1.0", "root": { "class": "Form", "name": "Dlg", "width": 200, "height": 100,
          "children": [
            { "class": "Panel", "name": "pnlFoot", "align": "bottom", "height": 40,
              "children": [ { "class": "Button", "name": "btnOk", "anchors": ["top", "right"], "left": 110, "top": 6, "width": 80, "height": 28 } ] },
            { "class": "Memo", "name": "memo", "align": "client", "margin": { "l": 8, "t": 8, "r": 8, "b": 8 } }
          ] } }
        """;

    [Fact]
    public void ChangeScale_rescales_a_loaded_form_to_its_owners_scale()
    {
        var f = new Form();
        FormReader.LoadInto(f, DesignedDialog, new FormLoadOptions { Scale = 1 });
        Assert.True(f.IsScaled);
        Assert.Throws<InvalidOperationException>(() => f.ApplyScale(2));   // why ChangeScale exists

        f.ChangeScale(2);

        Assert.Equal(2, f.Scale);
        Assert.Equal((200, 100), (f.DesignWidth, f.DesignHeight));
        Assert.Equal(new Rect(0, 0, 400, 200), f.Bounds);
        var foot = (Panel)f.FindControl("pnlFoot")!;
        var ok = (Button)f.FindControl("btnOk")!;
        Assert.Equal(new Rect(0, 120, 400, 80), foot.Bounds);
        Assert.Equal(new Rect(220, 12, 160, 56), ok.Bounds);              // the anchors re-captured at the new scale
        Assert.Equal(new Rect(16, 16, 368, 88), f.FindControl("memo")!.Bounds);
        // the same as loading at 2 in the first place
        var g = new Form();
        FormReader.LoadInto(g, DesignedDialog, new FormLoadOptions { Scale = 2 });
        Assert.Equal(g.FindControl("btnOk")!.Bounds, ok.Bounds);
        Assert.Equal(g.FindControl("memo")!.Bounds, f.FindControl("memo")!.Bounds);
    }

    [Fact]
    public void ChangeScale_to_the_same_scale_does_nothing_and_on_a_code_built_form_acts_as_ApplyScale()
    {
        var f = new Form();
        FormReader.LoadInto(f, DesignedDialog, new FormLoadOptions { Scale = 1.5 });
        var before = f.FindControl("btnOk")!.Bounds;
        f.ChangeScale(1.5);
        Assert.Equal(before, f.FindControl("btnOk")!.Bounds);

        var g = new Form();
        g.SetBounds(0, 0, 100, 60);
        Assert.False(g.IsScaled);
        g.ChangeScale(2);
        Assert.True(g.IsScaled);
        Assert.Equal(new Rect(0, 0, 200, 120), g.Bounds);
        Assert.Throws<ArgumentOutOfRangeException>(() => g.ChangeScale(-1));
    }
}
