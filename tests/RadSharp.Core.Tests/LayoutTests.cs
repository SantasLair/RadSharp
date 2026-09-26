namespace RadSharp.Tests;

/// <summary>Layout rules from Appendix B that the conformance vectors don't cover yet.</summary>
public class LayoutTests
{
    static (Form Form, Control Child) FormWithAnchored(int formW, int formH, Rect child, Anchors anchors)
    {
        var form = new Form();
        form.SetBounds(0, 0, formW, formH);
        var c = new Panel { Name = "c", Anchors = anchors };
        c.SetBounds(child.X, child.Y, child.Width, child.Height);
        form.Add(c);
        form.PerformLayout();
        return (form, c);
    }

    [Fact]
    public void Proportional_centre_rounds_half_away_from_zero()
    {
        // Centre 0.5 in a 100-wide parent → 1.0 in 200 → x = 1.0 - 0.5 = 0.5, which rounds to 1
        // (banker's rounding would give 0).
        var (form, c) = FormWithAnchored(100, 100, new Rect(0, 0, 1, 1), Anchors.None);
        form.Resize(200, 100);
        Assert.Equal(1, c.Left);
    }

    [Fact]
    public void Zero_size_parent_at_capture_behaves_as_near_anchored()
    {
        var (form, c) = FormWithAnchored(0, 0, new Rect(10, 20, 30, 40), Anchors.None);
        form.Resize(500, 400);
        Assert.Equal(new Rect(10, 20, 30, 40), c.Bounds);
    }

    [Fact]
    public void Moving_from_code_recaptures_anchors()
    {
        var (form, c) = FormWithAnchored(200, 100, new Rect(150, 0, 40, 20), Anchors.Right | Anchors.Top);
        form.Resize(300, 100);
        Assert.Equal(250, c.Left);

        c.Left = 100;  // captured again at parent width 300
        form.Resize(200, 100);
        Assert.Equal(0, c.Left);
    }

    [Fact]
    public void Min_constraint_wins_over_max()
    {
        var form = new Form();
        form.SetBounds(0, 0, 400, 300);
        form.Add(new Panel { Name = "p", Align = Align.Client, MinWidth = 200, MaxWidth = 100 });
        form.PerformLayout();
        Assert.Equal(200, form.FindControl("p")!.Width);
    }

    [Fact]
    public void Top_aligned_child_uses_constrained_height_when_shrinking_the_client_rect()
    {
        var form = new Form();
        form.SetBounds(0, 0, 400, 300);
        var top = new Panel { Align = Align.Top, MinHeight = 50 };
        top.Height = 10;
        var client = new Panel { Align = Align.Client };
        form.Add(top);
        form.Add(client);
        form.PerformLayout();
        Assert.Equal(new Rect(0, 0, 400, 50), top.Bounds);
        Assert.Equal(new Rect(0, 50, 400, 250), client.Bounds);
    }

    [Fact]
    public void Padding_affects_aligned_children_only()
    {
        var form = new Form { Padding = new Thickness(10, 10, 10, 10) };
        form.SetBounds(0, 0, 400, 300);
        var client = new Panel { Align = Align.Client };
        var anchored = new Panel();
        anchored.SetBounds(0, 0, 50, 50);
        form.Add(client);
        form.Add(anchored);
        form.PerformLayout();
        Assert.Equal(new Rect(10, 10, 380, 280), client.Bounds);
        Assert.Equal(new Rect(0, 0, 50, 50), anchored.Bounds);
    }

    [Fact]
    public void Absolute_positions_follow_the_tree()
    {
        var form = new Form();
        form.SetBounds(0, 0, 400, 300);
        var outer = new Panel { Align = Align.Right, Width = 100 };
        var inner = new Button();
        inner.SetBounds(5, 7, 80, 28);
        outer.Add(inner);
        form.Add(outer);
        form.PerformLayout();
        Assert.Equal(305, inner.AbsoluteLeft);
        Assert.Equal(7, inner.AbsoluteTop);
    }
}
