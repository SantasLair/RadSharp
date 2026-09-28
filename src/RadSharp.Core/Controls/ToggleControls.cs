using RadSharp.Drawing;

namespace RadSharp;

/// <summary>Shared behaviour of CheckBox and RadioButton: a box or circle followed by a caption.</summary>
public abstract class ToggleControl : Control
{
    string _caption = "";
    bool _checked;

    protected ToggleControl()
    {
        SetDefaultSize(120, 20);
        TabStop = true;
    }

    [Published(Category = "Appearance")] public string Caption { get => _caption; set { _caption = value; Invalidate(); } }

    /// <summary>The name of an ImageList whose pictures this control shows by <see cref="ImageIndex"/>, between the indicator and the caption.</summary>
    [Published("images", Category = "Appearance")] public string ImagesName { get; set; } = "";
    [Published(Category = "Appearance")] public int ImageIndex { get; set; } = -1;

    [Published(Category = "Behavior")]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            OnCheckedChanged();
            OnChange();
        }
    }

    /// <summary>Raised when <see cref="Checked"/> changes (by the user or from code).</summary>
    [Published] public event NotifyEvent? Change;

    protected virtual void OnChange() => Change?.Invoke(this);
    protected virtual void OnCheckedChanged() { }

    /// <summary>What a click or Space does.</summary>
    protected abstract void Toggle();

    protected internal override void OnClick()
    {
        Toggle();
        base.OnClick();
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Space && e.Modifiers == KeyModifiers.None)
        {
            Toggle();
            e.Handled = true;
        }
    }

    protected Rect IndicatorRect(Canvas canvas)
    {
        int size = canvas.Px(16);
        return new Rect(0, (Height - size) / 2, size, size);
    }

    protected void PaintCaption(Canvas canvas, Rect indicator)
    {
        int x = indicator.Width + canvas.Px(6);
        if (ImageList.Resolve(this, ImagesName, ImageIndex) is { } pic)
        {
            int s = canvas.Px(16);
            canvas.DrawPicture(pic, new Rect(x, (Height - s) / 2, s, s));
            x += s + canvas.Px(4);
        }
        var color = IsEffectivelyEnabled ? canvas.Theme.Text : canvas.Theme.TextDisabled;
        canvas.DrawText(Caption, EffectiveFont, color, new Rect(x, 0, Width - x, Height), TextAlign.Left, VerticalAlign.Center);
    }

}

public class CheckBox : ToggleControl
{
    protected override void Toggle() => Checked = !Checked;

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var box = IndicatorRect(canvas);
        float radius = canvas.Px(3);
        bool enabled = IsEffectivelyEnabled;
        if (Checked)
        {
            canvas.FillRoundRect(box, radius, enabled ? t.Accent : t.TextDisabled);
            canvas.DrawCheckMark(box, t.OnAccent, Math.Max(1.5f, canvas.Px(2)));
        }
        else
        {
            canvas.FillRoundRect(box, radius, enabled ? t.InputBackground : t.FormBackground);
            canvas.DrawRoundBorder(box, radius, enabled && IsHovered ? t.Accent : enabled ? t.InputBorder : t.TextDisabled, canvas.Px(1));
        }
        if (Focused && OwningForm is { ShowFocusCues: true }) canvas.DrawFocusRing(Canvas.Inflate(box, canvas.Px(2)), radius + canvas.Px(2));
        PaintCaption(canvas, box);
    }
}

/// <summary>Checking a radio button unchecks the other radio buttons with the same parent.</summary>
public class RadioButton : ToggleControl
{
    protected override void Toggle() => Checked = true;

    protected override void OnCheckedChanged()
    {
        if (!Checked || Parent == null) return;
        foreach (var sibling in Parent.Children)
            if (sibling != this && sibling is RadioButton rb) rb.Checked = false;
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var circle = IndicatorRect(canvas);
        bool enabled = IsEffectivelyEnabled;
        if (Checked)
        {
            canvas.FillEllipse(circle, enabled ? t.Accent : t.TextDisabled);
            canvas.FillEllipse(Canvas.Inflate(circle, -canvas.Px(5)), t.OnAccent);
        }
        else
        {
            canvas.FillEllipse(circle, enabled ? t.InputBackground : t.FormBackground);
            canvas.DrawEllipseBorder(circle, enabled && IsHovered ? t.Accent : enabled ? t.InputBorder : t.TextDisabled, canvas.Px(1));
        }
        if (Focused && OwningForm is { ShowFocusCues: true })
        {
            var ring = Canvas.Inflate(circle, canvas.Px(2));
            canvas.DrawFocusRing(ring, ring.Width / 2f);
        }
        PaintCaption(canvas, circle);
    }
}

/// <summary>A captioned frame around related controls; its client area starts below the caption.</summary>
public class GroupBox : Control
{
    string _caption = "";

    public GroupBox() => SetDefaultSize(200, 120);

    [Published(Category = "Appearance")] public string Caption { get => _caption; set { _caption = value; Invalidate(); } }

    int HeaderHeight => LayoutMath.Round(CurrentTheme.FontSize * 1.6 * UiScale);

    protected override Rect GetClientRect()
    {
        var r = base.GetClientRect();
        int inset = Px(8), top = HeaderHeight + Px(6);
        return new Rect(r.X + inset, r.Y + top, Math.Max(0, r.Width - 2 * inset), Math.Max(0, r.Height - top - inset));
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        int header = HeaderHeight;
        var frame = new Rect(0, header, Width, Height - header);
        canvas.DrawRoundBorder(frame, canvas.Px(4), t.PanelBorder, canvas.Px(1));
        canvas.DrawText(Caption, EffectiveFont with { Bold = true }, IsEffectivelyEnabled ? t.Text : t.TextDisabled,
            new Rect(canvas.Px(2), 0, Width - canvas.Px(4), header), TextAlign.Left, VerticalAlign.Center);
    }

}
