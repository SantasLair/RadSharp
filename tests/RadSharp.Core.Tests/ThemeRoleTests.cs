using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>Button.Primary and Label.Muted: styles that follow the theme instead of a fixed colour.</summary>
public class ThemeRoleTests
{
    static Color Pixel(Form f, int x, int y)
    {
        using var bmp = FormRenderer.RenderToBitmap(f);
        var c = bmp.GetPixel(x, y);
        return new Color(c.Red, c.Green, c.Blue, c.Alpha);
    }

    static Form NewForm(Theme theme, Control c)
    {
        var f = new Form { Theme = theme };
        f.SetBounds(0, 0, 160, 60);
        f.Add(c);
        f.PerformLayout();
        return f;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_primary_button_has_the_theme_accent_face_in_light_and_dark(bool dark)
    {
        var theme = dark ? Theme.Dark : Theme.Default;
        var b = new Button { Caption = "", Primary = true };
        b.SetBounds(20, 10, 80, 28);
        var f = NewForm(theme, b);
        Assert.Equal(theme.Accent, Pixel(f, 24, 24));
    }

    [Fact]
    public void A_primary_button_draws_its_caption_in_OnAccent_and_Color_still_wins()
    {
        var theme = Theme.Default with { OnAccent = Color.FromHex("#FF00FF") };
        var b = new Button { Caption = "████", Primary = true, Font = new Font("Inter", 30) };
        b.SetBounds(0, 0, 160, 60);
        var f = NewForm(theme, b);
        Assert.Contains(Enumerable.Range(20, 120).Select(x => Pixel(f, x, 30)), c => c == theme.OnAccent);
        b.Color = Color.FromHex("#3A7D44");
        Assert.Equal(Color.FromHex("#3A7D44"), Pixel(f, 6, 30));
    }

    [Fact]
    public void A_muted_label_uses_the_theme_TextMuted_and_FontColor_still_wins()
    {
        var theme = Theme.Default with { TextMuted = Color.FromHex("#FF00FF"), FormBackground = Color.FromHex("#000000") };
        var l = new Label { Caption = "████████", Muted = true, FontSize = 40 };
        l.SetBounds(0, 0, 160, 60);
        var f = NewForm(theme, l);
        Assert.Contains(Enumerable.Range(0, 60).Select(x => Pixel(f, x, 30)), c => c == theme.TextMuted);
        l.FontColor = Color.FromHex("#00FF00");
        Assert.DoesNotContain(Enumerable.Range(0, 60).Select(x => Pixel(f, x, 30)), c => c == theme.TextMuted);
    }

    [Fact]
    public void Both_are_published_and_default_to_off()
    {
        Assert.False(new Label().Muted);
        Assert.False(new Button().Primary);
        Assert.NotEqual(Theme.Default.TextMuted, Theme.Default.Text);
        Assert.NotEqual(Theme.Dark.TextMuted, Theme.Dark.Text);
    }
}
