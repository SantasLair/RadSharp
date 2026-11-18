using System.Globalization;
using System.Runtime.CompilerServices;
using RadSharp.Drawing;
using SkiaSharp;

namespace RadSharp.Tests;

/// <summary>
/// Calendar and DateTimePicker rendered headlessly and compared with stored PNGs in Golden/ (RADSHARP_UPDATE_GOLDEN=1 rewrites
/// them). Today is fixed at 2026-11-17 and the culture at en-US, so the images don't depend on the machine.
/// </summary>
[Collection("Clipboard")]
public class DateGoldenTests
{
    static readonly string OutputGoldenDir = Path.Combine(AppContext.BaseDirectory, "Golden");
    static readonly DateOnly Today = new(2026, 11, 17);
    static readonly CultureInfo US = CultureInfo.GetCultureInfo("en-US");

    static Form NewForm(string caption, int width, int height)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var form = new Form { Caption = caption };
        form.SetBounds(0, 0, width, height);
        return form;
    }

    static T Put<T>(Form form, T c, int x, int y, int w, int h) where T : Control { c.SetBounds(x, y, w, h); form.Add(c); return c; }

    static DateTimePicker Picker(Form form, int x, int y, int w, string format = "") =>
        Put(form, new DateTimePicker { Culture = US, Clock = () => Today, Format = format }, x, y, w, 26);

    /// <summary>
    /// A calendar for November 2026 with today (the 17th, ringed), a selected 19th, Monday first, and days before the 3rd and
    /// after the 26th disabled; beside it a focused picker (the month segment highlighted), a closed picker, and a picker with
    /// its check box off.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Calendar_and_pickers_match_golden_image(double scale)
    {
        var form = NewForm("Dates", 440, 240);
        Put(form, new Calendar
        {
            Culture = US, Clock = () => Today, FirstDayOfWeek = WeekStart.Monday,
            MinDate = new DateOnly(2026, 11, 3), MaxDate = new DateOnly(2026, 11, 26), SelectedDate = new DateOnly(2026, 11, 19),
        }, 12, 12, 224, 208);
        var focused = Picker(form, 252, 12, 160);
        focused.SelectedDate = new DateOnly(2026, 3, 9);
        var closed = Picker(form, 252, 50, 160, "ddd, d MMM yyyy");
        closed.SelectedDate = Today;
        var optional = Picker(form, 252, 88, 160, "yyyy-MM-dd");
        optional.ShowCheckbox = true;
        optional.SelectedDate = new DateOnly(2026, 12, 24);
        optional.Checked = false;
        var checkedOn = Picker(form, 252, 126, 160, "yyyy-MM-dd");
        checkedOn.ShowCheckbox = true;
        checkedOn.SelectedDate = new DateOnly(2026, 12, 24);
        form.ApplyScale(scale);
        form.PerformLayout();

        form.Simulate.Key(Key.Tab);                      // the calendar
        form.Simulate.Key(Key.Tab);                      // the first picker, with focus cues
        Assert.True(focused.Focused);
        form.Simulate.Key(Key.Right);

        using var actual = FormRenderer.RenderToBitmap(form);
        CompareOrUpdate(actual, $"dates@{scale * 100:0}%-{form.Width}x{form.Height}.png");
    }

    /// <summary>A picker with its calendar open under it, the highlight moved to the 24th and a minimum date that greys the early days.</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Open_picker_matches_golden_image(double scale)
    {
        var form = NewForm("Dates open", 300, 290);
        var picker = Picker(form, 12, 12, 170);
        picker.SelectedDate = new DateOnly(2026, 11, 17);
        picker.MinDate = new DateOnly(2026, 11, 5);
        form.ApplyScale(scale);
        form.PerformLayout();

        form.Simulate.Key(Key.Tab);
        form.Simulate.Key(Key.F4);
        form.Simulate.Key(Key.Down);                     // a week on: Nov 24
        var calendar = Assert.IsType<Calendar>(Assert.Single(form.Popups));
        Assert.Equal(new DateOnly(2026, 11, 24), calendar.SelectedDate);

        using var actual = FormRenderer.RenderToBitmap(form);
        CompareOrUpdate(actual, $"dates-open@{scale * 100:0}%-{form.Width}x{form.Height}.png");
    }

    static void CompareOrUpdate(SKBitmap actual, string imageName, [CallerFilePath] string testFile = "")
    {
        var sourceGoldenDir = Path.Combine(Path.GetDirectoryName(testFile)!, "Golden");
        var expectedPath = Path.Combine(OutputGoldenDir, imageName);

        if (Environment.GetEnvironmentVariable("RADSHARP_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllBytes(Path.Combine(sourceGoldenDir, imageName), FormRenderer.EncodePng(actual));
            return;
        }

        Assert.True(File.Exists(expectedPath),
            $"No golden image {imageName}. Run the tests with RADSHARP_UPDATE_GOLDEN=1 to create it.");
        using var expected = SKBitmap.Decode(expectedPath).Copy(SKColorType.Bgra8888);

        int diff = CountDifferentPixels(expected, actual);
        if (diff != 0)
        {
            var failDir = Path.Combine(AppContext.BaseDirectory, "golden-failures");
            Directory.CreateDirectory(failDir);
            File.WriteAllBytes(Path.Combine(failDir, imageName), FormRenderer.EncodePng(actual));
            Assert.Fail($"{imageName}: {diff} pixel(s) differ from the golden image; actual output written to {failDir}.");
        }
    }

    /// <summary>The same tolerance as GoldenImageTests: Skia rounds anti-aliased edges by at most 2 of 255 between x64 and ARM.</summary>
    const int ChannelTolerance = 2;

    static int CountDifferentPixels(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return int.MaxValue;
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        int diff = 0;
        for (int i = 0; i < pa.Length; i += 4)
            for (int c = 0; c < 4; c++)
                if (Math.Abs(pa[i + c] - pb[i + c]) > ChannelTolerance) { diff++; break; }
        return diff;
    }
}
