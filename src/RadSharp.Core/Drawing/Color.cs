using System.Globalization;

namespace RadSharp.Drawing;

/// <summary>An sRGB colour with alpha. Form files write it as <c>"#RRGGBB"</c> or <c>"#RRGGBBAA"</c>.</summary>
public readonly record struct Color(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Color Transparent = new(0, 0, 0, 0);
    public static readonly Color Black = new(0, 0, 0);
    public static readonly Color White = new(255, 255, 255);

    public static Color FromHex(string hex)
    {
        if (TryParse(hex, out var c)) return c;
        throw new FormatException($"expected a colour \"#RRGGBB\" or \"#RRGGBBAA\", got \"{hex}\"");
    }

    public static bool TryParse(string? hex, out Color color)
    {
        color = default;
        if (hex is null || hex.Length is not (7 or 9) || hex[0] != '#') return false;
        if (!uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = hex.Length == 7
            ? new Color((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new Color((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>
    /// This colour moved <paramref name="amount"/> (0 to 1) of the way towards <paramref name="other"/>, channel by
    /// channel (alpha too); used to derive hover and pressed shades from a base colour.
    /// </summary>
    public Color Blend(Color other, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
        return new Color(Mix(R, other.R, amount), Mix(G, other.G, amount), Mix(B, other.B, amount), Mix(A, other.A, amount));
    }

    public string ToHex() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    public override string ToString() => ToHex();

    internal SkiaSharp.SKColor ToSK() => new(R, G, B, A);
}
