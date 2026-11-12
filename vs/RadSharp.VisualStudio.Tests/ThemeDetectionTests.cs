using Xunit;

namespace RadSharp.VisualStudio.Tests
{
    public class ThemeDetectionTests
    {
        // Tool window backgrounds of the built-in VS themes.
        [Theory]
        [InlineData(0x25, 0x25, 0x26, true)]  // Dark
        [InlineData(0x1F, 0x1F, 0x1F, true)]  // Dark (17.x refresh)
        [InlineData(0x00, 0x00, 0x00, true)]  // High contrast black
        [InlineData(0xF5, 0xF5, 0xF5, false)] // Light
        [InlineData(0xFF, 0xFF, 0xFF, false)] // Light / high contrast white
        [InlineData(0xCF, 0xD6, 0xE5, false)] // Blue
        [InlineData(0x55, 0x55, 0x55, true)]
        [InlineData(0x99, 0x99, 0x99, false)]
        public void Classifies_vs_theme_backgrounds(byte r, byte g, byte b, bool dark)
        {
            Assert.Equal(dark, ThemeDetection.IsDark(r, g, b));
            Assert.Equal(dark ? "dark" : null, ThemeDetection.HostThemeFor(r, g, b));
        }

        [Fact]
        public void Luminance_spans_zero_to_one()
        {
            Assert.Equal(0.0, ThemeDetection.Luminance(0, 0, 0), 6);
            Assert.Equal(1.0, ThemeDetection.Luminance(255, 255, 255), 6);
            Assert.True(ThemeDetection.Luminance(0, 255, 0) > ThemeDetection.Luminance(255, 0, 0));
        }
    }
}
