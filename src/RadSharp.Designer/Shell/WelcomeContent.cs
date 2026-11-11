using RadSharp.Drawing;

namespace RadSharp.Designer;

/// <summary>A project on the welcome page: where it is, when it was last opened, whether it is pinned.</summary>
public sealed record RecentEntry(string Path, string Name, string Directory, DateTime? LastOpened, bool Pinned);

/// <summary>
/// The lower part of the welcome page, drawn and hit-tested by one control: the recent projects (pinned ones on top, each
/// with its folder, the time it was last opened, a pin toggle and a remove button), the new-project templates, and a short
/// "Keyboard essentials" panel. It holds no logic: it raises <see cref="OpenRequested"/>, <see cref="PinToggled"/>,
/// <see cref="RemoveRequested"/> and <see cref="TemplateChosen"/>, and the designer acts.
/// </summary>
public sealed class WelcomeContent : Control
{
    enum Part { Open, Pin, Remove, Template }
    readonly List<(Rect Rect, Part Part, int Index)> _hits = [];
    int _hot = -1;

    public WelcomeContent()
    {
        Name = "pnlWelcomeContent";
        TabStop = false;
        SetDefaultSize(700, 360);
    }

    public IReadOnlyList<RecentEntry> Recent { get; set; } = [];
    public IReadOnlyList<ProjectTemplate> Templates { get; set; } = [];
    /// <summary>Title and key text pairs of the "Keyboard essentials" panel.</summary>
    public IReadOnlyList<(string Title, string Keys)> Shortcuts { get; set; } = [];
    /// <summary>The time "last opened" is measured against (tests fix it).</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    public event Action<string>? OpenRequested;
    public event Action<string>? PinToggled;
    public event Action<string>? RemoveRequested;
    public event Action<ProjectTemplate>? TemplateChosen;

    int RowH => Px(48);
    int CardH => Px(44);
    int LeftW => Math.Min(Px(560), Width * 11 / 20);
    int RightX => LeftW + Px(32);

    // ---- Layout ----

    /// <summary>The rectangle of a recent row, or of its pin or remove button (in this control's coordinates).</summary>
    public Rect RecentRowRect(int index) => new(0, Px(34) + index * RowH, LeftW, RowH - Px(4));
    public Rect PinRect(int index) { var r = RecentRowRect(index); int s = Px(18); return new Rect(r.X + r.Width - Px(8) - s * 2 - Px(10), r.Y + (r.Height - s) / 2, s, s); }
    public Rect RemoveRect(int index) { var r = RecentRowRect(index); int s = Px(18); return new Rect(r.X + r.Width - Px(8) - s, r.Y + (r.Height - s) / 2, s, s); }
    public Rect TemplateRect(int index) => new(RightX, Px(34) + index * CardH, Math.Max(0, Width - RightX), CardH - Px(6));

    /// <summary>How many recent rows fit.</summary>
    public int VisibleRecent => Math.Max(0, Math.Min(Recent.Count, (Height - Px(34)) / RowH));

    int ShortcutsTop => Px(34) + Templates.Count * CardH + Px(8);

    void ComputeHits()
    {
        _hits.Clear();
        for (int i = 0; i < VisibleRecent; i++)
        {
            _hits.Add((PinRect(i), Part.Pin, i));
            _hits.Add((RemoveRect(i), Part.Remove, i));
            _hits.Add((RecentRowRect(i), Part.Open, i));
        }
        for (int i = 0; i < Templates.Count; i++) _hits.Add((TemplateRect(i), Part.Template, i));
    }

    (Rect Rect, Part Part, int Index)? HitAt(int x, int y)
    {
        ComputeHits();
        foreach (var h in _hits)
            if (x >= h.Rect.X && y >= h.Rect.Y && x < h.Rect.X + h.Rect.Width && y < h.Rect.Y + h.Rect.Height) return h;
        return null;
    }

    // ---- Input ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButton.Left || HitAt(e.X, e.Y) is not { } hit) return;
        switch (hit.Part)
        {
            case Part.Open: OpenRequested?.Invoke(Recent[hit.Index].Path); break;
            case Part.Pin: PinToggled?.Invoke(Recent[hit.Index].Path); break;
            case Part.Remove: RemoveRequested?.Invoke(Recent[hit.Index].Path); break;
            case Part.Template: TemplateChosen?.Invoke(Templates[hit.Index]); break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = HitAt(e.X, e.Y);
        int hot = h is { Part: Part.Open } o ? o.Index : h is { Part: Part.Template } tp ? 1000 + tp.Index : -1;
        if (hot == _hot) return;
        _hot = hot;
        Invalidate();
    }

    protected override void OnMouseLeave()
    {
        base.OnMouseLeave();
        _hot = -1;
        Invalidate();
    }

    public override string? GetHintAt(int x, int y, out Rect region)
    {
        region = new Rect(0, 0, Width, Height);
        if (HitAt(x, y) is not { } h) return null;
        region = h.Rect;
        return h.Part switch
        {
            Part.Pin => Recent[h.Index].Pinned ? "Unpin" : "Pin to the top",
            Part.Remove => "Remove from the list",
            Part.Open => Recent[h.Index].Path,
            _ => Templates[h.Index].Description,
        };
    }

    // ---- Painting ----

    /// <summary>"just now", "5 minutes ago", "yesterday", "3 days ago", else the date.</summary>
    public static string FormatAge(DateTime whenUtc, DateTime nowUtc)
    {
        var span = nowUtc - whenUtc;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} minute{((int)span.TotalMinutes == 1 ? "" : "s")} ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} hour{((int)span.TotalHours == 1 ? "" : "s")} ago";
        if (span < TimeSpan.FromDays(2)) return "yesterday";
        if (span < TimeSpan.FromDays(30)) return $"{(int)span.TotalDays} days ago";
        return whenUtc.ToLocalTime().ToString("yyyy-MM-dd");
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var bold = EffectiveFont with { Bold = true };
        var normal = EffectiveFont;
        var small = EffectiveFont with { Size = Math.Max(9, EffectiveFont.Size - 1) };
        canvas.FillRect(new Rect(0, 0, Width, Height), t.FormBackground);

        canvas.DrawText("Recent projects", bold, t.Text, new Rect(0, 0, LeftW, Px(28)), TextAlign.Left, VerticalAlign.Center);
        if (Recent.Count == 0)
            canvas.DrawText("No recent projects yet. Create one on the right, or open an existing project.", normal, t.TextDisabled,
                new Rect(0, Px(34), LeftW, Px(40)), TextAlign.Left, VerticalAlign.Center, ellipsis: true);
        var now = Now();
        for (int i = 0; i < VisibleRecent; i++)
        {
            var entry = Recent[i];
            var r = RecentRowRect(i);
            if (_hot == i) canvas.FillRect(r, t.ButtonFaceHover);
            canvas.FillRect(new Rect(r.X, r.Y + r.Height, r.Width, Px(1)), t.PanelBorder);
            int right = PinRect(i).X - Px(10);
            int ageW = Px(110);
            var text = new Rect(r.X + Px(8), r.Y + Px(4), right - ageW - r.X - Px(16), r.Height / 2 - Px(4));
            canvas.DrawText(entry.Name, bold, t.Text, text, TextAlign.Left, VerticalAlign.Center, ellipsis: true);
            canvas.DrawText(entry.Directory, small, t.TextDisabled, new Rect(text.X, r.Y + r.Height / 2, right - r.X - Px(16), r.Height / 2 - Px(4)),
                TextAlign.Left, VerticalAlign.Center, ellipsis: true);
            if (entry.LastOpened is { } opened)
                canvas.DrawText(FormatAge(opened, now), small, t.TextDisabled, new Rect(right - ageW, r.Y + Px(4), ageW, r.Height / 2 - Px(4)),
                    TextAlign.Right, VerticalAlign.Center);
            DrawPin(canvas, PinRect(i), entry.Pinned, t);
            DrawCross(canvas, RemoveRect(i), t.TextDisabled);
        }

        canvas.DrawText("New project", bold, t.Text, new Rect(RightX, 0, Width - RightX, Px(28)), TextAlign.Left, VerticalAlign.Center);
        for (int i = 0; i < Templates.Count; i++)
        {
            var r = TemplateRect(i);
            canvas.FillRoundRect(r, Px(4), _hot == 1000 + i ? t.ButtonFaceHover : t.ButtonFace);
            canvas.DrawRoundBorder(r, Px(4), t.ButtonBorder, 1);
            canvas.FillRect(new Rect(r.X, r.Y + Px(4), Px(3), r.Height - Px(8)), t.Accent);
            canvas.DrawText(Templates[i].Name, bold, t.Text, new Rect(r.X + Px(14), r.Y + Px(4), r.Width - Px(24), r.Height / 2 - Px(2)),
                TextAlign.Left, VerticalAlign.Center, ellipsis: true);
            canvas.DrawText(Templates[i].Description, small, t.TextDisabled, new Rect(r.X + Px(14), r.Y + r.Height / 2 - Px(2), r.Width - Px(24), r.Height / 2 - Px(2)),
                TextAlign.Left, VerticalAlign.Center, ellipsis: true);
        }

        int y = ShortcutsTop;
        canvas.DrawText("Keyboard essentials", bold, t.Text, new Rect(RightX, y, Width - RightX, Px(28)), TextAlign.Left, VerticalAlign.Center);
        y += Px(30);
        int rowH = Px(21);
        foreach (var (title, keys) in Shortcuts)
        {
            if (y + rowH > Height) break;
            canvas.DrawText(title, normal, t.Text, new Rect(RightX, y, (Width - RightX) * 6 / 10, rowH), TextAlign.Left, VerticalAlign.Center, ellipsis: true);
            canvas.DrawText(keys, small, t.TextDisabled, new Rect(RightX + (Width - RightX) * 6 / 10, y, (Width - RightX) * 4 / 10, rowH), TextAlign.Right, VerticalAlign.Center, ellipsis: true);
            y += rowH;
        }
    }

    void DrawPin(Canvas canvas, Rect r, bool pinned, Theme t)
    {
        var color = pinned ? t.Accent : t.TextDisabled;
        // A pin: a head and a needle, tilted; filled when pinned, outlined otherwise.
        var head = new Rect(r.X + r.Width / 4, r.Y + Px(1), r.Width / 2, r.Width / 2);
        if (pinned) canvas.FillEllipse(head, color); else canvas.DrawEllipseBorder(head, color, 1);
        canvas.DrawLine(new PointF(r.X + r.Width / 2f, head.Y + head.Height), new PointF(r.X + r.Width / 2f, r.Y + r.Height - Px(1)), color, Math.Max(1, Px(1)));
    }

    void DrawCross(Canvas canvas, Rect r, Color color)
    {
        float pad = Px(4);
        canvas.DrawLine(new PointF(r.X + pad, r.Y + pad), new PointF(r.X + r.Width - pad, r.Y + r.Height - pad), color, Px(1) * 1.2f);
        canvas.DrawLine(new PointF(r.X + r.Width - pad, r.Y + pad), new PointF(r.X + pad, r.Y + r.Height - pad), color, Px(1) * 1.2f);
    }
}
