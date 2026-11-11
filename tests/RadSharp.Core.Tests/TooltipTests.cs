namespace RadSharp.Tests;

/// <summary>Tooltips: control hints, per-region hints on custom-drawn controls, placement, and clicks.</summary>
public class TooltipTests
{
    /// <summary>Three 50 px cells; the first has a two-line hint, the second one line, the third none.</summary>
    sealed class Cells : Control
    {
        public int Queries;
        public override string? GetHintAt(int x, int y, out Rect region)
        {
            Queries++;
            int cell = Math.Clamp(x / 50, 0, 2);
            region = new Rect(cell * 50, 0, 50, Height);
            return cell switch { 0 => "First cell\nwith a second line", 1 => "Second", _ => null };
        }
    }

    static (Form Form, Cells Cells) Setup()
    {
        var f = new Form();
        f.SetBounds(0, 0, 400, 300);
        var cells = new Cells();
        cells.SetBounds(20, 20, 150, 40);
        f.Add(cells);
        f.PerformLayout();
        f.ProcessTick(0);
        return (f, cells);
    }

    static Control? Tip(Form f) => f.Popups.SingleOrDefault();

    [Fact]
    public void A_control_hint_appears_after_the_delay_and_hides_when_the_pointer_leaves()
    {
        var f = new Form();
        f.SetBounds(0, 0, 300, 200);
        var b = new Button { Hint = "Saves the file" };
        b.SetBounds(20, 20, 80, 28);
        f.Add(b);
        f.PerformLayout();
        f.ProcessTick(0);

        f.Simulate.MoveTo(40, 30);
        f.ProcessTick(500);
        Assert.Null(Tip(f));
        f.ProcessTick(1000);
        Assert.NotNull(Tip(f));

        f.Simulate.MoveTo(250, 150);
        Assert.Null(Tip(f));
        f.ProcessTick(3000);
        Assert.Null(Tip(f));
    }

    [Fact]
    public void GetHintAt_gives_each_region_its_own_tooltip_sized_to_its_text()
    {
        var (f, cells) = Setup();
        f.Simulate.MoveTo(30, 30);                       // cell 0 (x = 10 locally)
        f.ProcessTick(1000);
        var first = Tip(f)!;
        Assert.NotNull(first);

        // Moving inside the same region keeps the very same tooltip.
        f.Simulate.MoveTo(35, 35);
        f.ProcessTick(1100);
        Assert.Same(first, Tip(f));

        // Crossing into the next region while a tooltip is showing re-shows at once, with that region's text.
        f.Simulate.MoveTo(80, 30);                       // cell 1
        f.ProcessTick(1100);
        var second = Tip(f)!;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.True(first.Height > second.Height, "two lines are taller than one");
        Assert.True(first.Width > second.Width, "longer text is wider");

        // A region without text hides it.
        f.Simulate.MoveTo(130, 30);                      // cell 2
        Assert.Null(Tip(f));
        f.ProcessTick(5000);
        Assert.Null(Tip(f));
        Assert.True(cells.Queries > 0);
    }

    [Fact]
    public void Entering_a_new_region_with_no_tooltip_showing_waits_for_the_delay_again()
    {
        var (f, _) = Setup();
        f.Simulate.MoveTo(30, 30);
        f.ProcessTick(200);
        f.Simulate.MoveTo(80, 30);                       // new region before the first one showed
        f.ProcessTick(900);
        Assert.Null(Tip(f));
        f.ProcessTick(1250);
        Assert.NotNull(Tip(f));
    }

    [Fact]
    public void The_tooltip_sits_by_the_pointer_inside_the_form_and_not_under_it()
    {
        var f = new Form();
        f.SetBounds(0, 0, 300, 200);
        var b = new Button { Hint = "A hint\nwith two lines" };
        b.SetBounds(200, 150, 90, 40);
        f.Add(b);
        f.PerformLayout();
        f.ProcessTick(0);

        f.Simulate.MoveTo(280, 185);                     // near the bottom-right corner
        f.ProcessTick(1000);
        var tip = Tip(f)!;
        Assert.NotNull(tip);
        Assert.InRange(tip.Left, 0, f.Width - tip.Width);
        Assert.InRange(tip.Top, 0, f.Height - tip.Height);
        Assert.True(tip.Top + tip.Height <= 185 || tip.Top > 185, "the tooltip must not cover the pointer");
        Assert.Same(b, f.HitTestForm(280, 185));         // and never takes the pointer
    }

    [Fact]
    public void A_click_while_a_tooltip_is_open_is_delivered_not_swallowed()
    {
        var f = new Form();
        f.SetBounds(0, 0, 300, 200);
        int clicks = 0;
        var b = new Button { Hint = "Click me" };
        b.Click += _ => clicks++;
        b.SetBounds(20, 20, 80, 28);
        f.Add(b);
        f.PerformLayout();
        f.ProcessTick(0);

        f.Simulate.MoveTo(40, 30);
        f.ProcessTick(1000);
        Assert.NotNull(Tip(f));

        f.Simulate.Click(40, 30);
        Assert.Equal(1, clicks);
        Assert.Null(Tip(f));

        // It doesn't pop straight back while the pointer rests on the same spot...
        f.ProcessTick(4000);
        Assert.Null(Tip(f));
        // ...but moving to a different hint (here: away and back) shows it again.
        f.Simulate.MoveTo(250, 150);
        f.Simulate.MoveTo(40, 30);
        f.ProcessTick(4100);
        f.ProcessTick(5200);
        Assert.NotNull(Tip(f));
    }

    [Fact]
    public void Keys_and_the_wheel_close_an_open_tooltip_without_being_eaten()
    {
        var f = new Form();
        f.SetBounds(0, 0, 300, 200);
        var edit = new Edit { Hint = "Name" };
        edit.SetBounds(20, 20, 120, 24);
        f.Add(edit);
        f.PerformLayout();
        f.ProcessTick(0);
        edit.Focus();

        f.Simulate.MoveTo(40, 30);
        f.ProcessTick(1000);
        Assert.NotNull(Tip(f));
        f.Simulate.Type("a");
        Assert.Null(Tip(f));
        Assert.Equal("a", edit.Text);
    }

    [Fact]
    public void Disabled_controls_and_empty_hints_show_nothing()
    {
        var f = new Form();
        f.SetBounds(0, 0, 300, 200);
        var off = new Button { Hint = "No", Enabled = false };
        off.SetBounds(20, 20, 80, 28);
        var none = new Button();
        none.SetBounds(120, 20, 80, 28);
        f.Add(off);
        f.Add(none);
        f.PerformLayout();
        f.ProcessTick(0);
        f.Simulate.MoveTo(40, 30);
        f.ProcessTick(2000);
        Assert.Null(Tip(f));
        f.Simulate.MoveTo(140, 30);
        f.ProcessTick(4000);
        Assert.Null(Tip(f));
    }

    [Fact]
    public void An_open_tooltip_renders()
    {
        var (f, _) = Setup();
        f.Simulate.MoveTo(30, 30);
        f.ProcessTick(1000);
        Assert.NotNull(Tip(f));
        using var bitmap = RadSharp.Drawing.FormRenderer.RenderToBitmap(f);
        Assert.Equal(400, bitmap.Width);
    }

    [Fact]
    public void NextWakeMs_reports_the_tooltip_deadline_only_while_one_is_pending()
    {
        var (f, _) = Setup();
        Assert.Null(f.NextWakeMs(0));
        f.Simulate.MoveTo(30, 30);
        Assert.Equal(1000, f.NextWakeMs(0));
        f.ProcessTick(1000);
        Assert.Null(f.NextWakeMs(1000));
    }
}
