using RadSharp.Design.Code;

namespace RadSharp.Tests;

public class ActionEventTests
{
    [Fact]
    public void An_actions_default_event_is_onExecute_and_its_handler_takes_the_sender()
    {
        var act = new RadSharp.Actions.Action { Name = "actSave" };
        var ev = CodeIntegration.DefaultEvent(act);
        Assert.Equal("onExecute", ev!.Name);
        Assert.Equal("Control sender", ev.Parameters);
        Assert.NotNull(ComponentRegistry.Of(act).FindEvent("onUpdate"));
        var form = new Form { Name = "frmMain" };
        Assert.Equal("actSave_Execute", CodeIntegration.HandlerName(act, form, ev));
    }
}
