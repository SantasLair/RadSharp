using RadSharp.Design;

namespace RadSharp.Tests;

/// <summary>
/// A control that builds a child of its own in its constructor (an application's search bar with its edit box): on the
/// surface the child is part of the control, as a Delphi subcomponent is.
/// </summary>
public class OwnChildrenDesignTests
{
    public sealed class SearchBox : Panel
    {
        public readonly Edit Search;

        public SearchBox()
        {
            SetDefaultSize(300, 40);
            Search = new Edit { Align = Align.Right, Width = 120 };
            Add(Search);
        }
    }

    public sealed class InnerPanelBox : Panel
    {
        public readonly Panel Inner;

        public InnerPanelBox()
        {
            SetDefaultSize(300, 120);
            Inner = new Panel { Align = Align.Right, Width = 150 };
            Add(Inner);
        }
    }

    static readonly ComponentClass SearchBoxClass = Register("DesignSearchBox", typeof(SearchBox), () => new SearchBox());
    static readonly ComponentClass InnerPanelBoxClass = Register("DesignInnerPanelBox", typeof(InnerPanelBox), () => new InnerPanelBox());

    static ComponentClass Register(string name, Type type, Func<Control> create)
    {
        var cls = new ComponentClass(name, type, typeof(Panel), create, [], []);
        ComponentRegistry.Register(cls);
        return cls;
    }

    static (Form Host, DesignSurface Surface, DesignDocument Doc) Setup()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var host = new Form();
        host.SetBounds(0, 0, 800, 600);
        var doc = new DesignDocument();
        var surface = new DesignSurface { Document = doc, Align = Align.Client, ShowGrid = false };
        host.Add(surface);
        host.PerformLayout();
        surface.Focus();
        return (host, surface, doc);
    }

    static (int X, int Y) At(DesignSurface s, int x, int y)
    {
        var (sx, sy) = s.DesignToScreen(x, y);
        return (s.AbsoluteLeft + sx, s.AbsoluteTop + sy);
    }

    [Fact]
    public void Clicking_a_controls_own_child_selects_the_control_and_select_all_leaves_the_child_out()
    {
        var (host, surface, doc) = Setup();
        var box = (SearchBox)doc.Create(SearchBoxClass, doc.Form, 20, 20);
        host.PerformLayout();
        doc.Select(null);

        var p = At(surface, box.Search.AbsoluteLeft + 10, box.Search.AbsoluteTop + 10);
        host.Simulate.Click(p.X, p.Y);
        Assert.Same(box, Assert.Single(doc.Selection));

        // Select All inside the box (it is the paste target while selected): its own edit isn't one of its controls.
        host.ProcessKeyDown(Key.A, KeyModifiers.Ctrl);
        Assert.DoesNotContain(box.Search, doc.Selection);
    }

    [Fact]
    public void A_control_dropped_on_a_controls_own_panel_goes_into_the_control_and_is_saved()
    {
        var (host, surface, doc) = Setup();
        var box = (InnerPanelBox)doc.Create(InnerPanelBoxClass, doc.Form, 20, 20);
        host.PerformLayout();
        surface.PendingClass = ComponentRegistry.Find("Button");
        var p = At(surface, box.Inner.AbsoluteLeft + 10, box.Inner.AbsoluteTop + 10);
        host.Simulate.Click(p.X, p.Y);

        var button = Assert.Single(doc.Form.Descendants().OfType<Button>());
        Assert.Same(box, button.Parent);   // not the inner panel: the form file doesn't save that one's children
        Assert.Contains(button.Name, doc.Save());
    }
}
