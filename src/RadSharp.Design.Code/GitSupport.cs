using System.Diagnostics;
using System.Text;

namespace RadSharp.Design.Code;

/// <summary>How git sees a path, reduced to what the project tree shows.</summary>
public enum GitState { Clean, Modified, Added, Untracked, Deleted, Renamed, Conflicted }

/// <summary>One line of <c>git status --porcelain=v1 -z</c>: the index and work-tree status letters and the path (relative to the repository root).</summary>
public sealed record GitEntry(char Index, char WorkTree, string Path, string? OriginalPath)
{
    public GitState State => (Index, WorkTree) switch
    {
        ('?', '?') => GitState.Untracked,
        ('U', _) or (_, 'U') or ('A', 'A') or ('D', 'D') => GitState.Conflicted,
        ('R', _) or ('C', _) => GitState.Renamed,
        ('A', _) => GitState.Added,
        ('D', _) or (_, 'D') => GitState.Deleted,
        _ => GitState.Modified,
    };
}

/// <summary>Parsing of <c>git status --porcelain=v1 -z</c> output.</summary>
public static class GitStatusParser
{
    /// <summary>
    /// Entries are <c>XY path\0</c>; a rename or copy is <c>XY new\0old\0</c>. Ignored files (<c>!!</c>) are skipped.
    /// </summary>
    public static IReadOnlyList<GitEntry> Parse(string output)
    {
        var entries = new List<GitEntry>();
        var parts = output.Split('\0');
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length < 4 || p[2] != ' ') continue;
            char x = p[0], y = p[1];
            var path = p[3..];
            string? original = null;
            if ((x is 'R' or 'C' || y is 'R' or 'C') && i + 1 < parts.Length) original = parts[++i];
            if (x == '!' && y == '!') continue;
            entries.Add(new GitEntry(x, y, path, original));
        }
        return entries;
    }
}

/// <summary>A git repository the project lives in. Everything is best-effort: without git, or outside a repository, <see cref="Find"/> returns null.</summary>
public sealed class GitRepo
{
    GitRepo(string root) => Root = root;

    /// <summary>The repository's top-level folder.</summary>
    public string Root { get; }

    /// <summary>The repository containing <paramref name="directory"/>, or null (no git, not a repository, or git failed).</summary>
    public static GitRepo? Find(string directory)
    {
        var top = Run(directory, "rev-parse", "--show-toplevel");
        if (top == null) return null;
        var root = top.Trim();
        return root.Length == 0 ? null : new GitRepo(Path.GetFullPath(root));
    }

    /// <summary>Status of every changed path, keyed by full path; folders are not included (use <see cref="Aggregate"/>).</summary>
    public IReadOnlyDictionary<string, GitState> Status()
    {
        var map = new Dictionary<string, GitState>(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var output = Run(Root, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (output == null) return map;
        foreach (var e in GitStatusParser.Parse(output))
            map[Path.GetFullPath(Path.Combine(Root, e.Path))] = e.State;
        return map;
    }

    /// <summary>The file's content at HEAD, or null if it isn't tracked there (new file, no commits yet).</summary>
    public string? ShowHead(string file)
    {
        var rel = Path.GetRelativePath(Root, Path.GetFullPath(file)).Replace('\\', '/');
        return Run(Root, "show", "HEAD:" + rel);
    }

    /// <summary>
    /// Folder markers: a folder is marked <see cref="GitState.Modified"/> if anything under it changed. Returns the
    /// status of every folder between the changed files and <paramref name="stopAt"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, GitState> Aggregate(IReadOnlyDictionary<string, GitState> files, string stopAt)
    {
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var folders = new Dictionary<string, GitState>(comparer);
        stopAt = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));
        foreach (var path in files.Keys)
            for (var dir = Path.GetDirectoryName(path); dir != null && IsSameOrUnder(dir, stopAt, comparer); dir = Path.GetDirectoryName(dir))
                folders[dir] = GitState.Modified;
        return folders;
    }

    /// <summary>True if <paramref name="dir"/> is <paramref name="root"/> or lies below it (a separator boundary, so "App2" is not under "App").</summary>
    static bool IsSameOrUnder(string dir, string root, StringComparer comparer)
    {
        if (dir.Length < root.Length) return false;
        if (!comparer.Equals(dir[..root.Length], root)) return false;
        return dir.Length == root.Length || dir[root.Length] == Path.DirectorySeparatorChar || dir[root.Length] == Path.AltDirectorySeparatorChar
            || root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar);
    }

    /// <summary>Runs git and returns stdout, or null if git is missing, exits non-zero or takes longer than 15 s.</summary>
    static string? Run(string workingDirectory, params string[] args) =>
        ProcessCapture.Run("git", args, workingDirectory, TimeSpan.FromSeconds(15), new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0" });   // a status refresh must not take the index lock
}

/// <summary>Runs a short-lived process and captures its stdout, never blocking beyond a timeout.</summary>
public static class ProcessCapture
{
    /// <summary>
    /// Runs <paramref name="fileName"/> and returns its stdout, or null if it couldn't start, exited non-zero or ran
    /// longer than <paramref name="timeout"/> (then its whole process tree is killed). stdout and stderr are read
    /// asynchronously so a full pipe can't stall it, and the wait for them is bounded as well.
    /// </summary>
    public static string? Run(string fileName, IEnumerable<string> args, string workingDirectory, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (environment != null) foreach (var (k, v) in environment) psi.Environment[k] = v;
            using var p = Process.Start(psi);
            if (p == null) return null;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue)))
            {
                AppProcess.KillTree(p);
                return null;
            }
            // The process ended; a grandchild that inherited the pipes may still hold them open, so don't wait forever.
            if (!Task.WhenAll(outTask, errTask).Wait(2000)) return null;
            return p.ExitCode == 0 ? outTask.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or AggregateException) { return null; }
    }
}

/// <summary>A small line-based unified diff, enough for "Compare with HEAD".</summary>
public static class TextDiff
{
    /// <summary>A unified diff of two texts (empty if they are equal), with <paramref name="context"/> lines around each change.</summary>
    public static string Unified(string oldText, string newText, string oldLabel, string newLabel, int context = 3)
    {
        var a = Lines(oldText);
        var b = Lines(newText);
        var ops = Diff(a, b);
        if (ops.All(o => o.Kind == ' ')) return "";
        var sb = new StringBuilder();
        sb.Append("--- ").Append(oldLabel).Append('\n').Append("+++ ").Append(newLabel).Append('\n');
        int i = 0;
        while (i < ops.Count)
        {
            while (i < ops.Count && ops[i].Kind == ' ') i++;
            if (i >= ops.Count) break;
            int start = Math.Max(0, i - context);
            int end = i;
            // Extend the hunk while changes are within 2*context of each other.
            int lastChange = i;
            for (int j = i; j < ops.Count; j++)
            {
                if (ops[j].Kind != ' ') lastChange = j;
                else if (j - lastChange > 2 * context) break;
            }
            end = Math.Min(ops.Count, lastChange + context + 1);
            int oldStart = ops.Take(start).Count(o => o.Kind != '+') + 1, newStart = ops.Take(start).Count(o => o.Kind != '-') + 1;
            var hunk = ops.Skip(start).Take(end - start).ToList();
            int oldCount = hunk.Count(o => o.Kind != '+'), newCount = hunk.Count(o => o.Kind != '-');
            sb.Append($"@@ -{(oldCount == 0 ? oldStart - 1 : oldStart)},{oldCount} +{(newCount == 0 ? newStart - 1 : newStart)},{newCount} @@\n");
            foreach (var op in hunk) sb.Append(op.Kind).Append(op.Text).Append('\n');
            i = end;
        }
        return sb.ToString();
    }

    static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    readonly record struct Op(char Kind, string Text);

    static List<Op> Diff(string[] a, string[] b)
    {
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        var am = a[prefix..(a.Length - suffix)];
        var bm = b[prefix..(b.Length - suffix)];
        var ops = new List<Op>();
        for (int i = 0; i < prefix; i++) ops.Add(new(' ', a[i]));
        if ((long)am.Length * bm.Length > 16_000_000)
        {
            foreach (var l in am) ops.Add(new('-', l));
            foreach (var l in bm) ops.Add(new('+', l));
        }
        else
        {
            var lcs = new int[am.Length + 1, bm.Length + 1];
            for (int i = am.Length - 1; i >= 0; i--)
                for (int j = bm.Length - 1; j >= 0; j--)
                    lcs[i, j] = am[i] == bm[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < am.Length && y < bm.Length)
            {
                if (am[x] == bm[y]) { ops.Add(new(' ', am[x])); x++; y++; }
                else if (lcs[x + 1, y] >= lcs[x, y + 1]) ops.Add(new('-', am[x++]));
                else ops.Add(new('+', bm[y++]));
            }
            while (x < am.Length) ops.Add(new('-', am[x++]));
            while (y < bm.Length) ops.Add(new('+', bm[y++]));
        }
        for (int i = a.Length - suffix; i < a.Length; i++) ops.Add(new(' ', a[i]));
        return ops;
    }
}
