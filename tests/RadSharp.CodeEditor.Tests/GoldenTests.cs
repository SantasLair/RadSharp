using System.Runtime.CompilerServices;
using RadSharp.Drawing;
using SkiaSharp;

namespace RadSharp.CodeEditing.Tests;

/// <summary>
/// The editor rendered headlessly and compared pixel for pixel with PNGs in Golden/ (the same convention as
/// RadSharp.Core.Tests). Set RADSHARP_UPDATE_GOLDEN=1 to (re)write them from the current output.
/// </summary>
public class GoldenTests
{
    static readonly string OutputGoldenDir = Path.Combine(AppContext.BaseDirectory, "Golden");

    const string Sample =
        "using System;\n" +
        "\n" +
        "namespace Demo;\n" +
        "\n" +
        "/// <summary>A greeter.</summary>\n" +
        "public sealed class Greeter\n" +
        "{\n" +
        "    readonly string _name = \"World\";\n" +
        "\n" +
        "    public void Greet(int times)\n" +
        "    {\n" +
        "        for (int i = 0; i < times; i++)\n" +
        "            Console.WriteLine($\"Hello, {_name}!\"); // 42\n" +
        "        retrun;\n" +
        "    }\n" +
        "}\n";

    sealed class Fixed(TextSnapshot snapshot, params Decoration[] items) : IDecorationSource
    {
        public DecorationSet? GetDecorations(TextSnapshot current) => new(snapshot, items);
        public event EventHandler<SourceChangedEventArgs>? Changed { add { } remove { } }
    }

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(1.5, false)]
    [InlineData(1.0, true)]
    [InlineData(1.5, true)]
    public void Editor_matches_golden_image(double scale, bool dark)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var form = new Form { Caption = "Code", Scale = scale, Theme = dark ? Theme.Dark : Theme.Default };
        int w = (int)(460 * scale), h = (int)(300 * scale);
        form.SetBounds(0, 0, w, h);
        var ed = new RadSharp.CodeEditor { Text = Sample, Align = Align.Client };
        form.Add(ed);
        form.PerformLayout();
        ed.Focus();

        var snap = ed.Snapshot;
        int Off(int line, int col) => snap.GetOffset(line, col);
        ed.DecorationSources.Add(new Fixed(snap,
            new Decoration(Off(13, 8), 6, DecorationKind.ErrorSquiggle, "Unknown name"),
            new Decoration(Off(7, 20), 5, DecorationKind.WarningSquiggle),
            new Decoration(Off(9, 22), 3, DecorationKind.InfoSquiggle),
            new Decoration(Off(11, 0), 0, DecorationKind.GutterGlyph, Glyph: GutterGlyph.Breakpoint),
            new Decoration(Off(12, 0), 0, DecorationKind.GutterGlyph, Glyph: GutterGlyph.CurrentStatement),
            new Decoration(Off(13, 0), 0, DecorationKind.GutterGlyph, Glyph: GutterGlyph.Error)));

        // A selection across two lines, plus two extra carets.
        ed.SetSelections([
            new TextSelection(Off(5, 7), Off(6, 1)),
            TextSelection.At(Off(9, 16)),
            new TextSelection(Off(10, 4), Off(10, 5)),
            TextSelection.At(Off(12, 12)),
        ]);

        using var bmp = FormRenderer.RenderToBitmap(form);
        CompareOrUpdate(bmp, $"editor-{(dark ? "dark" : "light")}@{scale * 100:0}%-{w}x{h}.png");
    }

    static void CompareOrUpdate(SKBitmap actual, string imageName, [CallerFilePath] string testFile = "")
    {
        var sourceGoldenDir = Path.Combine(Path.GetDirectoryName(testFile)!, "Golden");
        var expectedPath = Path.Combine(OutputGoldenDir, imageName);
        if (Environment.GetEnvironmentVariable("RADSHARP_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(sourceGoldenDir);
            File.WriteAllBytes(Path.Combine(sourceGoldenDir, imageName), FormRenderer.EncodePng(actual));
            return;
        }
        Assert.True(File.Exists(expectedPath), $"No golden image {imageName}. Run the tests with RADSHARP_UPDATE_GOLDEN=1 to create it.");
        using var expected = SKBitmap.Decode(expectedPath).Copy(SKColorType.Bgra8888);
        int diff = CountDifferentPixels(expected, actual);
        if (diff != 0)
        {
            var failDir = Path.Combine(AppContext.BaseDirectory, "golden-failures");
            Directory.CreateDirectory(failDir);
            File.WriteAllBytes(Path.Combine(failDir, imageName), FormRenderer.EncodePng(actual));
            Assert.Fail($"{imageName}: {diff} pixel(s) differ from the golden image; actual output written to {failDir}.");
        }
    }

    static int CountDifferentPixels(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return int.MaxValue;
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        int diff = 0;
        for (int i = 0; i < pa.Length; i += 4)
            if (!pa.Slice(i, 4).SequenceEqual(pb.Slice(i, 4))) diff++;
        return diff;
    }
}
