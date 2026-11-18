using System.Diagnostics;

namespace RadSharp.Tests;

/// <summary>ListBox.Style = Variable: per-item heights from MeasureItem, with scrolling, hit testing and paging staying correct.</summary>
public class ListBoxVariableHeightTests
{
    /// <summary>Items 0..39; item i is (i % 3 == 0 ? 40 : 20) tall, in a 100 px viewport (122 px control).</summary>
    static (Form Form, ListBox List) Setup(double scale = 1, bool variable = true)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form { Scale = scale };
        f.SetBounds(0, 0, 500, 400);
        var list = new ListBox { Items = [.. Enumerable.Range(0, 40).Select(i => $"Item {i}")], ItemHeight = 20 };
        if (variable)
        {
            list.MeasureItem += (_, e) => e.Height = e.Index % 3 == 0 ? 40 : 20;
            list.Style = ListBoxStyle.Variable;
        }
        list.SetBounds(10, 10, 150, 102);
        f.Add(list);
        f.PerformLayout();
        return (f, list);
    }

    static int TopOf(int i, double scale = 1)
    {
        int top = 0;
        for (int k = 0; k < i; k++) top += (int)Math.Round((k % 3 == 0 ? 40 : 20) * scale);
        return top;
    }

    [Fact]
    public void Row_rectangles_and_hit_testing_follow_the_measured_heights()
    {
        var (_, list) = Setup();
        var view = list.Viewport;
        Assert.Equal(new Rect(view.X, view.Y, view.Width, 40), list.ItemRect(0));
        Assert.Equal(new Rect(view.X, view.Y + 40, view.Width, 20), list.ItemRect(1));
        Assert.Equal(view.Y + 80, list.ItemRect(3).Y);
        Assert.Equal(0, list.IndexAt(view.X + 5, view.Y + 39));
        Assert.Equal(1, list.IndexAt(view.X + 5, view.Y + 40));
        Assert.Equal(2, list.IndexAt(view.X + 5, view.Y + 79));
        Assert.Equal(3, list.IndexAt(view.X + 5, view.Y + 80));
        Assert.Equal(-1, list.IndexAt(view.X - 1, view.Y + 5));
        // The content is the sum of the rows: 14 rows of 40 and 26 of 20.
        Assert.True(list.VerticalBarVisible);
        list.ScrollY = int.MaxValue;
        Assert.Equal(14 * 40 + 26 * 20 - view.Height, list.ScrollY);
    }

    [Fact]
    public void Clicking_a_tall_row_selects_it_and_the_wheel_scrolls_by_the_default_row()
    {
        var (f, list) = Setup();
        f.Simulate.Click(list.AbsoluteLeft + 20, list.AbsoluteTop + list.ItemRect(3).Y + 10);   // inside row 3 (40 px, from 80)
        Assert.Equal(3, list.ItemIndex);
        f.Simulate.Wheel(list.AbsoluteLeft + 20, list.AbsoluteTop + 20, -1);
        Assert.Equal(20 + 3 * 20, list.ScrollY);   // the click scrolled row 3 (80..120) into the 100 px viewport, then three default rows
        Assert.Equal(list.IndexAt(list.Viewport.X, list.Viewport.Y), list.TopIndex);
        list.TopIndex = 7;
        Assert.Equal(TopOf(7), list.ScrollY);
        Assert.Equal(7, list.TopIndex);
    }

    [Fact]
    public void ScrollIntoView_and_keyboard_keep_the_current_row_fully_visible()
    {
        var (f, list) = Setup();
        list.Focus();
        list.ItemIndex = 0;
        f.Simulate.Key(Key.Down);   // row 1: 40..60, already in view
        Assert.Equal(0, list.ScrollY);
        for (int i = 0; i < 4; i++) f.Simulate.Key(Key.Down);   // row 5: 120..140 -> bottom edge at the viewport's
        Assert.Equal(5, list.ItemIndex);
        int h = list.Viewport.Height;
        Assert.Equal(TopOf(6) - h, list.ScrollY);
        f.Simulate.Key(Key.End);
        Assert.Equal(39, list.ItemIndex);
        Assert.Equal(list.ScrollY + h, TopOf(40));   // the last row ends at the viewport's bottom
        f.Simulate.Key(Key.Home);
        Assert.Equal(0, list.ScrollY);

        list.ScrollIntoView(20);
        Assert.InRange(TopOf(20) - list.ScrollY, 0, h - 20);
    }

    [Fact]
    public void Paging_moves_about_a_viewport_and_always_by_at_least_one_row()
    {
        var (f, list) = Setup();
        list.Focus();
        list.ItemIndex = 0;
        int h = list.Viewport.Height;
        f.Simulate.Key(Key.PageDown);
        int first = list.ItemIndex;
        Assert.InRange(first, 1, 6);
        Assert.InRange(TopOf(first), h - 40 - 20, h);                // about one viewport further
        f.Simulate.Key(Key.PageDown);
        Assert.True(list.ItemIndex > first);
        f.Simulate.Key(Key.PageUp);
        Assert.Equal(first, list.ItemIndex);
        for (int i = 0; i < 40; i++) f.Simulate.Key(Key.PageDown);
        Assert.Equal(39, list.ItemIndex);
        for (int i = 0; i < 40; i++) f.Simulate.Key(Key.PageUp);
        Assert.Equal(0, list.ItemIndex);
    }

    [Fact]
    public void Selection_by_shift_click_and_drag_uses_the_variable_rows()
    {
        var (f, list) = Setup();
        list.MultiSelect = true;
        f.Simulate.Click(list.AbsoluteLeft + 20, list.AbsoluteTop + list.ItemRect(1).Y + 5);
        f.Simulate.Click(list.AbsoluteLeft + 20, list.AbsoluteTop + list.ItemRect(3).Y + 10, MouseButton.Left, KeyModifiers.Shift);
        Assert.Equal([1, 2, 3], list.SelectedIndices);
    }

    [Fact]
    public void Heights_follow_the_ui_scale()
    {
        var (_, list) = Setup(scale: 1.5);
        var view = list.Viewport;
        Assert.Equal(60, list.ItemRect(0).Height);
        Assert.Equal(30, list.ItemRect(1).Height);
        Assert.Equal(view.Y + TopOf(3, 1.5), list.ItemRect(3).Y);
        Assert.Equal(2, list.IndexAt(view.X + 4, view.Y + TopOf(2, 1.5) + 5));
        list.ScrollY = int.MaxValue;
        Assert.Equal(TopOf(40, 1.5) - view.Height, list.ScrollY);
    }

    [Fact]
    public void Editing_items_and_changing_heights_rebuilds_the_table()
    {
        var (_, list) = Setup();
        int total = TopOf(40);
        list.InsertItem(0, "new");                       // becomes row 0 (index % 3 == 0: 40 tall); the rest move down
        list.ScrollY = int.MaxValue;
        Assert.Equal(TopOf(41) - list.Viewport.Height, list.ScrollY);   // 41 rows now, heights by index
        list.DeleteItem(0);
        list.ScrollY = int.MaxValue;
        Assert.Equal(total - list.Viewport.Height, list.ScrollY);

        int calls = 0;
        list.MeasureItem += (_, _) => calls++;           // adding a handler asks again, once per row
        list.ScrollY = 0;
        _ = list.ItemRect(10);
        Assert.Equal(40, calls);
        _ = list.ItemRect(11);
        Assert.Equal(40, calls);                         // cached
        list.InvalidateItemHeights();
        _ = list.ItemRect(11);
        Assert.Equal(80, calls);
    }

    [Fact]
    public void Switching_back_to_fixed_restores_uniform_rows()
    {
        var (_, list) = Setup();
        list.Style = ListBoxStyle.Fixed;
        Assert.Equal(20, list.ItemRect(0).Height);
        Assert.Equal(20, list.ItemRect(3).Y - list.ItemRect(2).Y);
    }

    [Fact]
    public void A_handler_that_asks_for_geometry_does_not_recurse_forever()
    {
        var (_, list) = Setup(variable: false);
        list.MeasureItem += (s, e) => { _ = ((ListBox)s).ItemRect(0); e.Height = 25; };
        list.Style = ListBoxStyle.Variable;
        Assert.Equal(25, list.ItemRect(5).Height);
    }

    [Fact]
    public void Heights_below_one_count_as_one()
    {
        var (_, list) = Setup(variable: false);
        list.MeasureItem += (_, e) => e.Height = -5;
        list.Style = ListBoxStyle.Variable;
        Assert.Equal(1, list.ItemRect(0).Height);
    }

    [Fact]
    public void A_fixed_list_of_100000_items_stays_arithmetic()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form();
        f.SetBounds(0, 0, 500, 400);
        int calls = 0;
        var list = new ListBox { Items = [.. Enumerable.Range(0, 100_000).Select(i => "Row " + i)] };
        list.MeasureItem += (_, _) => calls++;   // never asked while the style is Fixed
        list.SetBounds(10, 10, 200, 200);
        f.Add(list);
        f.PerformLayout();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 2000; i++)
        {
            list.ScrollY = i * 37;
            _ = list.IndexAt(20, 50);
            _ = list.ItemRect(i);
            list.ScrollIntoView(i * 7 % 100_000);
        }
        sw.Stop();
        Assert.Equal(0, calls);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms");
        list.Style = ListBoxStyle.Variable;       // the variable table is built once, then lookups are binary searches
        Assert.Equal(100_000, calls);
        calls = 0;
        for (int i = 0; i < 2000; i++) { list.ScrollY = i * 37; _ = list.IndexAt(20, 50); }
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Style_and_MeasureItem_are_published_and_round_trip()
    {
        var cls = ComponentRegistry.Find("ListBox")!;
        var style = cls.FindProperty("style")!;
        Assert.Equal(PropertyType.Enum, style.Type);
        Assert.Equal(["fixed", "variable"], style.Values!);
        Assert.Equal(ListBoxStyle.Fixed, style.GetValue(cls.Prototype));
        Assert.NotNull(cls.FindEvent("onMeasureItem"));

        var form = new Form { Name = "frmVar" };
        form.SetBounds(0, 0, 300, 200);
        var list = new ListBox { Name = "lstVar", Style = ListBoxStyle.Variable, Items = ["a"] };
        list.EventHandlers["onMeasureItem"] = "lstVar_MeasureItem";
        list.SetBounds(10, 10, 150, 100);
        form.Add(list);
        var json = FormWriter.Save(form);
        Assert.Contains("\"style\": \"variable\"", json);
        Assert.Contains("\"onMeasureItem\": \"lstVar_MeasureItem\"", json);
        var (loaded, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);
        Assert.Equal(ListBoxStyle.Variable, ((ListBox)loaded.FindControl("lstVar")!).Style);
        Assert.Equal(json, FormWriter.Save(loaded));
        // A default list writes neither.
        var plain = new ListBox { Name = "lstPlain" };
        form.Add(plain);
        Assert.DoesNotContain("lstPlain\",\n        \"style", FormWriter.Save(form));
    }

    [Fact]
    public void Wiring_a_measure_handler_does_not_call_it_until_the_rows_are_needed()
    {
        var list = new ListBox { Items = ["a", "b", "c"], Style = ListBoxStyle.Variable };
        int calls = 0;
        MeasureItemEvent handler = (_, _) => calls++;
        list.MeasureItem += handler;   // a form's generated wiring: the code-behind isn't ready to be called back yet
        list.MeasureItem -= handler;
        list.MeasureItem += handler;
        Assert.Equal(0, calls);
        _ = list.ItemRect(1);
        Assert.Equal(3, calls);

        var f = new Form();
        f.SetBounds(0, 0, 300, 300);
        list.SetBounds(0, 0, 100, 100);
        f.Add(list);
        list.MeasureItem += (_, e) => e.Height = 50;   // the next layout measures the new heights
        f.PerformLayout();
        Assert.Equal(150, list.ContentHeight);
    }
}
