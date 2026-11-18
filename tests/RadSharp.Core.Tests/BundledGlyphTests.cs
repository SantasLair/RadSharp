using System.Runtime.CompilerServices;
using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>
/// The framework's own text must come from the bundled font. A character Inter lacks is drawn from a system fallback
/// font, which differs per OS (and is a missing-glyph box on a Linux machine without one), so a golden image made on
/// Windows can't match elsewhere. That is how the tree view's "▾"/"▸" expanders broke the Linux and macOS goldens; this
/// catches the next one on any OS, before CI does.
/// </summary>
public class BundledGlyphTests
{
    [Fact]
    public void Framework_source_only_uses_characters_the_bundled_font_has()
    {
        var src = Path.GetFullPath(Path.Combine(SourceDir(), "..", "..", "src"));
        Assert.True(Directory.Exists(src), $"Source folder not found: {src}");

        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            bool cs = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            if (!cs && !file.EndsWith(".radform.json", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = Path.GetRelativePath(src, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj")) continue;

            var lines = File.ReadAllLines(file);
            for (int n = 0; n < lines.Length; n++)
            {
                var line = lines[n];
                if (cs && line.IndexOf("//", StringComparison.Ordinal) is >= 0 and var comment) line = line[..comment];   // comments aren't drawn
                foreach (var rune in line.EnumerateRunes())
                    if (rune.Value > 127 && !(FontFace.Regular.HasGlyph(rune.Value) && FontFace.Bold.HasGlyph(rune.Value)))
                        missing.Add($"{Path.GetRelativePath(src, file)}:{n + 1}: U+{rune.Value:X4} '{rune}'");
            }
        }
        Assert.True(missing.Count == 0,
            "Characters the bundled Inter font doesn't have (draw them as paths, or pick one Inter has):\n" + string.Join("\n", missing));
    }

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
