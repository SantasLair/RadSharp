using System.Text;
using System.Text.RegularExpressions;

namespace RadSharp.Designer;

/// <summary>What Find in Files looks for.</summary>
/// <param name="FileFilter">
/// Glob patterns separated by <c>;</c> or <c>,</c> (<c>*.cs;*.radform.json</c>); one starting with <c>!</c> excludes
/// (<c>*.cs;!*Designer*</c>). A pattern without a slash is matched against the file name, one with a slash against the
/// path relative to the project. Empty: every text file.
/// </param>
public sealed record FindOptions(string Text, bool MatchCase = false, bool WholeWord = false, bool UseRegex = false, string FileFilter = "")
{
    /// <summary>The regular expression for these options, or null with <paramref name="error"/> set (empty text, bad regex).</summary>
    public Regex? ToRegex(out string? error)
    {
        error = null;
        if (Text.Length == 0) { error = "Nothing to search for."; return null; }
        var pattern = UseRegex ? Text : Regex.Escape(Text);
        if (WholeWord) pattern = @"(?<![\p{L}\p{N}_])(?:" + pattern + @")(?![\p{L}\p{N}_])";
        var flags = RegexOptions.CultureInvariant | RegexOptions.Multiline | (MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try { return new Regex(pattern, flags, TimeSpan.FromSeconds(5)); }
        catch (ArgumentException e) { error = e.Message; return null; }
    }
}

/// <summary>One occurrence. <see cref="Line"/> and <see cref="Column"/> are 1-based, <see cref="Start"/> a 0-based offset into the text that
/// was searched. <see cref="Owner"/>, for a hit in a form file, is the name of the control whose JSON region contains it (empty: the form).</summary>
public sealed record FindHit(string File, int Line, int Column, int Start, int Length, string LineText, string? Owner = null);

/// <summary>The hits in one file.</summary>
public sealed record FileHits(string File, IReadOnlyList<FindHit> Hits);

/// <summary>
/// The search and replace behind Find in Files: pure functions over file lists and text, so they run on a background thread and
/// are tested without the IDE. Files are the caller's (the open projects' folders); folders like <c>bin</c> and <c>obj</c> are skipped.
/// </summary>
public static class FindInFiles
{
    /// <summary>Folders never searched.</summary>
    public static readonly IReadOnlySet<string> SkippedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".vs", ".vscode", ".git", ".idea", "node_modules", "packages" };

    const int MaxLineText = 300;
    const long MaxFileBytes = 4_000_000;

    // ---- Files ----

    /// <summary>The files under <paramref name="roots"/> that pass the filter, skipping <see cref="SkippedFolders"/> (and anything unreadable).</summary>
    public static IEnumerable<string> EnumerateFiles(IEnumerable<string> roots, string filter, CancellationToken cancel = default)
    {
        var glob = Glob.Parse(filter);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                    if (glob.Accepts(f, root, Path.GetFileName(f)) && IsSearchable(f, glob.HasNoInclude) && seen.Add(f)) yield return f;
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                for (int i = dirs.Length - 1; i >= 0; i--)
                    if (!SkippedFolders.Contains(Path.GetFileName(dirs[i]))) pending.Push(dirs[i]);
            }
        }
    }

    static bool IsSearchable(string file, bool defaultFilter)
    {
        if (defaultFilter && !ProjectTreeBuilder.IsTextFile(file)) return false;
        try { return new FileInfo(file).Length <= MaxFileBytes; }
        catch (IOException) { return false; }
    }

    /// <summary>A parsed file filter.</summary>
    public sealed class Glob
    {
        readonly List<Regex> _include = [], _exclude = [];
        readonly List<bool> _includeByPath = [], _excludeByPath = [];

        public bool HasNoInclude => _include.Count == 0;

        public static Glob Parse(string filter)
        {
            var g = new Glob();
            foreach (var raw in (filter ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                bool exclude = raw.StartsWith('!');
                var pattern = exclude ? raw[1..].TrimStart() : raw;
                if (pattern.Length == 0) continue;
                bool byPath = pattern.Contains('/') || pattern.Contains('\\');
                var rx = new Regex("^" + Regex.Escape(pattern.Replace('\\', '/')).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                (exclude ? g._exclude : g._include).Add(rx);
                (exclude ? g._excludeByPath : g._includeByPath).Add(byPath);
            }
            return g;
        }

        /// <summary>Whether <paramref name="file"/> (under <paramref name="root"/>) passes.</summary>
        public bool Accepts(string file, string root, string name)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            bool Hit(List<Regex> list, List<bool> byPath)
            {
                for (int i = 0; i < list.Count; i++)
                    if (list[i].IsMatch(byPath[i] ? relative : name)) return true;
                return false;
            }
            if (Hit(_exclude, _excludeByPath)) return false;
            return _include.Count == 0 || Hit(_include, _includeByPath);
        }
    }

    // ---- Searching text ----

    /// <summary>The occurrences of <paramref name="regex"/> in <paramref name="text"/>, in order. Empty matches are ignored.</summary>
    public static List<FindHit> FindInText(Regex regex, string file, string text, CancellationToken cancel = default)
    {
        var hits = new List<FindHit>();
        bool isForm = file.EndsWith(".radform.json", StringComparison.OrdinalIgnoreCase);
        var lineStarts = LineStarts(text);
        FormRegions? regions = isForm ? FormRegions.Scan(text) : null;
        try
        {
            foreach (Match m in regex.Matches(text))
            {
                cancel.ThrowIfCancellationRequested();
                if (m.Length == 0) continue;
                int line = Array.BinarySearch(lineStarts, m.Index);
                if (line < 0) line = ~line - 1;
                int start = lineStarts[line];
                int end = line + 1 < lineStarts.Length ? lineStarts[line + 1] : text.Length;
                var lineText = text[start..end].TrimEnd('\r', '\n').Trim();
                if (lineText.Length > MaxLineText) lineText = lineText[..MaxLineText] + "…";
                hits.Add(new FindHit(file, line + 1, m.Index - start + 1, m.Index, m.Length, lineText, regions?.OwnerAt(m.Index)));
            }
        }
        catch (RegexMatchTimeoutException) { }
        return hits;
    }

    static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        return [.. starts];
    }

    /// <summary>
    /// Searches the files. <paramref name="read"/> gives a file's text (the IDE returns an open buffer's text, unsaved edits included,
    /// and null for a file that should be read from disk). Files with no hits are left out; binary and unreadable files are skipped.
    /// </summary>
    public static List<FileHits> Search(FindOptions options, IEnumerable<string> files, Func<string, string?>? read = null,
        CancellationToken cancel = default, Action<int>? progress = null)
    {
        var regex = options.ToRegex(out _);
        var result = new List<FileHits>();
        if (regex == null) return result;
        int scanned = 0;
        foreach (var file in files)
        {
            cancel.ThrowIfCancellationRequested();
            var text = ReadText(file, read);
            if (++scanned % 50 == 0) progress?.Invoke(scanned);
            if (text == null) continue;
            var hits = FindInText(regex, file, text, cancel);
            if (hits.Count > 0) result.Add(new FileHits(file, hits));
        }
        progress?.Invoke(scanned);
        return result;
    }

    /// <summary>The text of a file (the override from <paramref name="read"/> if it gives one), or null if it can't be read or looks binary.</summary>
    public static string? ReadText(string file, Func<string, string?>? read = null)
    {
        if (read?.Invoke(file) is { } open) return open;
        try
        {
            var bytes = File.ReadAllBytes(file);
            int probe = Math.Min(bytes.Length, 8000);
            for (int i = 0; i < probe; i++) if (bytes[i] == 0) return null;
            return File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- Replacing ----

    /// <summary>
    /// The text with every match replaced. <paramref name="replacement"/> is literal unless the search is a regex, where <c>$1</c> and
    /// <c>${name}</c> refer to groups, as in .NET.
    /// </summary>
    public static string ReplaceInText(Regex regex, string text, string replacement, bool literal) =>
        literal
            ? regex.Replace(text, m => m.Length == 0 ? m.Value : replacement)
            : regex.Replace(text, m => m.Length == 0 ? m.Value : m.Result(replacement));

    /// <summary>The number of matches (empty matches excluded).</summary>
    public static int Count(Regex regex, string text)
    {
        int n = 0;
        try { foreach (Match m in regex.Matches(text)) if (m.Length > 0) n++; }
        catch (RegexMatchTimeoutException) { }
        return n;
    }

    /// <summary>Writes text to a file, keeping a UTF-8 byte order mark if it had one.</summary>
    public static void WriteText(string file, string text)
    {
        bool bom = false;
        try
        {
            using var s = File.OpenRead(file);
            Span<byte> head = stackalloc byte[3];
            bom = s.Read(head) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        File.WriteAllText(file, text, new UTF8Encoding(bom));
    }
}

/// <summary>
/// Which control a place in a <c>.radform.json</c> belongs to: the form file is nested objects, each with a <c>"name"</c>, and a hit
/// belongs to the control of the nearest enclosing object that has one (a hit in <c>"margin": {…}</c> or in an event entry belongs to
/// the control that owns it).
/// </summary>
public sealed class FormRegions
{
    readonly List<(int Start, int End, string? Name)> _objects = [];

    FormRegions() { }

    /// <summary>Scans the JSON text for its objects and their names. Tolerant: broken JSON just gives fewer regions.</summary>
    public static FormRegions Scan(string text)
    {
        var regions = new FormRegions();
        var stack = new Stack<int>();   // indexes into _objects of the open objects
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '"':
                {
                    var sb = new StringBuilder();
                    for (i++; i < text.Length && text[i] != '"'; i++)
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) i++;
                        sb.Append(text[i]);
                    }
                    // "name": "value" directly in the innermost object.
                    if (sb.ToString() == "name" && stack.Count > 0 && NextIs(text, i + 1, ':') is { } colon && StringValueAfter(text, colon) is { } value)
                    {
                        int top = stack.Peek();
                        if (regions._objects[top].Name == null) regions._objects[top] = (regions._objects[top].Start, regions._objects[top].End, value);
                    }
                    break;
                }
                case '{':
                    regions._objects.Add((i, text.Length, null));
                    stack.Push(regions._objects.Count - 1);
                    break;
                case '}':
                    if (stack.Count > 0)
                    {
                        int idx = stack.Pop();
                        regions._objects[idx] = (regions._objects[idx].Start, i + 1, regions._objects[idx].Name);
                    }
                    break;
            }
        }
        return regions;
    }

    static int? NextIs(string text, int from, char c)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            return text[i] == c ? i : null;
        }
        return null;
    }

    static string? StringValueAfter(string text, int colon)
    {
        int i = colon + 1;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length || text[i] != '"') return null;
        var sb = new StringBuilder();
        for (i++; i < text.Length && text[i] != '"'; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length) i++;
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    /// <summary>The name of the control owning <paramref name="offset"/>: the nearest enclosing object with a <c>"name"</c>; empty when none does.</summary>
    public string OwnerAt(int offset)
    {
        string? name = null;
        int bestStart = -1;
        // Objects are recorded in order of their opening brace, so the last containing one with a name is the innermost.
        foreach (var (start, end, n) in _objects)
            if (n != null && start <= offset && offset < end && start >= bestStart) { name = n; bestStart = start; }
        return name ?? "";
    }

    /// <summary>The name of the control owning <paramref name="offset"/> in a form file's text.</summary>
    public static string OwnerOf(string text, int offset) => Scan(text).OwnerAt(offset);
}
