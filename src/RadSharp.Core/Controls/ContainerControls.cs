using RadSharp.Drawing;

namespace RadSharp;

/// <summary>A page of a <see cref="PageControl"/>; fills the page control's client area.</summary>
public class TabSheet : Control
{
    string _caption = "";

    public TabSheet() => Align = Align.Client;

    [Published(Category = "Appearance")]
    public string Caption { get => _caption; set { _caption = value; Parent?.Invalidate(); } }

    protected override void Paint(Canvas canvas) => canvas.FillRect(new Rect(0, 0, Width, Height), canvas.Theme.FormBackground);
}

/// <summary>Tabbed pages (Delphi's TPageControl): its TabSheet children, one shown at a time.</summary>
public class PageControl : Control
{
    int _active;

    public PageControl() => SetDefaultSize(300, 200);

    public IReadOnlyList<TabSheet> Pages => [.. Children.OfType<TabSheet>().Where(p => p.Visible)];

    [Published(Category = "Behavior")]
    public int ActivePageIndex
    {
        get => Math.Clamp(_active, 0, Math.Max(0, Pages.Count - 1));
        set
        {
            var pages = Pages;
            value = Math.Clamp(value, 0, Math.Max(0, pages.Count - 1));
            if (value == _active) return;
            var old = _active < pages.Count ? pages[_active] : null;
            _active = value;
            OwningForm?.InvalidateAll();
            if (old != null) OwningForm?.ControlBecameUnavailable(old);  // focus can't stay on a hidden page
            Change?.Invoke(this);
        }
    }

    public TabSheet? ActivePage => Pages.Count > 0 ? Pages[ActivePageIndex] : null;

    [Published] public event NotifyEvent? Change;

    int StripHeight => Px(30);

    protected internal override bool ShowsChild(Control child) => child is not TabSheet || child == ActivePage;

    protected override Rect GetClientRect() =>
        new(Px(1), StripHeight + Px(1), Math.Max(0, Width - Px(2)), Math.Max(0, Height - StripHeight - Px(2)));

    /// <summary>Each visible page's tab rectangle, in this control's coordinates.</summary>
    public List<Rect> TabRects()
    {
        var font = new RenderFont(CurrentTheme.FontFace, (float)(CurrentTheme.FontSize * UiScale));
        var list = new List<Rect>();
        int x = 0;
        foreach (var page in Pages)
        {
            int w = (int)Math.Ceiling(font.MeasureWidth(page.Caption)) + Px(24);
            list.Add(new Rect(x, 0, w, StripHeight));
            x += w;
        }
        return list;
    }

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButton.Left || e.Y >= StripHeight) return;
        var tabs = TabRects();
        for (int i = 0; i < tabs.Count; i++)
            if (e.X >= tabs[i].X && e.X < tabs[i].X + tabs[i].Width) { ActivePageIndex = i; return; }
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab switch pages while focus is inside this page control.</summary>
    protected internal override bool ProcessShortcut(KeyEventArgs e)
    {
        if (e.Key != Key.Tab || !e.Modifiers.HasFlag(KeyModifiers.Ctrl)) return false;
        if (OwningForm?.FocusedControl is not { } f || !f.IsSelfOrDescendantOf(this)) return false;
        int n = Pages.Count;
        if (n < 2) return false;
        ActivePageIndex = (ActivePageIndex + (e.Shift ? n - 1 : 1)) % n;
        return true;
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var body = new Rect(0, StripHeight, Width, Height - StripHeight);
        canvas.FillRect(new Rect(0, 0, Width, StripHeight), t.PanelBackground);
        canvas.FillRect(body, t.FormBackground);
        canvas.DrawBorder(body, t.PanelBorder, canvas.Px(1));
        var tabs = TabRects();
        var pages = Pages;
        for (int i = 0; i < tabs.Count; i++)
        {
            var r = tabs[i];
            bool active = i == ActivePageIndex;
            if (active)
            {
                canvas.FillRect(new Rect(r.X, r.Y, r.Width, r.Height + canvas.Px(1)), t.FormBackground);
                canvas.FillRect(new Rect(r.X, r.Y, r.Width, canvas.Px(2)), t.Accent);
                canvas.FillRect(new Rect(r.X, r.Y, canvas.Px(1), r.Height), t.PanelBorder);
                canvas.FillRect(new Rect(r.X + r.Width - canvas.Px(1), r.Y, canvas.Px(1), r.Height), t.PanelBorder);
            }
            canvas.DrawText(pages[i].Caption, active ? canvas.DefaultBoldFont : canvas.DefaultFont,
                IsEffectivelyEnabled ? t.Text : t.TextDisabled, r, TextAlign.Center, VerticalAlign.Center);
        }
    }
}

/// <summary>A status line along the bottom of a form.</summary>
public class StatusBar : Control
{
    string _text = "";

    public StatusBar()
    {
        SetDefaultSize(200, 24);
        Align = Align.Bottom;
    }

    [Published(Category = "Appearance")]
    public string SimpleText { get => _text; set { _text = value; Invalidate(); } }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        canvas.FillRect(new Rect(0, 0, Width, Height), t.PanelBackground);
        canvas.FillRect(new Rect(0, 0, Width, canvas.Px(1)), t.PanelBorder);
        canvas.DrawText(_text, canvas.DefaultFont, t.Text, new Rect(canvas.Px(8), 0, Width - canvas.Px(16), Height), TextAlign.Left, VerticalAlign.Center);
    }
}

/// <summary>A row of tool buttons along the top of a form; children are laid out left to right.</summary>
public class ToolBar : Control
{
    public ToolBar()
    {
        SetDefaultSize(300, 36);
        Align = Align.Top;
    }

    /// <summary>The name of an ImageList whose pictures ToolButtons show by <see cref="ToolButton.ImageIndex"/>.</summary>
    [Published("images", Category = "Appearance")] public string ImagesName { get; set; } = "";

    public ImageList? Images => ImagesName.Length > 0 ? OwningForm?.FindControl(ImagesName) as ImageList : null;

    protected override bool AfterArrange()
    {
        int x = Px(4), gap = Px(2);
        foreach (var c in Children)
        {
            if (!c.Visible || c.Align != Align.None || c is Component) continue;
            int y = Math.Max(0, (Height - c.Height) / 2);
            if (c.Left != x || c.Top != y) c.SetBounds(x, y, c.Width, c.Height);
            x += c.Width + gap;
        }
        return false;
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        canvas.FillRect(new Rect(0, 0, Width, Height), t.PanelBackground);
        canvas.FillRect(new Rect(0, Height - canvas.Px(1), Width, canvas.Px(1)), t.PanelBorder);
    }
}

/// <summary>A flat button for a ToolBar; can stay pushed in (<see cref="Down"/>) as a toggle.</summary>
public class ToolButton : Button
{
    bool _down;

    public ToolButton()
    {
        SetDefaultSize(64, 28);
        TabStop = false;
    }

    /// <summary>Clicking toggles <see cref="Down"/>.</summary>
    [Published(Category = "Behavior")] public bool AllowToggle { get; set; }
    [Published(Category = "Behavior")] public bool Down { get => _down; set { _down = value; Invalidate(); } }
    [Published(Category = "Appearance")] public int ImageIndex { get; set; } = -1;

    protected internal override void OnClick()
    {
        if (AllowToggle) Down = !Down;
        base.OnClick();
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var r = new Rect(0, 0, Width, Height);
        bool enabled = IsEffectivelyEnabled;
        float radius = canvas.Px(4);
        if (enabled && (IsPressed || _down)) canvas.FillRoundRect(r, radius, t.ButtonFacePressed);
        else if (enabled && IsHovered) canvas.FillRoundRect(r, radius, t.ButtonFaceHover);
        var textRect = r;
        if ((Parent as ToolBar)?.Images?.Get(ImageIndex) is { } pic)
        {
            int s = canvas.Px(16);
            int x = Caption.Length > 0 ? canvas.Px(6) : (Width - s) / 2;
            canvas.DrawPicture(pic, new Rect(x, (Height - s) / 2, s, s));
            if (Caption.Length > 0) textRect = new Rect(x + s + canvas.Px(4), 0, Width - x - s - canvas.Px(10), Height);
        }
        canvas.DrawText(Caption, canvas.DefaultFont, enabled ? t.Text : t.TextDisabled, textRect,
            textRect == r ? TextAlign.Center : TextAlign.Left, VerticalAlign.Center);
    }
}

/// <summary>A vertical divider between groups of tool buttons.</summary>
public class ToolSeparator : Control
{
    public ToolSeparator() => SetDefaultSize(9, 24);

    protected override void Paint(Canvas canvas) =>
        canvas.FillRect(new Rect(Width / 2, canvas.Px(3), canvas.Px(1), Height - canvas.Px(6)), canvas.Theme.PanelBorder);
}

public delegate void PaintEvent(Control sender, Canvas canvas);

/// <summary>A blank area you draw yourself in <see cref="PaintContent"/> (Delphi's TPaintBox).</summary>
public class PaintBox : Control
{
    public PaintBox() => SetDefaultSize(200, 150);

    [Published(Category = "Appearance")] public Color? Color { get; set; }

    [Published("onPaint")] public event PaintEvent? PaintContent;

    protected override void Paint(Canvas canvas)
    {
        if (Color is { } c) canvas.FillRect(new Rect(0, 0, Width, Height), c);
        PaintContent?.Invoke(this, canvas);
    }
}

public enum ImageScaling { None, Stretch, Fit, Center }

/// <summary>Shows a picture (Delphi's TImage).</summary>
public class Image : Control
{
    string _source = "";
    Picture? _picture;

    public Image() => SetDefaultSize(100, 100);

    /// <summary>A data URI, an embedded resource name or a file path (see <see cref="Drawing.Picture.FromSource"/>).</summary>
    [Published("picture", Category = "Appearance")]
    public string PictureSource
    {
        get => _source;
        set { _source = value ?? ""; _picture = null; Invalidate(); }
    }

    /// <summary>The picture: loaded from <see cref="PictureSource"/>, or set from code.</summary>
    public Picture? Picture
    {
        get => _picture ??= Drawing.Picture.FromSource(_source, (OwningForm ?? (Control)this).GetType().Assembly);
        set { _picture = value; Invalidate(); }
    }

    [Published(Category = "Appearance")] public ImageScaling Scaling { get; set; } = ImageScaling.Fit;

    protected override void Paint(Canvas canvas)
    {
        if (Picture is not { } pic || pic.Width == 0 || pic.Height == 0) return;
        Rect dest = Scaling switch
        {
            ImageScaling.Stretch => new Rect(0, 0, Width, Height),
            ImageScaling.None => new Rect(0, 0, pic.Width, pic.Height),
            ImageScaling.Center => new Rect((Width - pic.Width) / 2, (Height - pic.Height) / 2, pic.Width, pic.Height),
            _ => FitRect(pic),
        };
        canvas.DrawPicture(pic, dest);
    }

    Rect FitRect(Picture pic)
    {
        double k = Math.Min((double)Width / pic.Width, (double)Height / pic.Height);
        int w = (int)Math.Round(pic.Width * k), h = (int)Math.Round(pic.Height * k);
        return new Rect((Width - w) / 2, (Height - h) / 2, w, h);
    }
}

/// <summary>A list of small pictures for tool buttons and menus (Delphi's TImageList).</summary>
public class ImageList : Component
{
    string[] _sources = [];
    Picture?[]? _pictures;

    [Published(Category = "Appearance")]
    public string[] Images { get => _sources; set { _sources = value ?? []; _pictures = null; } }

    public Picture? Get(int index)
    {
        if (index < 0 || index >= _sources.Length) return null;
        _pictures ??= new Picture?[_sources.Length];
        return _pictures[index] ??= Picture.FromSource(_sources[index], (OwningForm ?? (Control)this).GetType().Assembly);
    }
}
