using RadSharp.Drawing;
using SkiaSharp;

namespace RadSharp.Tests;

/// <summary>Ellipsis, word wrap and the Label options built on them.</summary>
public class TextHelperTests
{
    static readonly RenderFont F = new(FontFace.Regular, 13);

    [Fact]
    public void Ellipsize_keeps_text_that_fits_and_shortens_text_that_does_not()
    {
        const string text = "Quarterly revenue report";
        Assert.Equal(text, TextLayout.Ellipsize(text, F, F.MeasureWidth(text)));
        Assert.Equal("", TextLayout.Ellipsize("", F, 10));

        float max = F.MeasureWidth(text) / 2;
        var cut = TextLayout.Ellipsize(text, F, max);
        Assert.EndsWith("…", cut);
        Assert.True(F.MeasureWidth(cut) <= max);
        Assert.StartsWith(cut[..^1], text);
        // The cut is the longest one that fits: one more grapheme would not.
        int next = cut.Length;   // body length + 1
        while (text[next - 1] == ' ') next++;   // a trailing space is trimmed, so look past it
        var longer = text[..next] + "…";
        Assert.True(F.MeasureWidth(longer) > max);
    }

    [Fact]
    public void Ellipsize_degrades_to_the_ellipsis_alone_and_then_to_nothing()
    {
        float ellipsisWidth = F.MeasureWidth(TextLayout.EllipsisText);
        Assert.Equal("…", TextLayout.Ellipsize("abcdef", F, ellipsisWidth + 0.01f));
        Assert.Equal("", TextLayout.Ellipsize("abcdef", F, ellipsisWidth / 2));
    }

    [Fact]
    public void Ellipsize_never_splits_a_grapheme()
    {
        const string text = "éééééééé";   // e + combining acute, repeated
        var cut = TextLayout.Ellipsize(text, F, F.MeasureWidth(text) / 2);
        var body = cut[..^1];
        Assert.True(body.Length % 2 == 0, "cut inside an e + accent pair");
    }

    [Fact]
    public void WrapLines_breaks_at_spaces_and_keeps_every_word()
    {
        const string text = "the quick brown fox jumps over the lazy dog and keeps running";
        float max = 90;
        var lines = TextLayout.WrapLines(text, F, max);
        Assert.True(lines.Count > 2);
        Assert.All(lines, l => Assert.True(F.MeasureWidth(l) <= max, l));
        Assert.Equal(text, string.Join(" ", lines));
    }

    [Fact]
    public void WrapLines_honours_newlines_and_splits_overlong_words()
    {
        Assert.Equal(["one", "", "two"], TextLayout.WrapLines("one\r\n\ntwo", F, 500));

        const string word = "Supercalifragilisticexpialidocious";
        float max = F.MeasureWidth(word) / 3;
        var lines = TextLayout.WrapLines(word, F, max);
        Assert.True(lines.Count >= 3);
        Assert.All(lines, l => Assert.True(F.MeasureWidth(l) <= max, l));
        Assert.Equal(word, string.Concat(lines));

        Assert.Equal(["a b c"], TextLayout.WrapLines("a b c", F, 0));   // no width: only newlines split
    }

    static int Ink(Form f, int y0, int y1)
    {
        using var bmp = FormRenderer.RenderToBitmap(f);
        var bg = bmp.GetPixel(0, 0);
        int n = 0;
        for (int y = y0; y < y1 && y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) != bg) n++;
        return n;
    }

    static (Form Form, Label Label) LabelForm(Action<Label> setup)
    {
        var f = new Form { Color = Color.White };
        f.SetBounds(0, 0, 120, 100);
        var l = new Label { Caption = "A fairly long caption that needs several lines" };
        l.SetBounds(0, 0, 100, 80);
        setup(l);
        f.Add(l);
        return (f, l);
    }

    [Fact]
    public void Label_WordWrap_draws_further_lines()
    {
        var (plain, _) = LabelForm(_ => { });
        var (wrapped, label) = LabelForm(l => l.WordWrap = true);
        int below = (int)Math.Ceiling(F.LineHeight) + 4;
        Assert.Equal(0, Ink(plain, below, 100));
        Assert.True(Ink(wrapped, below, 100) > 0);
        Assert.True(label.WordWrap);
    }

    [Fact]
    public void Label_Ellipsis_changes_the_rendering_of_overlong_single_line_text()
    {
        var (plain, _) = LabelForm(_ => { });
        var (trimmed, _) = LabelForm(l => l.Ellipsis = true);
        using var a = FormRenderer.RenderToBitmap(plain);
        using var b = FormRenderer.RenderToBitmap(trimmed);
        Assert.NotEqual(FormRenderer.EncodePng(a), FormRenderer.EncodePng(b));
    }

    [Fact]
    public void Label_text_options_round_trip_through_the_form_file()
    {
        var (f, _) = LabelForm(l => { l.Name = "lbl"; l.WordWrap = true; l.Ellipsis = true; });
        var (loaded, warnings) = FormReader.Load(FormWriter.Save(f));
        Assert.Empty(warnings);
        var label = Assert.IsType<Label>(loaded.FindControl("lbl"));
        Assert.True(label.WordWrap);
        Assert.True(label.Ellipsis);
    }

    [Fact]
    public void Canvas_DrawWrappedText_reports_the_block_height()
    {
        using var bmp = new SKBitmap(FormRenderer.FrameInfo(100, 100));
        using var sk = new SKCanvas(bmp);
        var canvas = new Canvas(sk, Theme.Default, 1);
        var font = new Font("Inter", 13);
        var lines = TextLayout.WrapLines("one two three four five six", canvas.Resolve(font), 60);
        int h = canvas.DrawWrappedText("one two three four five six", font, Color.Black, new Rect(0, 0, 60, 100), TextAlign.Left, VerticalAlign.Top);
        Assert.Equal(lines.Count * (int)Math.Ceiling(canvas.Resolve(font).LineHeight), h);
    }
}
