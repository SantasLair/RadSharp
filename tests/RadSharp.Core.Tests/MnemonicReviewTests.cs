namespace RadSharp.Tests;

/// <summary>Regression tests from the M13 review (mnemonics, message boxes, stuck Alt).</summary>
public class MnemonicReviewTests
{
    static Form NewForm()
    {
        var f = new Form { Caption = "Review" };
        f.SetBounds(0, 0, 420, 300);
        return f;
    }

    [Fact]
    public void Message_boxes_show_ampersands_literally()
    {
        var box = Dialogs.CreateMessageForm("Saved Q&A and R&D", MessageType.Information, MessageButtons.Ok);
        var label = Assert.IsType<Label>(box.FindControl("lblLine0"));
        Assert.False(label.ShowAccelChar);
    }

    [Fact]
    public void Changing_FocusControlName_replaces_a_FocusControl_set_from_code()
    {
        var f = NewForm();
        var a = new Edit { Name = "edA" }; a.SetBounds(10, 40, 100, 24); f.Add(a);
        var b = new Edit { Name = "edB" }; b.SetBounds(10, 80, 100, 24); f.Add(b);
        var label = new Label { Caption = "&Name" }; label.SetBounds(10, 10, 80, 20); f.Add(label);
        label.FocusControl = a;
        label.FocusControlName = "edB";
        Assert.Same(b, label.FocusControl);
        f.Simulate.Key(Key.N, KeyModifiers.Alt);
        Assert.True(b.Focused);
    }

    [Fact]
    public void FocusControlName_follows_a_rename_of_the_target()
    {
        var f = NewForm();
        var a = new Edit { Name = "edA" }; a.SetBounds(10, 40, 100, 24); f.Add(a);
        var label = new Label { Caption = "&Name" }; label.SetBounds(10, 10, 80, 20); f.Add(label);
        label.FocusControl = a;
        a.Name = "edRenamed";
        Assert.Equal("edRenamed", label.FocusControlName);
    }

    [Fact]
    public void Underlines_do_not_stay_after_Alt_is_released_elsewhere()
    {
        var f = NewForm();
        f.Simulate.KeyDown(Key.Alt, KeyModifiers.Alt);
        Assert.True(f.ShowAccelerators);
        // Alt+Tab: the Alt key-up goes to another window. The next pointer event reports no modifiers.
        f.Simulate.MoveTo(5, 5);
        Assert.False(f.ShowAccelerators);
    }

    [Fact]
    public void Losing_window_focus_clears_the_Alt_state()
    {
        var f = NewForm();
        f.Simulate.KeyDown(Key.Alt, KeyModifiers.Alt);
        f.ProcessFocusLost();
        Assert.False(f.ShowAccelerators);
    }
}
