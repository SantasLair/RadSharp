using RadSharp.Design;

namespace RadSharp.Tests;

/// <summary>
/// Controls the designer creates are in design mode, and a class whose constructor throws on the surface (an
/// application's control at design time) doesn't take the designer down.
/// </summary>
public class DesignTimeCreationTests
{
    sealed class Thrower : Label;

    /// <summary>A registered class whose factory throws, as a project's control that needs its database would.</summary>
    static readonly ComponentClass Throwing = Register();

    static ComponentClass Register()
    {
        var cls = new ComponentClass("DesignTimeThrower", typeof(Thrower), typeof(Label),
            () => throw new InvalidOperationException("no database at design time"), [], []);
        ComponentRegistry.Register(cls);
        return cls;
    }

    static (Form Host, DesignSurface Surface, DesignDocument Doc) Setup()
    {
        var host = new Form();
        host.SetBounds(0, 0, 800, 600);
        var doc = new DesignDocument();
        var surface = new DesignSurface { Document = doc, Align = Align.Client };
        host.Add(surface);
        host.PerformLayout();
        surface.Focus();
        return (host, surface, doc);
    }

    [Fact]
    public void What_the_document_creates_and_loads_is_in_design_mode()
    {
        var doc = new DesignDocument();
        Assert.True(doc.Form.DesignMode);
        var button = doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 8, 8);
        Assert.True(button.DesignMode);
        doc.Load(doc.Save());
        var loaded = doc.Form.FindControl(button.Name)!;
        Assert.True(loaded.DesignMode);
        doc.Select(loaded);
        Assert.True(Assert.Single(doc.Paste(doc.CopySelection(), doc.Form)).DesignMode);
        Assert.True(doc.Undo());
        Assert.True(doc.Redo());
        Assert.True(doc.Form.DesignMode);
        Assert.All(doc.Form.Descendants(), c => Assert.True(c.DesignMode));
        Assert.False(new Button().DesignMode);   // outside the designer's operations
    }

    [Fact]
    public void A_class_that_throws_when_placed_reports_it_and_changes_nothing()
    {
        var (host, surface, doc) = Setup();
        var failures = new List<(ComponentClass Class, Exception Error)>();
        surface.CreateFailed += (cls, e) => failures.Add((cls, e));

        // Click-to-place, from the palette.
        surface.PendingClass = Throwing;
        var (sx, sy) = surface.DesignToScreen(40, 40);
        host.Simulate.Click(surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy);
        var (cls, error) = Assert.Single(failures);
        Assert.Same(Throwing, cls);
        Assert.Equal("no database at design time", error.Message);
        Assert.Null(surface.PendingClass);
        Assert.Empty(doc.Form.Children);
        Assert.False(doc.CanUndo);   // no empty undo step either

        // Drag and drop.
        Assert.Null(surface.CreateAt(Throwing, sx, sy));
        Assert.Equal(2, failures.Count);
        Assert.Empty(doc.Form.Children);

        // A real class still places.
        Assert.IsType<Button>(surface.CreateAt(ComponentRegistry.Find("Button")!, sx, sy));
    }
}
