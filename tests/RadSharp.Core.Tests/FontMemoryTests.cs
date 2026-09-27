using System.Runtime;
using RadSharp.Drawing;

namespace RadSharp.Tests;

public class FontMemoryTests
{
    /// <summary>
    /// Regression: HarfBuzzSharp's Blob.FromStream left HarfBuzz pointing into an unpinned managed array,
    /// so shaping after a compacting GC read moved or freed memory (an access violation seen after the
    /// designer's F9 compile). Font data now lives in native memory; this must shape correctly however
    /// much the GC moves things around.
    /// </summary>
    [Fact]
    public void Shaping_survives_compacting_garbage_collections()
    {
        var face = new FontFace("stress", File.ReadAllBytes(InterPath()));
        var font = new RenderFont(face, 20);
        float expected = font.MeasureWidth("Stress test: shaping after GC ✓");
        for (int round = 0; round < 25; round++)
        {
            // Churn and compact the heap so any unpinned buffer HarfBuzz still used would move.
            var junk = new List<byte[]>();
            for (int i = 0; i < 200; i++) junk.Add(new byte[8192]);
            junk.Clear();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            // A new string each time bypasses the shaping cache, so HarfBuzz really reads the font.
            var shaped = face.Shape($"Round {round}: The quick brown fox");
            Assert.True(shaped.Glyphs.Length > 10);
        }
        Assert.Equal(expected, new RenderFont(face, 20).MeasureWidth("Stress test: shaping after GC ✓"));
    }

    static string InterPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "src", "RadSharp.Core", "Fonts", "Inter-Regular.ttf");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("Inter-Regular.ttf");
    }
}
