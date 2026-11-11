using RadSharp.Design.Code;

namespace RadSharp.Designer;

/// <summary>The IDE shell: dock host and tool windows, the document area, session restore and files changed on disk.</summary>
public partial class DesignerForm
{
    DockHost _dock = null!;
    DocumentArea _docs = null!;
    Panel _center = null!;
    readonly Timer _diskTimer = new() { Name = "tmrDiskCheck", Interval = 1500 };
    readonly Dictionary<IDocumentView, DateTime> _diskIgnored = [];
    readonly ExternalEditorNavigator _external;
    readonly EmbeddedEditorNavigator _embedded;
    bool _restoring, _checkingDisk;

    /// <summary>Tool window ids.</summary>
    public static class ToolWindows
    {
        public const string Palette = "palette", Inspector = "inspector", Structure = "structure", Project = "project",
            Output = "output", Problems = "problems", FindResults = "findResults";
    }

    void BuildShell()
    {
        _docs = new DocumentArea { Visible = false };
        _docs.ActiveChanged += OnActiveDocumentChanged;
        _docs.CloseRequested += doc => CloseDocument(doc);
        _docs.TabsChanged += () => SaveSession();
        _docs.SplitPair = SplitPairOf;
        _docs.TabMenuFactory = BuildTabMenu;

        _center = new Panel { Name = "pnlCenter", BorderStyle = BorderStyle.None };
        pnlWelcome.Align = Align.Client;
        _center.Add(pnlWelcome);
        _docs.Align = Align.Client;
        _center.Add(_docs);

        _dock = new DockHost(_center, Scale);
        _dock.WindowMenuFactory = BuildToolWindowMenu;
        _dock.LayoutChanged += () => SaveSession();
        void Tool(string id, string title, Control content, DockSide side)
        {
            if (content is Panel p) p.BorderStyle = BorderStyle.None;
            _dock.Add(new ToolWindow(id, title, content), side);
        }
        // The default layout: what the designer had before docking (palette left; inspector, structure and
        // project right; output below), plus the Problems and Find Results windows beside Output.
        Tool(ToolWindows.Palette, "Components", pnlPalette, DockSide.Left);
        Tool(ToolWindows.Inspector, "Inspector", pnlInspector, DockSide.Right);
        Tool(ToolWindows.Structure, "Structure", pnlStructure, DockSide.Right);
        Tool(ToolWindows.Project, "Project", pnlProject, DockSide.Right);
        Tool(ToolWindows.Output, "Output", pnlOutput, DockSide.Bottom);
        Tool(ToolWindows.Problems, "Problems", pnlProblems, DockSide.Bottom);
        Tool(ToolWindows.FindResults, "Find Results", pnlFindResults, DockSide.Bottom);
        foreach (var side in new[] { DockSide.Left, DockSide.Right, DockSide.Bottom })
            if (_dock.Region(side).Windows.FirstOrDefault() is { } first) _dock.Region(side).Activate(first);

        // Below the menu, tool bar and status bar (which are aligned top/bottom before it).
        Add(_dock);

        _diskTimer.Elapsed += _ => CheckDiskChanges();
        Add(_diskTimer);
    }

    public ToolWindow FindToolWindow(string id) => _dock.Find(id) ?? throw new KeyNotFoundException(id);

    // ---- Code navigation ----

    /// <summary>Where code navigation goes: the built-in editor or the external IDE (<see cref="IdeSettings.CodeNavigation"/>).</summary>
    public ICodeNavigator CodeNavigator => _ide.CodeNavigation == CodeNavigation.External ? _external : _embedded;

    /// <summary>Shows a place in the code through <see cref="CodeNavigator"/> and reports it in the status bar.</summary>
    public bool NavigateToCode(string file, int line, int column)
    {
        using var _ = JumpScope();
        var nav = CodeNavigator;
        if (nav.Navigate(file, line, column))
        {
            sbMain.SimpleText = nav == _external
                ? $"Opened {Path.GetFileName(file)}:{line} in {_external.LastEditorName}"
                : $"{Path.GetFileName(file)}:{line}";
            return true;
        }
        Output(nav == _external
            ? $"Could not start {_external.LastEditorName} for {Path.GetFileName(file)}:{line}; choose another editor in Tools › Options."
            : $"Could not open {Path.GetFileName(file)}.");
        return false;
    }

    /// <summary>The text of a source file: the open buffer if the file is open in the IDE, otherwise the disk.</summary>
    string ReadSource(string file) => FindDocument(file) is CodeDocument open ? open.Text : File.ReadAllText(file);

    /// <summary>Writes generated changes to a source file: into the open buffer (saved too, unless the user has
    /// unsaved edits there, which stay unsaved), or straight to disk if it isn't open.</summary>
    void WriteSource(string file, string text)
    {
        if (FindDocument(file) is CodeDocument open)
        {
            bool clean = !open.IsDirty;
            open.Text = text;
            if (clean) open.Save();
        }
        else File.WriteAllText(file, text);
    }

    // ---- Form/code toggle and split view ----

    /// <summary>F12: switches in place between a form and its code-behind (Delphi's gesture).</summary>
    public IDocumentView? ToggleFormCode()
    {
        switch (_docs.Active)
        {
            case FormDocument form:
                if (!File.Exists(form.CodeFile)) { sbMain.SimpleText = $"{Path.GetFileName(form.CodeFile)} is missing"; return null; }
                if (_ide.CodeNavigation == CodeNavigation.External) { NavigateToCode(form.CodeFile, 1, 1); return null; }
                return OpenCodeFile(form.CodeFile);
            case CodeDocument { FormFile: { } formFile }:
                return OpenFormFile(formFile);
        }
        return null;
    }

    (IDocumentView Left, IDocumentView Right)? SplitPairOf(IDocumentView doc) => doc switch
    {
        FormDocument f when FindDocument(f.CodeFile) is CodeDocument c => (f, c),
        CodeDocument { FormFile: { } ff } c when FindDocument(ff) is FormDocument f => (f, c),
        _ => null,
    };

    public bool SplitView => _docs.SplitView;

    /// <summary>View › Split Form and Code: shows the designer beside its code-behind (opening it), or back to one.</summary>
    public void ToggleSplitView()
    {
        if (_docs.SplitView) { _docs.SplitView = false; SaveSession(); RefreshCommandState(); return; }
        switch (_docs.Active)
        {
            case FormDocument form when File.Exists(form.CodeFile): OpenCodeFile(form.CodeFile, activate: false); break;
            case CodeDocument { FormFile: { } ff }: OpenFormFile(ff, activate: false); break;
        }
        _docs.SplitView = true;
        SaveSession();
        RefreshCommandState();
    }

    // ---- Session ----

    /// <summary>Writes what to restore on the next start: project, documents (with carets), split view, dock layout.</summary>
    public void SaveSession()
    {
        if (_restoring || _docs == null) return;
        var session = new IdeSession
        {
            Project = _solutionFile ?? _project?.ProjectFile,
            Active = _docs.Active?.Path,
            Split = _docs.SplitView,
            Layout = _dock.SaveLayout(),
        };
        foreach (var d in _docs.Documents)
            if (d.Path != null) session.Documents.Add(new IdeSession.Document(d.Kind, d.Path, _docs.IsPreview(d), d.SaveState()));
        try { session.Save(_paths.Session); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    bool RestoreDocuments(IdeSession session)
    {
        foreach (var d in session.Documents)
        {
            if (!File.Exists(d.Path)) continue;
            IDocumentView? doc = d.Kind switch
            {
                "form" => OpenFormFile(d.Path, d.Preview, activate: false),
                "code" => OpenCodeFile(d.Path, d.Preview, activate: false),
                _ => null,
            };
            if (doc != null && d.State != null) doc.RestoreState(d.State);
        }
        if (_docs.Documents.Count == 0) return false;
        var active = (session.Active != null ? FindDocument(session.Active) : null) ?? _docs.Documents[0];
        _docs.Activate(active);
        _docs.SplitView = session.Split;
        return true;
    }

    // ---- Files changed on disk ----

    /// <summary>
    /// Looks for open files changed outside the IDE (also run by a timer): a document without unsaved changes is
    /// reloaded silently; one with changes asks whether to reload (and if not, isn't asked again for that change).
    /// </summary>
    public void CheckDiskChanges()
    {
        if (_checkingDisk) return;
        _checkingDisk = true;
        try
        {
            foreach (var doc in _docs.Documents.ToList())
            {
                if (doc.Path is not { } path || !File.Exists(path)) continue;
                var stamp = DocumentFiles.Stamp(path);
                if (stamp == doc.DiskTimestamp || (_diskIgnored.TryGetValue(doc, out var ignored) && ignored == stamp)) continue;
                if (!doc.IsDirty)
                {
                    if (doc.Reload()) Output($"Reloaded {doc.Title}: it changed on disk.");
                }
                else if (Dialogs.MessageDlg($"{doc.Title} changed on disk.\nReload it and lose your changes?",
                             MessageType.Confirmation, MessageButtons.YesNo, this) == ModalResult.Yes)
                {
                    if (doc.Reload()) Output($"Reloaded {doc.Title}.");
                    _diskIgnored.Remove(doc);
                }
                else _diskIgnored[doc] = stamp;
            }
        }
        finally { _checkingDisk = false; }
        UpdateTitle();
        _docs.Invalidate();
    }
}
