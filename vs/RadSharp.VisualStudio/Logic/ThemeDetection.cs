namespace RadSharp.VisualStudio
{
    /// <summary>Picks the host theme from a VS theme colour. No VS SDK dependency, so it is unit-tested.</summary>
    public static class ThemeDetection
    {
        public const string EnvironmentVariable = "RADSHARP_THEME";

        /// <summary>Relative luminance (WCAG, sRGB) of a colour, 0 (black) to 1 (white).</summary>
        public static double Luminance(byte r, byte g, byte b) =>
            0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);

        /// <summary>True for a dark background. 0.18 is roughly mid-grey (#777) in perceived lightness, so VS Dark, Blue
        /// (Extra Contrast) and high-contrast black count as dark, and Light / Blue as light.</summary>
        public static bool IsDark(byte r, byte g, byte b) => Luminance(r, g, b) < 0.18;

        /// <summary>The RADSHARP_THEME value for the host ("dark"), or null for the default light theme.</summary>
        public static string? HostThemeFor(byte r, byte g, byte b) => IsDark(r, g, b) ? "dark" : null;

        private static double Linear(byte c)
        {
            var s = c / 255.0;
            return s <= 0.04045 ? s / 12.92 : System.Math.Pow((s + 0.055) / 1.055, 2.4);
        }
    }
}
