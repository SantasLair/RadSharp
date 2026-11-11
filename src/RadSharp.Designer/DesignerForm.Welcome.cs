using RadSharp.Design.Code;

namespace RadSharp.Designer;

/// <summary>The welcome page's lower half (<see cref="WelcomeContent"/>) and what its buttons do.</summary>
public partial class DesignerForm
{
    WelcomeContent? _welcome;

    /// <summary>The commands whose shortcuts the welcome page shows, in order.</summary>
    static readonly string[] EssentialCommands =
    [
        "Go.CommandPalette", "Run.Run", "Run.StartDebugging", "Debug.ToggleBreakpoint", "View.ToggleFormCode", "File.Save", "Go.File", "Run.Build", "Debug.StepOver", "Code.GoToDefinition",
        "Code.Rename", "Code.FormatDocument", "Search.FindInFiles",
    ];

    public WelcomeContent Welcome => EnsureWelcome();

    WelcomeContent EnsureWelcome()
    {
        if (_welcome != null) return _welcome;
        // The old list of recent projects gives way to the richer one; the header, the two buttons and the package note stay.
        lblRecent.Visible = false;
        lstRecent.Visible = false;
        _welcome = new WelcomeContent();
        _welcome.OpenRequested += path => OpenRecentProject(path);
        _welcome.PinToggled += path => PinProject(path, !_ide.PinnedProjects.Contains(path, StringComparer.OrdinalIgnoreCase));
        _welcome.RemoveRequested += RemoveRecent;
        _welcome.TemplateChosen += t => NewApplication(t);
        pnlWelcome.Add(_welcome);
        pnlWelcome.Resized += _ => LayoutWelcome();
        LayoutWelcome();
        return _welcome;
    }

    void LayoutWelcome()
    {
        if (_welcome == null) return;
        int top = Px(184), margin = Px(48);
        _welcome.SetBounds(margin, top, Math.Max(0, pnlWelcome.Width - 2 * margin), Math.Max(0, pnlWelcome.Height - top - Px(40)));
        lblPackages.SetBounds(margin, Math.Max(top, pnlWelcome.Height - Px(32)), Math.Max(0, pnlWelcome.Width - 2 * margin), Px(22));
    }

    /// <summary>Refreshes the welcome page from the recent list, the pins and the command registry.</summary>
    void RefreshWelcome()
    {
        var w = EnsureWelcome();
        w.Recent = WelcomeEntries();
        w.Templates = ProjectTemplates.All;
        w.Shortcuts = EssentialShortcuts();
        LayoutWelcome();
        w.Invalidate();
    }

    /// <summary>The "Keyboard essentials": title and current binding of the main commands, generated from the registry.</summary>
    public IReadOnlyList<(string Title, string Keys)> EssentialShortcuts() =>
    [
        .. EssentialCommands.Select(id => _commands.Find(id)).Where(c => c is { Shortcut.Length: > 0 })
            .Select(c => (c!.Title.TrimEnd('…'), ShortcutText(c.Shortcut))),
    ];

    /// <summary>A shortcut as the platform shows it (<c>Mod</c> becomes Ctrl or Cmd).</summary>
    public static string ShortcutText(string shortcut) =>
        RadSharp.Shortcut.TryParse(shortcut, out var s) ? s.ToString() : shortcut;

    /// <summary>The projects the welcome page lists: pinned ones first (in pin order), then the recent ones, newest first.</summary>
    public IReadOnlyList<RecentEntry> WelcomeEntries()
    {
        var list = new List<RecentEntry>();
        RecentEntry Entry(string p, bool pinned) => new(p, Path.GetFileNameWithoutExtension(p), Path.GetDirectoryName(p) ?? "",
            _ide.LastOpened.TryGetValue(p, out var t) ? t : null, pinned);
        foreach (var p in _ide.PinnedProjects)
            if (File.Exists(p)) list.Add(Entry(p, true));
        foreach (var p in _settings.RecentProjects)
            if (!_ide.PinnedProjects.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(Entry(p, false));
        return list;
    }

    /// <summary>Pins a project to the top of the welcome page (or unpins it).</summary>
    public void PinProject(string path, bool pin)
    {
        path = Path.GetFullPath(path);
        _ide.PinnedProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (pin) _ide.PinnedProjects.Add(path);
        SaveIdeSettings();
        RefreshWelcome();
    }

    /// <summary>Takes a project off the welcome page and the Reopen menu (its files stay where they are).</summary>
    public void RemoveRecent(string path)
    {
        path = Path.GetFullPath(path);
        _settings.RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _ide.PinnedProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _ide.LastOpened.Remove(path);
        SaveSettings();
        SaveIdeSettings();
        RebuildReopenMenu();
        RefreshWelcome();
    }

    /// <summary>Opens a project from the welcome page or the Reopen menu; a project that is gone offers to leave the list.</summary>
    public bool OpenRecentProject(string path)
    {
        if (File.Exists(path)) return OpenProject(path);
        NextStep("Project not found", $"{path}\nwas moved or deleted.",
            ("Remove from the list", () => RemoveRecent(path)), ("Open another project…", OpenProjectDialog));
        return false;
    }

    /// <summary>Creates a project from a template in <paramref name="folder"/> and opens it. Null (with a message that says what to do) if it can not be created.</summary>
    public RadProject? CreateProject(ProjectTemplate template, string folder, string name, string? packageSource)
    {
        if (_project != null && !CloseProject()) return null;
        try
        {
            var project = template.Create(folder, name, packageSource);
            OpenProject(project.ProjectFile);
            Output($"Created {project.ProjectFile} from the {template.Name} template.");
            return project;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            NextStep("New project", $"Could not create the project in {folder}:\n{e.Message}",
                ("Try again", () => CreateProject(template, folder, name, packageSource)), ("Open another project…", OpenProjectDialog));
            return null;
        }
    }
}
