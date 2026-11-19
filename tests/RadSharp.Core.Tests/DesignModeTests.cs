namespace RadSharp.Tests;

/// <summary>A control the application defines that can't run at design time (it would open a database, say).</summary>
public class DesignTimeBomb : Label
{
    public DesignTimeBomb()
    {
        if (DesignMode) throw new InvalidOperationException("no database at design time");
    }

    [Published(Category = "Behavior")] public int Fuse { get; set; }
}

/// <summary>A form the application defines that can't be created at design time.</summary>
public class DesignTimeBombForm : Form
{
    public DesignTimeBombForm()
    {
        if (DesignMode) throw new InvalidOperationException("no database at design time");
    }
}

/// <summary>Remembers whether its constructor ran in design mode.</summary>
public class DesignModeProbe : Label
{
    public DesignModeProbe() => SeenInConstructor = DesignMode;

    public bool SeenInConstructor { get; }
}

/// <summary>A control whose property setter throws for some values.</summary>
public class PickyGauge : Label
{
    int _max;

    [Published(Category = "Behavior")]
    public int Max
    {
        get => _max;
        set => _max = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Max can't be negative");
    }
}

/// <summary>
/// <see cref="Control.DesignMode"/> (Delphi's csDesigning), the reader keeping a node whose class throws at design time
/// as a placeholder, and finding a class by name among several registered ones.
/// </summary>
public class DesignModeTests
{
    static string FormWith(string node) => $$"""
        {
          "format": "radform",
          "version": "1.0",
          "root": {
            "class": "Form",
            "name": "frmMain",
            "width": 300,
            "height": 200,
            "children": [
              {{node}},
              { "class": "Button", "name": "btnOk", "caption": "OK" }
            ]
          }
        }
        """;

    [Fact]
    public void DesignMode_is_set_from_the_constructor_on_for_controls_created_in_a_design_scope()
    {
        Assert.False(new DesignModeProbe().DesignMode);
        Assert.False(new DesignModeProbe().SeenInConstructor);
        DesignModeProbe inside, nested;
        using (Control.EnterDesignMode())
        {
            inside = new DesignModeProbe();
            using (Control.EnterDesignMode()) nested = new DesignModeProbe();
            Assert.True(new Button().DesignMode);   // still in the outer scope
        }
        Assert.True(inside.DesignMode);
        Assert.True(inside.SeenInConstructor);
        Assert.True(nested.SeenInConstructor);
        Assert.True(inside.DesignMode);             // for good: it is a designed control
        Assert.False(new DesignModeProbe().DesignMode);

        // Per thread: another thread's controls aren't designed ones.
        using (Control.EnterDesignMode())
        {
            bool other = true;
            var thread = new Thread(() => other = new DesignModeProbe().DesignMode);
            thread.Start();
            thread.Join();
            Assert.False(other);
        }
    }

    [Fact]
    public void A_class_that_throws_while_the_designer_reads_it_stays_a_placeholder_with_the_exception_in_a_warning()
    {
        var text = FormWith("""{ "class": "DesignTimeBomb", "name": "bomb1", "fuse": 3, "left": 8, "top": 8 }""");
        FormLoadResult result;
        using (Control.EnterDesignMode()) result = FormReader.Load(text, new FormLoadOptions { KeepUnknownClasses = true });
        var placeholder = Assert.IsType<UnknownControl>(result.Form.FindControl("bomb1"));
        Assert.Equal("DesignTimeBomb", placeholder.ClassName);
        Assert.Contains(placeholder.RawMembers, m => m.Key == "fuse" && m.Value == "3");
        Assert.Contains(result.Warnings, w => w.StartsWith("bomb1: DesignTimeBomb threw while it was created (InvalidOperationException: no database at design time)"));
        Assert.NotNull(result.Form.FindControl("btnOk"));

        // The running application reads it as usual; without KeepUnknownClasses the exception isn't swallowed either.
        var running = FormReader.Load(text).Form;
        Assert.Equal(3, Assert.IsType<DesignTimeBomb>(running.FindControl("bomb1")).Fuse);
        using (Control.EnterDesignMode())
            Assert.Throws<InvalidOperationException>(() => FormReader.Load(text));
    }

    [Fact]
    public void A_setter_that_throws_in_the_designer_keeps_the_node_as_written()
    {
        var text = FormWith("""{ "class": "PickyGauge", "name": "gauge1", "max": -1 }""");
        var (form, warnings) = FormReader.Load(text, new FormLoadOptions { KeepUnknownClasses = true });
        Assert.IsType<UnknownControl>(form.FindControl("gauge1"));
        Assert.Contains(warnings, w => w.Contains("PickyGauge threw while it was created (ArgumentOutOfRangeException"));
        var saved = FormWriter.Save(form);
        Assert.Contains("\"max\": -1", saved);
        Assert.Equal(saved, FormWriter.Save(FormReader.Load(saved, new FormLoadOptions { KeepUnknownClasses = true }).Form));
    }

    [Fact]
    public void A_root_that_throws_at_design_time_reports_why_not_just_that_the_root_must_be_a_form()
    {
        var text = """
            { "format": "radform", "version": "1.0", "root": { "class": "DesignTimeBombForm", "name": "frmBomb" } }
            """;
        using var designing = Control.EnterDesignMode();
        var e = Assert.Throws<FormFileException>(() => FormReader.Load(text, new FormLoadOptions { KeepUnknownClasses = true }));
        Assert.Contains("DesignTimeBombForm", e.Message);
        Assert.Contains("no database at design time", e.Message);
    }

    [Fact]
    public void FindClass_decides_which_class_a_name_means()
    {
        var text = FormWith("""{ "class": "PickyGauge", "name": "gauge1", "max": 4 }""");
        // Not that one: read as an unknown class.
        var (form, _) = FormReader.Load(text, new FormLoadOptions { KeepUnknownClasses = true, FindClass = n => n == "PickyGauge" ? null : ComponentRegistry.Find(n) });
        Assert.IsType<UnknownControl>(form.FindControl("gauge1"));
        // Another class for the name.
        (form, _) = FormReader.Load(text, new FormLoadOptions { FindClass = n => ComponentRegistry.Find(n == "PickyGauge" ? "Label" : n) });
        Assert.IsType<Label>(form.FindControl("gauge1"), exactMatch: true);
    }

    [Fact]
    public void Find_with_a_condition_looks_past_the_class_that_holds_the_name()
    {
        var label = ComponentRegistry.Find("Label")!;
        Assert.Same(label, ComponentRegistry.Find("Label", c => c.Type == typeof(Label)));
        Assert.Null(ComponentRegistry.Find("Label", c => c.Type == typeof(Button)));
        Assert.Null(ComponentRegistry.Find("NoSuchClass", _ => true));
    }
}
