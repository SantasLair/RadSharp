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
    public Color Selection { get; init; } = Color.FromHex("#C9DAFB");
    public Color SelectionInactive { get; init; } = Color.FromHex("#E3E6EA");
    public Color Caret { get; init; } = Color.FromHex("#1F2328");
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

    public static Theme Dark { get; } = new()
    {
        FormBackground = Color.FromHex("#1E1E1E"),
        PanelBackground = Color.FromHex("#252526"),
        PanelBorder = Color.FromHex("#3E3E42"),
        Text = Color.FromHex("#E0E0E0"),
        TextDisabled = Color.FromHex("#6A6A6A"),
        ButtonFace = Color.FromHex("#3C3C3C"),
        ButtonFaceHover = Color.FromHex("#454546"),
        ButtonFacePressed = Color.FromHex("#2D2D30"),
        ButtonBorder = Color.FromHex("#555555"),
        Accent = Color.FromHex("#007ACC"),
        FocusRing = Color.FromHex("#007ACC"),
        SplitterFace = Color.FromHex("#3E3E42"),
        InputBackground = Color.FromHex("#3C3C3C"),
        InputBorder = Color.FromHex("#555555"),
        Selection = Color.FromHex("#094771"),
        SelectionInactive = Color.FromHex("#3F3F46"),
        Caret = Color.FromHex("#E0E0E0"),
        ScrollTrack = Color.FromHex("#2D2D30"),
        ScrollThumb = Color.FromHex("#555555"),
        ScrollThumbHover = Color.FromHex("#6A6A6A"),
        OnAccent = Color.FromHex("#FFFFFF"),
        PopupBackground = Color.FromHex("#2D2D30"),
        PopupBorder = Color.FromHex("#555555"),
    };

    public static Theme Current { get; set; } = Default;
}
