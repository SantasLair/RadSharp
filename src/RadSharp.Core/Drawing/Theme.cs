namespace RadSharp.Drawing;

/// <summary>
/// Design tokens every control paints with. Lengths are at 100 % scale; controls scale them with
/// <see cref="Canvas.Px(int)"/>.
/// </summary>
public sealed record Theme
{
    public Color FormBackground { get; init; } = Color.FromHex("#F3F4F6");
    public Color PanelBackground { get; init; } = Color.FromHex("#F3F4F6");
    public Color PanelBorder { get; init; } = Color.FromHex("#D1D5DB");
    public Color Text { get; init; } = Color.FromHex("#1F2328");
    public Color TextDisabled { get; init; } = Color.FromHex("#9CA3AF");
    public Color ButtonFace { get; init; } = Color.FromHex("#FFFFFF");
    public Color ButtonFaceHover { get; init; } = Color.FromHex("#F0F4FF");
    public Color ButtonFacePressed { get; init; } = Color.FromHex("#DCE5FB");
    public Color ButtonBorder { get; init; } = Color.FromHex("#B8BEC8");
    public Color Accent { get; init; } = Color.FromHex("#2F6FEB");
    public Color FocusRing { get; init; } = Color.FromHex("#2F6FEB");
    public Color SplitterFace { get; init; } = Color.FromHex("#E5E7EB");
    public Color InputBackground { get; init; } = Color.FromHex("#FFFFFF");
    public Color InputBorder { get; init; } = Color.FromHex("#9AA1AC");
    public Color ScrollTrack { get; init; } = Color.FromHex("#EEF0F3");
    public Color ScrollThumb { get; init; } = Color.FromHex("#C4C9D1");
    public Color ScrollThumbHover { get; init; } = Color.FromHex("#A7AEB9");
    public Color OnAccent { get; init; } = Color.FromHex("#FFFFFF");
    public Color PopupBackground { get; init; } = Color.FromHex("#FFFFFF");
    public Color PopupBorder { get; init; } = Color.FromHex("#B8BEC8");

    public FontFace FontFace { get; init; } = FontFace.Regular;
    public FontFace BoldFontFace { get; init; } = FontFace.Bold;
    /// <summary>Default text size in pixels at 100 %.</summary>
    public float FontSize { get; init; } = 13;
    public int ButtonRadius { get; init; } = 4;

    public static Theme Default { get; } = new();
}
