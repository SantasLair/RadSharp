using System.Globalization;
using RadSharp.Drawing;

namespace RadSharp;

/// <summary>Which weekday starts a calendar week: <see cref="Default"/> takes the culture's first day of the week.</summary>
public enum WeekStart { Default, Sunday, Monday, Tuesday, Wednesday, Thursday, Friday, Saturday }

/// <summary>Calendar arithmetic that stops at <see cref="DateOnly.MinValue"/> and <see cref="DateOnly.MaxValue"/> instead of throwing.</summary>
static class DateMath
{
    static readonly int MaxDay = DateOnly.MaxValue.DayNumber;

    public static DateOnly AddDays(DateOnly d, int days) => DateOnly.FromDayNumber((int)Math.Clamp((long)d.DayNumber + days, 0, MaxDay));

    /// <summary>Moves by whole months; the day of month is kept, or cut back to the last day of a shorter month.</summary>
    public static DateOnly AddMonths(DateOnly d, int months)
    {
        long total = Math.Clamp((long)d.Year * 12 + d.Month - 1 + months, 12L, 9999L * 12 + 11);
        int year = (int)(total / 12), month = (int)(total % 12) + 1;
        return new DateOnly(year, month, Math.Min(d.Day, DateTime.DaysInMonth(year, month)));
    }

    public static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);
    public static DateOnly LastOfMonth(DateOnly d) => new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));
    public static DateOnly Clamp(DateOnly d, DateOnly min, DateOnly max) => d < min ? min : d > max ? max : d;
    /// <summary>
    /// The culture's names and patterns on the Gregorian calendar: <see cref="DateOnly"/> is Gregorian, so a Thai Buddhist or
    /// Hijri default calendar must not shift years (or throw for years outside the Hijri range).
    /// </summary>
    public static DateTimeFormatInfo GregorianFormat(CultureInfo culture)
    {
        var fmt = culture.DateTimeFormat;
        if (fmt.Calendar is GregorianCalendar) return fmt;
        try
        {
            var copy = (DateTimeFormatInfo)fmt.Clone();
            copy.Calendar = new GregorianCalendar();
            return copy;
        }
        catch (ArgumentException) { return DateTimeFormatInfo.InvariantInfo; }
    }

    public static DateOnly Today(Func<DateOnly>? clock) => clock?.Invoke() ?? DateOnly.FromDateTime(DateTime.Now);
}

/// <summary>
/// A month grid for choosing one date (Delphi's TMonthCalendar, date only): a header with the month and year and
/// previous/next buttons, the weekday names, and six weeks of days with the neighbouring months' days greyed.
/// </summary>
/// <remarks>
/// <para><see cref="SelectedDate"/> is null until a date is chosen (nothing highlighted; keys start from today);
/// <see cref="Date"/> is the selection, or today when there is none. Days outside <see cref="MinDate"/>..<see cref="MaxDate"/>
/// are disabled and can't be selected or reached from the keyboard.</para>
/// <para>Keyboard: arrows move one day / week, PageUp/PageDown one month, Ctrl+PageUp/PageDown one year, Home/End the
/// first/last day of the month, Enter or Space picks the date (<see cref="DateClick"/>). Mouse: a click selects a day
/// (<see cref="DateClick"/> on release), the header arrows and the wheel change the month shown without changing the selection.</para>
/// <para>Month and weekday names, the week start and the month/year title follow <see cref="Culture"/> (the current culture
/// when null). <see cref="Clock"/> supplies "today" (the system clock when null); tests set both to stay machine-independent.</para>
/// </remarks>
public class Calendar : Control
{
    DateOnly? _selected;
    DateOnly _min = DateOnly.MinValue, _max = DateOnly.MaxValue;
    DateOnly _view;          // first day of the month shown, once set
    bool _viewSet;
    WeekStart _weekStart;
    CultureInfo? _culture;
    int _hoverCell = -1, _hoverNav;   // -1..41 / -1 previous, 1 next
    int _pressCell = -1;

    public Calendar()
    {
        SetDefaultSize(224, 208);
        TabStop = true;
    }

    /// <summary>The chosen date, or null for none. Setting it clamps to <see cref="MinDate"/>..<see cref="MaxDate"/>, shows its month and raises <see cref="Change"/>.</summary>
    [Published("date", Category = "Behavior")]
    public DateOnly? SelectedDate
    {
        get => _selected;
        set
        {
            if (value is { } v) value = DateMath.Clamp(v, _min, _max);
            if (value == _selected) return;
            _selected = value;
            if (value is { } d) { _view = DateMath.FirstOfMonth(d); _viewSet = true; }
            Invalidate();
            OnChange();
        }
    }

    /// <summary>The selected date, or today (kept inside the limits) when nothing is selected.</summary>
    public DateOnly Date
    {
        get => _selected ?? DateMath.Clamp(Today, _min, _max);
        set => SelectedDate = value;
    }

    /// <summary>The earliest selectable date; earlier days are disabled.</summary>
    [Published(Category = "Behavior")]
    public DateOnly MinDate
    {
        get => _min;
        set
        {
            _min = value;
            if (_max < value) _max = value;
            Reclamp();
        }
    }

    /// <summary>The latest selectable date; later days are disabled.</summary>
    [Published(Category = "Behavior")]
    public DateOnly MaxDate
    {
        get => _max;
        set
        {
            _max = value;
            if (_min > value) _min = value;
            Reclamp();
        }
    }

    /// <summary>The weekday in the first column; <see cref="WeekStart.Default"/> follows the culture.</summary>
    [Published(Category = "Behavior")]
    public WeekStart FirstDayOfWeek { get => _weekStart; set { _weekStart = value; Invalidate(); } }

    /// <summary>Raised when the selected date changes (click, keys, or from code).</summary>
    [Published] public event NotifyEvent? Change;

    /// <summary>Raised when the user picks a date: a click on a day, or Enter/Space (also when it was already selected).</summary>
    [Published] public event NotifyEvent? DateClick;

    /// <summary>The culture for names and the week start; null (the default) is the current culture.</summary>
    public CultureInfo? Culture { get => _culture; set { _culture = value; Invalidate(); } }

    /// <summary>Supplies today's date; null (the default) uses the system clock.</summary>
    public Func<DateOnly>? Clock { get; set; }

    public DateOnly Today => DateMath.Today(Clock);

    /// <summary>The first day of the month currently shown. Setting it moves the view only, within the months that have selectable days.</summary>
    public DateOnly ViewMonth
    {
        get => _viewSet ? _view : DateMath.FirstOfMonth(Date);
        set
        {
            var m = DateMath.FirstOfMonth(value);
            var lo = DateMath.FirstOfMonth(_min); var hi = DateMath.FirstOfMonth(_max);
            m = DateMath.Clamp(m, lo, hi);
            if (_viewSet && m == _view) return;
            _view = m;
            _viewSet = true;
            Invalidate();
        }
    }

    /// <summary>The weekday shown in the first column, with the culture and <see cref="FirstDayOfWeek"/> applied.</summary>
    public DayOfWeek FirstDay => _weekStart == WeekStart.Default ? Fmt.FirstDayOfWeek : (DayOfWeek)((int)_weekStart - 1);

    /// <summary>The date shown in grid cell <paramref name="index"/> (0..41, row by row), or null past the ends of the calendar.</summary>
    public DateOnly? CellDate(int index)
    {
        var first = DateMath.FirstOfMonth(ViewMonth);
        int offset = ((int)first.DayOfWeek - (int)FirstDay + 7) % 7;
        long dn = (long)first.DayNumber - offset + index;
        return dn < 0 || dn > DateOnly.MaxValue.DayNumber ? null : DateOnly.FromDayNumber((int)dn);
    }

    internal event Action? PopupClosed;

    protected internal override void OnPopupClosed()
    {
        base.OnPopupClosed();
        PopupClosed?.Invoke();
    }

    protected virtual void OnChange() => Change?.Invoke(this);
    protected virtual void OnDateClick() => DateClick?.Invoke(this);

    CultureInfo Cult => _culture ?? CultureInfo.CurrentCulture;
    DateTimeFormatInfo Fmt => DateMath.GregorianFormat(Cult);

    void Reclamp()
    {
        if (_selected is { } d && (d < _min || d > _max)) SelectedDate = d;   // the setter clamps
        if (_viewSet) ViewMonth = _view;
        Invalidate();
    }

    bool InRange(DateOnly d) => d >= _min && d <= _max;

    // ---- Geometry (pixels) ----

    int HeaderHeight => Px(30);
    int WeekdayHeight => Px(22);
    int GridTop => HeaderHeight + WeekdayHeight;
    int NavSize => HeaderHeight - Px(6);

    Rect NavRect(int dir) => dir < 0
        ? new Rect(Px(3), Px(3), NavSize, NavSize)
        : new Rect(Width - Px(3) - NavSize, Px(3), NavSize, NavSize);

    Rect CellRect(int index)
    {
        int row = index / 7, col = index % 7, gridH = Math.Max(0, Height - GridTop);
        int x0 = col * Width / 7, x1 = (col + 1) * Width / 7;
        int y0 = GridTop + row * gridH / 6, y1 = GridTop + (row + 1) * gridH / 6;
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>The cell at a point, or -1.</summary>
    int CellAt(int x, int y)
    {
        if (x < 0 || y < GridTop || x >= Width || y >= Height) return -1;
        for (int i = 0; i < 42; i++)
        {
            var r = CellRect(i);
            if (x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height) return i;
        }
        return -1;
    }

    int NavAt(int x, int y)
    {
        foreach (int dir in new[] { -1, 1 })
        {
            var r = NavRect(dir);
            if (x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height) return dir;
        }
        return 0;
    }

    bool CanShift(int dir)
    {
        var first = DateMath.FirstOfMonth(ViewMonth);
        return dir < 0 ? first > _min : DateMath.LastOfMonth(first) < _max;
    }

    void ShiftMonth(int dir)
    {
        if (CanShift(dir)) ViewMonth = DateMath.AddMonths(ViewMonth, dir);
    }

    // ---- Input ----

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int cell = CellAt(e.X, e.Y), nav = NavAt(e.X, e.Y);
        if (cell == _hoverCell && nav == _hoverNav) return;
        _hoverCell = cell; _hoverNav = nav;
        Invalidate();
    }

    protected internal override void OnMouseLeave()
    {
        base.OnMouseLeave();
        _hoverCell = -1; _hoverNav = 0;
        Invalidate();
    }

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressCell = -1;
        if (e.Button != MouseButton.Left) return;
        if (NavAt(e.X, e.Y) is var nav and not 0) { ShiftMonth(nav); return; }
        int cell = CellAt(e.X, e.Y);
        if (cell >= 0 && CellDate(cell) is { } d && InRange(d))
        {
            _pressCell = cell;
            SelectedDate = d;
        }
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int pressed = _pressCell;
        _pressCell = -1;
        if (e.Button == MouseButton.Left && pressed >= 0 && CellAt(e.X, e.Y) == pressed) OnDateClick();
    }

    protected internal override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled || e.DeltaY == 0) return;
        ShiftMonth(e.DeltaY > 0 ? -1 : 1);
        e.Handled = true;
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var from = Date;
        bool ctrl = e.Modifiers == KeyModifiers.Ctrl;
        DateOnly? to = null;
        if (e.Modifiers == KeyModifiers.None)
        {
            to = e.Key switch
            {
                Key.Left => DateMath.AddDays(from, -1),
                Key.Right => DateMath.AddDays(from, 1),
                Key.Up => DateMath.AddDays(from, -7),
                Key.Down => DateMath.AddDays(from, 7),
                Key.PageUp => DateMath.AddMonths(from, -1),
                Key.PageDown => DateMath.AddMonths(from, 1),
                Key.Home => DateMath.FirstOfMonth(from),
                Key.End => DateMath.LastOfMonth(from),
                _ => null,
            };
            if (e.Key is Key.Enter or Key.Space)
            {
                SelectedDate = from;
                OnDateClick();
                e.Handled = true;
                return;
            }
        }
        else if (ctrl)
        {
            to = e.Key switch
            {
                Key.PageUp => DateMath.AddMonths(from, -12),
                Key.PageDown => DateMath.AddMonths(from, 12),
                _ => null,
            };
        }
        if (to is not { } target) return;
        SelectedDate = DateMath.Clamp(target, _min, _max);
        if (_selected == null) SelectedDate = from;   // nothing moved (a limit): the date shown becomes the selection
        e.Handled = true;
    }

    // ---- Painting ----

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        bool enabled = IsEffectivelyEnabled;
        var font = EffectiveFont;
        var r = new Rect(0, 0, Width, Height);
        float radius = canvas.Px(3);
        canvas.FillRoundRect(r, radius, enabled ? t.InputBackground : t.FormBackground);

        // Header: previous / month and year / next.
        var view = ViewMonth;
        var title = view.ToString("y", Fmt);
        var textColor = enabled ? t.Text : t.TextDisabled;
        canvas.DrawText(title, font with { Bold = true }, textColor,
            new Rect(NavSize + Px(6), 0, Math.Max(0, Width - 2 * (NavSize + Px(6))), HeaderHeight), TextAlign.Center, VerticalAlign.Center, ellipsis: true);
        foreach (int dir in new[] { -1, 1 })
        {
            var nr = NavRect(dir);
            bool can = enabled && CanShift(dir);
            if (can && _hoverNav == dir) canvas.FillRoundRect(nr, radius, t.ButtonFaceHover);
            float cx = nr.X + nr.Width / 2f, cy = nr.Y + nr.Height / 2f, a = canvas.Px(4) * 0.9f, s = dir < 0 ? -1 : 1;
            canvas.DrawPolyline([new PointF(cx - s * a / 2, cy - a), new PointF(cx + s * a / 2, cy), new PointF(cx - s * a / 2, cy + a)],
                can ? t.Text : t.TextDisabled, canvas.Px(1) * 1.5f, LineCap.Round, LineJoin.Round);
        }

        // Weekday names and the rule under them.
        var names = Fmt.ShortestDayNames;
        for (int col = 0; col < 7; col++)
        {
            var cr = CellRect(col);
            canvas.DrawText(names[((int)FirstDay + col) % 7], font, t.TextMuted,
                new Rect(cr.X, HeaderHeight, cr.Width, WeekdayHeight), TextAlign.Center, VerticalAlign.Center);
        }
        canvas.FillRect(new Rect(canvas.Px(6), GridTop - canvas.Px(1), Math.Max(0, Width - canvas.Px(12)), canvas.Px(1)), t.PanelBorder);

        // Days.
        var today = Today;
        for (int i = 0; i < 42; i++)
        {
            if (CellDate(i) is not { } d) continue;
            var cell = Canvas.Inflate(CellRect(i), -canvas.Px(1));
            bool ok = enabled && InRange(d), selected = _selected == d, isToday = d == today;
            if (selected) canvas.FillRoundRect(cell, radius, t.Accent);
            else if (ok && _hoverCell == i) canvas.FillRoundRect(cell, radius, t.ButtonFaceHover);
            if (isToday) canvas.DrawRoundBorder(selected ? Canvas.Inflate(cell, -canvas.Px(2)) : cell, radius, selected ? t.OnAccent : t.Accent, canvas.Px(1));
            Color color = selected ? t.OnAccent : !ok ? t.TextDisabled : d.Month != view.Month ? t.TextMuted : t.Text;
            canvas.DrawText(d.Day.ToString(CultureInfo.InvariantCulture), isToday ? font with { Bold = true } : font, color, cell, TextAlign.Center, VerticalAlign.Center);
        }

        canvas.DrawRoundBorder(r, radius, Focused || IsHovered && enabled ? (Focused ? t.Accent : t.ButtonBorder) : t.InputBorder, canvas.Px(1));
        if (Focused && OwningForm is { ShowFocusCues: true })
        {
            int idx = _selected is { } sel ? FindCell(sel) : -1;
            if (idx >= 0) canvas.DrawFocusRing(CellRect(idx), radius);
        }
    }

    int FindCell(DateOnly d)
    {
        for (int i = 0; i < 42; i++) if (CellDate(i) == d) return i;
        return -1;
    }
}
