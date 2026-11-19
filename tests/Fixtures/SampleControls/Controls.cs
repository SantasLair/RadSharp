using RadSharp;
using RadSharp.Drawing;

[assembly: DesignTimeSetup(typeof(SampleControls.Setup), nameof(SampleControls.Setup.Init))]

namespace SampleControls;

/// <summary>A plain custom control with a published property; it paints with a colour from another assembly.</summary>
public sealed class Gauge : Control
{
    public Gauge()
    {
        if (Setup.Calls == 0) throw new InvalidOperationException("Setup.Init didn't run first");
        SetDefaultSize(120, 40);
    }

    [Published(Category = "Appearance")] public int Value { get; set; } = 3;

    protected override void Paint(Canvas canvas) => canvas.FillRect(new Rect(0, 0, Width, Height), Color.FromHex(SampleData.Swatches.Fill));
}

/// <summary>A control that builds a child of its own (a subcomponent).</summary>
public sealed class TitleBar : Panel
{
    public readonly Edit Search;

    public TitleBar()
    {
        SetDefaultSize(300, 30);
        Search = new Edit { Align = Align.Right, Width = 120 };
        Add(Search);
    }
}

/// <summary>A control whose constructor throws at design time (it wants data the designer doesn't have).</summary>
public sealed class Exploding : Control
{
    public Exploding()
    {
        if (DesignMode) throw new InvalidOperationException("no budget here");
    }
}

/// <summary>A control whose painting throws.</summary>
public sealed class BadPainter : Control
{
    protected override void Paint(Canvas canvas) => throw new InvalidOperationException("paint broke");
}

/// <summary>A project form: the designer must never create it.</summary>
public sealed class SampleForm : Form
{
    public SampleForm() => throw new InvalidOperationException("the designer created a project form");
}

/// <summary>The project's design-time setup.</summary>
public static class Setup
{
    public static int Calls;

    public static void Init(bool dark = true)
    {
        Calls++;
        Theme.Current = dark ? Theme.Dark : Theme.Default;   // a side effect the designer must undo
    }
}
