using System.Text.Json;

namespace RadSharp;

/// <summary>The root of a form. Its width/height in the form file are the design size.</summary>
public class Form : Control
{
    public Form() => SetDefaultSize(640, 480);

    public string Caption { get; set; } = "";

    /// <summary>The form's size as designed; set when the form is loaded.</summary>
    public int DesignWidth { get; internal set; }
    public int DesignHeight { get; internal set; }

    /// <summary>Resizes the form (e.g. to its window) and lays it out.</summary>
    public void Resize(int width, int height)
    {
        SetBounds(Left, Top, width, height);
        PerformLayout();
    }

    public override bool ReadProperty(string name, JsonElement value)
    {
        if (name == "caption") { Caption = JsonValues.String(value); return true; }
        return base.ReadProperty(name, value);
    }
}

public class Panel : Control
{
    public string Caption { get; set; } = "";

    public override bool ReadProperty(string name, JsonElement value)
    {
        if (name == "caption") { Caption = JsonValues.String(value); return true; }
        return base.ReadProperty(name, value);
    }
}

public class Label : Control
{
    public Label() => SetDefaultSize(80, 20);

    public string Caption { get; set; } = "";

    public override bool ReadProperty(string name, JsonElement value)
    {
        if (name == "caption") { Caption = JsonValues.String(value); return true; }
        return base.ReadProperty(name, value);
    }
}

public class Button : Control
{
    public Button()
    {
        SetDefaultSize(80, 28);
        TabStop = true;
    }

    public string Caption { get; set; } = "";

    public override bool ReadProperty(string name, JsonElement value)
    {
        if (name == "caption") { Caption = JsonValues.String(value); return true; }
        return base.ReadProperty(name, value);
    }
}

/// <summary>
/// Resizes the nearest earlier visible sibling with the same align (Delphi's rule; spec, Appendix B).
/// </summary>
public class Splitter : Control
{
    public Splitter()
    {
        SetDefaultSize(4, 100);
        Align = Align.Left;
    }

    /// <summary>The smallest size of the target, and of the client space left beside it.</summary>
    public int MinSize { get; set; } = 30;

    bool Horizontal => Align is Align.Left or Align.Right;

    public Control? FindTarget()
    {
        if (Parent == null || Align is Align.None or Align.Client) return null;
        var siblings = Parent.Children;
        for (int i = IndexIn(siblings) - 1; i >= 0; i--)
            if (siblings[i].Visible && siblings[i].Align == Align) return siblings[i];
        return null;
    }

    int IndexIn(IReadOnlyList<Control> siblings)
    {
        for (int i = 0; i < siblings.Count; i++)
            if (siblings[i] == this) return i;
        return -1;
    }

    /// <summary>
    /// Drags the splitter by <paramref name="delta"/> pixels along its axis (positive = right/down).
    /// Dragging away from the target grows it; toward it shrinks it.
    /// </summary>
    public void DragBy(int delta)
    {
        if (FindTarget() is not { } target || Parent is not { } parent) return;
        Root.PerformLayout();  // RemainingClientRect must be current

        int size = Horizontal ? target.Width : target.Height;
        int remaining = Horizontal ? parent.RemainingClientRect.Width : parent.RemainingClientRect.Height;
        int sign = Align is Align.Left or Align.Top ? 1 : -1;
        int max = size + remaining - MinSize;
        int newSize = Math.Max(MinSize, Math.Min(size + sign * delta, max));

        if (Horizontal) target.Width = newSize;
        else target.Height = newSize;
    }

    public override bool ReadProperty(string name, JsonElement value)
    {
        if (name == "minSize") { MinSize = JsonValues.Int(value); return true; }
        return base.ReadProperty(name, value);
    }
}
