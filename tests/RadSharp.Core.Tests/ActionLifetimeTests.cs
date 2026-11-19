namespace RadSharp.Tests;

/// <summary>Action links made from code or by an unresolved name: unlinking, and clients that leave their form.</summary>
[Collection("PlatformHost")]   // idle updates and closing forms touch process-wide state
public class ActionLifetimeTests
{
    [Fact]
    public void Setting_Action_to_null_cancels_a_name_that_never_resolved()
    {
        var btn = new Button { ActionName = "actSave" };    // no form yet: pending
        Assert.Equal("actSave", btn.ActionName);
        btn.Action = null;
        Assert.Equal("", btn.ActionName);

        var f = new Form();
        f.SetBounds(0, 0, 300, 100);
        var act = new ActionItem { Name = "actSave", Caption = "&Save" };
        f.Add(act);
        f.Add(btn);
        f.PerformLayout();
        Assert.Null(btn.Action);                            // the cancelled name doesn't resolve later
        Assert.DoesNotContain(btn, act.Clients);
    }
}
