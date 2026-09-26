namespace RadSharp.Tests;

/// <summary>Behaviour of individual controls, driven through the simulator.</summary>
public class ControlTests
{
    static Form NewForm(int w = 400, int h = 300)
    {
        var f = new Form();
        f.SetBounds(0, 0, w, h);
        return f;
    }

    static T Put<T>(Control parent, T c, int x, int y, int w, int h) where T : Control
    {
        c.SetBounds(x, y, w, h);
        parent.Add(c);
        return c;
    }

    [Fact]
    public void CheckBox_toggles_on_click_and_space_and_raises_change()
    {
        var f = NewForm();
        var box = Put(f, new CheckBox { Caption = "Wrap" }, 10, 10, 120, 20);
        int changes = 0;
        box.Change += _ => changes++;
        f.Simulate.Click(box);
        Assert.True(box.Checked);
        f.Simulate.Key(Key.Space);
        Assert.False(box.Checked);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void RadioButtons_are_exclusive_within_a_parent()
    {
        var f = NewForm();
        var g1 = Put(f, new GroupBox(), 0, 0, 200, 150);
        var g2 = Put(f, new GroupBox(), 200, 0, 200, 150);
        var a = Put(g1, new RadioButton { Checked = true }, 0, 0, 100, 20);
        var b = Put(g1, new RadioButton(), 0, 30, 100, 20);
        var c = Put(g2, new RadioButton { Checked = true }, 0, 0, 100, 20);
        f.Simulate.Click(b);
        Assert.False(a.Checked);
        Assert.True(b.Checked);
        Assert.True(c.Checked);  // different parent
        f.Simulate.Click(b);
        Assert.True(b.Checked);  // clicking a checked radio keeps it checked
    }

    [Fact]
    public void GroupBox_client_area_starts_below_its_caption()
    {
        var f = NewForm();
        var g = Put(f, new GroupBox { Caption = "Options" }, 0, 0, 200, 150);
        var inner = Put(g, new Panel { Align = Align.Client }, 0, 0, 10, 10);
        f.PerformLayout();
        Assert.True(inner.Top > 20);
        Assert.True(inner.Left > 0 && inner.Left + inner.Width < 200);
    }

    [Fact]
    public void Splitter_can_be_dragged_with_the_mouse()
    {
        var f = NewForm(600, 300);
        var left = Put(f, new Panel { Align = Align.Left }, 0, 0, 200, 0);
        var split = Put(f, new Splitter { MinSize = 40 }, 0, 0, 4, 0);
        Put(f, new Panel { Align = Align.Client }, 0, 0, 0, 0);
        f.PerformLayout();

        f.Simulate.MoveTo(202, 150);
        Assert.Equal(Cursor.SizeWE, f.CurrentCursor);
        f.Simulate.Drag(202, 150, 262, 150);
        Assert.Equal(260, left.Width);
        f.Simulate.Drag(262, 150, 0, 150);
        Assert.Equal(40, left.Width);
        _ = split;
    }

    [Fact]
    public void ScrollBar_pages_drags_and_uses_keys()
    {
        var f = NewForm();
        var bar = Put(f, new ScrollBar { Min = 0, Max = 100, PageSize = 20, LargeChange = 20 }, 0, 0, 200, 14);
        f.Simulate.Click(150, 7);  // on the track after the thumb (thumb is 0..40)
        Assert.Equal(20, bar.Position);
        f.Simulate.Drag(50, 7, 130, 7);  // thumb at 40..80, drag it right by 80 of 160 travel
        Assert.Equal(60, bar.Position);
        bar.Focus();
        f.Simulate.Key(Key.End);
        Assert.Equal(80, bar.Position);  // Max - PageSize
        f.Simulate.Key(Key.Left);
        Assert.Equal(79, bar.Position);
        f.Simulate.Wheel(100, 7, -1);
        Assert.Equal(80, bar.Position);  // 79 + 3 clamps at Max - PageSize
    }

    [Fact]
    public void ScrollBox_shows_bars_when_content_overflows_and_scrolls_its_children()
    {
        var f = NewForm();
        var box = Put(f, new ScrollBox(), 10, 10, 200, 150);
        var far = Put(box, new Button { Name = "far" }, 20, 400, 80, 28);
        f.PerformLayout();

        Assert.True(box.VerticalBarVisible);
        Assert.False(box.HorizontalBarVisible);
        Assert.Equal(10 + 1 + 20, far.AbsoluteLeft);

        f.Simulate.Wheel(50, 50, -1);  // scroll down
        Assert.True(box.ScrollY > 0);
        Assert.Equal(10 + 1 + 400 - box.ScrollY, far.AbsoluteTop);

        box.ScrollY = 0;
        box.ScrollInView(far);
        f.PerformLayout();
        Assert.Equal(far, f.HitTestForm(far.AbsoluteLeft + 5, far.AbsoluteTop + 5));
        Assert.True(far.AbsoluteTop + far.Height <= box.AbsoluteTop + box.Viewport.Y + box.Viewport.Height);
    }

    [Fact]
    public void ScrollBox_children_outside_the_viewport_are_not_hit()
    {
        var f = NewForm();
        var box = Put(f, new ScrollBox(), 0, 0, 200, 150);
        Put(box, new Button(), 0, 400, 80, 28);
        f.PerformLayout();
        // The vertical bar area belongs to the scroll box itself.
        Assert.Same(box, f.HitTestForm(195, 10));
    }
}
