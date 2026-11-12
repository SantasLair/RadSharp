using RadSharp.Drawing;
using SkiaSharp;

namespace RadSharp.Tests;

/// <summary>Flat and coloured buttons.</summary>
public class ButtonStyleTests
{
    static readonly Color Backdrop = Color.FromHex("#102030");

    static (Form Form, Button Button) Setup(Action<Button> style)
    {
        var f = new Form { Color = Backdrop };
        f.SetBounds(0, 0, 120, 60);
        var b = new Button { Caption = "OK" };
        b.SetBounds(20, 10, 80, 28);
        style(b);
        f.Add(b);
        f.PerformLayout();
        return (f, b);
    }

    static Color Pixel(Form f, int x, int y)
    {
        using var bmp = FormRenderer.RenderToBitmap(f);
        var c = bmp.GetPixel(x, y);
        return new Color(c.Red, c.Green, c.Blue, c.Alpha);
    }

    // A point inside the button's face, away from the caption and the rounded corners.
    const int FaceX = 24, FaceY = 24;

    [Fact]
    public void A_flat_button_has_no_face_or_border_until_hovered()
    {
        var (f, b) = Setup(x => x.Flat = true);
        Assert.Equal(Backdrop, Pixel(f, FaceX, FaceY));
        Assert.Equal(Backdrop, Pixel(f, 20, 24));   // where the border would be

        f.Simulate.MoveTo(b.AbsoluteLeft + 10, b.AbsoluteTop + 14);
        Assert.Equal(f.Theme.ButtonFaceHover, Pixel(f, FaceX, FaceY));
    }

    [Fact]
    public void A_plain_button_still_has_the_theme_face_and_border()
    {
        var (f, _) = Setup(_ => { });
        Assert.Equal(f.Theme.ButtonFace, Pixel(f, FaceX, FaceY));
        Assert.NotEqual(Backdrop, Pixel(f, 20, 24));
    }

    [Fact]
    public void Color_sets_the_face_and_hover_and_pressed_default_to_shades_of_it()
    {
        var face = Color.FromHex("#3A7D44");
        var (f, b) = Setup(x => x.Color = face);
        Assert.Equal(face, Pixel(f, FaceX, FaceY));

        f.Simulate.MoveTo(b.AbsoluteLeft + 10, b.AbsoluteTop + 14);
        var hover = Pixel(f, FaceX, FaceY);
        Assert.Equal(face.Blend(f.Theme.Text, 0.12), hover);
        Assert.NotEqual(face, hover);

        f.Simulate.Press(b.AbsoluteLeft + 10, b.AbsoluteTop + 14);
        var pressed = Pixel(f, FaceX, FaceY);
        Assert.NotEqual(hover, pressed);
        Assert.NotEqual(face, pressed);
    }

    [Fact]
    public void HoverColor_overrides_the_derived_hover_face()
    {
        var hoverColor = Color.FromHex("#AA2233");
        var (f, b) = Setup(x => { x.Flat = true; x.Color = Color.FromHex("#334455"); x.HoverColor = hoverColor; });
        Assert.Equal(Color.FromHex("#334455"), Pixel(f, FaceX, FaceY));
        f.Simulate.MoveTo(b.AbsoluteLeft + 10, b.AbsoluteTop + 14);
        Assert.Equal(hoverColor, Pixel(f, FaceX, FaceY));
    }

    [Fact]
    public void FontColor_colours_the_caption()
    {
        var (f, _) = Setup(x => { x.Color = Color.White; x.FontColor = Color.FromHex("#FF0000"); });
        using var bmp = FormRenderer.RenderToBitmap(f);
        bool red = false;
        for (int y = 10; y < 38 && !red; y++)
            for (int x = 20; x < 100 && !red; x++)
            {
                var p = bmp.GetPixel(x, y);
                red = p.Red > 200 && p.Green < 60 && p.Blue < 60;
            }
        Assert.True(red, "no caption pixel in the requested colour");
    }

    [Fact]
    public void A_disabled_flat_button_shows_no_face_and_does_not_react_to_hover()
    {
        var (f, b) = Setup(x => { x.Flat = true; x.Enabled = false; });
        f.Simulate.MoveTo(b.AbsoluteLeft + 10, b.AbsoluteTop + 14);
        Assert.Equal(Backdrop, Pixel(f, FaceX, FaceY));
    }

    [Fact]
    public void Button_style_round_trips_through_the_form_file()
    {
        var (f, b) = Setup(x =>
        {
            x.Name = "btn";
            x.Flat = true;
            x.Color = Color.FromHex("#112233");
            x.FontColor = Color.FromHex("#445566");
            x.HoverColor = Color.FromHex("#778899");
        });
        var (loaded, warnings) = FormReader.Load(FormWriter.Save(f));
        Assert.Empty(warnings);
        var copy = Assert.IsType<Button>(loaded.FindControl("btn"));
        Assert.True(copy.Flat);
        Assert.Equal(Color.FromHex("#112233"), copy.Color);
        Assert.Equal(Color.FromHex("#445566"), copy.FontColor);
        Assert.Equal(Color.FromHex("#778899"), copy.HoverColor);
        Assert.NotNull(b);
    }

    [Fact]
    public void Blend_moves_each_channel_towards_the_other_colour()
    {
        Assert.Equal(new Color(100, 50, 0), new Color(100, 50, 0).Blend(Color.White, 0));
        Assert.Equal(Color.White, new Color(100, 50, 0).Blend(Color.White, 1));
        Assert.Equal(new Color(128, 128, 128), Color.Black.Blend(Color.White, 0.5));
    }
}
