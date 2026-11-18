using System.Globalization;

namespace RadSharp.Tests;

/// <summary>Cultures whose default calendar isn't Gregorian: dates are Gregorian (DateOnly) everywhere in the controls.</summary>
[Collection("Clipboard")]
public class DateCalendarSystemTests
{
    static readonly DateOnly Today = new(2026, 11, 17);

    static CultureInfo? Make(string name, Type calendar)
    {
        try
        {
            var c = new CultureInfo(name);
            return calendar.IsInstanceOfType(c.DateTimeFormat.Calendar) ? c : null;
        }
        catch (CultureNotFoundException) { return null; }
    }

    [Fact]
    public void Two_digit_years_are_Gregorian_in_a_culture_with_a_Buddhist_calendar()
    {
        var th = Make("th-TH", typeof(ThaiBuddhistCalendar));
        if (th == null) return;   // no such culture data on this machine
        var f = new Form();
        f.SetBounds(0, 0, 400, 200);
        var p = new DateTimePicker { Culture = th, Clock = () => Today, Format = "d/M/yy" };
        p.SetBounds(10, 10, 160, 26);
        p.SelectedDate = new DateOnly(2026, 11, 17);
        f.Add(p);
        f.PerformLayout();
        p.Focus();
        f.Simulate.Key(Key.End);
        f.Simulate.Type("27");
        Assert.Equal(2027, p.Date.Year);
    }

    [Fact]
    public void A_Hijri_culture_paints_months_beyond_its_calendar_range()
    {
        var ar = Make("ar-SA", typeof(UmAlQuraCalendar));
        if (ar == null) return;
        var f = new Form();
        f.SetBounds(0, 0, 400, 300);
        var c = new Calendar { Culture = ar, Clock = () => Today, SelectedDate = new DateOnly(2100, 6, 1) };
        c.SetBounds(10, 10, 224, 208);
        f.Add(c);
        f.PerformLayout();
        using var bmp = RadSharp.Drawing.FormRenderer.RenderToBitmap(f);
        Assert.NotNull(bmp);
    }

    [Fact]
    public void A_Hijri_culture_shows_Gregorian_text_in_the_picker()
    {
        var ar = Make("ar-SA", typeof(UmAlQuraCalendar));
        if (ar == null) return;
        var p = new DateTimePicker { Culture = ar, Clock = () => Today, Format = "yyyy", SelectedDate = new DateOnly(2100, 6, 1) };
        Assert.Equal("2100", p.DisplayText);
    }
}

public class DateFormatRobustnessTests
{
    [Theory]
    [InlineData("")] [InlineData("'")] [InlineData("\\")] [InlineData("ddddd MMMMM yyyyyyy")] [InlineData("'x")]
    [InlineData("hh:mm")] [InlineData("dd dd MM MM yy yy")] [InlineData("ddd")] [InlineData("日 d 月 M")] [InlineData("//")]
    public void Odd_formats_never_throw_while_editing_at_the_date_extremes(string format)
    {
        foreach (var date in new[] { DateOnly.MinValue, DateOnly.MaxValue, new DateOnly(2028, 2, 29) })
        {
            var f = new Form();
            f.SetBounds(0, 0, 500, 400);
            var p = new DateTimePicker { Culture = CultureInfo.GetCultureInfo("en-US"), Clock = () => new DateOnly(2026, 11, 17), Format = format, SelectedDate = date };
            p.SetBounds(10, 10, 160, 26);
            f.Add(p);
            f.PerformLayout();
            p.Focus();
            _ = p.DisplayText;
            foreach (var key in new[] { Key.Right, Key.Up, Key.Up, Key.Right, Key.Down, Key.Down, Key.Left, Key.End, Key.Up, Key.Home, Key.Down, Key.F4, Key.PageUp, Key.PageDown, Key.End, Key.Home, Key.Escape })
                f.Simulate.Key(key);
            f.Simulate.Type("0123456789/.-99999");
            f.Simulate.Click(p.AbsoluteLeft + 30, p.AbsoluteTop + 10);
            f.Simulate.Click(p.AbsoluteLeft + p.Width - 5, p.AbsoluteTop + 10);
            using var bmp = RadSharp.Drawing.FormRenderer.RenderToBitmap(f);
        }
    }
}

public class CalendarExtremesTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void The_calendar_survives_navigation_and_painting_at_both_ends_of_time(int end)
    {
        var d = end == 0 ? DateOnly.MinValue : DateOnly.MaxValue;
        var f = new Form();
        f.SetBounds(0, 0, 400, 300);
        var c = new Calendar { Culture = CultureInfo.GetCultureInfo("en-US"), Clock = () => d, SelectedDate = d };
        c.SetBounds(10, 10, 224, 208);
        f.Add(c);
        f.PerformLayout();
        c.Focus();
        foreach (var key in new[] { Key.Left, Key.Right, Key.Up, Key.Down, Key.PageUp, Key.PageDown, Key.Home, Key.End })
        {
            f.Simulate.Key(key);
            using var bmp = RadSharp.Drawing.FormRenderer.RenderToBitmap(f);
        }
        f.Simulate.Click(c.AbsoluteLeft + 15, c.AbsoluteTop + 15);
        f.Simulate.Click(c.AbsoluteLeft + c.Width - 15, c.AbsoluteTop + 15);
        f.Simulate.Key(Key.PageUp, KeyModifiers.Ctrl);
        f.Simulate.Key(Key.PageDown, KeyModifiers.Ctrl);
    }
}
