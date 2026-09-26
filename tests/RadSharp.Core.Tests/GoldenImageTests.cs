using System.Runtime.CompilerServices;
using RadSharp.Drawing;
using SkiaSharp;

namespace RadSharp.Tests;

/// <summary>
/// Renders forms headlessly and compares them pixel for pixel with stored PNGs in Golden/.
/// Set RADSHARP_UPDATE_GOLDEN=1 to (re)write the stored images from the current output.
/// </summary>
public class GoldenImageTests
{
    static readonly string OutputGoldenDir = Path.Combine(AppContext.BaseDirectory, "Golden");

    [Theory]
    [InlineData("showcase", 1.0, 480, 300)]
    [InlineData("showcase", 1.0, 640, 360)]
    [InlineData("showcase", 1.5, 720, 450)]
    public void Matches_golden_image(string formName, double scale, int width, int height)
    {
        var json = File.ReadAllText(Path.Combine(OutputGoldenDir, formName + ".radform.json"));
        var (form, warnings) = FormReader.Load(json, new FormLoadOptions { Scale = scale });
        Assert.Empty(warnings);
        form.Resize(width, height);

        using var actual = FormRenderer.RenderToBitmap(form);
        var imageName = $"{formName}@{scale * 100:0}%-{width}x{height}.png";
        CompareOrUpdate(actual, imageName);
    }

    [Fact]
    public void Controls_in_live_states_match_golden_image()
    {
        var json = File.ReadAllText(Path.Combine(OutputGoldenDir, "controls.radform.json"));
        var (form, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);

        form.Simulate.Key(Key.Tab);             // keyboard focus (with focus cues) on the first tab stop: chkWrap
        form.Simulate.Key(Key.Tab);             // ...then chkRuler
        var cancel = form.FindControl("btnCancel")!;
        form.Simulate.MoveTo(cancel.AbsoluteLeft + 10, cancel.AbsoluteTop + 10);  // hover
        ((ScrollBox)form.FindControl("sbxList")!).ScrollY = 30;

        var popup = new Panel { Caption = "Popup", Color = Theme.Default.PopupBackground };
        popup.SetBounds(0, 0, 140, 60);
        form.OpenPopup(popup, 40, 190);

        using var actual = FormRenderer.RenderToBitmap(form);
        CompareOrUpdate(actual, "controls@100%-460x320.png");
    }

    static Form EditForm(out Edit name, out Memo notes)
    {
        var form = new Form { Caption = "Edits" };
        form.SetBounds(0, 0, 420, 300);
        Control Put(Control c, int x, int y, int w, int h) { c.SetBounds(x, y, w, h); form.Add(c); return c; }
        name = (Edit)Put(new Edit { Text = "Ada Lovelace" }, 16, 16, 180, 26);
        Put(new Edit { PasswordChar = "•", Text = "hunter2" }, 16, 52, 180, 26);
        Put(new Edit { Placeholder = "Search…" }, 16, 88, 180, 26);
        Put(new Edit { Text = "Read-only value", ReadOnly = true }, 16, 124, 180, 26);
        notes = (Memo)Put(new Memo
        {
            Text = "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs.\n\nSphinx of black quartz, judge my vow.\nHow vexingly quick daft zebras jump!",
        }, 212, 16, 190, 134);
        form.PerformLayout();
        return form;
    }

    [Fact]
    public void Edit_controls_match_golden_image()
    {
        var form = EditForm(out var name, out _);
        name.Focus();
        name.Select(4, 8);   // "Lovelace" selected, caret at the end
        using var actual = FormRenderer.RenderToBitmap(form);
        CompareOrUpdate(actual, "edits@100%-420x300.png");
    }

    /// <summary>
    /// Not a golden image: CJK and emoji come from system fallback fonts, which differ per machine.
    /// Writes a PNG for eyeballing when RADSHARP_RENDER_DIR is set.
    /// </summary>
    [Fact]
    public void Fallback_text_and_composition_render()
    {
        var form = EditForm(out var name, out var notes);
        notes.Text = "日本語のテキスト 😀👨‍👩‍👧 café\n한국어 and 中文";
        name.Text = "Name: ";
        name.Focus();
        form.Simulate.Key(Key.End);
        form.Simulate.Compose("やまだ", 3);
        using var bmp = FormRenderer.RenderToBitmap(form);
        Assert.True(name.IsComposing);
        if (Environment.GetEnvironmentVariable("RADSHARP_RENDER_DIR") is { Length: > 0 } dir)
            File.WriteAllBytes(Path.Combine(dir, "fallback.png"), FormRenderer.EncodePng(bmp));
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

    static int CountDifferentPixels(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return int.MaxValue;
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        int diff = 0;
        for (int i = 0; i < pa.Length; i += 4)
            if (!pa.Slice(i, 4).SequenceEqual(pb.Slice(i, 4))) diff++;
        return diff;
    }
}
