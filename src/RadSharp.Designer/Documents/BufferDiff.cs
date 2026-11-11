using RadSharp.CodeEditing;

namespace RadSharp.Designer;

/// <summary>
/// The edits that turn one text into another, small enough that an editor applying them keeps its carets where they
/// were: the common start and end are dropped, the middle is compared line by line (longest common subsequence), and
/// each changed run of lines becomes one edit trimmed to the characters that differ. Generated changes (a handler
/// stub, a textual rename) come out as one edit per changed place.
/// </summary>
public static class BufferDiff
{
    /// <summary>Above this many lines (in the differing middle, either side) the middle is one edit: the comparison is quadratic.</summary>
    const int MaxLines = 4000;

    public static IReadOnlyList<TextEdit> Edits(string oldText, string newText)
    {
        if (oldText == newText) return [];
        int prefix = CommonPrefix(oldText, newText, 0, 0);
        int suffix = CommonSuffix(oldText, newText, prefix);
        // Widen the middle to whole lines so the line comparison lines up.
        int start = prefix == 0 ? 0 : oldText.LastIndexOf('\n', prefix - 1) + 1;
        int oldEnd = LineEnd(oldText, oldText.Length - suffix);
        int newEnd = newText.Length - (oldText.Length - oldEnd);
        var oldLines = SplitLines(oldText, start, oldEnd);
        var newLines = SplitLines(newText, start, newEnd);
        if (oldLines.Count > MaxLines || newLines.Count > MaxLines || (long)oldLines.Count * newLines.Count > 4_000_000)
            return [Trim(oldText, newText, start, oldEnd - start, newText[start..newEnd])];

        // LCS over the lines (strings compared by value).
        int n = oldLines.Count, m = newLines.Count;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = oldLines[i].Text == newLines[j].Text ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var edits = new List<TextEdit>();
        int a = 0, b = 0;
        while (a < n || b < m)
        {
            if (a < n && b < m && oldLines[a].Text == newLines[b].Text) { a++; b++; continue; }
            int a0 = a, b0 = b;
            while (a < n || b < m)
            {
                if (a < n && b < m && oldLines[a].Text == newLines[b].Text) break;
                if (b < m && (a >= n || lcs[a, b + 1] >= lcs[a + 1, b])) b++;
                else a++;
            }
            int oldStart = a0 < n ? oldLines[a0].Start : oldEnd;
            int oldStop = a < n ? oldLines[a].Start : oldEnd;
            int newStart = b0 < m ? newLines[b0].Start : newEnd;
            int newStop = b < m ? newLines[b].Start : newEnd;
            edits.Add(Trim(oldText, newText, oldStart, oldStop - oldStart, newText[newStart..newStop]));
        }
        return [.. edits.Where(e => e.Length > 0 || e.NewText.Length > 0)];
    }

    static int CommonPrefix(string a, string b, int ia, int ib)
    {
        int n = 0, max = Math.Min(a.Length - ia, b.Length - ib);
        while (n < max && a[ia + n] == b[ib + n]) n++;
        return n;
    }

    static int CommonSuffix(string a, string b, int prefix)
    {
        int n = 0, max = Math.Min(a.Length, b.Length) - prefix;
        while (n < max && a[a.Length - 1 - n] == b[b.Length - 1 - n]) n++;
        return n;
    }

    /// <summary>The end of the line containing <paramref name="at"/> (after its line break), or the text's end.</summary>
    static int LineEnd(string text, int at)
    {
        if (at > 0 && text[at - 1] == '\n') return at;
        int nl = text.IndexOf('\n', at);
        return nl < 0 ? text.Length : nl + 1;
    }

    /// <summary>The lines of [start, end), each with its line break.</summary>
    static List<(int Start, string Text)> SplitLines(string text, int start, int end)
    {
        var lines = new List<(int, string)>();
        int i = start;
        while (i < end)
        {
            int nl = text.IndexOf('\n', i, end - i);
            int stop = nl < 0 ? end : nl + 1;
            lines.Add((i, text[i..stop]));
            i = stop;
        }
        return lines;
    }

    /// <summary>An edit replacing [start, start+length) of the old text with <paramref name="replacement"/>, with the common ends dropped.</summary>
    static TextEdit Trim(string oldText, string newText, int start, int length, string replacement)
    {
        var removed = oldText.AsSpan(start, length);
        int p = 0, max = Math.Min(removed.Length, replacement.Length);
        while (p < max && removed[p] == replacement[p]) p++;
        int s = 0;
        while (s < max - p && removed[^(s + 1)] == replacement[^(s + 1)]) s++;
        return new TextEdit(start + p, length - p - s, replacement[p..(replacement.Length - s)]);
    }
}
