using RadSharp.Design.Code;

namespace RadSharp.Designer;

/// <summary>
/// The Project tool window (milestone I8): a tree of the project's real folders and files with the file operations
/// (new from template, rename, move, delete, reveal), git markers and "Compare with HEAD", NuGet packages, project
/// properties and solutions. Every action is a <see cref="Command"/> (<c>Project.*</c>) taking an optional
/// <see cref="ProjectItem"/> (or a path) as its parameter, and acting on the tree's selection without one.
/// </summary>
public partial class DesignerForm
{
    string? _solutionFile;
    IReadOnlyList<RadProject>? _solutionProjects;
    readonly Dictionary<string, GitRepo?> _repos = new(StringComparer.OrdinalIgnoreCase);
    IReadOnlyDictionary<string, GitState> _gitStatus = new Dictionary<string, GitState>();
    IReadOnlyDictionary<string, GitState> _gitFolders = new Dictionary<string, GitState>();
    readonly Timer _gitTimer = new() { Name = "tmrGit", Interval = 5000 };
    readonly List<string> _rewritten = [];
    bool _buildingTree, _gitBusy;
    NuGetService? _nuget;

    /// <summary>The NuGet service the package dialog uses (tests replace it with one over a fake runner).</summary>
    public NuGetService NuGet { get => _nuget ??= new NuGetService(); set => _nuget = value; }

    /// <summary>Shows a file or folder in the OS file manager; replaced in tests.</summary>
    public Func<string, bool> RevealHandler { get; set; } = RevealInFileManager;

    /// <summary>The project tree (the Project tool window's content).</summary>
    public TreeView ProjectTree => tvProject;

    /// <summary>The projects of the open solution, or just the open project.</summary>
    public IReadOnlyList<RadProject> Projects => _solutionProjects ?? (_project != null ? [_project] : []);

    /// <summary>The solution file that is open, or null when a single project is.</summary>
    public string? SolutionFilePath => _solutionFile;

    /// <summary>The project whose folder holds <paramref name="path"/> (the deepest one, if projects nest).</summary>
    public RadProject? ProjectOf(string path)
    {
        var full = Path.GetFullPath(path);
        return Projects.Where(p => IsUnder(p.Directory, full)).OrderByDescending(p => p.Directory.Length).FirstOrDefault();
    }

    static bool IsUnder(string dir, string path) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase);

    // ---- Setup (called from the constructor) ----

    void InitProjectTree()
    {
        tvProject.DragMode = DragMode.Automatic;
        tvProject.Change += _ => { if (!_buildingTree && ItemOfNode(tvProject.Selected) is { IsFile: true } item) OpenProjectItem(item, preview: true); };
        tvProject.DoubleClick += _ => { if (ItemOfNode(tvProject.Selected) is { IsFile: true } item) OpenProjectItem(item, preview: false); };
        tvProject.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButton.Right) return;
            if (tvProject.NodeAt(e.Y) is { } node)
            {
                _buildingTree = true;   // right-click selects without previewing
                try { tvProject.Selected = node; }
                finally { _buildingTree = false; }
                tvProject.ContextMenu = ItemOfNode(node) is { } item ? BuildProjectContextMenu(item) : null;
            }
            else tvProject.ContextMenu = null;
        };
        tvProject.DragOver += (_, e) => e.Accept = e.Source == tvProject && DropTarget(tvProject.NodeAt(e.Y)) is { } dir && CanMoveInto(DraggedItem(), dir);
        tvProject.DragDrop += (_, e) =>
        {
            if (DraggedItem() is { } item && DropTarget(tvProject.NodeAt(e.Y)) is { } dir) MoveProjectItem(item.Path, dir);
        };
        tvProject.GotFocus += _ => RefreshGit();
        _gitTimer.Elapsed += _ => RefreshGit();
        Add(_gitTimer);
    }

    static ProjectItem? ItemOfNode(TreeNode? node) => node?.Tag as ProjectItem;

    ProjectItem? DraggedItem() => ItemOfNode(tvProject.Selected) is { Kind: not ProjectItemKind.Project } item ? item : null;

    /// <summary>The folder a drop on <paramref name="node"/> goes into: the folder itself, the project folder, or a file's own folder.</summary>
    static string? DropTarget(TreeNode? node) => ItemOfNode(node)?.Directory;

    static bool CanMoveInto(ProjectItem? item, string targetDir)
    {
        if (item == null) return false;
        var source = item.Kind == ProjectItemKind.Folder ? item.Path : Path.GetDirectoryName(item.Path)!;
        if (item.Kind == ProjectItemKind.Folder && IsUnder(item.Path, targetDir)) return false;   // not into itself
        return !string.Equals(Path.TrimEndingDirectorySeparator(source), Path.TrimEndingDirectorySeparator(targetDir), StringComparison.OrdinalIgnoreCase);
    }

    // ---- The tree ----

    /// <summary>Rebuilds the tree from disk (keeping expansion and selection) with the current git markers.</summary>
    public void RebuildProjectTree()
    {
        lblProject.Caption = _solutionFile != null ? Path.GetFileNameWithoutExtension(_solutionFile) : _project?.Name ?? "";
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in ProjectTreeBuilder.Walk(tvProject.Nodes))
            if (n.Expanded && ItemOfNode(n) is { } i) expanded.Add(i.Path);
        bool first = tvProject.Nodes.Count == 0;
        var selected = ItemOfNode(tvProject.Selected)?.Path;

        _buildingTree = true;
        try
        {
            tvProject.Selected = null;
            tvProject.Nodes.Clear();
            foreach (var p in Projects)
            {
                bool primary = _project != null && SamePath(p.ProjectFile, _project.ProjectFile);
                var root = ProjectTreeBuilder.Build(p, _gitStatus, _gitFolders, expanded: first ? primary || Projects.Count == 1 : expanded.Contains(p.ProjectFile));
                foreach (var n in ProjectTreeBuilder.Walk(root.Nodes))
                    if (ItemOfNode(n) is { } i && expanded.Contains(i.Path)) n.Expanded = true;
                tvProject.Nodes.Add(root);
            }
            if (selected != null && FindNode(selected) is { } sel) tvProject.Selected = sel;
        }
        finally { _buildingTree = false; }
        tvProject.Invalidate();
    }

    /// <summary>The tree node for a path (a project file, folder, file or form file), if it is in the tree.</summary>
    public TreeNode? FindNode(string path) =>
        ProjectTreeBuilder.Walk(tvProject.Nodes).FirstOrDefault(n => ItemOfNode(n) is { } i && SamePath(i.Path, path));

    /// <summary>Selects a path in the tree (expanding its folders) without opening it.</summary>
    public bool SelectInProjectTree(string path)
    {
        if (FindNode(path) is not { } node) return false;
        _buildingTree = true;
        try { tvProject.Selected = node; }
        finally { _buildingTree = false; }
        return true;
    }

    /// <summary>The project tree's selected item.</summary>
    public ProjectItem? SelectedProjectItem => ItemOfNode(tvProject.Selected);

    /// <summary>A single click previews a file, a double-click opens it. A form row opens the designer; any other
    /// file opens in the code editor (or the external IDE, if so configured).</summary>
    public void OpenProjectItem(ProjectItem item, bool preview)
    {
        if (item.Kind == ProjectItemKind.Form) { OpenFormFile(item.Path, preview); return; }
        if (!item.IsFile || !ProjectTreeBuilder.IsTextFile(item.Path)) return;
        if (_ide.CodeNavigation == CodeNavigation.External)
        {
            if (!preview) NavigateToCode(item.Path, 1, 1);
        }
        else OpenCodeFile(item.Path, preview);
    }

    // ---- Commands ----

    void RegisterProjectCommands()
    {
        var c = _commands;
        void Reg(string id, string title, Action<object?> run, Func<bool>? enabled = null, string shortcut = "") =>
            c.Register(new Command(id, title, "Project", run, shortcut, enabled ?? (() => HasProject)));

        Reg("Project.NewClass", "New Class…", p => NewProjectFile(FileTemplate.Class, p));
        Reg("Project.NewInterface", "New Interface…", p => NewProjectFile(FileTemplate.Interface, p));
        Reg("Project.NewFormFile", "New Form…", p => NewProjectFile(FileTemplate.Form, p));
        Reg("Project.NewFolder", "New Folder…", NewProjectFolder);
        Reg("Project.Rename", "Rename…", RenameProjectItem);
        Reg("Project.Move", "Move…", MoveProjectItemCommand);
        Reg("Project.Delete", "Delete…", DeleteProjectItem);
        Reg("Project.Reveal", "Reveal in File Manager", RevealProjectItem);
        Reg("Project.CompareWithHead", "Compare with HEAD", p => CompareWithHead(p), () => HasProject && RepoFor(ProjectItemTarget(null) ?? _project!.ProjectFile) != null);
        Reg("Project.ManagePackages", "Manage NuGet Packages…", _ => ManagePackages());
        Reg("Project.Properties", "Properties…", _ => ShowProjectProperties());
    }

    void AddProjectMenuItems(MenuItem project)
    {
        MenuItem Cmd(string id) { var m = _commands.CreateMenuItem(id); project.Items.Add(m); return m; }
        project.AddSeparator();
        Cmd("Project.NewClass");
        Cmd("Project.NewInterface");
        Cmd("Project.NewFormFile");
        Cmd("Project.NewFolder");
        project.AddSeparator();
        Cmd("Project.Rename");
        Cmd("Project.Move");
        Cmd("Project.Delete");
        Cmd("Project.Reveal");
        project.AddSeparator();
        Cmd("Project.CompareWithHead");
        Cmd("Project.ManagePackages");
        Cmd("Project.Properties");
    }

    /// <summary>The tree's right-click menu for an item (the same commands as the Project menu, with the item as parameter).</summary>
    public PopupMenu BuildProjectContextMenu(ProjectItem item)
    {
        var menu = new PopupMenu();
        bool notRoot = item.Kind != ProjectItemKind.Project;
        Item(menu, "Project.NewClass", parameter: item);
        Item(menu, "Project.NewInterface", parameter: item);
        Item(menu, "Project.NewFormFile", parameter: item);
        Item(menu, "Project.NewFolder", parameter: item);
        Separator(menu);
        Item(menu, "Project.Rename", parameter: item, enabled: notRoot);
        Item(menu, "Project.Move", parameter: item, enabled: notRoot);
        Item(menu, "Project.Delete", parameter: item, enabled: notRoot);
        Separator(menu);
        Item(menu, "Project.Reveal", parameter: item);
        if (item.IsFile) Item(menu, "Project.CompareWithHead", parameter: item, enabled: RepoFor(item.Path) != null);
        if (!notRoot)
        {
            Separator(menu);
            Item(menu, "Project.ManagePackages", parameter: item);
            Item(menu, "Project.Properties", parameter: item);
        }
        return menu;
    }

    /// <summary>The item a command acts on: its parameter (an item or a path), else the tree's selection.</summary>
    ProjectItem? ItemFrom(object? parameter) => parameter switch
    {
        ProjectItem item => item,
        string path when FindNode(path) is { } n => ItemOfNode(n),
        _ => SelectedProjectItem,
    };

    string? ProjectItemTarget(object? parameter) => ItemFrom(parameter)?.Path ?? _docs.Active?.Path;

    ProjectFileOps OpsFor(RadProject project) => new(project) { SourceRewritten = f => _rewritten.Add(f) };

    string? Ask(string title, string label, string initial)
    {
        var dialog = new PromptDialog(title, label, initial, this);
        return dialog.ShowModal() == ModalResult.Ok && dialog.Value.Length > 0 ? dialog.Value : null;
    }

    void ShowError(string message) => Dialogs.MessageDlg(message, MessageType.Error, owner: this);

    // ---- New ----

    void NewProjectFile(FileTemplate template, object? parameter)
    {
        var item = ItemFrom(parameter);
        var project = item?.Project ?? _project;
        if (project == null) return;
        var dir = item?.Directory ?? project.Directory;
        var (title, stem) = template switch
        {
            FileTemplate.Interface => ("New Interface", "IInterface"),
            FileTemplate.Form => ("New Form", "Form"),
            _ => ("New Class", "Class"),
        };
        int n = 1;
        while (File.Exists(Path.Combine(dir, $"{stem}{n}.cs")) || File.Exists(Path.Combine(dir, $"{stem}{n}.radform.json"))) n++;
        if (Ask(title, "Name", $"{stem}{n}") is not { } name) return;
        try
        {
            var file = OpsFor(project).NewFile(dir, name, template);
            RebuildProjectTree();
            SelectInProjectTree(file);
            OpenProjectItem(ItemOfNode(FindNode(file)) ?? new ProjectItem(template == FileTemplate.Form ? ProjectItemKind.Form : ProjectItemKind.File, file, project), preview: false);
            Output($"Created {Path.GetRelativePath(project.Directory, file)}");
        }
        catch (IOException e) { ShowError(e.Message); }
    }

    void NewProjectFolder(object? parameter)
    {
        var item = ItemFrom(parameter);
        var project = item?.Project ?? _project;
        if (project == null) return;
        var dir = item?.Directory ?? project.Directory;
        if (Ask("New Folder", "Name", "NewFolder") is not { } name) return;
        try
        {
            var folder = OpsFor(project).CreateFolder(dir, name);
            RebuildProjectTree();
            if (FindNode(folder) is { } node)
            {
                for (var p = node.Parent; p != null; p = p.Parent) p.Expanded = true;
                SelectInProjectTree(folder);
            }
            Output($"Created folder {Path.GetRelativePath(project.Directory, folder)}");
        }
        catch (IOException e) { ShowError(e.Message); }
    }

    // ---- Rename, move, delete ----

    /// <summary>Retargets open tabs after files moved or were renamed, and reloads the buffers the operation rewrote.</summary>
    void ApplyFileChanges(IReadOnlyList<FileChange> changes)
    {
        foreach (var c in changes)
            switch (FindDocument(c.OldPath))
            {
                case FormDocument f: f.MovedTo(c.NewPath); break;
                case CodeDocument d: d.MovedTo(c.NewPath); break;
            }
        foreach (var d in _docs.Documents.OfType<CodeDocument>())
            if (d.FormFile is { } ff && changes.FirstOrDefault(c => SamePath(c.OldPath, ff)) is { } moved) d.FormFile = moved.NewPath;
        foreach (var file in _rewritten.Distinct())
            if (FindDocument(file) is { IsDirty: false } open) open.Reload();
        _rewritten.Clear();
        RefreshGit(sync: true);
        UpdateUi();
        SaveSession();
    }

    void SaveAllDirty()
    {
        foreach (var d in _docs.Documents.Where(d => d.IsDirty && d.Path != null).ToList()) Save(d);
    }

    /// <summary>Renames a file, form (with its code-behind and class) or folder; open tabs follow.</summary>
    public bool RenameProjectPath(string path, string newName)
    {
        if (ProjectOf(path) is not { } project) return false;
        SaveAllDirty();
        try
        {
            _rewritten.Clear();
            var changes = OpsFor(project).Rename(path, newName);
            ApplyFileChanges(changes);
            if (changes.Count > 0) Output($"Renamed {Path.GetFileName(path)} to {newName}");
            return true;
        }
        catch (IOException e) { ShowError(e.Message); return false; }
    }

    void RenameProjectItem(object? parameter)
    {
        if (ItemFrom(parameter) is not { Kind: not ProjectItemKind.Project } item) return;
        var initial = item.Kind == ProjectItemKind.Folder ? Path.GetFileName(item.Path)
            : item.Kind is ProjectItemKind.Form ? RadProject.FormClassName(item.Path) : Path.GetFileNameWithoutExtension(item.Path);
        if (Ask("Rename", item.Kind == ProjectItemKind.Form ? "New class name (renames both files)" : "New name", initial) is not { } name || name == initial) return;
        RenameProjectPath(item.Path, name);
    }

    /// <summary>
    /// Moves a file, form or folder into <paramref name="targetDirectory"/>; open tabs follow. If a moved file's
    /// namespace would no longer match the folder, offers to rewrite it (a textual rewrite of the declaration) unless
    /// <paramref name="fixNamespace"/> says what to do.
    /// </summary>
    public bool MoveProjectItem(string path, string targetDirectory, bool? fixNamespace = null)
    {
        if (ProjectOf(path) is not { } project) return false;
        var ops = OpsFor(project);
        if (fixNamespace == null)
        {
            fixNamespace = false;
            if (ops.WouldChangeNamespace(path, targetDirectory))
            {
                var ns = ops.NamespaceFor(targetDirectory);
                switch (Dialogs.MessageDlg($"Update the namespace to {ns}?\nThe namespace declaration of the moved file(s) is rewritten; other files that refer to the old namespace are not.",
                            MessageType.Confirmation, MessageButtons.YesNoCancel, this))
                {
                    case ModalResult.Yes: fixNamespace = true; break;
                    case ModalResult.No: break;
                    default: return false;
                }
            }
        }
        SaveAllDirty();
        try
        {
            _rewritten.Clear();
            var changes = ops.Move(path, targetDirectory, fixNamespace.Value);
            ApplyFileChanges(changes);
            if (changes.Count > 0) Output($"Moved {Path.GetFileName(path)} to {Path.GetRelativePath(project.Directory, targetDirectory)}");
            if (changes.Count > 0) SelectInProjectTree(changes[0].NewPath);
            return true;
        }
        catch (IOException e) { ShowError(e.Message); return false; }
    }

    void MoveProjectItemCommand(object? parameter)
    {
        if (ItemFrom(parameter) is not { Kind: not ProjectItemKind.Project } item) return;
        var project = item.Project;
        var root = tvProject.Nodes.FirstOrDefault(n => ItemOfNode(n) is { } i && SamePath(i.Project.ProjectFile, project.ProjectFile));
        var folders = new List<string> { project.Directory };
        if (root != null) folders.AddRange(ProjectTreeBuilder.Walk(root.Nodes).Select(ItemOfNode).Where(i => i is { Kind: ProjectItemKind.Folder }).Select(i => i!.Path));
        folders = [.. folders.Where(d => CanMoveInto(item, d))];
        if (folders.Count == 0) { Dialogs.ShowMessage("There is no other folder to move it to. Create one first (Project › New Folder).", this); return; }
        var labels = folders.Select(d => SamePath(d, project.Directory) ? "(project folder)" : Path.GetRelativePath(project.Directory, d)).ToList();
        var dialog = new ChooseDialog("Move", $"Move {Path.GetFileName(item.Kind == ProjectItemKind.Form ? RadProject.FormClassName(item.Path) : item.Path)} to", labels, 0, this);
        if (dialog.ShowModal() != ModalResult.Ok || dialog.Selected < 0) return;
        MoveProjectItem(item.Path, folders[dialog.Selected]);
    }

    /// <summary>Deletes a file (a form with its code-behind) or a folder from disk and closes its tabs. Permanent: there is no recycle bin.</summary>
    public bool DeleteProjectPath(string path, bool confirm = true)
    {
        if (ProjectOf(path) is not { } project) return false;
        var name = Directory.Exists(path) ? Path.GetFileName(path) + " and everything in it" : Path.GetFileName(path);
        if (confirm && Dialogs.MessageDlg($"Delete {name}?\nThis can't be undone: the files are removed from disk, not sent to the recycle bin.",
                MessageType.Warning, MessageButtons.YesNo, this) != ModalResult.Yes) return false;
        try
        {
            var removed = OpsFor(project).Delete(path);
            foreach (var f in removed)
                if (FindDocument(f) is { } doc) RemoveDocument(doc);
            if (project.MainFormClass is { } main && removed.Any(f => f.EndsWith(".radform.json", StringComparison.OrdinalIgnoreCase) && RadProject.FormClassName(f) == main))
                Output($"{main} was the main form: choose another with Project › Set as Main Form.");
            RefreshGit(sync: true);
            UpdateUi();
            Output($"Deleted {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowError(e.Message); return false; }
    }

    void DeleteProjectItem(object? parameter)
    {
        if (ItemFrom(parameter) is { Kind: not ProjectItemKind.Project } item) DeleteProjectPath(item.Path);
    }

    // ---- Reveal ----

    void RevealProjectItem(object? parameter)
    {
        var path = ProjectItemTarget(parameter);
        if (path == null) return;
        if (!RevealHandler(path)) Output($"Could not open the file manager for {path}");
    }

    static bool RevealInFileManager(string path)
    {
        try
        {
            System.Diagnostics.ProcessStartInfo psi;
            bool exists = File.Exists(path) || Directory.Exists(path);
            if (OperatingSystem.IsWindows())
            {
                psi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                psi.ArgumentList.Add(exists && File.Exists(path) ? "/select," + path : Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
            }
            else if (OperatingSystem.IsMacOS())
            {
                psi = new System.Diagnostics.ProcessStartInfo("open") { UseShellExecute = false };
                if (File.Exists(path)) psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(path);
            }
            else
            {
                psi = new System.Diagnostics.ProcessStartInfo("xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
            }
            using var p = System.Diagnostics.Process.Start(psi);
            return p != null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    // ---- Git ----

    GitRepo? RepoFor(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (dir == null || !Directory.Exists(dir)) return null;
        var key = ProjectOf(path)?.Directory ?? dir;
        if (!_repos.TryGetValue(key, out var repo)) _repos[key] = repo = GitRepo.Find(key);
        return repo;
    }

    /// <summary>
    /// Re-reads <c>git status</c> and refreshes the tree's markers if they changed. Runs on a worker thread (it is
    /// also called on save, tree focus and a 5 s timer); <paramref name="sync"/> does it inline. Does nothing if git
    /// is missing or the project is not in a repository.
    /// </summary>
    public void RefreshGit(bool sync = false)
    {
        if (_project == null || _gitBusy) return;
        var repos = Projects.Select(p => RepoFor(p.Directory)).Where(r => r != null).Select(r => r!).DistinctBy(r => r.Root).ToList();
        if (repos.Count == 0)
        {
            if (_gitStatus.Count > 0) { _gitStatus = new Dictionary<string, GitState>(); _gitFolders = new Dictionary<string, GitState>(); RebuildProjectTree(); }
            return;
        }
        var roots = Projects.Select(p => p.Directory).ToList();
        (IReadOnlyDictionary<string, GitState> Files, IReadOnlyDictionary<string, GitState> Folders) Compute()
        {
            var files = new Dictionary<string, GitState>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in repos) foreach (var (k, v) in r.Status()) files[k] = v;
            var folders = new Dictionary<string, GitState>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots) foreach (var (k, v) in GitRepo.Aggregate(files, root)) folders[k] = v;
            return (files, folders);
        }
        void Apply((IReadOnlyDictionary<string, GitState> Files, IReadOnlyDictionary<string, GitState> Folders) r)
        {
            _gitBusy = false;
            if (_project == null) return;
            bool same = r.Files.Count == _gitStatus.Count && r.Files.All(kv => _gitStatus.TryGetValue(kv.Key, out var s) && s == kv.Value);
            _gitStatus = r.Files;
            _gitFolders = r.Folders;
            if (!same) RebuildProjectTree();
        }
        _gitBusy = true;
        if (sync) { Apply(Compute()); return; }
        _ = Task.Run(Compute).ContinueWith(t => Application.Post(() => { if (t.IsCompletedSuccessfully) Apply(t.Result); else _gitBusy = false; }));
    }

    /// <summary>The git state of a path (for tests and tooltips).</summary>
    public GitState GitStateOf(string path) => _gitStatus.TryGetValue(Path.GetFullPath(path), out var s) ? s : GitState.Clean;

    /// <summary>Project › Compare with HEAD: a read-only unified diff of the file (its current text, with unsaved edits) against HEAD.</summary>
    public DiffDocument? CompareWithHead(object? parameter = null)
    {
        var path = ProjectItemTarget(parameter);
        if (path == null || !File.Exists(path)) return null;
        if (path.EndsWith(".radform.json", StringComparison.OrdinalIgnoreCase) && FindDocument(path) is FormDocument open) path = open.Path;
        if (RepoFor(path) is not { } repo) { sbMain.SimpleText = "This project is not in a git repository"; return null; }
        var head = repo.ShowHead(path) ?? "";
        var current = FindDocument(path) switch
        {
            FormDocument f => f.Document.Save(),
            CodeDocument c => c.Text,
            _ => File.ReadAllText(path),
        };
        var rel = Path.GetRelativePath(repo.Root, path).Replace('\\', '/');
        var diff = TextDiff.Unified(head, current, head.Length == 0 ? "/dev/null" : "a/" + rel, "b/" + rel);
        if (diff.Length == 0) { sbMain.SimpleText = $"{Path.GetFileName(path)}: no changes against HEAD"; return null; }
        var doc = new DiffDocument(Path.GetFileName(path) + " (HEAD)", diff, path);
        AddDocument(doc, preview: false, activate: true);
        return doc;
    }

    // ---- NuGet and properties ----

    /// <summary>Project › Manage NuGet Packages…: the dialog over <c>dotnet add/remove package</c>; its output goes to Output.</summary>
    public void ManagePackages()
    {
        if (_project == null) return;
        var dialog = new NuGetDialog(_project, NuGet, line => Application.Post(() => Output(line)), this);
        dialog.Changed += () => UpdateUi();
        dialog.ShowModal();
    }

    /// <summary>Project › Properties…: the .csproj's name, version, icon, namespace, runtime identifiers and AOT switch.</summary>
    public void ShowProjectProperties()
    {
        if (_project == null) return;
        var dialog = new ProjectPropertiesDialog(_project, this);
        if (dialog.ShowModal() != ModalResult.Ok) return;
        try
        {
            if (dialog.Save()) Output($"Updated {Path.GetFileName(_project.ProjectFile)}");
        }
        catch (IOException e) { ShowError("Could not save the project file:\n" + e.Message); }
        UpdateUi();
    }

    // ---- Solutions ----

    /// <summary>Opens a <c>.sln</c>/<c>.slnx</c>: every C# project is a top-level node of the tree. The app project (the one with a main form, else the first) is the one whose forms open.</summary>
    bool OpenSolution(string solutionFile, IdeSession? session)
    {
        IReadOnlyList<string> files;
        try { files = RadSharp.Design.Code.SolutionFile.ProjectFiles(solutionFile); }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            ShowError($"Could not read the solution:\n{e.Message}");
            return false;
        }
        if (files.Count == 0) { ShowError($"{Path.GetFileName(solutionFile)} lists no C# projects."); return false; }
        if (_project != null && !CloseProject()) return false;
        var projects = new List<RadProject>();
        foreach (var f in files)
        {
            try { projects.Add(new RadProject(f)); }
            catch (IOException) { }
        }
        if (projects.Count == 0) return false;
        var primary = projects.FirstOrDefault(p => p.MainFormClass != null) ?? projects[0];
        _solutionFile = Path.GetFullPath(solutionFile);
        _solutionProjects = projects;
        if (!OpenProject(primary.ProjectFile, session))
        {
            _solutionFile = null;
            _solutionProjects = null;
            return false;
        }
        _settings.AddRecent(_solutionFile);
        NoteProjectOpened(_solutionFile);
        SaveSettings();
        Output($"Opened solution {_solutionFile} ({projects.Count} project(s)); designing and building target the project that owns the active file.");
        SaveSession();
        return true;
    }

    /// <summary>When the active document belongs to another project of the solution, that project becomes the one Run, Build and new forms work on.</summary>
    void ActivateOwningProject(IDocumentView? doc)
    {
        if (_solutionProjects == null || doc?.Path == null) return;
        if (ProjectOf(doc.Path) is { } owner && (_project == null || !SamePath(owner.ProjectFile, _project.ProjectFile))) _project = owner;
    }

    void ResetSolution()
    {
        _solutionFile = null;
        _solutionProjects = null;
        _repos.Clear();
        _gitStatus = new Dictionary<string, GitState>();
        _gitFolders = new Dictionary<string, GitState>();
    }
}
