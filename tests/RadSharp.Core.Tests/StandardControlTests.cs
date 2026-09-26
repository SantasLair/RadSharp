using RadSharp.Drawing;

namespace RadSharp.Tests;

public class StandardControlTests
{
    static Form NewForm(int w = 500, int h = 400)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
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
    public void ListBox_selects_by_mouse_keys_and_typing_and_scrolls()
    {
        var f = NewForm();
        var list = Put(f, new ListBox { Items = [.. Enumerable.Range(1, 30).Select(i => $"Item {i}")] }, 10, 10, 150, 122);
        int changes = 0, doubles = 0;
        list.Change += _ => changes++;
        list.DoubleClick += _ => doubles++;
        f.PerformLayout();

        var row2 = list.ItemRect(2);
        f.Simulate.Click(list.AbsoluteLeft + 20, list.AbsoluteTop + row2.Y + 5);
        Assert.Equal(2, list.ItemIndex);
        f.Simulate.DoubleClick(list.AbsoluteLeft + 20, list.AbsoluteTop + row2.Y + 5);
        Assert.Equal(1, doubles);

        f.Simulate.Key(Key.End);
        Assert.Equal(29, list.ItemIndex);
        Assert.True(list.TopOffset > 0);   // scrolled to show the last row
        f.Simulate.Type("i");              // next item starting with "i" wraps to the first
        Assert.Equal(0, list.ItemIndex);
        Assert.Equal(0, list.TopOffset);
        Assert.Equal(3, changes);   // the double click hit the already selected row
    }

    [Fact]
    public void ComboBox_drops_down_a_list_and_takes_the_chosen_item()
    {
        var f = NewForm();
        var combo = Put(f, new ComboBox { Items = ["Small", "Medium", "Large"] }, 10, 10, 150, 26);
        f.Simulate.Click(combo);
        var list = Assert.IsType<ListBox>(f.Popups.Single());
        Assert.Equal(combo.AbsoluteTop + combo.Height, list.Top);
        var row = list.ItemRect(1);
        f.Simulate.MoveTo(list.Left + 20, list.Top + row.Y + 5);
        f.Simulate.Click(list.Left + 20, list.Top + row.Y + 5);
        Assert.Equal("Medium", combo.Text);
        Assert.Empty(f.Popups);
        Assert.False(combo.IsDroppedDown);

        combo.Focus();
        f.Simulate.Key(Key.Down);
        Assert.Equal("Large", combo.Text);
        f.Simulate.Key(Key.Down, KeyModifiers.Alt);
        Assert.True(combo.IsDroppedDown);
        f.Simulate.Key(Key.Up);
        f.Simulate.Key(Key.Enter);
        Assert.Equal("Medium", combo.Text);

        f.Simulate.Key(Key.F4);
        f.Simulate.Key(Key.Escape);
        Assert.False(combo.IsDroppedDown);
    }

    [Fact]
    public void ColorBox_selects_by_colour()
    {
        var box = new ColorBox { Selected = Color.FromHex("#008080") };
        Assert.Equal("Teal", box.Text);
        Assert.Equal(Color.FromHex("#008080"), box.Selected);
    }

    [Fact]
    public void TrackBar_and_SpinEdit_values()
    {
        var f = NewForm();
        var track = Put(f, new TrackBar { Min = 0, Max = 10 }, 10, 10, 216, 32);
        f.PerformLayout();
        f.Simulate.Click(track.AbsoluteLeft + track.OffsetOf(7), track.AbsoluteTop + 16);
        Assert.Equal(7, track.Position);
        f.Simulate.Key(Key.Right);
        f.Simulate.Key(Key.End);
        Assert.Equal(10, track.Position);

        var spin = Put(f, new SpinEdit { MinValue = 0, MaxValue = 20, Value = 5 }, 10, 60, 100, 24);
        spin.Focus();
        f.Simulate.Key(Key.Up);
        Assert.Equal(6, spin.Value);
        f.Simulate.Key("Mod+A");
        f.Simulate.Type("x1a5");              // only digits get in
        Assert.Equal("15", spin.Text);
        f.Simulate.Key(Key.Tab);              // leaving commits
        Assert.Equal(15, spin.Value);
        spin.Value = 99;
        Assert.Equal(20, spin.Value);
    }

    [Fact]
    public void PageControl_shows_one_page_and_switches_by_click_and_ctrl_tab()
    {
        var f = NewForm();
        var pages = Put(f, new PageControl(), 10, 10, 300, 200);
        var p1 = new TabSheet { Caption = "General" };
        var p2 = new TabSheet { Caption = "Advanced" };
        pages.Add(p1);
        pages.Add(p2);
        var edit = Put(p1, new Edit(), 10, 10, 100, 24);
        var box = Put(p2, new CheckBox(), 10, 10, 100, 20);
        f.PerformLayout();

        Assert.True(p1.IsShown);
        Assert.False(p2.IsShown);
        Assert.True(p2.Visible);                        // hidden by the page control, not by Visible
        Assert.Equal([edit], f.GetTabOrderList().Where(c => c is Edit or CheckBox));

        var tab2 = pages.TabRects()[1];
        f.Simulate.Click(pages.AbsoluteLeft + tab2.X + 5, pages.AbsoluteTop + 5);
        Assert.Same(p2, pages.ActivePage);
        Assert.Same(box, f.HitTestForm(box.AbsoluteLeft + 2, box.AbsoluteTop + 2));

        box.Focus();
        f.Simulate.Key(Key.Tab, KeyModifiers.Ctrl);
        Assert.Same(p1, pages.ActivePage);
        Assert.Null(f.FocusedControl);                  // focus doesn't stay on a hidden page
    }

    [Fact]
    public void ToolBar_lines_up_its_children()
    {
        var f = NewForm();
        var bar = Put(f, new ToolBar(), 0, 0, 0, 36);
        var a = Put(bar, new ToolButton { Caption = "A" }, 0, 0, 50, 28);
        Put(bar, new ToolSeparator(), 0, 0, 9, 24);
        var b = Put(bar, new ToolButton { Caption = "B", AllowToggle = true }, 0, 0, 50, 28);
        f.PerformLayout();
        Assert.Equal(new Rect(0, 0, 500, 36), bar.Bounds);
        Assert.Equal(new Rect(4, 4, 50, 28), a.Bounds);
        Assert.Equal(4 + 50 + 2 + 9 + 2, b.Left);
        f.Simulate.Click(b);
        Assert.True(b.Down);
    }

    [Fact]
    public void Image_loads_a_data_uri_and_PaintBox_paints()
    {
        using var bmp = new SkiaSharp.SKBitmap(2, 2);
        bmp.Erase(SkiaSharp.SKColors.Red);
        var uri = Picture.ToDataUri(FormRenderer.EncodePng(bmp));
        var image = new Image { PictureSource = uri };
        Assert.Equal((2, 2), (image.Picture!.Width, image.Picture.Height));

        var f = NewForm(50, 50);
        var box = Put(f, new PaintBox(), 0, 0, 50, 50);
        int painted = 0;
        box.PaintContent += (_, c) => { painted++; c.FillRect(new Rect(0, 0, 10, 10), Color.Black); };
        using var frame = FormRenderer.RenderToBitmap(f);
        Assert.Equal(1, painted);
        Assert.Equal(SkiaSharp.SKColors.Black, frame.GetPixel(5, 5));
    }
}
