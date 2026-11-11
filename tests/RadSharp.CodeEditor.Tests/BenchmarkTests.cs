using System.Diagnostics;
using System.Text;
using RadSharp.Drawing;
using Xunit.Abstractions;

namespace RadSharp.CodeEditing.Tests;

/// <summary>
/// The I1 budgets: a 20 000-line file opens in under 150 ms and typing at its end costs under 4 ms a keystroke.
/// Each measurement takes the best of several runs after a warm-up, and the assertions allow twice the budget so
/// a noisy CI machine doesn't flake them; the measured numbers are written to the test output.
/// </summary>
public class BenchmarkTests(ITestOutputHelper output)
{
    const double OpenBudgetMs = 150, KeystrokeBudgetMs = 4, Slack = 2;

    static string BigFile(int lines)
    {
        var sb = new StringBuilder();
        sb.Append("using System;\n\nnamespace Bench;\n\npublic static class Generated\n{\n");
        int i = 6;
        while (i < lines - 2)
        {
            sb.Append($"    /// <summary>Method number {i}.</summary>\n");
            sb.Append($"    public static int Method{i}(int value) => value * {i} + \"text {i}\".Length; // comment\n");
            i += 2;
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    static (Form, RadSharp.CodeEditor) Open(string text)
    {
        var form = new Form();
        form.SetBounds(0, 0, 1000, 700);
        var ed = new RadSharp.CodeEditor { Align = Align.Client };
        form.Add(ed);
        ed.Text = text;
        form.PerformLayout();
        ed.Focus();
        using (FormRenderer.RenderToBitmap(form)) { }      // first paint: the visible lines are laid out
        return (form, ed);
    }

    [Fact]
    public void Opening_a_20000_line_file()
    {
        var text = BigFile(20_000);
        Open(text);                                          // warm-up: JIT, font loading
        double best = double.MaxValue;
        for (int run = 0; run < 5; run++)
        {
            var sw = Stopwatch.StartNew();
            var (_, ed) = Open(text);
            sw.Stop();
            Assert.True(ed.LineCount >= 19_999);
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        output.WriteLine($"open 20 000 lines + first paint: {best:0.0} ms (budget {OpenBudgetMs} ms)");
        Assert.True(best < OpenBudgetMs * Slack, $"Opening took {best:0.0} ms");
    }

    [Fact]
    public void Typing_at_the_end_of_a_20000_line_file()
    {
        var text = BigFile(20_000);
        var (form, ed) = Open(text);
        form.Simulate.Key(Key.End, KeyModifiers.Ctrl);
        form.Simulate.Key(Key.Enter);
        const string typed = "var answer = Generated.Method100(42); ";
        foreach (var c in typed) form.Simulate.Type(c.ToString());   // warm-up

        double bestEdit = double.MaxValue, bestWithPaint = double.MaxValue;
        for (int run = 0; run < 3; run++)
        {
            var sw = Stopwatch.StartNew();
            foreach (var c in typed) form.Simulate.Type(c.ToString());
            sw.Stop();
            bestEdit = Math.Min(bestEdit, sw.Elapsed.TotalMilliseconds / typed.Length);

            // The same keystrokes followed by a repaint of the whole window each time (what a user sees).
            sw.Restart();
            foreach (var c in typed)
            {
                form.Simulate.Type(c.ToString());
                using var _ = FormRenderer.RenderToBitmap(form);
            }
            sw.Stop();
            bestWithPaint = Math.Min(bestWithPaint, sw.Elapsed.TotalMilliseconds / typed.Length);
        }
        output.WriteLine($"keystroke at the end of 20 000 lines: {bestEdit:0.000} ms (budget {KeystrokeBudgetMs} ms); with a full 1000x700 repaint: {bestWithPaint:0.00} ms");
        Assert.EndsWith(typed, ed.Text.TrimEnd() + " ");
        Assert.True(bestEdit < KeystrokeBudgetMs * Slack, $"A keystroke took {bestEdit:0.000} ms");
    }
}
