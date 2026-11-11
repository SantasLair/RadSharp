using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>Gaps found while porting the Biweekly Cash Flow app: Edit appearance/alignment, and a form's window position.</summary>
public class BcfPortFixTests
{
    [Fact]
    public void Form_window_position_is_not_added_to_control_coordinates()
    {
        var f = new Form();
        f.SetBounds(40, 40, 400, 300);        // a window position, e.g. StartPosition = Manual
        int clicks = 0;
        var b = new Button();
        b.SetBounds(10, 10, 80, 24);
        b.Click += _ => clicks++;
        f.Add(b);
        f.PerformLayout();
        Assert.Equal(10, b.AbsoluteLeft);
        Assert.Equal(10, b.AbsoluteTop);
        f.Simulate.Click(b);
        Assert.Equal(1, clicks);
        Assert.Same(b, f.HitTestForm(20, 20));
    }

    [Fact]
    public void Popups_keep_their_own_position()
    {
        var f = new Form();
        f.SetBounds(40, 40, 400, 300);
        f.PerformLayout();
        var p = new Panel();
        p.SetBounds(0, 0, 50, 30);
        f.OpenPopup(p, 100, 120);
        Assert.Equal(100, p.AbsoluteLeft);
        Assert.Equal(120, p.AbsoluteTop);
        Assert.Same(p, f.HitTestForm(110, 130));
    }

    static (Form, Edit) EditOnForm(Edit e)
    {
        var f = new Form();
        f.SetBounds(0, 0, 400, 100);
        e.SetBounds(10, 10, 200, 28);
        f.Add(e);
        f.PerformLayout();
        e.Focus();
        return (f, e);
    }

    [Fact]
    public void Right_aligned_edit_puts_the_caret_and_clicks_at_the_right()
    {
        var (f, e) = EditOnForm(new Edit { Text = "12.50", Alignment = TextAlign.Right });
        var caret = e.CaretRect;              // caret after the last character
        Assert.True(caret.X > e.Width / 2, $"caret at {caret.X}");
        var (_, left) = EditOnForm(new Edit { Text = "12.50" });
        Assert.True(left.CaretRect.X < left.Width / 2);
        // clicking just left of the right-aligned text puts the caret at its start
        Assert.Equal(0, e.IndexAtPoint(e.CaretRect.X - 200, 14));
        Assert.Equal(5, e.IndexAtPoint(e.Width - 2, 14));
        f.Simulate.Click(e.AbsoluteLeft + 15, e.AbsoluteTop + 14);
        Assert.Equal(0, e.CaretPosition);
    }

    [Fact]
    public void Edit_uses_its_font_and_colours()
    {
        var big = new Edit { Text = "8888", Font = new Font("Inter", 26, Bold: true) };
        var (_, _) = EditOnForm(big);
        var small = new Edit { Text = "8888" };
        EditOnForm(small);
        Assert.True(big.CaretRect.X > small.CaretRect.X + 10, "a bigger font makes wider text");
        Assert.True(big.CaretRect.Height > small.CaretRect.Height);

        var e = new Edit { Text = "x", Color = Color.FromHex("#102030"), FontColor = Color.FromHex("#FF0000"), BorderStyle = BorderStyle.None };
        var (f, _) = EditOnForm(e);
        using var bmp = FormRenderer.RenderToBitmap(f);
        var bg = bmp.GetPixel(e.AbsoluteLeft + e.Width - 4, e.AbsoluteTop + 4);
        Assert.Equal((byte)0x10, bg.Red);
        Assert.Equal((byte)0x20, bg.Green);
        Assert.Equal((byte)0x30, bg.Blue);
    }
}
