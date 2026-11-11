using RadSharp.Design;
using RadSharp.Design.Code;
using RadSharp.Drawing;

namespace RadSharp.Designer;

/// <summary>
/// The RADSharp IDE — itself a RADSharp form (DesignerForm.radform.json). As in Delphi and Lazarus, the
/// designer always works on a project: with none open it shows the welcome page (new application, open
/// project, recent projects); with one open, each open form has a tab with its own design surface.
/// </summary>
public partial class DesignerForm : Form
{
    /// <summary>A form open in the designer: its file, document and design surface on a tab.</summary>
    public sealed class OpenForm(string path, DesignDocument doc, DesignSurface surface, TabSheet sheet)
    {
        public string Path { get; } = path;
        public DesignDocument Document { get; } = doc;
        public DesignSurface Surface { get; } = surface;
        public TabSheet Sheet { get; } = sheet;
        public string ClassName => RadProject.FormClassName(Path);
    }

    readonly List<OpenForm> _forms = [];
    OpenForm? _active;
    RadProject? _project;
    readonly DesignerSettings _settings;
    readonly string? _settingsPath;
    readonly List<ComponentClass> _palette;
    bool _syncing;

    public DesignerForm(string? projectFile = null, string? settingsPath = null)
    {
        InitializeComponent();
        _settingsPath = settingsPath;
        _settings = DesignerSettings.Load(settingsPath);
        _settings.RemoveMissing();

        // Apply theme from settings
        Theme.Current = _settings.Theme == "dark" ? Theme.Dark : Theme.Default;
        this.Theme = Theme.Current;

        _palette = ComponentPalette.Classes();
        RebuildPalette();
        tvPalette.DragMode = DragMode.Automatic;   // drag a palette entry onto a form to place it, not just click

        foreach (var grid in new[] { gridProps, gridEvents })
        {
            grid.PropertyChanging += (_, _) => Document?.Snapshot();
            grid.PropertyChanged += (_, _) => Document?.NotifyChanged();
        }
        gridEvents.EventDoubleClick += (_, ev) => { if (Document is { } d) EditHandler(d.Primary, ev); };
        gridProps.Arrangement = PropertyGridArrangement.Categorized;   // matches cboPropsArrange's default selection

        if (projectFile != null && File.Exists(projectFile)) OpenProject(projectFile);
        UpdateUi();
        Shown += _ => CheckSdk();
    }

    // ---- State (also used by tests) ----

    public RadProject? Project => _project;
    public IReadOnlyList<OpenForm> OpenForms => _forms;
    public OpenForm? ActiveForm => _active;
    public DesignDocument? Document => _active?.Document;
    public DesignerSettings Settings => _settings;
    public bool WelcomeVisible => pnlWelcome.Visible;

    void SaveSettings()
    {
        try { _settings.Save(_settingsPath); }
        catch (IOException) { }
    }

    // ---- Projects ----

    /// <summary>Opens a project (closing the current one first), then its main form.</summary>
    public bool OpenProject(string projectFile)
    {
        if (_project != null && !CloseProject()) return false;
        try { _project = new RadProject(projectFile); }
        catch (IOException e)
        {
            Dialogs.MessageDlg($"Could not open the project:\n{e.Message}", MessageType.Error, owner: this);
            return false;
        }
        _settings.AddRecent(_project.ProjectFile);
        SaveSettings();
        var main = _project.MainFormClass is { } cls ? _project.FormFileFor(cls) : null;
        if ((main ?? _project.FormFiles.FirstOrDefault()) is { } first) OpenFormFile(first);
        UpdateUi();
        Output($"Opened project {_project.ProjectFile}");
        return true;
    }

    /// <summary>Closes every form (asking to save changes) and returns to the welcome page.</summary>
    public bool CloseProject()
    {
        if (!ConfirmSave(_forms)) return false;
        CloseLiveForm();
        foreach (var f in _forms.ToList()) RemoveTab(f);
        _project = null;
        UpdateUi();
        return true;
    }

    // ---- Forms and tabs ----

    /// <summary>Opens a form of the project in its own tab (or switches to it if it is open).</summary>
    public OpenForm? OpenFormFile(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (_forms.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            Activate(open);
            return open;
        }
        var doc = new DesignDocument();
        try
        {
            var warnings = doc.Load(File.ReadAllText(path));
            doc.FileName = path;
            if (warnings.Count > 0) Output($"{System.IO.Path.GetFileName(path)}: " + string.Join("; ", warnings.Take(5)));
        }
        catch (Exception e) when (e is IOException or FormFileException or UnauthorizedAccessException)
        {
            Dialogs.MessageDlg($"Could not open {System.IO.Path.GetFileName(path)}:\n{e.Message}", MessageType.Error, owner: this);
            return null;
        }

        var sheet = new TabSheet { Caption = RadProject.FormClassName(path) };
        var surface = new DesignSurface { Document = doc, Align = Align.Client, ShowGrid = mniShowGrid.Checked, Locked = mniLockControls.Checked };
        sheet.Add(surface);
        pgForms.Add(sheet);
        var form = new OpenForm(path, doc, surface, sheet);
        _forms.Add(form);

        surface.Created += _ => tvPalette.Selected = null;
        surface.DoubleClicked += c => { if (CodeIntegration.DefaultEvent(c) is { } ev) EditHandler(c, ev); };
        surface.ContextMenuRequested += (x, y) => ShowSurfaceContextMenu(surface, x, y);
        // Drag a palette entry onto the surface to place it (an alternative to click-then-place); the surface
        // itself has no idea what a palette or a TreeView is, so the class comes from the drag's own source.
        surface.DragOver += (_, e) => e.Accept = DraggedPaletteClass(e.Source) != null;
        surface.DragDrop += (_, e) => { if (DraggedPaletteClass(e.Source) is { } cls) surface.CreateAt(cls, e.X, e.Y); };
        doc.SelectionChanged += () => { if (_active == form) UpdateInspector(); };
        doc.FormReplaced += () => { if (_active == form) { UpdateInspector(); RebuildStructure(); } };
        doc.Changed += () =>
        {
            sheet.Caption = form.ClassName + (doc.Modified ? " *" : "");
            if (_active != form) return;
            gridProps.RefreshValues();
            gridEvents.RefreshValues();
            RebuildStructure();
            UpdateTitle();
        };
        Activate(form);
        return form;
    }

    void Activate(OpenForm form)
    {
        _active = form;
        int i = pgForms.Pages.ToList().IndexOf(form.Sheet);
        if (i >= 0 && pgForms.ActivePageIndex != i) pgForms.ActivePageIndex = i;
        form.Surface.PendingClass = tvPalette.Selected?.Tag as ComponentClass;
        UpdateInspector();
        RebuildStructure();
        UpdateTitle();
        form.Surface.Focus();
    }

    void pgForms_Change(Control sender)
    {
        if (_forms.FirstOrDefault(f => f.Sheet == pgForms.ActivePage) is { } f && f != _active) Activate(f);
    }

    /// <summary>Closes a form's tab, asking to save its changes first.</summary>
    public bool CloseForm(OpenForm form)
    {
        if (!ConfirmSave([form])) return false;
        RemoveTab(form);
        return true;
    }

    void RemoveTab(OpenForm form)
    {
        _forms.Remove(form);
        pgForms.Remove(form.Sheet);
        if (_active == form)
        {
            _active = null;
            if (_forms.Count > 0) Activate(_forms[^1]);
        }
        UpdateUi();
    }

    public bool Save(OpenForm form)
    {
        File.WriteAllText(form.Path, form.Document.Save());
        form.Document.MarkSaved();
        form.Sheet.Caption = form.ClassName;
        UpdateTitle();
        return true;
    }

    public void SaveAll()
    {
        foreach (var f in _forms.Where(f => f.Document.Modified)) Save(f);
        sbMain.SimpleText = "All forms saved";
    }

    /// <summary>Asks once about all modified forms in <paramref name="forms"/>; false if the user cancelled.</summary>
    bool ConfirmSave(IEnumerable<OpenForm> forms)
    {
        var modified = forms.Where(f => f.Document.Modified).ToList();
        if (modified.Count == 0) return true;
        var names = string.Join(", ", modified.Select(f => f.ClassName));
        switch (Dialogs.MessageDlg($"Save changes to {names}?", MessageType.Confirmation, MessageButtons.YesNoCancel, this))
        {
            case ModalResult.Yes: foreach (var f in modified) Save(f); return true;
            case ModalResult.No: return true;
            default: return false;
        }
    }

    // ---- UI state ----

    void UpdateUi()
    {
        bool hasProject = _project != null;
        pnlWelcome.Visible = !hasProject;
        pgForms.Visible = hasProject;
        pnlPalette.Enabled = hasProject && _active != null;
        pnlInspector.Enabled = hasProject && _active != null;
        foreach (var b in new Control[] { btnSaveAll, btnNewForm, btnUndo, btnRedo, btnToggleCode, btnOpenInIde, btnRun, btnBuild })
            b.Enabled = hasProject;
        mniNewForm.Enabled = mniSave.Enabled = mniSaveAll.Enabled = mniCloseForm.Enabled = mniCloseProject.Enabled = hasProject;

        lblProject.Caption = _project?.Name ?? "";
        lstForms.Items = _project == null ? [] : [.. _project.SourceFiles.Select(ProjectFileLabel)];

        lstRecent.Items = [.. _settings.RecentProjects.Select(p => $"{System.IO.Path.GetFileNameWithoutExtension(p)}   —   {System.IO.Path.GetDirectoryName(p)}")];
        var source = _settings.EffectivePackageSource;
        lblPackages.Caption = source != null
            ? $"RADSharp packages for new projects: {source}"
            : "RADSharp packages: none found locally, so new projects use nuget.org (run dotnet pack in the RADSharp repository).";

        mniReopen.Items.Clear();
        foreach (var p in _settings.RecentProjects)
        {
            var path = p;
            mniReopen.Items.Add(new MenuItem(System.IO.Path.GetFileNameWithoutExtension(p), _ => OpenProject(path)));
        }
        if (mniReopen.Items.Count == 0) mniReopen.Items.Add(new MenuItem("(none)") { Enabled = false });

        if (!hasProject || _active == null)
        {
            gridProps.Target = null;
            gridEvents.Target = null;
            tvStructure.Nodes.Clear();
            edtName.Text = "";
        }
        UpdateTitle();
        InvalidateAll();
    }

    void UpdateTitle()
    {
        if (_project == null) { Caption = "RADSharp"; return; }
        Caption = _active == null
            ? $"RADSharp — {_project.Name}"
            : $"RADSharp — {_project.Name} — {_active.ClassName}{(_active.Document.Modified ? " *" : "")}";
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
        _active!.Surface.Invalidate();
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
        if (_active == null) return;
        var cls = tvPalette.Selected?.Tag as ComponentClass;
        _active.Surface.PendingClass = cls;
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
        ComponentPalette.Fill(tvPalette, _palette, edtPaletteFilter.Text);
        if (_active != null) _active.Surface.PendingClass = null;
    }

    /// <summary>A form's code-behind shows as its class name (like Delphi's Project Manager); any other
    /// source file (a plain model or helper class) shows by its path relative to the project.</summary>
    string ProjectFileLabel(string file)
    {
        if (_project!.FormFiles.FirstOrDefault(f => RadProject.CodeFileFor(f) == file) is { } formFile)
        {
            var cls = RadProject.FormClassName(formFile);
            return cls + (cls == _project.MainFormClass ? "   (main form)" : "");
        }
        return Path.GetRelativePath(_project.Directory, file);
    }

    void lstForms_DblClick(Control sender)
    {
        if (_project == null || lstForms.ItemIndex < 0) return;
        var file = _project.SourceFiles[lstForms.ItemIndex];
        if (_project.FormFiles.FirstOrDefault(f => RadProject.CodeFileFor(f) == file) is { } formFile)
            OpenFormFile(formFile);
        else
            OpenInEditor(file, 1);
    }

    void lstRecent_DblClick(Control sender)
    {
        if (lstRecent.ItemIndex >= 0 && lstRecent.ItemIndex < _settings.RecentProjects.Count)
            OpenProject(_settings.RecentProjects[lstRecent.ItemIndex]);
    }

    // ---- Edit and component commands (menu items and tool buttons share these handlers) ----

    void mnuUndo_Click(object sender) => Document?.Undo();
    void mnuRedo_Click(object sender) => Document?.Redo();
    void mnuCut_Click(object sender) { _active?.Surface.CopySelection(); Document?.DeleteSelection(); }
    void mnuCopy_Click(object sender) => _active?.Surface.CopySelection();
    void mnuPaste_Click(object sender) => _active?.Surface.PasteClipboard();
    void mnuDelete_Click(object sender) => Document?.DeleteSelection();

    void mnuSelectAll_Click(object sender)
    {
        if (Document is { } doc) doc.SelectMany(doc.PasteTarget().Children.Where(c => c.IsShown || c is Component));
    }

    void mnuMenuEditor_Click(object sender)
    {
        if (Document is not { } doc) return;
        var menu = doc.Primary as IMenuOwner ?? doc.Form.Descendants().OfType<IMenuOwner>().FirstOrDefault();
        if (menu == null) { Dialogs.ShowMessage("Add a MainMenu or PopupMenu to the form first.", this); return; }
        MenuEditor.Edit(doc, menu, this);
    }

    void mnuTabOrder_Click(object sender)
    {
        if (Document is { } doc) TabOrderEditor.Edit(doc, doc.PasteTarget(), this);
    }

    void mnuBringToFront_Click(object sender) => Reorder(toFront: true);
    void mnuSendToBack_Click(object sender) => Reorder(toFront: false);

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

    /// <summary>The design surface's right-click menu: the same commands as the Edit/Component menus, acting
    /// on the (already updated) selection.</summary>
    void ShowSurfaceContextMenu(DesignSurface surface, int x, int y)
    {
        if (Document is not { } doc) return;
        bool hasSelection = doc.Selection.Count > 0;
        bool hasMenu = doc.Primary is IMenuOwner || doc.Form.Descendants().OfType<IMenuOwner>().Any();
        bool canPaste = RadSharp.Text.Clipboard.GetText() is { } t && t.TrimStart().StartsWith('[');

        bool canAlign = Movable.Count >= 2;

        var menu = new PopupMenu();
        MenuItem Add(string caption, Action action, bool enabled = true)
        {
            var item = menu.Add(caption);
            item.Enabled = enabled;
            item.Click += _ => action();
            return item;
        }
        void Sep() => menu.Items.Add(new MenuItem { Separator = true });
        MenuItem Submenu(string caption, bool enabled, params (string Caption, Action Action)[] children)
        {
            var item = new MenuItem(caption) { Enabled = enabled };
            foreach (var (c, a) in children) { var child = item.Add(c); child.Click += _ => a(); }
            menu.Items.Add(item);
            return item;
        }

        Add("Cut", () => mnuCut_Click(this), hasSelection);
        Add("Copy", () => mnuCopy_Click(this), hasSelection);
        Add("Paste", () => mnuPaste_Click(this), canPaste);
        Add("Delete", () => mnuDelete_Click(this), hasSelection);
        Sep();
        Add("Bring to Front", () => mnuBringToFront_Click(this), hasSelection);
        Add("Send to Back", () => mnuSendToBack_Click(this), hasSelection);
        Sep();
        Submenu("Align", canAlign,
            ("Lefts", () => mnuAlignLefts_Click(this)), ("Centers", () => mnuAlignCenters_Click(this)), ("Rights", () => mnuAlignRights_Click(this)),
            ("Tops", () => mnuAlignTops_Click(this)), ("Middles", () => mnuAlignMiddles_Click(this)), ("Bottoms", () => mnuAlignBottoms_Click(this)));
        Submenu("Make Same Size", canAlign,
            ("Width", () => mnuSameWidth_Click(this)), ("Height", () => mnuSameHeight_Click(this)), ("Both", () => mnuSameBoth_Click(this)));
        Sep();
        Add("Tab Order…", () => mnuTabOrder_Click(this));
        Add("Menu Editor…", () => mnuMenuEditor_Click(this), hasMenu);
        Sep();
        Add("Select All", () => mnuSelectAll_Click(this));
        Sep();
        Add("Lock Controls", () => mnuLockControls_Click(this)).Checked = surface.Locked;

        menu.Popup(this, surface.AbsoluteLeft + x, surface.AbsoluteTop + y);
    }

    void mnuShowGrid_Click(object sender)
    {
        mniShowGrid.Checked = !mniShowGrid.Checked;
        foreach (var f in _forms) { f.Surface.ShowGrid = mniShowGrid.Checked; f.Surface.Invalidate(); }
    }

    void mnuLockControls_Click(object sender)
    {
        mniLockControls.Checked = !mniLockControls.Checked;
        foreach (var f in _forms) { f.Surface.Locked = mniLockControls.Checked; f.Surface.Invalidate(); }
    }

    void frmDesigner_CloseQuery(Control sender, CloseQueryEventArgs e)
    {
        e.CanClose = ConfirmSave(_forms);
        if (e.CanClose) CloseLiveForm();
    }
}
