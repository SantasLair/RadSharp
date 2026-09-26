using System.Text.Json;
using RadSharp.Drawing;

namespace RadSharp;

public enum BorderStyle { None, Single }

/// <summary>The root of a form. Its width/height in the form file are the design size.</summary>
public class Form : Control
{
    bool _dirtyAll = true;
    Rect? _dirty;
    string _caption = "";

    public Form() => SetDefaultSize(640, 480);

    public string Caption { get => _caption; set { _caption = value; CaptionChanged?.Invoke(this); } }

    /// <summary>Raised when <see cref="Caption"/> changes, so a host can retitle its window.</summary>
    public event Action<Form>? CaptionChanged;

    /// <summary>Background colour; null uses the theme's.</summary>
    public Color? Color { get; set; }

    /// <summary>The form's size as designed (at 100 %); set when the form is loaded.</summary>
    public int DesignWidth { get; internal set; }
    public int DesignHeight { get; internal set; }

    /// <summary>The UI scale factor lengths were multiplied by when the form was loaded.</summary>
    public double Scale { get; internal set; } = 1;

    public Theme Theme { get; set; } = Theme.Default;

    /// <summary>Resizes the form (e.g. to its window) and lays it out.</summary>
    public void Resize(int width, int height)
    {
        SetBounds(Left, Top, width, height);
        PerformLayout();
    }

    // ---- Repaint tracking (form coordinates) ----

    public void InvalidateAll() => _dirtyAll = true;

    public void InvalidateRect(Rect r)
    {
        if (_dirtyAll || r.Width <= 0 || r.Height <= 0) return;
        _dirty = _dirty is { } d ? Union(d, r) : r;
    }

    /// <summary>Returns the area needing a repaint since the last call, clipped to the form, and clears it.</summary>
    public bool TryTakeDirtyRect(out Rect rect)
    {
        var full = new Rect(0, 0, Width, Height);
        Rect? result = _dirtyAll ? full : _dirty is { } d ? Intersect(d, full) : null;
        _dirtyAll = false;
        _dirty = null;
        rect = result ?? default;
        return result is { Width: > 0, Height: > 0 };
    }

    static Rect Union(Rect a, Rect b)
    {
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new Rect(x, y, Math.Max(a.X + a.Width, b.X + b.Width) - x, Math.Max(a.Y + a.Height, b.Y + b.Height) - y);
    }

    static Rect? Intersect(Rect a, Rect b)
    {
        int x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        int r = Math.Min(a.X + a.Width, b.X + b.Width), bt = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return r > x && bt > y ? new Rect(x, y, r - x, bt - y) : null;
    }

    protected override void Paint(Canvas canvas) =>
        canvas.FillRect(new Rect(0, 0, Width, Height), Color ?? canvas.Theme.FormBackground);

    public override bool ReadProperty(string name, JsonElement value)
    {
        switch (name)
        {
            case "caption": Caption = JsonValues.String(value); return true;
            case "color": Color = JsonValues.Color(value); return true;
        }
        return base.ReadProperty(name, value);
    }
}

public class Panel : Control
{
    string _caption = "";

    public string Caption { get => _caption; set { _caption = value; Invalidate(); } }
    public Color? Color { get; set; }
    public BorderStyle BorderStyle { get; set; } = BorderStyle.Single;

    protected override void Paint(Canvas canvas)
    {
        var r = new Rect(0, 0, Width, Height);
        canvas.FillRect(r, Color ?? canvas.Theme.PanelBackground);
        if (BorderStyle == BorderStyle.Single) canvas.DrawBorder(r, canvas.Theme.PanelBorder, canvas.Px(1));
        canvas.DrawText(Caption, canvas.DefaultFont, canvas.Theme.Text, r, TextAlign.Center, VerticalAlign.Center);
    }

    public override bool ReadProperty(string name, JsonElement value)
    {
        switch (name)
        {
            case "caption": Caption = JsonValues.String(value); return true;
            case "color": Color = JsonValues.Color(value); return true;
            case "borderStyle": BorderStyle = JsonValues.Enum<BorderStyle>(value); return true;
        }
        return base.ReadProperty(name, value);
    }
}

public class Label : Control
{
    string _caption = "";

    public Label() => SetDefaultSize(80, 20);

    public string Caption { get => _caption; set { _caption = value; Invalidate(); } }
    public TextAlign Alignment { get; set; }
    public VerticalAlign Layout { get; set; }

    protected override void Paint(Canvas canvas)
    {
        var color = Enabled ? canvas.Theme.Text : canvas.Theme.TextDisabled;
        canvas.DrawText(Caption, canvas.DefaultFont, color, new Rect(0, 0, Width, Height), Alignment, Layout);
    }

    public override bool ReadProperty(string name, JsonElement value)
    {
        switch (name)
        {
            case "caption": Caption = JsonValues.String(value); return true;
            case "alignment": Alignment = JsonValues.Enum<TextAlign>(value); return true;
            case "layout": Layout = JsonValues.Enum<VerticalAlign>(value); return true;
        }
        return base.ReadProperty(name, value);
    }
}

public class Button : Control
{
    string _caption = "";

    public Button()
    {
        SetDefaultSize(80, 28);
        TabStop = true;
    }

    public string Caption { get => _caption; set { _caption = value; Invalidate(); } }

    /// <summary>Visual state, driven by input (roadmap M3).</summary>
    public bool IsHovered { get; internal set; }
    public bool IsPressed { get; internal set; }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var r = new Rect(0, 0, Width, Height);
        float radius = canvas.Px(t.ButtonRadius);
        var face = !Enabled ? t.FormBackground : IsPressed ? t.ButtonFacePressed : IsHovered ? t.ButtonFaceHover : t.ButtonFace;
        canvas.FillRoundRect(r, radius, face);
        canvas.DrawRoundBorder(r, radius, IsHovered && Enabled ? t.Accent : t.ButtonBorder, canvas.Px(1));
        canvas.DrawText(Caption, canvas.DefaultFont, Enabled ? t.Text : t.TextDisabled, r, TextAlign.Center, VerticalAlign.Center);
    }

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

    protected override void Paint(Canvas canvas) =>
        canvas.FillRect(new Rect(0, 0, Width, Height), canvas.Theme.SplitterFace);

    protected override void ScaleOwnLengths(double scale) => MinSize = LayoutMath.Round(MinSize * scale);

    public override bool ReadProperty(string name, JsonElement value)
    {
        if (name == "minSize") { MinSize = JsonValues.Int(value); return true; }
        return base.ReadProperty(name, value);
    }
}
