using System.Globalization;
using System.Text;
using RadSharp.Drawing;

namespace RadSharp;

/// <summary>
/// A date field with a drop-down <see cref="Calendar"/> (Delphi's TDateTimePicker, date only). The date is shown in the
/// culture's short date pattern (or <see cref="Format"/>) and edited by segment, as in Windows: Left/Right move between the
/// day, month and year, Up/Down or typed digits change the focused segment, and F4 or Alt+Down opens the calendar.
/// </summary>
/// <remarks>
/// <para><see cref="SelectedDate"/> is null until a date is chosen, which shows today; <see cref="Date"/> is the date shown.
/// With <see cref="ShowCheckbox"/> a check box at the left stands for "no date" (Delphi's way): unchecked greys the date out and
/// <see cref="Value"/> is null. <see cref="Value"/> is the date, or null when there is none.</para>
/// <para>Typing: digits fill the focused segment and move on when it is complete (two digits, or a first digit that can't
/// take a second); a separator character moves on too. A day is cut back to the month's length when the month or year
/// changes. <see cref="MinDate"/>/<see cref="MaxDate"/> clamp every result; the calendar greys out days outside them.</para>
/// <para>In the open calendar the keys move its highlight; Enter, Space or a click on a day picks it and closes the calendar,
/// Esc or a click outside closes it and keeps the old date.</para>
/// <para><see cref="Format"/> accepts the day (<c>d dd ddd dddd</c>), month (<c>M MM MMM MMMM</c>) and year (<c>y yy yyyy</c>)
/// tokens of .NET custom date patterns, with literal text in quotes or after a backslash; <c>/</c> is the culture's date
/// separator. Empty (the default) means the culture's short date pattern. Other characters are shown as written.</para>
/// </remarks>
public class DateTimePicker : Control
{
    DateOnly? _selected;
    DateOnly _min = DateOnly.MinValue, _max = DateOnly.MaxValue;
    bool _showCheckbox, _checked = true;
    string _format = "";
    WeekStart _weekStart;
    CultureInfo? _culture;
    Calendar? _dropDown;
    int _part;               // the focused part: -1 the check box, else an index into the editable segments
    string _typed = "";      // digits typed into the focused segment so far

    public DateTimePicker()
    {
        SetDefaultSize(140, 26);
        TabStop = true;
    }

    /// <summary>The chosen date, or null (the default) for "today". Clamped to <see cref="MinDate"/>..<see cref="MaxDate"/>.</summary>
    [Published("date", Category = "Behavior")]
    public DateOnly? SelectedDate
    {
        get => _selected;
        set
        {
            var before = Date;
            if (value is { } v) value = DateMath.Clamp(v, _min, _max);
            _selected = value;
            Invalidate();
            if (Date != before) OnChange();
        }
    }

    /// <summary>The date shown: the chosen date, or today (kept inside the limits) when none was chosen.</summary>
    public DateOnly Date
    {
        get => _selected ?? DateMath.Clamp(Today, _min, _max);
        set => SelectedDate = value;
    }

    /// <summary>
    /// The date, or null when <see cref="ShowCheckbox"/> is on and the box is unchecked. Setting null turns the check box on
    /// and unchecks it; setting a date checks the box (when shown) and chooses the date.
    /// </summary>
    public DateOnly? Value
    {
        get => _showCheckbox && !_checked ? null : Date;
        set
        {
            if (value is { } d)
            {
                Checked = true;
                Date = d;
            }
            else
            {
                ShowCheckbox = true;
                Checked = false;
            }
        }
    }

    [Published(Category = "Behavior")]
    public DateOnly MinDate
    {
        get => _min;
        set { _min = value; if (_max < value) _max = value; Reclamp(); }
    }

    [Published(Category = "Behavior")]
    public DateOnly MaxDate
    {
        get => _max;
        set { _max = value; if (_min > value) _min = value; Reclamp(); }
    }

    /// <summary>Shows a check box before the date; unchecked means no date (<see cref="Value"/> is null).</summary>
    [Published(Category = "Behavior")]
    public bool ShowCheckbox
    {
        get => _showCheckbox;
        set
        {
            if (_showCheckbox == value) return;
            bool hadValue = Value != null;
            _showCheckbox = value;
            if (!value && _part < 0) _part = 0;
            if (!value) CloseUp();
            Invalidate();
            if ((Value != null) != hadValue) OnChange();
        }
    }

    /// <summary>With <see cref="ShowCheckbox"/>: whether there is a date. Without it, ignored.</summary>
    [Published(Category = "Behavior")]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            bool hadValue = Value != null;
            _checked = value;
            if (!value) { CloseUp(); _typed = ""; }
            Invalidate();
            if (_showCheckbox && (Value != null) != hadValue) OnChange();
        }
    }

    /// <summary>A date pattern (see the class remarks), or empty for the culture's short date pattern.</summary>
    [Published(Category = "Appearance")]
    public string Format { get => _format; set { _format = value ?? ""; _typed = ""; Invalidate(); } }

    /// <summary>The first column of the drop-down calendar; <see cref="WeekStart.Default"/> follows the culture.</summary>
    [Published(Category = "Behavior")]
    public WeekStart FirstDayOfWeek { get => _weekStart; set { _weekStart = value; Invalidate(); } }

    /// <summary>Raised when the date, or whether there is one, changes (typing, the calendar, the check box, or from code).</summary>
    [Published] public event NotifyEvent? Change;

    /// <summary>The culture for the date pattern and names; null (the default) is the current culture.</summary>
    public CultureInfo? Culture { get => _culture; set { _culture = value; _typed = ""; Invalidate(); } }

    /// <summary>Supplies today's date; null (the default) uses the system clock.</summary>
    public Func<DateOnly>? Clock { get; set; }

    public DateOnly Today => DateMath.Today(Clock);

    public bool IsDroppedDown => _dropDown != null;

    /// <summary>The text as displayed, e.g. <c>17/11/2026</c>.</summary>
    public string DisplayText => Fields().Text;

    protected virtual void OnChange() => Change?.Invoke(this);

    CultureInfo Cult => _culture ?? CultureInfo.CurrentCulture;

    bool Editable => IsEffectivelyEnabled && (!_showCheckbox || _checked);

    void Reclamp()
    {
        if (_selected is { } d && (d < _min || d > _max)) SelectedDate = d;   // the setter clamps
        Invalidate();
    }

    // ---- The pattern: segments ----

    enum Kind { Literal, Day, Month, Year, DayName }

    readonly record struct Seg(Kind Kind, int Len, int Start, int End);

    sealed record FieldSet(string Text, List<Seg> Segments)
    {
        public List<int> Editable { get; } = [.. Segments.Select((s, i) => (s, i)).Where(x => x.s.Kind is Kind.Day or Kind.Month or Kind.Year).Select(x => x.i)];
    }

    FieldSet Fields()
    {
        var fmt = DateMath.GregorianFormat(Cult);
        string pattern = _format.Length > 0 ? _format : fmt.ShortDatePattern;
        var d = Date;
        var sb = new StringBuilder();
        var segs = new List<Seg>();
        void Add(Kind kind, int len, string text)
        {
            int start = sb.Length;
            sb.Append(text);
            if (kind == Kind.Literal && segs.Count > 0 && segs[^1].Kind == Kind.Literal) segs[^1] = segs[^1] with { End = sb.Length };
            else segs.Add(new Seg(kind, len, start, sb.Length));
        }
        for (int i = 0; i < pattern.Length;)
        {
            char c = pattern[i];
            if (c is 'd' or 'M' or 'y')
            {
                int n = 1;
                while (i + n < pattern.Length && pattern[i + n] == c) n++;
                i += n;
                switch (c)
                {
                    case 'd' when n >= 3:
                        Add(Kind.DayName, n, n == 3 ? fmt.GetAbbreviatedDayName(d.DayOfWeek) : fmt.GetDayName(d.DayOfWeek));
                        break;
                    case 'd':
                        Add(Kind.Day, n, n == 1 ? d.Day.ToString(CultureInfo.InvariantCulture) : d.Day.ToString("D2", CultureInfo.InvariantCulture));
                        break;
                    case 'M' when n == 3: Add(Kind.Month, n, fmt.GetAbbreviatedMonthName(d.Month)); break;
                    case 'M' when n >= 4: Add(Kind.Month, n, fmt.GetMonthName(d.Month)); break;
                    case 'M':
                        Add(Kind.Month, n, n == 1 ? d.Month.ToString(CultureInfo.InvariantCulture) : d.Month.ToString("D2", CultureInfo.InvariantCulture));
                        break;
                    default:
                        Add(Kind.Year, n, n == 1 ? (d.Year % 100).ToString(CultureInfo.InvariantCulture)
                            : n == 2 ? (d.Year % 100).ToString("D2", CultureInfo.InvariantCulture)
                            : d.Year.ToString("D4", CultureInfo.InvariantCulture));
                        break;
                }
            }
            else if (c is '\'' or '"')
            {
                int end = pattern.IndexOf(c, i + 1);
                if (end < 0) end = pattern.Length;
                Add(Kind.Literal, 0, pattern[(i + 1)..end]);
                i = end + 1;
            }
            else if (c == '\\' && i + 1 < pattern.Length) { Add(Kind.Literal, 0, pattern[i + 1].ToString()); i += 2; }
            else if (c == '/') { Add(Kind.Literal, 0, fmt.DateSeparator); i++; }
            else { Add(Kind.Literal, 0, c.ToString()); i++; }
        }
        return new FieldSet(sb.ToString(), segs);
    }

    int PartCount => Fields().Editable.Count;

    // ---- Editing by segment ----

    static int ValueOf(Seg seg, DateOnly d) => seg.Kind switch { Kind.Day => d.Day, Kind.Month => d.Month, _ => d.Year };

    static DateOnly With(DateOnly d, Kind kind, int value)
    {
        int y = d.Year, m = d.Month, day = d.Day;
        if (kind == Kind.Day) day = value;
        else if (kind == Kind.Month) m = value;
        else y = value;
        return new DateOnly(y, m, Math.Min(day, DateTime.DaysInMonth(y, m)));
    }

    void Commit(DateOnly d)
    {
        if (d == Date && _selected != null) return;
        SelectedDate = d;
    }

    /// <summary>Up/Down on the focused segment: the day and month wrap around, the year stops at its limits; results outside the limits are ignored.</summary>
    void Step(int delta)
    {
        var lay = Fields();
        if (_part < 0 || _part >= lay.Editable.Count) return;
        var seg = lay.Segments[lay.Editable[_part]];
        var d = Date;
        DateOnly next;
        switch (seg.Kind)
        {
            case Kind.Day:
                int dim = DateTime.DaysInMonth(d.Year, d.Month);
                next = With(d, Kind.Day, ((d.Day - 1 + delta) % dim + dim) % dim + 1);
                break;
            case Kind.Month:
                next = With(d, Kind.Month, ((d.Month - 1 + delta) % 12 + 12) % 12 + 1);
                break;
            default:
                next = With(d, Kind.Year, Math.Clamp(d.Year + delta, 1, 9999));
                break;
        }
        _typed = "";
        if (next >= _min && next <= _max) Commit(next);
    }

    /// <summary>A typed digit for the focused segment.</summary>
    void TypeDigit(char c)
    {
        var lay = Fields();
        if (_part < 0 || _part >= lay.Editable.Count) return;
        var seg = lay.Segments[lay.Editable[_part]];
        var d = Date;
        int digits = seg.Kind == Kind.Year && seg.Len >= 3 ? 4 : 2;
        int max = seg.Kind switch { Kind.Day => DateTime.DaysInMonth(d.Year, d.Month), Kind.Month => 12, _ => digits == 4 ? 9999 : 99 };

        var next = _typed + c;
        if (next.Length > digits || int.Parse(next, CultureInfo.InvariantCulture) > max) next = c.ToString();   // start over with this digit
        _typed = next;
        int n = int.Parse(next, CultureInfo.InvariantCulture);
        bool complete = next.Length == digits || n * 10 > max;
        if (n == 0)
        {
            if (complete) _typed = "";
            return;
        }
        int value = seg.Kind == Kind.Year && digits == 2 ? DateMath.GregorianFormat(Cult).Calendar.ToFourDigitYear(n) : n;
        var candidate = With(d, seg.Kind, Math.Clamp(value, 1, seg.Kind == Kind.Year ? 9999 : max));
        if (candidate >= _min && candidate <= _max) Commit(candidate);
        else if (complete) Commit(DateMath.Clamp(candidate, _min, _max));
        if (complete)
        {
            _typed = "";
            if (_part + 1 < lay.Editable.Count) _part++;
        }
        Invalidate();
    }

    void MovePart(int delta)
    {
        _typed = "";
        int count = PartCount;
        int next = _part + delta;
        if (next < 0) next = _showCheckbox ? -1 : 0;
        else if (next >= count) next = Math.Max(0, count - 1);
        _part = next;
        Invalidate();
    }

    // ---- Geometry ----

    RenderFont TextFont
    {
        get
        {
            var f = EffectiveFont;
            var theme = CurrentTheme;
            if (f == theme.DefaultFont) return new(theme.FontFace, (float)(theme.FontSize * UiScale));
            return new(FontFace.Resolve(f), (float)(f.Size * UiScale));
        }
    }

    int ButtonWidth => Px(22);
    int CheckSize => Px(14);
    int TextLeft => Px(8) + (_showCheckbox ? CheckSize + Px(6) : 0);
    Rect CheckRect => new(Px(8), (Height - CheckSize) / 2, CheckSize, CheckSize);

    /// <summary>The x extent of a segment's text, in pixels.</summary>
    (int X, int W) SegmentX(FieldSet lay, Seg seg)
    {
        var font = TextFont;
        float x0 = font.MeasureWidth(lay.Text[..seg.Start]), x1 = font.MeasureWidth(lay.Text[..seg.End]);
        return (TextLeft + (int)Math.Round(x0), Math.Max(1, (int)Math.Round(x1 - x0)));
    }

    // ---- Popup ----

    /// <summary>Opens the drop-down calendar (nothing happens when the date is unchecked or the picker disabled).</summary>
    public void DropDown()
    {
        if (OwningForm is not { } form || _dropDown != null || !Editable) return;
        _typed = "";
        var cal = new Calendar
        {
            Culture = _culture,
            Clock = Clock,
            MinDate = _min,
            MaxDate = _max,
            FirstDayOfWeek = _weekStart,
        };
        cal.SelectedDate = Date;
        int w = Math.Max(Width, Px(224)), h = Px(208);
        cal.SetBounds(0, 0, w, h);
        cal.DateClick += _ =>
        {
            Date = cal.Date;
            CloseUp();
        };
        cal.KeyDown += (_, e) =>
        {
            if ((e.Key == Key.Down && e.Modifiers == KeyModifiers.Alt) || (e.Key == Key.F4 && e.Modifiers == KeyModifiers.None))
            {
                CloseUp();
                e.Handled = true;
            }
        };
        cal.PopupClosed += () =>
        {
            if (_dropDown == cal) _dropDown = null;
            Invalidate();
            if (OwningForm is { FocusedControl: null }) Focus();   // a click in the calendar had taken the focus
        };
        _dropDown = cal;
        int x = AbsoluteLeft, y = AbsoluteTop + Height;
        if (y + h > form.Height && AbsoluteTop - h >= 0) y = AbsoluteTop - h;   // no room below: open above the field
        form.OpenPopup(cal, x, y, this);
        Invalidate();
    }

    public void CloseUp()
    {
        if (_dropDown is { } cal && OwningForm is { } form) form.ClosePopup(cal);
        _dropDown = null;
        Invalidate();
    }

    // ---- Input ----

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButton.Left) return;
        if (e.X >= Width - ButtonWidth) { DropDown(); return; }
        if (_showCheckbox && e.X < TextLeft)
        {
            _part = -1;
            Checked = !_checked;
            return;
        }
        if (!Editable) return;
        var lay = Fields();
        if (lay.Editable.Count == 0) return;
        // The segment under the pointer, else the nearest one.
        int best = 0, bestDistance = int.MaxValue;
        for (int i = 0; i < lay.Editable.Count; i++)
        {
            var (x, w) = SegmentX(lay, lay.Segments[lay.Editable[i]]);
            int dist = e.X < x ? x - e.X : e.X > x + w ? e.X - x - w : 0;
            if (dist < bestDistance) { best = i; bestDistance = dist; }
        }
        _typed = "";
        _part = best;
        Invalidate();
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if ((e.Key == Key.Down && e.Modifiers == KeyModifiers.Alt) || (e.Key == Key.F4 && e.Modifiers == KeyModifiers.None))
        {
            DropDown();
            e.Handled = true;
            return;
        }
        if (e.Modifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Left: MovePart(-1); e.Handled = true; break;
            case Key.Right: MovePart(1); e.Handled = true; break;
            case Key.Home when _part >= 0: _typed = ""; _part = 0; Invalidate(); e.Handled = true; break;
            case Key.End when _part >= 0: _typed = ""; _part = Math.Max(0, PartCount - 1); Invalidate(); e.Handled = true; break;
            case Key.Up when Editable && _part >= 0: Step(1); e.Handled = true; break;
            case Key.Down when Editable && _part >= 0: Step(-1); e.Handled = true; break;
            case Key.Space when _showCheckbox && _part < 0: Checked = !_checked; e.Handled = true; break;
        }
    }

    protected internal override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || !Editable || _part < 0) return;
        foreach (char c in e.Text)
        {
            if (c is >= '0' and <= '9') TypeDigit(c);
            else if (!char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && _part + 1 < PartCount) { _typed = ""; _part++; Invalidate(); }
        }
        e.Handled = true;
    }

    protected internal override void OnLostFocus()
    {
        _typed = "";
        base.OnLostFocus();
    }

    // ---- Painting ----

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var r = new Rect(0, 0, Width, Height);
        bool enabled = IsEffectivelyEnabled;
        bool hasDate = enabled && (!_showCheckbox || _checked);
        float radius = canvas.Px(3);
        canvas.FillRoundRect(r, radius, enabled ? t.InputBackground : t.FormBackground);
        canvas.DrawRoundBorder(r, radius, Focused || IsDroppedDown ? t.Accent : IsHovered && enabled ? t.ButtonBorder : t.InputBorder, canvas.Px(1));

        if (_showCheckbox)
        {
            var box = CheckRect;
            canvas.FillRoundRect(box, canvas.Px(2), _checked && enabled ? t.Accent : t.InputBackground);
            canvas.DrawRoundBorder(box, canvas.Px(2), _checked && enabled ? t.Accent : enabled ? t.InputBorder : t.TextDisabled, canvas.Px(1));
            if (_checked) canvas.DrawCheckMark(Canvas.Inflate(box, -canvas.Px(3)), enabled ? t.OnAccent : t.TextDisabled, canvas.Px(2));
            if (Focused && _part < 0 && OwningForm is { ShowFocusCues: true }) canvas.DrawFocusRing(Canvas.Inflate(box, canvas.Px(2)), canvas.Px(4));
        }

        var lay = Fields();
        var font = EffectiveFont;
        int arrowW = ButtonWidth;
        var textRect = new Rect(TextLeft, 0, Math.Max(0, Width - TextLeft - arrowW), Height);
        canvas.Save();
        canvas.ClipRect(textRect);
        canvas.DrawText(lay.Text, font, hasDate ? t.Text : t.TextDisabled, textRect, TextAlign.Left, VerticalAlign.Center);
        if (Focused && hasDate && _part >= 0 && _part < lay.Editable.Count)
        {
            var (x, w) = SegmentX(lay, lay.Segments[lay.Editable[_part]]);
            var hi = new Rect(x - canvas.Px(1), canvas.Px(4), w + canvas.Px(2), Math.Max(0, Height - 2 * canvas.Px(4)));
            canvas.FillRect(hi, t.Accent);
            canvas.ClipRect(hi);
            canvas.DrawText(lay.Text, font, t.OnAccent, textRect, TextAlign.Left, VerticalAlign.Center);
        }
        canvas.Restore();

        canvas.DrawText("⌄", canvas.DefaultFont, enabled ? t.Text : t.TextDisabled,
            new Rect(Width - arrowW, -canvas.Px(3), arrowW - canvas.Px(4), Height), TextAlign.Center, VerticalAlign.Center);
    }
}
