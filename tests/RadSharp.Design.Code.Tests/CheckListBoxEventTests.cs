using RadSharp.Design.Code;

namespace RadSharp.Tests;

public class CheckListBoxEventTests
{
    [Fact]
    public void CheckListBox_double_click_creates_the_same_default_event_as_a_ListBox()
    {
        var ev = CodeIntegration.DefaultEvent(new CheckListBox());
        Assert.Equal("onChange", ev!.Name);
        Assert.Equal(CodeIntegration.DefaultEvent(new ListBox())!.Name, ev.Name);
        Assert.NotNull(ComponentRegistry.Find("CheckListBox")!.FindEvent("onClickCheck"));
    }

    [Fact]
    public void Every_M14_list_control_has_onChange_as_its_default_event()
    {
        foreach (Control c in new Control[] { new ListBox(), new ComboBox(), new CheckListBox(), new ListView(), new TreeView(), new StringGrid(), new DrawGrid() })
            Assert.Equal("onChange", CodeIntegration.DefaultEvent(c)!.Name);
    }
}
