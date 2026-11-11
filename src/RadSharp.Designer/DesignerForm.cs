using RadSharp.Design;
using RadSharp.Design.Code;
using RadSharp.Drawing;

namespace RadSharp.Designer;

/// <summary>
/// The RADSharp IDE — itself a RADSharp form (DesignerForm.radform.json). As in Delphi and Lazarus, the
/// designer always works on a project: with none open it shows the welcome page (new application, open
/// project, recent projects); with one open, forms and code files are documents in one tab strip
/// (<see cref="DocumentArea"/>), surrounded by dockable tool windows (<see cref="DockHost"/>), and every
/// menu item, tool button and shortcut is a <see cref="Command"/> in <see cref="Commands"/>.
/// </summary>
public partial class DesignerForm : Form
{
    readonly List<FormDocument> _forms = [];
    FormDocument? _current;          // the form the inspector, palette and structure work on
    RadProject? _project;
    readonly DesignerSettings _settings;
    readonly string? _settingsPath;
    readonly IdePaths _paths;
    readonly IdeSettings _ide;
    readonly List<ComponentClass> _palette;
    bool _syncing;
    bool _showGrid = true, _locked;

    public DesignerForm(string? projectFile = null, string? settingsPath = null)
    {
        InitializeComponent();
        _settingsPath = settingsPath;
        _settings = DesignerSettings.Load(settingsPath);
        _settings.RemoveMissing();
        _paths = new IdePaths(settingsPath);
        _ide = IdeSettings.Load(_paths.Settings);

        // Apply theme from settings
        Theme.Current = _settings.Theme == "dark" ? Theme.Dark : Theme.Default;
        this.Theme = Theme.Current;

        _palette = [.. ComponentRegistry.All
            .Where(c => c.CanCreate && c.Type != typeof(Control) && !typeof(Form).IsAssignableFrom(c.Type) && c.Type != typeof(DesignSurface)
                && c.Type.Assembly != typeof(DesignerForm).Assembly)   // the IDE's own controls (DocumentArea…) aren't components
            .OrderBy(c => c.Name, StringComparer.Ordinal)];
        RebuildPalette();
        tvPalette.DragMode = DragMode.Automatic;   // drag a palette entry onto a form to place it, not just click

        foreach (var grid in new[] { gridProps, gridEvents })
        {
            grid.PropertyChanging += (_, _) => Document?.Snapshot();
            grid.PropertyChanged += (_, _) => Document?.NotifyChanged();
        }
        gridEvents.EventDoubleClick += (_, ev) => { if (Document is { } d) EditHandler(d.Primary, ev); };
        gridProps.Arrangement = PropertyGridArrangement.Categorized;   // matches cboPropsArrange's default selection
        InitProjectTree();

        _external = new ExternalEditorNavigator(_settings, () => _project);
        _embedded = new EmbeddedEditorNavigator(file => OpenCodeFile(file));
        BuildShell();
        InitBuildSupport();
        RegisterCommands();
        _commands.LoadKeymap(_paths.Keymap);
        BuildMenus();

        var session = IdeSession.Load(_paths.Session);
        if (session.Layout != null) _dock.RestoreLayout(session.Layout);
        if (projectFile != null && File.Exists(projectFile)) OpenProject(projectFile, session);
        else if (projectFile == null && session.Project is { } last && File.Exists(last)) OpenProject(last, session);
        UpdateUi();
        Shown += _ => CheckSdk();
    }

    // ---- State (also used by tests) ----

    public RadProject? Project => _project;
    /// <summary>The open forms, in the order they were opened.</summary>
    public IReadOnlyList<FormDocument> OpenForms => _forms;
    /// <summary>The form the inspector shows: the active document if it's a form, else the last active form.</summary>
    public FormDocument? ActiveForm => _current;
    public DesignDocument? Document => _current?.Document;
    public DesignerSettings Settings => _settings;
    public IdeSettings IdeSettings => _ide;
    public IdePaths Paths => _paths;
    public bool WelcomeVisible => pnlWelcome.Visible;
    public CommandRegistry Commands => _commands;
    public DockHost Dock => _dock;
    public DocumentArea Documents => _docs;
    public IDocumentView? ActiveDocument => _docs.Active;
    public IReadOnlyList<CodeDocument> OpenCodeDocuments => [.. _docs.Documents.OfType<CodeDocument>()];

    void SaveSettings()
    {
        try { _settings.Save(_settingsPath); }
        catch (IOException) { }
    }

    /// <summary>Saves the IDE preferences (<c>ide.json</c>) and keymap (<c>keymap.json</c>).</summary>
    public void SaveIdeSettings()
    {
        try
        {
            _ide.Save(_paths.Settings);
            _commands.SaveKeymap(_paths.Keymap);
        }
        catch (IOException) { }
    }

    // ---- Projects ----

    /// <summary>Opens a project (closing the current one first), then its main form.</summary>
    public bool OpenProject(string projectFile) => OpenProject(projectFile, null);

    bool OpenProject(string projectFile, IdeSession? session)
    {
        if (RadSharp.Design.Code.SolutionFile.IsSolution(projectFile)) return OpenSolution(projectFile, session);
        if (_project != null && !CloseProject()) return false;
        try { _project = new RadProject(projectFile); }
        catch (IOException e)
        {
            Dialogs.MessageDlg($"Could not open the project:\n{e.Message}", MessageType.Error, owner: this);
            return false;
        }
        _settings.AddRecent(_project.ProjectFile);
        SaveSettings();
        _restoring = true;
        try
        {
            bool restored = session != null && SamePath(session.Project, _solutionFile ?? _project.ProjectFile) && RestoreDocuments(session);
            if (!restored)
            {
                var main = _project.MainFormClass is { } cls ? _project.FormFileFor(cls) : null;
                if ((main ?? _project.FormFiles.FirstOrDefault()) is { } first) OpenFormFile(first);
            }
        }
        finally { _restoring = false; }
        CaptureLocation();   // where the project opened is the first place in the history
        StartLanguage();
        UpdateUi();
        Output($"Opened project {_project.ProjectFile}");
        SaveSession();
        return true;
    }

    /// <summary>Closes every document (asking to save changes) and returns to the welcome page.</summary>
    public bool CloseProject()
    {
        if (!ConfirmSave(_docs.Documents)) return false;
        CloseLiveForm();
        ResetBuildState();
        foreach (var d in _docs.Documents.ToList()) RemoveDocument(d);
        StopLanguage();
        ClearNavigationHistory();
        _project = null;
        ResetSolution();
        _docs.SplitView = false;
        UpdateUi();
        SaveSession();
        return true;
    }

    static bool SamePath(string? a, string? b) =>
        a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // ---- Documents ----

    /// <summary>The open document for a file, if any.</summary>
    public IDocumentView? FindDocument(string path)
    {
        var full = Path.GetFullPath(path);
        return _docs.Documents.FirstOrDefault(d => d.Path != null && string.Equals(d.Path, full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Opens a form of the project in its own tab (or switches to it if it is open). A preview tab
    /// (single click in the Project window) is replaced by the next preview.</summary>
    public FormDocument? OpenFormFile(string path, bool preview = false, bool activate = true)
    {
        path = Path.GetFullPath(path);
        if (FindDocument(path) is FormDocument open)
        {
            if (!preview) _docs.MakePermanent(open);
            if (activate) _docs.Activate(open);
            return open;
        }
        var doc = new DesignDocument();
        try
        {
            var warnings = doc.Load(File.ReadAllText(path));
            doc.FileName = path;
            if (warnings.Count > 0) Output($"{Path.GetFileName(path)}: " + string.Join("; ", warnings.Take(5)));
        }
        catch (Exception e) when (e is IOException or FormFileException or UnauthorizedAccessException)
        {
            Dialogs.MessageDlg($"Could not open {Path.GetFileName(path)}:\n{e.Message}", MessageType.Error, owner: this);
            return null;
        }

        var surface = new DesignSurface { Document = doc, ShowGrid = _showGrid, Locked = _locked };
        var form = new FormDocument(path, doc, surface);

        surface.Created += _ => tvPalette.Selected = null;
        surface.DoubleClicked += c => { if (CodeIntegration.DefaultEvent(c) is { } ev) EditHandler(c, ev); };
        surface.ContextMenuRequested += (x, y) => ShowSurfaceContextMenu(surface, x, y);
        // Drag a palette entry onto the surface to place it (an alternative to click-then-place); the surface
        // itself has no idea what a palette or a TreeView is, so the class comes from the drag's own source.
        surface.DragOver += (_, e) => e.Accept = DraggedPaletteClass(e.Source) != null;
        surface.DragDrop += (_, e) => { if (DraggedPaletteClass(e.Source) is { } cls) surface.CreateAt(cls, e.X, e.Y); };
        doc.SelectionChanged += () => { if (_current == form) UpdateInspector(); };
        doc.FormReplaced += () => { if (_current == form) { UpdateInspector(); RebuildStructure(); } };
        doc.Changed += () =>
        {
            if (_current != form) return;
            gridProps.RefreshValues();
            gridEvents.RefreshValues();
            RebuildStructure();
        };
        form.StateChanged += UpdateTitle;
        _forms.Add(form);
        AttachLanguage(form);
        HookNavigation(form);
        AddDocument(form, preview, activate);
        return form;
    }

    /// <summary>Opens a code file in the built-in editor (or switches to it if it is open).</summary>
    public CodeDocument? OpenCodeFile(string path, bool preview = false, bool activate = true)
    {
        path = Path.GetFullPath(path);
        if (FindDocument(path) is CodeDocument open)
        {
            if (!preview) _docs.MakePermanent(open);
            if (activate) _docs.Activate(open);
            return open;
        }
        CodeDocument doc;
        try { doc = new CodeDocument(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dialogs.MessageDlg($"Could not open {Path.GetFileName(path)}:\n{e.Message}", MessageType.Error, owner: this);
            return null;
        }
        doc.FormFile = (ProjectOf(path) ?? _project)?.FormFiles.FirstOrDefault(f => SamePath(RadProject.CodeFileFor(f), path));
        doc.StateChanged += UpdateTitle;
        AttachLanguage(doc);
        HookNavigation(doc);
        AddDocument(doc, preview, activate);
        return doc;
    }

    void AddDocument(IDocumentView doc, bool preview, bool activate)
    {
        if (preview && _docs.Preview is { } old && old != doc && !old.IsDirty) RemoveDocument(old);
        _docs.Add(doc, preview, activate);
        UpdateUi();
        SaveSession();
    }

    void OnActiveDocumentChanged(IDocumentView? doc)
    {
        ActivateOwningProject(doc);
        CaptureLocation();
        if (doc is FormDocument form)
        {
            _current = form;
            form.Surface.PendingClass = tvPalette.Selected?.Tag as ComponentClass;
            UpdateInspector();
            RebuildStructure();
        }
        UpdateTitle();
        RefreshCommandState();
        SaveSession();
    }

    /// <summary>Closes a form's tab, asking to save its changes first.</summary>
    public bool CloseForm(FormDocument form) => CloseDocument(form);

    /// <summary>Closes a document's tab, asking to save its changes first.</summary>
    public bool CloseDocument(IDocumentView doc)
    {
        if (!doc.CanClose() || !ConfirmSave([doc])) return false;
        RemoveDocument(doc);
        return true;
    }

    /// <summary>Closes every document except <paramref name="keep"/> (asking once about unsaved changes).</summary>
    public bool CloseOthers(IDocumentView keep)
    {
        var others = _docs.Documents.Where(d => d != keep).ToList();
        if (others.Any(d => !d.CanClose()) || !ConfirmSave(others)) return false;
        foreach (var d in others) RemoveDocument(d);
        return true;
    }

    public bool CloseAllDocuments()
    {
        var all = _docs.Documents.ToList();
        if (all.Any(d => !d.CanClose()) || !ConfirmSave(all)) return false;
        foreach (var d in all) RemoveDocument(d);
        return true;
    }

    void RemoveDocument(IDocumentView doc)
    {
        _docs.Remove(doc);
        doc.Close();
        DetachLanguage(doc);
        _diskIgnored.Remove(doc);
        if (doc is FormDocument form)
        {
            _forms.Remove(form);
            if (_current == form)
            {
                _current = _docs.Active as FormDocument ?? _forms.LastOrDefault();
                if (_current != null) { UpdateInspector(); RebuildStructure(); }
            }
        }
        UpdateUi();
        SaveSession();
    }

    public bool Save(IDocumentView doc)
    {
        if (!doc.Save())
        {
            Dialogs.MessageDlg($"Could not save {doc.Title}.", MessageType.Error, owner: this);
            return false;
        }
        UpdateTitle();
        RefreshGit();
        return true;
    }

    /// <summary>Saves every document with unsaved changes (forms and code).</summary>
    public void SaveAll()
    {
        foreach (var d in _docs.Documents.Where(d => d.IsDirty).ToList()) Save(d);
        sbMain.SimpleText = "All files saved";
    }

    /// <summary>Asks once about all modified documents in <paramref name="docs"/>; false if the user cancelled.</summary>
    bool ConfirmSave(IEnumerable<IDocumentView> docs)
    {
        var modified = docs.Where(d => d.IsDirty).ToList();
        if (modified.Count == 0) return true;
        var names = string.Join(", ", modified.Select(d => d.Title));
        switch (Dialogs.MessageDlg($"Save changes to {names}?", MessageType.Confirmation, MessageButtons.YesNoCancel, this))
        {
            case ModalResult.Yes: return modified.All(Save);
            case ModalResult.No: return true;
            default: return false;
        }
    }

    // ---- UI state ----

    void UpdateUi()
    {
        bool hasProject = _project != null;
        pnlWelcome.Visible = !hasProject;
        _docs.Visible = hasProject;
        pnlPalette.Enabled = hasProject && _current != null;
        pnlInspector.Enabled = hasProject && _current != null;

        lblProject.Caption = _project?.Name ?? "";
        RebuildProjectTree();

        lstRecent.Items = [.. _settings.RecentProjects.Select(p => $"{Path.GetFileNameWithoutExtension(p)}   —   {Path.GetDirectoryName(p)}")];
        var source = _settings.EffectivePackageSource;
        lblPackages.Caption = source != null
            ? $"RADSharp packages for new projects: {source}"
            : "RADSharp packages: none found locally, so new projects use nuget.org (run dotnet pack in the RADSharp repository).";

        RebuildReopenMenu();

        if (!hasProject || _current == null)
        {
            gridProps.Target = null;
            gridEvents.Target = null;
            tvStructure.Nodes.Clear();
            edtName.Text = "";
        }
        RefreshCommandState();
        UpdateTitle();
        InvalidateAll();
    }

    void UpdateTitle()
    {
        if (_project == null) { Caption = "RADSharp"; return; }
        Caption = _docs.Active is not { } doc
            ? $"RADSharp — {_project.Name}"
            : $"RADSharp — {_project.Name} — {DocumentArea.TabCaption(doc)}";
    }

    // ---- Inspector, name, structure ----

    void UpdateInspector()
    {
        if (Document is not { } doc) return;
        _syncing = true;
        var target = doc.Primary;
        IReadOnlyList<Control> targets = doc.Selection.Count > 0 ? doc.Selection : [target];
        gridProps.SetTargets(targets);
        gridEvents.SetTargets(targets);
        // A multi-selection can't be renamed as one (names must stay unique); show and edit only the primary's.
        edtName.Text = target.Name;
        edtName.Enabled = doc.Selection.Count <= 1;
        tvStructure.Selected = FindNode(tvStructure.Nodes, target);
        sbMain.SimpleText = doc.Selection.Count > 1
            ? $"{doc.Selection.Count} controls selected"
            : $"{target.Name}: {ComponentRegistry.Of(target).Name}  —  {target.Bounds}";
        _syncing = false;
        _current!.Surface.Invalidate();
        pnlPalette.Enabled = pnlInspector.Enabled = true;
    }

    void RebuildStructure()
    {
        if (Document is not { } doc) return;
        _syncing = true;
        tvStructure.Nodes.Clear();
        var root = new TreeNode(Label(doc.Form)) { Tag = doc.Form, Expanded = true };
        void Add(TreeNode node, Control c)
        {
            foreach (var child in c.Children)
            {
                var n = node.Add(Label(child));
                n.Tag = child;
                n.Expanded = true;
                Add(n, child);
            }
        }
        Add(root, doc.Form);
        tvStructure.Nodes.Add(root);
        tvStructure.Selected = FindNode(tvStructure.Nodes, doc.Primary);
        tvStructure.Invalidate();
        _syncing = false;
    }

    static string Label(Control c) => $"{c.Name}: {ComponentRegistry.Of(c).Name}";

    static TreeNode? FindNode(IEnumerable<TreeNode> nodes, Control c)
    {
        foreach (var n in nodes)
        {
            if (n.Tag == c) return n;
            if (FindNode(n.Nodes, c) is { } found) return found;
        }
        return null;
    }

    void tvStructure_Change(Control sender)
    {
        if (_syncing || Document is not { } doc || tvStructure.Selected?.Tag is not Control c) return;
        doc.Select(c == doc.Form ? null : c);
    }

    void edtName_KeyDown(Control sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Modifiers != KeyModifiers.None) return;
        ApplyName();
        e.Handled = true;
    }

    void edtName_Blur(Control sender) => ApplyName();

    void ApplyName()
    {
        if (Document is not { } doc) return;
        var target = doc.Primary;
        var name = edtName.Text.Trim();
        if (name == target.Name) return;
        var old = target.Name;
        if (!doc.Rename(target, name))
        {
            sbMain.SimpleText = $"\"{name}\" is not a valid, unused name";
            edtName.Text = target.Name;
            return;
        }
        if (old.Length > 0) RenameInCode(old, name);
    }

    void pgInspector_Change(Control sender) => UpdateInspector();

    void cboPropsArrange_Change(Control sender) =>
        gridProps.Arrangement = cboPropsArrange.ItemIndex == 1 ? PropertyGridArrangement.Categorized : PropertyGridArrangement.Alphabetical;

    void edtPropsFilter_Change(Control sender) => gridProps.Filter = edtPropsFilter.Text;
    void edtEventsFilter_Change(Control sender) => gridEvents.Filter = edtEventsFilter.Text;

    void tvPalette_Change(Control sender)
    {
        if (_current == null) return;
        var cls = tvPalette.Selected?.Tag as ComponentClass;
        _current.Surface.PendingClass = cls;
        if (cls != null) sbMain.SimpleText = $"Click or drag on the form to place a {cls.Name} (Shift keeps it selected)";
    }

    void edtPaletteFilter_Change(Control sender) => RebuildPalette();

    /// <summary>What a drag started on <paramref name="source"/> is placing, if it's the palette tree
    /// dragging one of its component leaves — the one place that needs to know <c>tvPalette</c> is a
    /// <see cref="TreeView"/> of <see cref="ComponentClass"/> tags; <see cref="DesignSurface"/> doesn't.</summary>
    static ComponentClass? DraggedPaletteClass(Control source) => source is TreeView tv ? tv.Selected?.Tag as ComponentClass : null;

    /// <summary>Rebuilds the palette tree from <see cref="_palette"/>, grouped by <see cref="PaletteCategories"/>
    /// and filtered by <c>edtPaletteFilter</c>'s text; clears any pending placement, since the armed class may
    /// no longer be in the filtered list.</summary>
    void RebuildPalette()
    {
        tvPalette.Nodes.Clear();
        string filter = edtPaletteFilter.Text.Trim();
        var groups = _palette
            .Where(c => filter.Length == 0 || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => PaletteCategories.Of(c.Name))
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var category = new TreeNode(g.Key) { Expanded = true };
            foreach (var cls in g.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                var node = category.Add(cls.Name);
                node.Tag = cls;
                node.Icon = PaletteIcons.For(cls);
            }
            tvPalette.Nodes.Add(category);
        }
        tvPalette.Invalidate();
        if (_current != null) _current.Surface.PendingClass = null;
    }

    void lstRecent_DblClick(Control sender)
    {
        if (lstRecent.ItemIndex >= 0 && lstRecent.ItemIndex < _settings.RecentProjects.Count)
            OpenProject(_settings.RecentProjects[lstRecent.ItemIndex]);
    }

    // ---- Edit and component commands ----

    IEditTarget? EditTarget => _docs.Active as IEditTarget;

    void OpenMenuEditor()
    {
        if (Document is not { } doc) return;
        var menu = doc.Primary as IMenuOwner ?? doc.Form.Descendants().OfType<IMenuOwner>().FirstOrDefault();
        if (menu == null) { Dialogs.ShowMessage("Add a MainMenu or PopupMenu to the form first.", this); return; }
        MenuEditor.Edit(doc, menu, this);
    }

    void OpenTabOrder()
    {
        if (Document is { } doc) TabOrderEditor.Edit(doc, doc.PasteTarget(), this);
    }

    void Reorder(bool toFront)
    {
        if (Document is not { } doc) return;
        var targets = doc.Selection.Where(c => c.Parent != null).ToList();
        if (targets.Count == 0) return;
        doc.Snapshot();
        foreach (var c in targets)
        {
            var parent = c.Parent!;
            parent.Remove(c);
            parent.Insert(toFront ? parent.Children.Count : 0, c);
        }
        doc.SelectMany(targets);
        doc.NotifyChanged();
    }

    void ToggleShowGrid()
    {
        _showGrid = !_showGrid;
        foreach (var f in _forms) { f.Surface.ShowGrid = _showGrid; f.Surface.Invalidate(); }
        RefreshCommandState();
    }

    void ToggleLockControls()
    {
        _locked = !_locked;
        foreach (var f in _forms) { f.Surface.Locked = _locked; f.Surface.Invalidate(); }
        RefreshCommandState();
    }

    void frmDesigner_CloseQuery(Control sender, CloseQueryEventArgs e)
    {
        e.CanClose = ConfirmSave(_docs.Documents);
        if (!e.CanClose) return;
        SaveSession();
        CloseLiveForm();
    }
}
