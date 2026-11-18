using System.Globalization;

namespace RadSharp.Tests;

/// <summary>Calendar and DateTimePicker through real input. "Today" and the culture are always set explicitly.</summary>
public class DateControlTests
{
    static readonly DateOnly Today = new(2026, 11, 17);
    static readonly CultureInfo US = CultureInfo.GetCultureInfo("en-US");
    static readonly CultureInfo DE = CultureInfo.GetCultureInfo("de-DE");

    static (Form Form, Calendar Cal) NewCalendar(DateOnly? selected = null, DateOnly? min = null, DateOnly? max = null)
    {
        var f = new Form();
        f.SetBounds(0, 0, 500, 400);
        var c = new Calendar { Culture = US, Clock = () => Today };
        c.SetBounds(10, 10, 224, 208);
        if (min is { } lo) c.MinDate = lo;
        if (max is { } hi) c.MaxDate = hi;
        if (selected is { } s) c.SelectedDate = s;
        f.Add(c);
        f.PerformLayout();
        c.Focus();
        return (f, c);
    }

    // Scale 1: header 30, weekday row 22, 6 rows of 26 px, 7 columns of 32 px.
    static (int X, int Y) Cell(Calendar c, DateOnly d)
    {
        for (int i = 0; i < 42; i++)
            if (c.CellDate(i) == d) return (c.AbsoluteLeft + (i % 7) * 32 + 16, c.AbsoluteTop + 52 + (i / 7) * 26 + 13);
        throw new InvalidOperationException($"{d} isn't shown");
    }

    static (int X, int Y) Prev(Calendar c) => (c.AbsoluteLeft + 3 + 12, c.AbsoluteTop + 3 + 12);
    static (int X, int Y) Next(Calendar c) => (c.AbsoluteLeft + c.Width - 3 - 12, c.AbsoluteTop + 3 + 12);

    static (Form Form, DateTimePicker Picker) NewPicker(string format = "d/M/yyyy", DateOnly? date = null, CultureInfo? culture = null)
    {
        var f = new Form();
        f.SetBounds(0, 0, 500, 400);
        var p = new DateTimePicker { Culture = culture ?? US, Clock = () => Today, Format = format };
        p.SetBounds(10, 10, 160, 26);
        if (date is { } d) p.SelectedDate = d;
        f.Add(p);
        var after = new Edit();
        after.SetBounds(10, 60, 100, 26);
        f.Add(after);
        f.PerformLayout();
        p.Focus();
        return (f, p);
    }

    static Calendar Popup(Form f) => Assert.IsType<Calendar>(Assert.Single(f.Popups));

    // ---- Calendar ----

    [Fact]
    public void A_new_calendar_selects_nothing_and_shows_the_month_of_today()
    {
        var (_, c) = NewCalendar();
        Assert.Null(c.SelectedDate);
        Assert.Equal(Today, c.Date);
        Assert.Equal(new DateOnly(2026, 11, 1), c.ViewMonth);
        Assert.Equal(new DateOnly(2026, 11, 1), c.CellDate(0));   // Nov 1 2026 is a Sunday
    }

    [Fact]
    public void The_week_starts_on_the_culture_day_unless_overridden()
    {
        var (_, c) = NewCalendar();
        Assert.Equal(DayOfWeek.Sunday, c.FirstDay);
        c.Culture = DE;
        Assert.Equal(DayOfWeek.Monday, c.FirstDay);
        Assert.Equal(new DateOnly(2026, 10, 26), c.CellDate(0));
        c.FirstDayOfWeek = WeekStart.Saturday;
        Assert.Equal(DayOfWeek.Saturday, c.FirstDay);
        Assert.Equal(new DateOnly(2026, 10, 31), c.CellDate(0));
    }

    [Fact]
    public void Arrow_keys_move_by_a_day_or_a_week_and_the_view_follows()
    {
        var (f, c) = NewCalendar(new DateOnly(2026, 11, 30));
        f.Simulate.Key(Key.Right);
        Assert.Equal(new DateOnly(2026, 12, 1), c.SelectedDate);
        Assert.Equal(new DateOnly(2026, 12, 1), c.ViewMonth);
        f.Simulate.Key(Key.Down);
        Assert.Equal(new DateOnly(2026, 12, 8), c.SelectedDate);
        f.Simulate.Key(Key.Up);
        f.Simulate.Key(Key.Left);
        Assert.Equal(new DateOnly(2026, 11, 30), c.SelectedDate);
        Assert.Equal(new DateOnly(2026, 11, 1), c.ViewMonth);
    }

    [Fact]
    public void Page_keys_move_by_a_month_or_with_Ctrl_a_year_and_cut_the_day_back()
    {
        var (f, c) = NewCalendar(new DateOnly(2026, 1, 31));
        f.Simulate.Key(Key.PageDown);
        Assert.Equal(new DateOnly(2026, 2, 28), c.SelectedDate);
        f.Simulate.Key(Key.PageUp);
        Assert.Equal(new DateOnly(2026, 1, 28), c.SelectedDate);
        f.Simulate.Key(Key.PageDown, KeyModifiers.Ctrl);
        Assert.Equal(new DateOnly(2027, 1, 28), c.SelectedDate);
        f.Simulate.Key(Key.PageUp, KeyModifiers.Ctrl);
        f.Simulate.Key(Key.PageUp, KeyModifiers.Ctrl);
        Assert.Equal(new DateOnly(2025, 1, 28), c.SelectedDate);
    }

    [Fact]
    public void Home_and_End_go_to_the_first_and_last_day_of_the_month()
    {
        var (f, c) = NewCalendar(new DateOnly(2026, 2, 10));
        f.Simulate.Key(Key.End);
        Assert.Equal(new DateOnly(2026, 2, 28), c.SelectedDate);
        f.Simulate.Key(Key.Home);
        Assert.Equal(new DateOnly(2026, 2, 1), c.SelectedDate);
    }

    [Fact]
    public void A_key_with_no_selection_starts_from_today()
    {
        var (f, c) = NewCalendar();
        f.Simulate.Key(Key.Right);
        Assert.Equal(Today.AddDays(1), c.SelectedDate);
    }

    [Fact]
    public void Enter_and_Space_pick_the_date_and_raise_DateClick_even_when_unchanged()
    {
        var (f, c) = NewCalendar(new DateOnly(2026, 11, 20));
        int clicks = 0, changes = 0;
        c.DateClick += _ => clicks++;
        c.Change += _ => changes++;
        f.Simulate.Key(Key.Enter);
        f.Simulate.Key(Key.Space);
        Assert.Equal(2, clicks);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Min_and_max_clamp_the_keyboard_and_disable_days_for_the_mouse()
    {
        var (f, c) = NewCalendar(new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 20));
        for (int i = 0; i < 5; i++) f.Simulate.Key(Key.Left);
        Assert.Equal(new DateOnly(2026, 11, 10), c.SelectedDate);
        for (int i = 0; i < 30; i++) f.Simulate.Key(Key.Right);
        Assert.Equal(new DateOnly(2026, 11, 20), c.SelectedDate);
        f.Simulate.Key(Key.PageDown, KeyModifiers.Ctrl);
        Assert.Equal(new DateOnly(2026, 11, 20), c.SelectedDate);

        var (x, y) = Cell(c, new DateOnly(2026, 11, 25));   // after MaxDate
        int clicks = 0;
        c.DateClick += _ => clicks++;
        f.Simulate.Click(x, y);
        Assert.Equal(new DateOnly(2026, 11, 20), c.SelectedDate);
        Assert.Equal(0, clicks);

        c.MinDate = new DateOnly(2026, 11, 15);   // limits that exclude the selection move it
        c.MaxDate = new DateOnly(2026, 11, 18);
        Assert.Equal(new DateOnly(2026, 11, 18), c.SelectedDate);
    }

    [Fact]
    public void A_click_selects_a_day_and_DateClick_comes_on_release()
    {
        var (f, c) = NewCalendar(new DateOnly(2026, 11, 3));
        var events = new List<string>();
        c.Change += _ => events.Add("change");
        c.DateClick += _ => events.Add("click");
        var (x, y) = Cell(c, new DateOnly(2026, 11, 18));
        f.Simulate.Press(x, y);
        Assert.Equal(new DateOnly(2026, 11, 18), c.SelectedDate);
        Assert.Equal(["change"], events);
        f.Simulate.Release(x, y);
        Assert.Equal(["change", "click"], events);
    }

    [Fact]
    public void Clicking_a_greyed_day_of_the_next_month_selects_it_and_shows_that_month()
    {
        var (f, c) = NewCalendar(new DateOnly(2026, 11, 3));
        var (x, y) = Cell(c, new DateOnly(2026, 12, 2));
        f.Simulate.Click(x, y);
        Assert.Equal(new DateOnly(2026, 12, 2), c.SelectedDate);
        Assert.Equal(new DateOnly(2026, 12, 1), c.ViewMonth);
    }

    [Fact]
    public void The_arrows_and_the_wheel_change_the_month_shown_but_not_the_selection()
    {
        var (f, c) = NewCalendar(new DateOnly(2026, 11, 3));
        var (nx, ny) = Next(c);
        f.Simulate.Click(nx, ny);
        Assert.Equal(new DateOnly(2026, 12, 1), c.ViewMonth);
        var (px, py) = Prev(c);
        f.Simulate.Click(px, py);
        f.Simulate.Click(px, py);
        Assert.Equal(new DateOnly(2026, 10, 1), c.ViewMonth);
        f.Simulate.Wheel(c.AbsoluteLeft + 100, c.AbsoluteTop + 100, -1);   // down = next month
        Assert.Equal(new DateOnly(2026, 11, 1), c.ViewMonth);
        f.Simulate.Wheel(c.AbsoluteLeft + 100, c.AbsoluteTop + 100, 1);
        f.Simulate.Wheel(c.AbsoluteLeft + 100, c.AbsoluteTop + 100, 1);
        Assert.Equal(new DateOnly(2026, 9, 1), c.ViewMonth);
        Assert.Equal(new DateOnly(2026, 11, 3), c.SelectedDate);
    }

    [Fact]
    public void The_arrows_stop_at_the_months_that_hold_selectable_days()
    {
        var (f, c) = NewCalendar(new DateOnly(2026, 11, 12), new DateOnly(2026, 10, 20), new DateOnly(2026, 11, 20));
        var (px, py) = Prev(c);
        f.Simulate.Click(px, py);
        Assert.Equal(new DateOnly(2026, 10, 1), c.ViewMonth);
        f.Simulate.Click(px, py);
        Assert.Equal(new DateOnly(2026, 10, 1), c.ViewMonth);
        var (nx, ny) = Next(c);
        f.Simulate.Click(nx, ny);
        f.Simulate.Click(nx, ny);
        Assert.Equal(new DateOnly(2026, 11, 1), c.ViewMonth);
    }

    [Fact]
    public void The_calendar_reaches_the_ends_of_DateOnly_without_throwing()
    {
        var (f, c) = NewCalendar(DateOnly.MinValue);
        f.Simulate.Key(Key.Left);
        f.Simulate.Key(Key.PageUp, KeyModifiers.Ctrl);
        Assert.Equal(DateOnly.MinValue, c.SelectedDate);
        c.SelectedDate = DateOnly.MaxValue;
        f.Simulate.Key(Key.Right);
        f.Simulate.Key(Key.PageDown, KeyModifiers.Ctrl);
        Assert.Equal(DateOnly.MaxValue, c.SelectedDate);
        Assert.Null(c.CellDate(41));
    }

    // ---- DateTimePicker: display and segment editing ----

    [Fact]
    public void The_date_is_shown_in_the_culture_short_date_pattern_or_the_format()
    {
        var (_, p) = NewPicker("", new DateOnly(2026, 11, 7), US);
        Assert.Equal("11/7/2026", p.DisplayText);
        p.Culture = DE;
        Assert.Equal("07.11.2026", p.DisplayText);
        p.Format = "yyyy-MM-dd";
        Assert.Equal("2026-11-07", p.DisplayText);
        p.Format = "dddd, d MMMM yyyy";
        p.Culture = US;
        Assert.Equal("Saturday, 7 November 2026", p.DisplayText);
        p.Format = "d MMM 'of' yy";
        Assert.Equal("7 Nov of 26", p.DisplayText);
    }

    [Fact]
    public void Without_a_chosen_date_the_picker_shows_the_clock_date()
    {
        var (_, p) = NewPicker();
        Assert.Null(p.SelectedDate);
        Assert.Equal(Today, p.Date);
        Assert.Equal("17/11/2026", p.DisplayText);
        p.Clock = () => new DateOnly(2027, 1, 2);
        Assert.Equal("2/1/2027", p.DisplayText);
    }

    [Fact]
    public void Up_and_Down_change_the_focused_segment_and_Left_Right_move_between_them()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 11, 30));
        int changes = 0;
        p.Change += _ => changes++;
        f.Simulate.Key(Key.Up);                       // the day wraps inside the month
        Assert.Equal(new DateOnly(2026, 11, 1), p.Date);
        f.Simulate.Key(Key.Down);
        Assert.Equal(new DateOnly(2026, 11, 30), p.Date);
        f.Simulate.Key(Key.Right);
        f.Simulate.Key(Key.Up);                       // month 12
        f.Simulate.Key(Key.Up);                       // wraps to January of the same year
        Assert.Equal(new DateOnly(2026, 1, 30), p.Date);
        f.Simulate.Key(Key.Down);                     // December again; the day stays 30
        f.Simulate.Key(Key.Right);
        f.Simulate.Key(Key.Up);
        Assert.Equal(new DateOnly(2027, 12, 30), p.Date);
        f.Simulate.Key(Key.Right);                    // already on the last segment
        f.Simulate.Key(Key.Up);
        Assert.Equal(new DateOnly(2028, 12, 30), p.Date);
        Assert.Equal(7, changes);
    }

    [Fact]
    public void A_day_is_cut_back_when_the_month_gets_shorter()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 1, 31));
        f.Simulate.Key(Key.Right);
        f.Simulate.Key(Key.Up);
        Assert.Equal(new DateOnly(2026, 2, 28), p.Date);
    }

    [Fact]
    public void Typed_digits_fill_a_segment_and_move_on_when_it_is_complete()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 11, 17));
        f.Simulate.Type("5");                         // 50 > 31: the day is complete
        Assert.Equal(new DateOnly(2026, 11, 5), p.Date);
        f.Simulate.Type("1");                         // a month starting with 1 may take a second digit
        Assert.Equal(new DateOnly(2026, 1, 5), p.Date);
        f.Simulate.Type("2");
        Assert.Equal(new DateOnly(2026, 12, 5), p.Date);
        f.Simulate.Type("2031");
        Assert.Equal(new DateOnly(2031, 12, 5), p.Date);
    }

    [Fact]
    public void A_typed_number_too_big_for_the_segment_starts_over_with_the_new_digit()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 11, 17));
        f.Simulate.Type("3");
        Assert.Equal(3, p.Date.Day);
        f.Simulate.Type("5");                         // 35 doesn't exist: 5 alone
        Assert.Equal(new DateOnly(2026, 11, 5), p.Date);
    }

    [Fact]
    public void Typing_the_day_31_and_then_a_short_month_cuts_the_day_back()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 10, 1));
        f.Simulate.Type("31");
        Assert.Equal(new DateOnly(2026, 10, 31), p.Date);
        f.Simulate.Type("11");
        Assert.Equal(new DateOnly(2026, 11, 30), p.Date);
    }

    [Fact]
    public void A_leading_zero_changes_nothing_until_the_next_digit()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 10, 17));
        f.Simulate.Type("0");
        Assert.Equal(new DateOnly(2026, 10, 17), p.Date);
        f.Simulate.Type("5");                         // "05": complete, on to the month
        Assert.Equal(new DateOnly(2026, 10, 5), p.Date);
        f.Simulate.Type("9");
        Assert.Equal(new DateOnly(2026, 9, 5), p.Date);
    }

    [Fact]
    public void A_separator_moves_to_the_next_segment()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 11, 17));
        f.Simulate.Type("/");
        f.Simulate.Type("3");
        Assert.Equal(new DateOnly(2026, 3, 17), p.Date);
    }

    [Fact]
    public void Two_digit_years_use_the_culture_pivot()
    {
        // The pivot is pinned: culture data (ICU vs NLS, user settings) may differ between machines.
        var culture = (CultureInfo)US.Clone();
        culture.DateTimeFormat.Calendar.TwoDigitYearMax = 2049;
        var (f, p) = NewPicker("d/M/yy", new DateOnly(2026, 11, 17), culture);
        f.Simulate.Key(Key.End);
        f.Simulate.Type("31");
        Assert.Equal(2031, p.Date.Year);
        Assert.Equal("17/11/31", p.DisplayText);
        f.Simulate.Key(Key.Left);
        f.Simulate.Key(Key.End);
        f.Simulate.Type("99");
        Assert.Equal(1999, p.Date.Year);
    }

    [Fact]
    public void Segments_clamp_to_the_limits()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 11, 17));
        p.MinDate = new DateOnly(2026, 6, 1);
        p.MaxDate = new DateOnly(2026, 12, 31);
        f.Simulate.Key(Key.End);
        f.Simulate.Type("2030");                      // complete and out of range: clamped
        Assert.Equal(new DateOnly(2026, 12, 31), p.Date);
        f.Simulate.Key(Key.Up);                       // 2027 is out of range: ignored
        Assert.Equal(new DateOnly(2026, 12, 31), p.Date);
        p.SelectedDate = new DateOnly(2000, 1, 1);
        Assert.Equal(new DateOnly(2026, 6, 1), p.Date);
        p.Date = new DateOnly(2100, 1, 1);
        Assert.Equal(new DateOnly(2026, 12, 31), p.Date);
    }

    [Fact]
    public void A_click_on_the_text_focuses_the_segment_under_the_pointer()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 11, 17));
        f.Simulate.Click(p.AbsoluteLeft + 65, p.AbsoluteTop + 13);   // the year
        Assert.False(p.IsDroppedDown);
        f.Simulate.Key(Key.Up);
        Assert.Equal(2027, p.Date.Year);
        f.Simulate.Click(p.AbsoluteLeft + 12, p.AbsoluteTop + 13);   // the day
        f.Simulate.Key(Key.Up);
        Assert.Equal(new DateOnly(2027, 11, 18), p.Date);
    }

    // ---- DateTimePicker: the drop-down ----

    [Fact]
    public void F4_and_Alt_Down_open_the_calendar_on_the_date_and_F4_closes_it()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 11, 17));
        f.Simulate.Key(Key.F4);
        Assert.True(p.IsDroppedDown);
        var cal = Popup(f);
        Assert.Equal(new DateOnly(2026, 11, 17), cal.SelectedDate);
        Assert.Equal(p.AbsoluteTop + p.Height, cal.Top);
        f.Simulate.Key(Key.F4);
        Assert.False(p.IsDroppedDown);
        Assert.Empty(f.Popups);
        f.Simulate.Key(Key.Down, KeyModifiers.Alt);
        Assert.True(p.IsDroppedDown);
    }

    [Fact]
    public void Keys_in_the_open_calendar_move_its_highlight_and_Enter_picks_and_closes()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 11, 17));
        int changes = 0;
        p.Change += _ => changes++;
        f.Simulate.Key(Key.F4);
        f.Simulate.Key(Key.Right);
        f.Simulate.Key(Key.PageDown);
        Assert.Equal(new DateOnly(2026, 12, 18), Popup(f).SelectedDate);
        Assert.Equal(new DateOnly(2026, 11, 17), p.Date);   // nothing picked yet
        f.Simulate.Key(Key.Enter);
        Assert.Empty(f.Popups);
        Assert.Equal(new DateOnly(2026, 12, 18), p.Date);
        Assert.Equal(1, changes);
        Assert.True(p.Focused);
    }

    [Fact]
    public void Esc_and_a_click_outside_close_the_calendar_and_keep_the_date()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 11, 17));
        f.Simulate.Key(Key.F4);
        f.Simulate.Key(Key.Right);
        f.Simulate.Key(Key.Escape);
        Assert.Empty(f.Popups);
        Assert.Equal(new DateOnly(2026, 11, 17), p.Date);

        f.Simulate.Key(Key.F4);
        f.Simulate.Click(400, 350);
        Assert.Empty(f.Popups);
        Assert.Equal(new DateOnly(2026, 11, 17), p.Date);
    }

    [Fact]
    public void The_button_opens_the_calendar_and_a_click_on_a_day_picks_it()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 11, 17));
        f.Simulate.Click(p.AbsoluteLeft + p.Width - 10, p.AbsoluteTop + 13);
        Assert.True(p.IsDroppedDown);
        var cal = Popup(f);
        var (x, y) = Cell(cal, new DateOnly(2026, 11, 4));
        f.Simulate.Click(x, y);
        Assert.Empty(f.Popups);
        Assert.Equal(new DateOnly(2026, 11, 4), p.Date);
        Assert.Equal("4/11/2026", p.DisplayText);
    }

    [Fact]
    public void The_calendar_opens_above_the_field_when_there_is_no_room_below()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 11, 17));
        p.SetBounds(10, 360, 160, 26);
        f.PerformLayout();
        f.Simulate.Key(Key.F4);
        var cal = Popup(f);
        Assert.Equal(p.AbsoluteTop - cal.Height, cal.Top);
    }

    [Fact]
    public void The_popup_calendar_gets_the_limits_culture_and_clock()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 11, 17), DE);
        p.MinDate = new DateOnly(2026, 11, 10);
        p.MaxDate = new DateOnly(2026, 11, 20);
        p.FirstDayOfWeek = WeekStart.Wednesday;
        f.Simulate.Key(Key.F4);
        var cal = Popup(f);
        Assert.Equal(new DateOnly(2026, 11, 10), cal.MinDate);
        Assert.Equal(new DateOnly(2026, 11, 20), cal.MaxDate);
        Assert.Equal(DayOfWeek.Wednesday, cal.FirstDay);
        Assert.Equal(Today, cal.Today);
        Assert.Same(DE, cal.Culture);
    }

    // ---- DateTimePicker: no date ----

    [Fact]
    public void Value_is_null_while_the_check_box_is_unchecked_and_the_calendar_stays_shut()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 11, 17));
        Assert.Equal(new DateOnly(2026, 11, 17), p.Value);
        p.ShowCheckbox = true;
        Assert.Equal(new DateOnly(2026, 11, 17), p.Value);   // checked by default
        var changes = new List<DateOnly?>();
        p.Change += _ => changes.Add(p.Value);
        f.Simulate.Click(p.AbsoluteLeft + 14, p.AbsoluteTop + 13);   // the check box
        Assert.False(p.Checked);
        Assert.Null(p.Value);
        f.Simulate.Key(Key.F4);
        Assert.Empty(f.Popups);
        f.Simulate.Type("5");                          // no editing without a date
        Assert.Equal(new DateOnly(2026, 11, 17), p.Date);
        f.Simulate.Click(p.AbsoluteLeft + 14, p.AbsoluteTop + 13);
        Assert.True(p.Checked);
        Assert.Equal(new DateOnly(2026, 11, 17), p.Value);
        Assert.Equal(new DateOnly?[] { null, new DateOnly(2026, 11, 17) }, changes);
    }

    [Fact]
    public void Left_from_the_first_segment_reaches_the_check_box_and_Value_can_be_set_from_code()
    {
        var (f, p) = NewPicker("d/M/yyyy", new DateOnly(2026, 11, 17));
        p.ShowCheckbox = true;
        f.Simulate.Key(Key.Left);
        f.Simulate.Key(Key.Space);
        Assert.Null(p.Value);
        f.Simulate.Key(Key.Right);                     // back to the day, which is greyed
        p.Value = new DateOnly(2027, 2, 3);
        Assert.True(p.Checked);
        Assert.Equal(new DateOnly(2027, 2, 3), p.Value);
        p.Value = null;
        Assert.False(p.Checked);
        Assert.Null(p.Value);
        Assert.Equal(new DateOnly(2027, 2, 3), p.Date);   // the date itself is kept
    }

    [Fact]
    public void Setting_Value_to_null_turns_the_check_box_on()
    {
        var (_, p) = NewPicker();
        Assert.False(p.ShowCheckbox);
        p.Value = null;
        Assert.True(p.ShowCheckbox);
        Assert.False(p.Checked);
    }

    // ---- Form files ----

    static string FormJson(string children) =>
        "{ \"format\": \"radform\", \"version\": \"1.0\", \"root\": { \"class\": \"Form\", \"name\": \"f\", \"width\": 400, \"height\": 300, \"children\": [" + children + "] } }";

    [Fact]
    public void Dates_are_read_from_and_written_to_the_form_file_as_ISO_strings_and_defaults_are_omitted()
    {
        var (form, warnings) = FormReader.Load(FormJson("""
            { "class": "Calendar", "name": "cal", "date": "2026-02-03", "minDate": "2026-01-01", "maxDate": "2026-12-31", "firstDayOfWeek": "monday" },
            { "class": "DateTimePicker", "name": "dtp", "date": "2026-05-06", "minDate": "2000-01-01", "showCheckbox": true, "checked": false, "format": "yyyy-MM-dd", "firstDayOfWeek": "saturday" },
            { "class": "DateTimePicker", "name": "plain" }
            """));
        Assert.Empty(warnings);
        var cal = (Calendar)form.FindControl("cal")!;
        var dtp = (DateTimePicker)form.FindControl("dtp")!;
        Assert.Equal(new DateOnly(2026, 2, 3), cal.SelectedDate);
        Assert.Equal(new DateOnly(2026, 12, 31), cal.MaxDate);
        Assert.Equal(WeekStart.Monday, cal.FirstDayOfWeek);
        Assert.Equal(new DateOnly(2026, 5, 6), dtp.SelectedDate);
        Assert.False(dtp.Checked);
        Assert.Null(dtp.Value);
        Assert.Equal("yyyy-MM-dd", dtp.Format);

        var saved = FormWriter.Save(form);
        Assert.Contains("\"date\": \"2026-02-03\"", saved);
        Assert.Contains("\"minDate\": \"2026-01-01\"", saved);
        Assert.Contains("\"checked\": false", saved);
        var plain = saved[saved.IndexOf("\"plain\"", StringComparison.Ordinal)..];
        Assert.DoesNotContain("date", plain, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checked", plain);

        var again = FormReader.Load(saved).Form;
        Assert.Equal(saved, FormWriter.Save(again));
        Assert.Equal(new DateOnly(2026, 5, 6), ((DateTimePicker)again.FindControl("dtp")!).SelectedDate);
    }

    [Fact]
    public void A_bad_date_in_a_form_file_is_a_warning_and_leaves_the_default()
    {
        var (form, warnings) = FormReader.Load(FormJson("""{ "class": "Calendar", "name": "cal", "date": "17/11/2026", "maxDate": "2026-13-01" }"""));
        Assert.Equal(2, warnings.Count);
        var cal = (Calendar)form.FindControl("cal")!;
        Assert.Null(cal.SelectedDate);
        Assert.Equal(DateOnly.MaxValue, cal.MaxDate);
    }

    [Fact]
    public void Date_properties_are_published_with_the_Date_property_type_and_edit_as_ISO_text()
    {
        var cls = ComponentRegistry.Find("DateTimePicker")!;
        foreach (var name in new[] { "date", "minDate", "maxDate" })
            Assert.Equal(PropertyType.Date, cls.FindProperty(name)!.Type);
        var dtp = new DateTimePicker { Clock = () => Today };
        var min = cls.FindProperty("minDate")!;
        Assert.True(min.TryParseValue(dtp, " 2026-03-04 "));
        Assert.Equal(new DateOnly(2026, 3, 4), dtp.MinDate);
        Assert.Equal("2026-03-04", min.FormatValue(dtp));
        Assert.False(min.TryParseValue(dtp, "4 March"));
        Assert.False(min.TryParseValue(dtp, "2026-02-30"));
        var date = cls.FindProperty("date")!;
        Assert.Equal("", date.FormatValue(dtp));
        Assert.True(date.TryParseValue(dtp, "2026-04-01"));
        Assert.Equal("2026-04-01", date.FormatValue(dtp));
        Assert.True(date.TryParseValue(dtp, ""));
        Assert.Null(dtp.SelectedDate);
        Assert.NotNull(ComponentRegistry.Find("Calendar")!.FindEvent("onChange"));
        Assert.NotNull(ComponentRegistry.Find("Calendar")!.FindEvent("onDateClick"));
    }

    [Fact]
    public void The_calendar_works_at_another_scale()
    {
        var f = new Form { Scale = 1.5 };
        f.SetBounds(0, 0, 600, 500);
        var c = new Calendar { Culture = US, Clock = () => Today };
        c.SetBounds(10, 10, 336, 312);
        f.Add(c);
        f.PerformLayout();
        f.Simulate.Click(c.AbsoluteLeft + c.Width - 22, c.AbsoluteTop + 23);   // the next-month arrow
        Assert.Equal(new DateOnly(2026, 12, 1), c.ViewMonth);
    }
}
