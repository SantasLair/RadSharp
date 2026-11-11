using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>The status tokens (Error, Warning, Info) exist in both themes and differ between them.</summary>
public class ThemeStatusColourTests
{
    [Fact]
    public void Status_tokens_are_distinct_per_theme()
    {
        foreach (var t in new[] { Theme.Default, Theme.Dark })
            Assert.Equal(3, new[] { t.Error, t.Warning, t.Info }.Distinct().Count());
        Assert.NotEqual(Theme.Default.Error, Theme.Dark.Error);
        Assert.NotEqual(Theme.Default.Info, Theme.Dark.Info);
    }
}
