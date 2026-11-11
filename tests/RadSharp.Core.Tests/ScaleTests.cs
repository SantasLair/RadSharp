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
}
