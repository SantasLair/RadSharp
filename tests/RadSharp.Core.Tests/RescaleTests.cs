using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>
/// M13 item 2: changing a form's scale at run time (<see cref="Form.ChangeScale"/>) recomputes every length from its
/// 100 % value, so it doesn't drift and gives the same layout as loading at the new scale.
/// </summary>
public class RescaleTests
{
    static readonly string GoldenDir = Path.Combine(AppContext.BaseDirectory, "Golden");

    static string Showcase => File.ReadAllText(Path.Combine(GoldenDir, "showcase.radform.json"));

    static Form Load(string json, double scale)
    {
        var (form, warnings) = FormReader.Load(json, new FormLoadOptions { Scale = scale });
        Assert.Empty(warnings);
        return form;
    }

    /// <summary>Every control's bounds and stored lengths, by name, after a layout.</summary>
    static List<string> Snapshot(Form form)
    {
        form.PerformLayout();
        var list = new List<string> { Describe(form) };
        foreach (var c in form.Descendants()) list.Add(Describe(c));
        return list;

        static string Describe(Control c) =>
            $"{c.Name}: {c.Bounds} abs {c.AbsoluteLeft},{c.AbsoluteTop} m {c.Margin} p {c.Padding} " +
            $"min {c.MinWidth}x{c.MinHeight} max {c.MaxWidth}x{c.MaxHeight}" + (c is Splitter s ? $" minSize {s.MinSize}" : "");
    }

    [Fact]
    public void Scaling_100_to_150_and_back_gives_exactly_the_layout_of_a_fresh_load_at_100()
    {
        var fresh = Load(Showcase, 1);
        var form = Load(Showcase, 1);
        form.ChangeScale(1.5);
        form.ChangeScale(1);
        Assert.Equal(Snapshot(fresh), Snapshot(form));

        // ...also after resizing (anchored buttons move) at both ends.
        fresh.Resize(640, 360);
        form.Resize(640, 360);
        Assert.Equal(Snapshot(fresh), Snapshot(form));
    }

    [Fact]
    public void Scaling_100_to_150_renders_exactly_the_150_golden_of_a_fresh_load()
    {
        var form = Load(Showcase, 1);
        form.ChangeScale(1.5);
        Assert.Equal(Snapshot(Load(Showcase, 1.5)), Snapshot(form));
        form.Resize(720, 450);
        using var actual = FormRenderer.RenderToBitmap(form);
        GoldenImageTests.CompareOrUpdate(actual, "showcase@150%-720x450.png");
    }

    [Theory]
    [InlineData(1.0, 1.5)]
    [InlineData(1.5, 1.0)]
    [InlineData(1.25, 1.75)]
    [InlineData(2.0, 1.25)]
    [InlineData(1.75, 3.0)]
    public void Changing_scale_matches_a_fresh_load_at_the_new_scale_after_any_number_of_steps(double first, double last)
    {
        var form = Load(Showcase, first);
        foreach (var s in new[] { 1.25, 3.0, 1.0, 1.5, 1.75, 2.0 }) form.ChangeScale(s);
        form.ChangeScale(last);
        Assert.Equal(Snapshot(Load(Showcase, last)), Snapshot(form));
    }

    [Fact]
    public void A_window_resized_before_the_change_keeps_its_anchored_controls_at_their_designed_distances()
    {
        var form = Load(Showcase, 1);
        form.Resize(700, 420);
        form.ChangeScale(1.5);
        form.Resize(1050, 630);   // what the host does: the window keeps its size in design units
        var fresh = Load(Showcase, 1.5);
        fresh.Resize(1050, 630);
        Assert.Equal(Snapshot(fresh), Snapshot(form));
    }

    [Fact]
    public void Anchor_distances_are_converted_not_recaptured_so_the_design_rect_comes_back()
    {
        var f = new Form();
        f.SetBounds(0, 0, 400, 300);
        var centred = new Button { Anchors = Anchors.None };
        centred.SetBounds(101, 77, 81, 29);
        var right = new Button { Anchors = Anchors.Top | Anchors.Right };
        right.SetBounds(301, 9, 87, 27);
        f.Add(centred);
        f.Add(right);
        f.ApplyScale(1);
        f.Resize(3, 3);                   // centred is now placed proportionally, rounded a long way off
        foreach (var s in new[] { 1.5, 1.25, 1.75, 1.0 }) { f.ChangeScale(s); f.Resize((int)Math.Round(3 * s), (int)Math.Round(3 * s)); }
        f.Resize(400, 300);
        Assert.Equal(new Rect(101, 77, 81, 29), centred.Bounds);
        Assert.Equal(new Rect(301, 9, 87, 27), right.Bounds);
    }

    [Fact]
    public void Lengths_set_from_code_are_converted_to_design_units_and_come_back_exactly()
    {
        var form = Load(Showcase, 1.5);
        var ok = form.FindControl("btnOk")!;
        var label = form.FindControl("lblLeft")!;
        ok.Width = 100;                                   // 1.5 pixels: 66.67 at 100 %
        label.Margin = new Thickness(3, 3, 3, 3);
        label.MinWidth = 61;

        form.ChangeScale(1);
        Assert.Equal(67, ok.Width);
        Assert.Equal(new Thickness(2, 2, 2, 2), label.Margin);
        Assert.Equal(41, label.MinWidth);

        form.ChangeScale(2);
        Assert.Equal(133, ok.Width);
        form.ChangeScale(1.5);
        Assert.Equal(100, ok.Width);                      // exactly what the code set
        Assert.Equal(new Thickness(3, 3, 3, 3), label.Margin);
        Assert.Equal(61, label.MinWidth);
        Assert.Equal(LayoutWidthAt(1.5, "lblLeft"), label.Width);   // untouched lengths still match a fresh load

        static int LayoutWidthAt(double scale, string name) => Load(Showcase, scale).FindControl(name)!.Width;
    }

    [Fact]
    public void A_code_built_form_rescales_its_splitter_and_constraints_without_drift()
    {
        Form Build()
        {
            var f = new Form { MinWidth = 101, MinHeight = 77 };
            f.SetBounds(0, 0, 401, 301);
            var left = new Panel { Name = "left", Align = Align.Left, MaxWidth = 207, Padding = new Thickness(3, 5, 7, 9) };
            left.SetBounds(0, 0, 133, 10);
            var split = new Splitter { Name = "split", MinSize = 33 };
            var client = new Panel { Name = "client", Align = Align.Client, Margin = new Thickness(1, 3, 5, 7) };
            var corner = new Button { Name = "corner", Anchors = Anchors.None };
            corner.SetBounds(17, 23, 81, 29);
            client.Add(corner);
            f.Add(left);
            f.Add(split);
            f.Add(client);
            return f;
        }

        var reference = Build();
        reference.ApplyScale(1.25);
        var form = Build();
        form.ApplyScale(1);
        form.Resize(613, 419);    // the centred button is now placed proportionally, with rounding
        foreach (var s in new[] { 1.5, 1.75, 1.0, 3.0, 1.25 }) form.ChangeScale(s);
        form.Resize(reference.Width, reference.Height);
        Assert.Equal(Snapshot(reference), Snapshot(form));
        reference.Resize(777, 555);
        form.Resize(777, 555);
        Assert.Equal(Snapshot(reference), Snapshot(form));
        Assert.Equal(41, ((Splitter)form.FindControl("split")!).MinSize);
    }

    [Fact]
    public void ChangeScale_raises_ScaleChanged_with_the_old_and_new_scale_and_closes_popups()
    {
        var form = Load(Showcase, 1);
        var seen = new List<(double, double)>();
        form.ScaleChanged += (f, change) =>
        {
            Assert.Same(form, f);
            Assert.Equal(f.Scale, change.To);
            seen.Add((change.From, change.To));
        };
        var popup = new Panel();
        popup.SetBounds(0, 0, 50, 50);
        form.OpenPopup(popup, 10, 10);

        form.ChangeScale(2);
        form.ChangeScale(2);        // unchanged: no event
        Assert.Equal([(1.0, 2.0)], seen);
        Assert.Empty(form.Popups);
    }

    [Fact]
    public void ScaleChange_Apply_remembers_the_design_value_until_the_pixels_change()
    {
        double design = double.NaN;
        Assert.Equal(11, new ScaleChange(1, 1.5).Apply(7, ref design));   // 10.5 rounds away from zero
        Assert.Equal(7, design);
        Assert.Equal(7, new ScaleChange(1.5, 1).Apply(11, ref design));
        Assert.Equal(14, new ScaleChange(1, 2).Apply(7, ref design));
        Assert.Equal(15, new ScaleChange(2, 2).Apply(15, ref design));    // changed from code: 7.5 now
        Assert.Equal(7.5, design);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScaleChange(0, 1));
    }

    const string AnchoredOnForm = """
        { "format": "radform", "version": "1.0", "root": { "class": "Form", "name": "Dlg", "width": 301, "height": 201,
          "children": [ { "class": "Button", "name": "btnOk", "anchors": ["right", "bottom"], "left": 211, "top": 163, "width": 81, "height": 29 } ] } }
        """;

    [Fact]
    public void A_form_file_loaded_again_into_the_same_form_rescales_from_the_new_load()
    {
        var form = new Form();
        FormReader.LoadInto(form, AnchoredOnForm, new FormLoadOptions { Scale = 1.5 });
        form.ChangeScale(2);
        foreach (var c in form.Children.ToList()) form.Remove(c);
        FormReader.LoadInto(form, AnchoredOnForm, new FormLoadOptions { Scale = 1.5 });   // the same Form object again
        form.Resize(700, 400);
        form.ChangeScale(1);
        form.Resize(301, 201);
        Assert.Equal(Snapshot(Load(AnchoredOnForm, 1)), Snapshot(form));
    }

    [Fact]
    public void A_form_whose_scale_was_set_by_hand_is_rescaled_from_that_scale()
    {
        // Dialogs.CreateMessageForm builds in pixels at DefaultScale and sets Scale itself, without ApplyScale.
        var f = new Form();
        f.SetBounds(0, 0, 300, 150);
        var b = new Button();
        b.SetBounds(30, 60, 120, 42);
        f.Add(b);
        f.Scale = 1.5;
        f.ChangeScale(1);
        Assert.Equal(new Rect(20, 40, 80, 28), b.Bounds);
        Assert.Equal(new Rect(0, 0, 200, 100), f.Bounds);
    }

    // ---- Review fixes ----

    static Form ClientPanelDialog(out Button ok)
    {
        // 301 - 103 = 198 at 100 % is 247.5 at 125 %, but the laid-out client panel is 376 - 129 = 247 there.
        var f = new Form { Name = "f" };
        f.SetBounds(0, 0, 301, 201);
        var left = new Panel { Name = "left", Align = Align.Left };
        left.SetBounds(0, 0, 103, 10);
        var client = new Panel { Name = "client", Align = Align.Client };
        var top = new Button { Name = "top", Anchors = Anchors.Top | Anchors.Right };
        top.SetBounds(100, 10, 81, 29);
        ok = new Button { Name = "ok", Anchors = Anchors.Bottom | Anchors.Right };
        ok.SetBounds(100, 150, 81, 29);
        client.Add(top);
        client.Add(ok);
        f.Add(left);
        f.Add(client);
        return f;
    }

    static Form FreshClientPanelDialog(double scale)
    {
        var f = ClientPanelDialog(out _);
        f.ApplyScale(scale);
        return f;
    }

    [Fact]
    public void An_anchored_control_in_a_parent_sized_by_layout_lands_where_a_fresh_load_puts_it()
    {
        var fresh = FreshClientPanelDialog(1.25);
        var form = ClientPanelDialog(out _);
        form.ApplyScale(1);
        form.ChangeScale(1.25);
        Assert.Equal(Snapshot(fresh), Snapshot(form));   // was a pixel left of it: PW0 converted to 248, not laid out as 247

        // Also for a control that was hidden while the scale changed.
        var hidden = ClientPanelDialog(out var ok);
        hidden.ApplyScale(1);
        ok.Visible = false;
        hidden.ChangeScale(1.25);
        ok.Visible = true;
        Assert.Equal(Snapshot(fresh), Snapshot(hidden));

        form.ChangeScale(1);                             // and still back exactly
        Assert.Equal(Snapshot(FreshClientPanelDialog(1)), Snapshot(form));
    }

    [Fact]
    public void Scroll_positions_keep_the_same_content_in_view_across_a_scale_change()
    {
        var f = new Form();
        f.SetBounds(0, 0, 400, 300);
        var box = new ScrollBox();
        box.SetBounds(0, 0, 200, 150);
        var tall = new Panel();
        tall.SetBounds(0, 0, 100, 1000);
        box.Add(tall);
        f.Add(box);
        string[] items = Enumerable.Range(0, 200).Select(i => $"Item {i}").ToArray();
        var list = new ListBox { Items = items };
        list.SetBounds(200, 0, 200, 200);
        f.Add(list);
        var end = new ListBox { Items = items };
        end.SetBounds(200, 0, 200, 200);
        f.Add(end);
        f.ApplyScale(1);
        box.ScrollY = 300;
        list.ScrollY = 24 * 50;                  // row 50 at the top
        end.ScrollY = int.MaxValue;              // the last row at the bottom
        int endAt100 = end.ScrollY;

        f.ChangeScale(1.5);
        Assert.Equal(450, box.ScrollY);          // were 300 and 1200: the content jumped
        Assert.Equal(box.AbsoluteTop + box.ContentOffset.Y, tall.AbsoluteTop);
        Assert.Equal(36 * 50, list.ScrollY);
        Assert.InRange(end.ScrollY, end.ContentHeight - end.Viewport.Height - 1, end.ContentHeight - end.Viewport.Height);   // was 4602

        f.ChangeScale(1);
        Assert.Equal((300, 24 * 50, endAt100), (box.ScrollY, list.ScrollY, end.ScrollY));
        list.ScrollY = 50;                       // scrolling after the change isn't undone by a later layout
        f.Resize(500, 400);
        Assert.Equal(50, list.ScrollY);
    }

    [Fact]
    public void ChangeScale_on_a_form_not_scaled_yet_raises_ScaleChanged()
    {
        var seen = new List<(double, double)>();
        var f = new Form();
        f.ScaleChanged += (_, c) => seen.Add((c.From, c.To));
        f.ChangeScale(1);                        // no change
        Assert.Empty(seen);

        var g = new Form();
        g.ScaleChanged += (_, c) => seen.Add((c.From, c.To));
        g.ChangeScale(2);
        Assert.Equal([(1.0, 2.0)], seen);
        Assert.True(g.IsScaled);
    }

    [Fact]
    public void A_message_box_built_at_100_percent_follows_the_display_like_one_built_at_150()
    {
        Assert.Equal(1, Form.DefaultScale);
        var f = Dialogs.CreateMessageForm("Saved.", MessageType.Information, MessageButtons.Ok);
        Assert.True(f.FollowsDisplayScale);      // was false only at 100 %: the host left it small on a 150 % display
        var ok = f.FindControl("btnOk")!;
        var before = ok.Bounds;
        f.ChangeScale(1.5);
        Assert.Equal((int)Math.Round(before.Width * 1.5, MidpointRounding.AwayFromZero), ok.Width);
        f.ChangeScale(1);
        Assert.Equal(before, ok.Bounds);
        Assert.False(new Form().FollowsDisplayScale);   // a code-built form never scaled still doesn't
    }

    [Fact]
    public void A_ScaleChanged_handler_can_change_the_scale_again()
    {
        var f = new Form();
        f.SetBounds(0, 0, 400, 300);
        var b = new Button { Anchors = Anchors.Right | Anchors.Bottom };
        b.SetBounds(300, 250, 80, 30);
        f.Add(b);
        f.ApplyScale(1);
        var seen = new List<(double, double)>();
        f.ScaleChanged += (form, c) => { seen.Add((c.From, c.To)); if (c.To == 1.5) form.ChangeScale(2); };
        f.ChangeScale(1.5);
        Assert.Equal([(1.0, 1.5), (1.5, 2.0)], seen);
        Assert.Equal(new Rect(600, 500, 160, 60), b.Bounds);
        f.ChangeScale(1);
        Assert.Equal(new Rect(300, 250, 80, 30), b.Bounds);
    }
}
