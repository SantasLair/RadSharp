using RadSharp.Design.Code;
using RadSharp.Drawing;

namespace RadSharp.Designer;

/// <summary>What a node of the project tree stands for.</summary>
public enum ProjectItemKind { Project, Folder, File, Form, CodeBehind }

/// <summary>The <see cref="TreeNode.Tag"/> of every project tree node: what it is, its path (the .csproj, folder, file or .radform.json) and its project.</summary>
public sealed record ProjectItem(ProjectItemKind Kind, string Path, RadProject Project)
{
    public bool IsFile => Kind is ProjectItemKind.File or ProjectItemKind.Form or ProjectItemKind.CodeBehind;
    public string Directory => Kind is ProjectItemKind.Project ? Project.Directory
        : Kind is ProjectItemKind.Folder ? Path : System.IO.Path.GetDirectoryName(Path)!;
}

/// <summary>
/// Builds the Project tool window's tree: each project is a node holding its real folders and files. A form shows
/// under its class name with the code-behind nested below it; git markers (modified, added, untracked…) decorate
/// the rows when the project is in a repository.
/// </summary>
public static class ProjectTreeBuilder
{
    static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".vs", ".vscode", ".git", "packages", ".idea", "node_modules" };

    /// <summary>Extensions the built-in code editor opens (double-click); anything else is left to the file manager.</summary>
    static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".targets", ".json", ".xml", ".md", ".txt", ".config", ".yml", ".yaml", ".sln", ".slnx", ".gitignore", ".editorconfig", ".resx", ".axaml", ".xaml", ".svg",
    };

    public static bool IsTextFile(string path) => TextExtensions.Contains(Path.GetExtension(path)) || Path.GetFileName(path).StartsWith('.');

    /// <summary>A node for <paramref name="project"/>, its subtree filled in.</summary>
    public static TreeNode Build(RadProject project, IReadOnlyDictionary<string, GitState>? git = null, IReadOnlyDictionary<string, GitState>? gitFolders = null, bool expanded = true)
    {
        var root = new TreeNode(project.Name)
        {
            Tag = new ProjectItem(ProjectItemKind.Project, project.ProjectFile, project),
            Icon = ProjectIcons.For(ProjectItemKind.Project, project.ProjectFile, GitState.Clean),
            Expanded = expanded,
        };
        AddDirectory(root, project.Directory, project, git, gitFolders);
        return root;
    }

    static GitState StateOf(IReadOnlyDictionary<string, GitState>? map, string path) =>
        map != null && map.TryGetValue(path, out var s) ? s : GitState.Clean;

    static void AddDirectory(TreeNode parent, string dir, RadProject project, IReadOnlyDictionary<string, GitState>? git, IReadOnlyDictionary<string, GitState>? gitFolders)
    {
        IEnumerable<string> dirs, files;
        try
        {
            dirs = Directory.EnumerateDirectories(dir).Where(d => !Hidden.Contains(Path.GetFileName(d))).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
            files = Directory.EnumerateFiles(dir).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        foreach (var d in dirs)
        {
            var state = StateOf(gitFolders, d);
            var node = Child(parent, Path.GetFileName(d) + (state != GitState.Clean ? "  •" : ""), new ProjectItem(ProjectItemKind.Folder, d, project), state);
            AddDirectory(node, d, project, git, gitFolders);
        }

        var all = files.ToList();
        var codeBehinds = new HashSet<string>(all.Where(f => f.EndsWith(".radform.json", StringComparison.OrdinalIgnoreCase)).Select(RadProject.CodeFileFor), StringComparer.OrdinalIgnoreCase);
        foreach (var f in all)
        {
            if (codeBehinds.Contains(f)) continue;   // shown under its form
            var name = Path.GetFileName(f);
            if (name.EndsWith(".radform.json", StringComparison.OrdinalIgnoreCase))
            {
                var cls = RadProject.FormClassName(f);
                var code = RadProject.CodeFileFor(f);
                var state = StateOf(git, f);
                if (state == GitState.Clean) state = StateOf(git, code);
                var node = Child(parent, cls + (cls == project.MainFormClass ? "   (main form)" : "") + Marker(state), new ProjectItem(ProjectItemKind.Form, f, project), state);
                if (File.Exists(code))
                {
                    var cs = StateOf(git, code);
                    Child(node, Path.GetFileName(code) + Marker(cs), new ProjectItem(ProjectItemKind.CodeBehind, code, project), cs);
                }
            }
            else
            {
                var state = StateOf(git, f);
                Child(parent, name + Marker(state), new ProjectItem(ProjectItemKind.File, f, project), state);
            }
        }
    }

    static TreeNode Child(TreeNode parent, string text, ProjectItem item, GitState state)
    {
        var node = parent.Add(text);
        node.Tag = item;
        node.Icon = ProjectIcons.For(item.Kind, item.Path, state);
        return node;
    }

    /// <summary>The marker letter appended to a row for a git state ("" if clean).</summary>
    public static string Marker(GitState state) => state switch
    {
        GitState.Modified => "  M",
        GitState.Added => "  A",
        GitState.Untracked => "  U",
        GitState.Deleted => "  D",
        GitState.Renamed => "  R",
        GitState.Conflicted => "  !",
        _ => "",
    };

    /// <summary>Every node of a subtree, depth first.</summary>
    public static IEnumerable<TreeNode> Walk(IEnumerable<TreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Walk(n.Nodes)) yield return c;
        }
    }

    public static ProjectItem? ItemOf(TreeNode? node) => node?.Tag as ProjectItem;
}

/// <summary>The project tree's small icons: a coloured swatch with a glyph per kind of item, and a dot for git changes.</summary>
static class ProjectIcons
{
    static readonly Color Folder = Color.FromHex("#D9A441"), Form = Color.FromHex("#3B82F6"), Code = Color.FromHex("#16A34A"),
        Project = Color.FromHex("#8B5CF6"), Other = Color.FromHex("#6B7280"), Dot = Color.FromHex("#F59E0B");

    public static Action<Canvas, Rect> For(ProjectItemKind kind, string path, GitState state)
    {
        var (color, glyph) = kind switch
        {
            ProjectItemKind.Project => (Project, "P"),
            ProjectItemKind.Folder => (Folder, ""),
            ProjectItemKind.Form => (Form, "F"),
            ProjectItemKind.CodeBehind => (Code, "{}"),
            _ => Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".cs" => (Code, "C#"),
                ".csproj" or ".sln" or ".slnx" => (Project, "P"),
                ".json" => (Other, "J"),
                ".md" or ".txt" => (Other, "T"),
                _ => (Other, ""),
            },
        };
        return (canvas, rect) =>
        {
            int size = Math.Min(rect.Width, rect.Height) - canvas.Px(6);
            var box = new Rect(rect.X + (rect.Width - size) / 2, rect.Y + (rect.Height - size) / 2, size, size);
            if (kind == ProjectItemKind.Folder)
            {
                // A tab and a body, like a folder.
                canvas.FillRoundRect(new Rect(box.X, box.Y + size / 5, size, size * 4 / 5), canvas.Px(2), color);
                canvas.FillRect(new Rect(box.X, box.Y + canvas.Px(1), size / 2, size / 4), color);
            }
            else
            {
                canvas.FillRoundRect(box, canvas.Px(3), new Color(color.R, color.G, color.B, 40));
                canvas.DrawRoundBorder(box, canvas.Px(3), color, Math.Max(1, canvas.Px(1)));
                if (glyph.Length > 0)
                {
                    var font = canvas.DefaultBoldFont.WithSize(canvas.DefaultBoldFont.Size * 0.62f);
                    canvas.DrawText(glyph, font, color, box, TextAlign.Center, VerticalAlign.Center);
                }
            }
            if (state != GitState.Clean)
            {
                int d = Math.Max(canvas.Px(5), 4);
                var dotColor = state switch { GitState.Added or GitState.Untracked => Color.FromHex("#16A34A"), GitState.Deleted or GitState.Conflicted => Color.FromHex("#DC2626"), _ => Dot };
                canvas.FillEllipse(new Rect(box.X + box.Width - d, box.Y + box.Height - d, d, d), dotColor);
            }
        };
    }
}
