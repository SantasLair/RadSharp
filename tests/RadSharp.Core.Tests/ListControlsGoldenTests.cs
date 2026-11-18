using System.Runtime.CompilerServices;
using RadSharp.Drawing;
using SkiaSharp;

namespace RadSharp.Tests;

/// <summary>
/// M14 list controls rendered headlessly and compared with stored PNGs in Golden/ (RADSHARP_UPDATE_GOLDEN=1 rewrites them):
/// the editable ComboBox with a selected completion, CheckListBox in each state, and a variable-height ListBox.
/// </summary>
[Collection("Clipboard")]
public class ListControlsGoldenTests
{
    static readonly string OutputGoldenDir = Path.Combine(AppContext.BaseDirectory, "Golden");

    static Form NewForm(string caption, int width = 360, int height = 200)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var form = new Form { Caption = caption };
        form.SetBounds(0, 0, width, height);
        return form;
    }

    static T Put<T>(Form form, T c, int x, int y, int w, int h) where T : Control { c.SetBounds(x, y, w, h); form.Add(c); return c; }

    /// <summary>
    /// An editable combo box that has just completed "Apr" to "Apricot" (focused, "icot" selected, its list open under it
    /// highlighting the match), another one holding free text, and a drop-down-list combo box for comparison.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Editable_combo_boxes_match_golden_image(double scale)
    {
        var form = NewForm("Combos");
        string[] fruit = ["Apple", "Apricot", "Banana", "Blueberry", "Cherry", "Fig"];
        var completing = Put(form, new ComboBox { Items = fruit, Style = ComboBoxStyle.DropDown }, 12, 12, 160, 26);
        Put(form, new ComboBox { Items = fruit, Style = ComboBoxStyle.DropDown, Text = "Dragon fruit" }, 188, 12, 160, 26);
        Put(form, new ComboBox { Items = fruit, ItemIndex = 3 }, 188, 50, 160, 26);
        form.ApplyScale(scale);
        form.PerformLayout();

        form.Simulate.Key(Key.Tab);                      // keyboard focus, with focus cues, on the first combo box
        Assert.True(completing.EditPart!.Focused);
        form.Simulate.Type("a");
        form.Simulate.Type("p");
        form.Simulate.Type("r");
        Assert.Equal("Apricot", completing.Text);
        Assert.Equal("icot", completing.SelectedText);
        completing.DropDown();
        Assert.Equal(1, ((ListBox)form.Popups.Single()).ItemIndex);

        using var actual = FormRenderer.RenderToBitmap(form);
        CompareOrUpdate(actual, $"combos@{scale * 100:0}%-{form.Width}x{form.Height}.png");
    }

    /// <summary>Checked, unchecked, grayed, a multi-selection over them, and the focus cue on the current row.</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Check_list_boxes_match_golden_image(double scale)
    {
        var form = NewForm("Check lists");
        string[] options = ["Autosave", "Backups", "Compression", "Dark theme", "Encryption", "Fast start", "Grid snap", "Hints"];
        var focused = Put(form, new CheckListBox { Items = options, MultiSelect = true, AllowGrayed = true }, 12, 12, 160, 170);
        var inactive = Put(form, new CheckListBox { Items = options }, 188, 12, 160, 170);
        form.ApplyScale(scale);
        form.PerformLayout();

        form.Simulate.Key(Key.Tab);
        Assert.True(focused.Focused);
        focused.Checked[0] = true;
        focused.Checked[3] = true;
        focused.States[1] = CheckState.Grayed;
        focused.Selection.SelectRange(2, 4);             // Compression..Encryption, over a checked and an unchecked row
        focused.Selection.Current = 6;                   // the cue on Grid snap, unselected
        inactive.Checked[1] = true;
        inactive.Checked[4] = true;
        inactive.ItemIndex = 4;

        using var actual = FormRenderer.RenderToBitmap(form);
        CompareOrUpdate(actual, $"checklists@{scale * 100:0}%-{form.Width}x{form.Height}.png");
    }

    /// <summary>
    /// ListBox.Style = Variable: section headers 36 px tall among 22 px rows (owner-drawn), a selected header and a scroll
    /// offset that starts in the middle of a row; beside it a list with a ramp of heights, every row its own.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Variable_height_list_boxes_match_golden_image(double scale)
    {
        var form = NewForm("Variable");
        string[] rows = ["Fruit", "Apple", "Apricot", "Banana", "Vegetables", "Carrot", "Leek", "Pea", "Grains", "Oat", "Rye"];
        bool IsHeader(int i) => rows[i] is "Fruit" or "Vegetables" or "Grains";
        var list = Put(form, new ListBox { Items = rows, ItemHeight = 22 }, 12, 12, 160, 170);
        list.MeasureItem += (_, e) => e.Height = IsHeader(e.Index) ? 36 : 22;
        list.Style = ListBoxStyle.Variable;
        list.DrawItem += (_, canvas, i, rect, selected) =>
        {
            var t = canvas.Theme;
            if (selected) canvas.FillRect(rect, t.Selection);
            else if (IsHeader(i)) canvas.FillRect(rect, t.InputBorder);
            canvas.DrawText(rows[i], list.EffectiveFont, t.Text,
                new Rect(rect.X + canvas.Px(IsHeader(i) ? 8 : 20), rect.Y, rect.Width - canvas.Px(12), rect.Height), TextAlign.Left, VerticalAlign.Center);
        };

        var ramp = Put(form, new ListBox { Items = rows, ItemHeight = 22 }, 188, 12, 160, 170);
        ramp.MeasureItem += (_, e) => e.Height = 22 + e.Index * 4;
        ramp.Style = ListBoxStyle.Variable;
        form.ApplyScale(scale);
        form.PerformLayout();

        form.Simulate.Key(Key.Tab);
        Assert.True(list.Focused);
        list.ItemIndex = 4;                              // the "Vegetables" header
        list.ScrollY = 10;                               // the first row is cut at the top
        ramp.ItemIndex = 6;
        ramp.ScrollY = 30;

        using var actual = FormRenderer.RenderToBitmap(form);
        CompareOrUpdate(actual, $"varlists@{scale * 100:0}%-{form.Width}x{form.Height}.png");
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
