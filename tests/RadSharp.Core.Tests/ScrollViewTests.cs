using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>ScrollView, the base for custom-drawn scrolling content, and the public ScrollBarDriver.</summary>
public class ScrollViewTests
{
    sealed class Probe : ScrollView
    {
        public Rect? LastVisible;
        public readonly List<string> Log = [];
        protected override void PaintContent(Canvas canvas, Rect visible) => LastVisible = visible;
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (IsOverScrollBar(e.X, e.Y)) return;
            var (cx, cy) = ContentPoint(e.X, e.Y);
            Log.Add($"content {cx},{cy}");
        }
    }

    static (Form Form, Probe View) Setup(int contentW, int contentH, int w = 200, int h = 100)
    {
        var f = new Form();
        f.SetBounds(0, 0, 300, 200);
        var v = new Probe { ContentWidth = contentW, ContentHeight = contentH };
        v.SetBounds(10, 10, w, h);
        f.Add(v);
        f.PerformLayout();
        return (f, v);
    }

    [Fact]
    public void Scroll_bars_appear_only_when_the_content_is_larger_than_the_viewport()
    {
        var (f, v) = Setup(100, 50);
        Assert.False(v.VerticalBarVisible);
        Assert.False(v.HorizontalBarVisible);
        var full = v.Viewport;

        v.ContentHeight = 500;
        f.PerformLayout();
        Assert.True(v.VerticalBarVisible);
        Assert.False(v.HorizontalBarVisible);
        Assert.True(v.Viewport.Width < full.Width);

        v.ContentWidth = 900;
        f.PerformLayout();
        Assert.True(v.HorizontalBarVisible);
        Assert.True(v.Viewport.Height < full.Height);

        v.ContentWidth = 10;
        v.ContentHeight = 10;
        f.PerformLayout();
        Assert.False(v.VerticalBarVisible || v.HorizontalBarVisible);
    }

    [Fact]
    public void The_wheel_scrolls_within_the_content_and_stops_at_its_end()
    {
        var (f, v) = Setup(100, 1000);
        int scrolls = 0;
        v.Scroll += _ => scrolls++;
        f.Simulate.Wheel(50, 50, -1);
        Assert.Equal(48, v.ScrollY);
        Assert.Equal(1, scrolls);

        v.WheelStep = 10;
        f.Simulate.Wheel(50, 50, 1);
        Assert.Equal(38, v.ScrollY);

        for (int i = 0; i < 100; i++) f.Simulate.Wheel(50, 50, -1);
        Assert.Equal(1000 - v.Viewport.Height, v.ScrollY);

        // Without a horizontal bar, Shift+wheel does nothing and isn't handled.
        f.Simulate.Wheel(50, 50, -1);
        Assert.Equal(0, v.ScrollX);
    }

    [Fact]
    public void Dragging_the_thumb_and_clicking_the_track_scroll()
    {
        var (f, v) = Setup(100, 1000);
        int barX = v.AbsoluteLeft + v.Viewport.Width + 4;                 // inside the vertical bar
        int top = v.AbsoluteTop + 2;

        f.Simulate.Press(barX, top + 5);                                   // on the thumb (at the top)
        Assert.True(v.IsScrolling);
        f.Simulate.MoveTo(barX, top + 40);
        Assert.True(v.ScrollY > 0);
        int dragged = v.ScrollY;
        f.Simulate.Release(barX, top + 40);
        Assert.False(v.IsScrolling);

        // A press on the track below the thumb pages down by a viewport.
        f.Simulate.Click(barX, v.AbsoluteTop + v.Height - 5);
        Assert.True(v.ScrollY > dragged);
    }

    [Fact]
    public void Content_is_painted_translated_and_the_visible_rect_follows_the_scroll_position()
    {
        var (f, v) = Setup(600, 1000);
        using (FormRenderer.RenderToBitmap(f)) { }
        var view = v.Viewport;
        Assert.Equal(new Rect(0, 0, view.Width, view.Height), v.LastVisible);

        v.ScrollX = 120;
        v.ScrollY = 300;
        using (FormRenderer.RenderToBitmap(f)) { }
        Assert.Equal(new Rect(120, 300, view.Width, view.Height), v.LastVisible);
    }

    [Fact]
    public void ContentPoint_maps_pointer_positions_and_bars_swallow_their_own_presses()
    {
        var (f, v) = Setup(600, 1000);
        v.ScrollX = 20;
        v.ScrollY = 200;
        var view = v.Viewport;
        f.Simulate.Click(v.AbsoluteLeft + view.X + 5, v.AbsoluteTop + view.Y + 7);
        Assert.Equal(["content 25,207"], v.Log);

        v.Log.Clear();
        f.Simulate.Click(v.AbsoluteLeft + view.Width + 3, v.AbsoluteTop + 20);   // on the vertical bar
        Assert.Empty(v.Log);
    }

    [Fact]
    public void ScrollIntoView_moves_the_least_distance_and_a_shrinking_content_clamps_the_position()
    {
        var (f, v) = Setup(100, 1000);
        int h = v.Viewport.Height;
        v.ScrollIntoView(new Rect(0, 400, 20, 20));
        Assert.Equal(420 - h, v.ScrollY);
        v.ScrollIntoView(new Rect(0, 410, 20, 5));      // already visible
        Assert.Equal(420 - h, v.ScrollY);
        v.ScrollIntoView(new Rect(0, 10, 20, 20));      // above: scroll up to its top
        Assert.Equal(10, v.ScrollY);

        v.ScrollY = 500;
        int scrolls = 0;
        v.Scroll += _ => scrolls++;
        v.ContentHeight = 120;
        f.PerformLayout();
        Assert.Equal(120 - v.Viewport.Height, v.ScrollY);
        Assert.Equal(1, scrolls);
    }

    [Fact]
    public void ScrollBarDriver_can_drive_a_hand_drawn_bar()
    {
        var axis = new ScrollAxis { Max = 1000, PageSize = 100 };
        var driver = new ScrollBarDriver(axis);

        // Track of 200 px: the thumb is 20 px long, at the top. A press on it grabs it; moving drags it.
        Assert.False(driver.Press(5, 200, 10, 100));
        Assert.True(driver.IsDragging);
        Assert.True(driver.Move(105, 200, 10));
        Assert.True(axis.Position > 0);
        driver.Release();
        Assert.False(driver.IsDragging);

        // A press on the track pages by the large change.
        axis.SetPosition(0);
        Assert.True(driver.Press(150, 200, 10, 100));
        Assert.Equal(100, axis.Position);
    }
}
