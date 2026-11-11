namespace RadSharp.Designer;

/// <summary>
/// Fuzzy matching for the command palette and quick open: the pattern's characters must appear in the text in order
/// (a case-insensitive subsequence), and the best alignment is scored. Matches at the start of a word (after a space,
/// <c>/ \ . _ -</c>, or at a camel-case hump) and runs of adjacent characters score higher; gaps cost a little.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>A match: its score (higher is better) and the indices in the text of the matched characters.</summary>
    public readonly record struct Match(int Score, int[] Indices);

    const int CharScore = 16, WordStartBonus = 14, CamelBonus = 10, AdjacentBonus = 9, ExactCaseBonus = 1, GapPenalty = 1, LeadingPenalty = 1, MaxLeading = 6, PrefixBonus = 8;

    /// <summary>The best alignment of <paramref name="pattern"/> in <paramref name="text"/>, or null if it isn't a subsequence.
    /// An empty pattern matches everything with score 0.</summary>
    public static Match? Find(string pattern, string text)
    {
        if (pattern.Length == 0) return new Match(0, []);
        int m = pattern.Length, n = text.Length;
        if (m > n) return null;

        // Quick reject: a plain subsequence test.
        int p = 0;
        for (int i = 0; i < n && p < m; i++)
            if (Same(text[i], pattern[p])) p++;
        if (p < m) return null;

        // best[i, j]: the best score with pattern[i] matched at text[j] (and the earlier ones before it); back[i, j]: where pattern[i-1] was.
        const int None = int.MinValue / 2;
        var best = new int[m, n];
        var back = new int[m, n];
        for (int j = 0; j < n; j++)
        {
            best[0, j] = Same(text[j], pattern[0]) ? Here(pattern, 0, text, j) - Math.Min(j, MaxLeading) * LeadingPenalty : None;
            back[0, j] = -1;
        }
        for (int i = 1; i < m; i++)
        {
            // Best earlier match k < j - 1, as best[i-1, k] + k * GapPenalty (the gap cost is then taken off at j).
            int run = None, runAt = -1;
            for (int j = 0; j < n; j++)
            {
                best[i, j] = None;
                if (j >= 2 && best[i - 1, j - 2] > None)
                {
                    int g = best[i - 1, j - 2] + (j - 2) * GapPenalty;
                    if (g > run) { run = g; runAt = j - 2; }
                }
                if (!Same(text[j], pattern[i]) || j < i) continue;
                int score = None, from = -1;
                if (best[i - 1, j - 1] > None) { score = best[i - 1, j - 1] + AdjacentBonus; from = j - 1; }
                if (runAt >= 0 && run - (j - 1) * GapPenalty > score) { score = run - (j - 1) * GapPenalty; from = runAt; }
                if (from < 0) continue;
                best[i, j] = score + Here(pattern, i, text, j);
                back[i, j] = from;
            }
        }

        int end = -1, top = None;
        for (int j = 0; j < n; j++)
            if (best[m - 1, j] > top) { top = best[m - 1, j]; end = j; }
        if (end < 0) return null;
        var indices = new int[m];
        for (int i = m - 1, j = end; i >= 0; i--) { indices[i] = j; j = back[i, j]; }
        if (indices[0] == 0 && text.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)) top += PrefixBonus;
        return new Match(top, indices);
    }

    /// <summary>Just the score (null: no match).</summary>
    public static int? Score(string pattern, string text) => Find(pattern, text)?.Score;

    static bool Same(char a, char b) => a == b || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

    // What matching text[j] as the pattern's character i is worth on its own.
    static int Here(string pattern, int i, string text, int j)
    {
        int s = CharScore;
        if (text[j] == pattern[i]) s += ExactCaseBonus;
        if (j == 0) return s + WordStartBonus;
        char prev = text[j - 1], c = text[j];
        if (prev is ' ' or '/' or '\\' or '.' or '_' or '-' or ':' or '(' or '[') s += WordStartBonus;
        else if (char.IsUpper(c) && !char.IsUpper(prev)) s += CamelBonus;
        else if (char.IsDigit(c) != char.IsDigit(prev)) s += CamelBonus / 2;
        return s;
    }

    /// <summary>Filters and ranks: the items that match, best first (ties keep their order).</summary>
    public static List<(T Item, Match Match)> Rank<T>(string pattern, IEnumerable<T> items, Func<T, string> text)
    {
        var result = new List<(T, Match)>();
        foreach (var item in items)
            if (Find(pattern, text(item)) is { } m) result.Add((item, m));
        // OrderBy is stable.
        return [.. result.OrderByDescending(r => r.Item2.Score)];
    }
}
