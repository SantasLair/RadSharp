using RadSharp.Design;

namespace RadSharp.Designer;

/// <summary>
/// The IDE's commands and the menus, tool buttons and context menus built from them. Nothing here holds logic of
/// its own: a menu item is a view of a <see cref="Command"/>, and shortcuts are matched by the registry before any
/// menu sees them (<see cref="ProcessShortcut"/>).
/// </summary>
public partial class DesignerForm
{
    readonly CommandRegistry _commands = new();
    MenuItem _mniReopen = null!;

    bool HasProject => _project != null;
    bool HasForm => _project != null && _current != null;
    bool HasDocument => _project != null && _docs.Active != null;

    void RegisterCommands()
    {
        var c = _commands;
        Command Reg(string id, string title, string category, Action run, string shortcut = "", Func<bool>? enabled = null, Func<bool>? isChecked = null) =>
            c.Register(new Command(id, title, category, run, shortcut, enabled, isChecked));

        // File
        Reg("File.NewApplication", "New Application…", "File", NewApplication, "Mod+Shift+N");
        Reg("File.NewForm", "New Form", "File", () => NewForm(), "Mod+N", () => HasProject);
        Reg("File.OpenProject", "Open Project…", "File", OpenProjectDialog, "Mod+O");
        c.Register(new Command("File.Reopen", "Reopen Project", "File", p => { if (p is string path) OpenProject(path); }));
        Reg("File.Save", "Save", "File", () => { if (_docs.Active is { } d) Save(d); }, "Mod+S", () => HasDocument);
        Reg("File.SaveAll", "Save All", "File", SaveAll, "Mod+Shift+S", () => HasProject);
        c.Register(new Command("File.Close", "Close Document", "File", p => { if ((p as IDocumentView ?? _docs.Active) is { } d) CloseDocument(d); }, "Ctrl+F4", () => HasDocument));
        c.Register(new Command("File.CloseOthers", "Close Others", "File", p => { if ((p as IDocumentView ?? _docs.Active) is { } d) CloseOthers(d); }, "", () => HasDocument));
        Reg("File.CloseAll", "Close All Documents", "File", () => CloseAllDocuments(), "", () => HasDocument);
        Reg("File.CloseProject", "Close Project", "File", () => CloseProject(), "", () => HasProject);
        Reg("File.Exit", "Exit", "File", () => Close());

        // Edit (acts on the active document: the designer or the code editor)
        Reg("Edit.Undo", "Undo", "Edit", () => EditTarget?.Undo(), "Mod+Z", () => HasProject);
        c.Register(new Command("Edit.Redo", "Redo", "Edit", () => EditTarget?.Redo(), "Mod+Shift+Z", () => HasProject) { AltShortcut = "Ctrl+Y" });
        Reg("Edit.Cut", "Cut", "Edit", () => EditTarget?.Cut(), "Mod+X", () => HasDocument);
        Reg("Edit.Copy", "Copy", "Edit", () => EditTarget?.Copy(), "Mod+C", () => HasDocument);
        Reg("Edit.Paste", "Paste", "Edit", () => EditTarget?.Paste(), "Mod+V", () => HasDocument);
        Reg("Edit.Delete", "Delete", "Edit", () => EditTarget?.Delete(), "Delete", () => HasDocument);
        Reg("Edit.SelectAll", "Select All", "Edit", () => EditTarget?.SelectAll(), "Mod+A", () => HasDocument);
        Reg("Edit.LockControls", "Lock Controls", "Edit", ToggleLockControls, "", null, () => _locked);

        // View
        Reg("View.ToggleFormCode", "Toggle Form/Code", "View", () => ToggleFormCode(), "F12", () => HasDocument);
        Reg("View.SplitFormCode", "Split Form and Code", "View", ToggleSplitView, "", () => HasDocument, () => _docs.SplitView);
        Reg("View.ShowGrid", "Show Grid", "View", ToggleShowGrid, "", null, () => _showGrid);
        foreach (var w in _dock.Windows)
        {
            var window = w;
            Reg($"View.ToolWindow.{window.Id}", window.Title, "View", () => _dock.Show(window), "", null, () => window.IsShown);
        }
        foreach (var side in new[] { DockSide.Left, DockSide.Right, DockSide.Bottom })
        {
            var s = side;
            Reg($"View.Collapse{s}", $"Collapse {s} Panel", "View", () => _dock.SetCollapsed(s, !_dock.Region(s).Collapsed), "",
                () => _dock.Region(s).Windows.Count > 0, () => _dock.Region(s).Collapsed);
        }
        Reg("View.ToggleDarkMode", "Toggle Dark Mode", "View", ToggleDarkMode);

        // Tool window tabs (their context menu; the window is the parameter)
        foreach (var side in new[] { DockSide.Left, DockSide.Right, DockSide.Bottom })
        {
            var s = side;
            c.Register(new Command($"Window.Dock{s}", $"Dock {s}", "Window", p => { if (p is ToolWindow w) _dock.Move(w, s); }));
        }
        c.Register(new Command("Window.Hide", "Hide", "Window", p => { if (p is ToolWindow w) _dock.Hide(w); }));
        c.Register(new Command("Window.Float", "Float (coming in phase B)", "Window", p => { if (p is ToolWindow w) _dock.Float(w); }, "", () => _dock.CanFloat));
        c.Register(new Command("Window.KeepOpen", "Keep Open", "Window", p => { if ((p as IDocumentView ?? _docs.Active) is { } d) _docs.MakePermanent(d); }));
        Reg("Window.NextDocument", "Next Document", "Window", () => CycleDocument(1), "Ctrl+Tab", () => _docs.Documents.Count > 1);
        Reg("Window.PreviousDocument", "Previous Document", "Window", () => CycleDocument(-1), "Ctrl+Shift+Tab", () => _docs.Documents.Count > 1);

        // Project
        Reg("Project.AddNewForm", "Add New Form", "Project", () => NewForm(), "", () => HasProject);
        Reg("Project.RemoveForm", "Remove Form from Project…", "Project", RemoveForm, "", () => HasForm);
        Reg("Project.SetMainForm", "Set as Main Form", "Project", SetMainForm, "", () => HasForm);
        Reg("Project.OpenInIde", "Open in IDE", "Project", OpenInIde, "Mod+Shift+E", () => HasProject);

        // Run
        Reg("Run.Run", "Run", "Run", () => Run(), "F9", () => HasProject);
        Reg("Run.Build", "Build", "Run", Build, "Ctrl+F9", () => HasProject);

        // Component
        Reg("Component.MenuEditor", "Menu Editor…", "Component", OpenMenuEditor, "Mod+M", () => HasForm);
        Reg("Component.TabOrder", "Tab Order…", "Component", OpenTabOrder, "", () => HasForm);
        Reg("Component.BringToFront", "Bring to Front", "Component", () => Reorder(toFront: true), "", () => HasForm);
        Reg("Component.SendToBack", "Send to Back", "Component", () => Reorder(toFront: false), "", () => HasForm);

        // Format
        Reg("Format.AlignLefts", "Align Lefts", "Format", AlignLefts, "", () => HasForm);
        Reg("Format.AlignCenters", "Align Centers", "Format", AlignCenters, "", () => HasForm);
        Reg("Format.AlignRights", "Align Rights", "Format", AlignRights, "", () => HasForm);
        Reg("Format.AlignTops", "Align Tops", "Format", AlignTops, "", () => HasForm);
        Reg("Format.AlignMiddles", "Align Middles", "Format", AlignMiddles, "", () => HasForm);
        Reg("Format.AlignBottoms", "Align Bottoms", "Format", AlignBottoms, "", () => HasForm);
        Reg("Format.SameWidth", "Make Same Width", "Format", SameWidth, "", () => HasForm);
        Reg("Format.SameHeight", "Make Same Height", "Format", SameHeight, "", () => HasForm);
        Reg("Format.SameSize", "Make Same Size", "Format", SameBoth, "", () => HasForm);
        Reg("Format.SpaceHorizontally", "Space Evenly Horizontally", "Format", SpaceHorizontally, "", () => HasForm);
        Reg("Format.SpaceVertically", "Space Evenly Vertically", "Format", SpaceVertically, "", () => HasForm);
        Reg("Format.CenterHorizontally", "Center in Form Horizontally", "Format", CenterHorizontally, "", () => HasForm);
        Reg("Format.CenterVertically", "Center in Form Vertically", "Format", CenterVertically, "", () => HasForm);

        // Tools
        Reg("Tools.Options", "Options…", "Tools", ShowOptions);
        Reg("Tools.KeyboardShortcuts", "Keyboard Shortcuts…", "Tools", ShowKeyboardShortcuts);

        RegisterProjectCommands();

        c.ShortcutsChanged += () => RefreshCommandState();
    }

    /// <summary>The main menu and tool bar, built from the registry.</summary>
    void BuildMenus()
    {
        var bar = mnuMain;
        bar.Items.Clear();
        MenuItem Top(string caption)
        {
            var top = new MenuItem(caption);
            top.Opening += m => _commands.Refresh(m.Items);
            bar.Items.Add(top);
            return top;
        }
        MenuItem Sub(MenuItem parent, string caption)
        {
            var sub = new MenuItem(caption);
            sub.Opening += m => _commands.Refresh(m.Items);
            parent.Items.Add(sub);
            return sub;
        }
        void Cmd(MenuItem parent, string id, string? caption = null) => parent.Items.Add(_commands.CreateMenuItem(id, caption));

        var file = Top("File");
        Cmd(file, "File.NewApplication");
        Cmd(file, "File.NewForm");
        file.AddSeparator();
        Cmd(file, "File.OpenProject");
        _mniReopen = Sub(file, "Reopen");
        file.AddSeparator();
        Cmd(file, "File.Save");
        Cmd(file, "File.SaveAll");
        Cmd(file, "File.Close");
        Cmd(file, "File.CloseAll");
        Cmd(file, "File.CloseProject");
        file.AddSeparator();
        Cmd(file, "File.Exit");

        var edit = Top("Edit");
        Cmd(edit, "Edit.Undo");
        Cmd(edit, "Edit.Redo");
        edit.AddSeparator();
        Cmd(edit, "Edit.Cut");
        Cmd(edit, "Edit.Copy");
        Cmd(edit, "Edit.Paste");
        Cmd(edit, "Edit.Delete");
        edit.AddSeparator();
        Cmd(edit, "Edit.SelectAll");
        edit.AddSeparator();
        Cmd(edit, "Edit.LockControls");

        var view = Top("View");
        Cmd(view, "View.ToggleFormCode");
        Cmd(view, "View.SplitFormCode");
        Cmd(view, "View.ShowGrid");
        view.AddSeparator();
        var tools = Sub(view, "Tool Windows");
        foreach (var w in _dock.Windows) Cmd(tools, $"View.ToolWindow.{w.Id}");
        Cmd(view, "View.CollapseLeft");
        Cmd(view, "View.CollapseRight");
        Cmd(view, "View.CollapseBottom");
        view.AddSeparator();
        Cmd(view, "Window.NextDocument");
        Cmd(view, "Window.PreviousDocument");
        view.AddSeparator();
        Cmd(view, "View.ToggleDarkMode");

        var project = Top("Project");
        Cmd(project, "Project.AddNewForm");
        Cmd(project, "Project.RemoveForm");
        Cmd(project, "Project.SetMainForm");
        project.AddSeparator();
        Cmd(project, "Project.OpenInIde");
        AddProjectMenuItems(project);

        var run = Top("Run");
        Cmd(run, "Run.Run");
        Cmd(run, "Run.Build");

        var component = Top("Component");
        Cmd(component, "Component.MenuEditor");
        Cmd(component, "Component.TabOrder");
        component.AddSeparator();
        Cmd(component, "Component.BringToFront");
        Cmd(component, "Component.SendToBack");

        var format = Top("Format");
        var align = Sub(format, "Align");
        Cmd(align, "Format.AlignLefts", "Lefts");
        Cmd(align, "Format.AlignCenters", "Centers");
        Cmd(align, "Format.AlignRights", "Rights");
        align.AddSeparator();
        Cmd(align, "Format.AlignTops", "Tops");
        Cmd(align, "Format.AlignMiddles", "Middles");
        Cmd(align, "Format.AlignBottoms", "Bottoms");
        var size = Sub(format, "Make Same Size");
        Cmd(size, "Format.SameWidth", "Width");
        Cmd(size, "Format.SameHeight", "Height");
        Cmd(size, "Format.SameSize", "Both");
        var space = Sub(format, "Space Evenly");
        Cmd(space, "Format.SpaceHorizontally", "Horizontally");
        Cmd(space, "Format.SpaceVertically", "Vertically");
        var center = Sub(format, "Center in Form");
        Cmd(center, "Format.CenterHorizontally", "Horizontally");
        Cmd(center, "Format.CenterVertically", "Vertically");

        var toolsMenu = Top("Tools");
        Cmd(toolsMenu, "Tools.Options");
        Cmd(toolsMenu, "Tools.KeyboardShortcuts");

        // Tool bar and welcome page buttons.
        _commands.Bind(btnOpenProject, "File.OpenProject");
        _commands.Bind(btnSaveAll, "File.SaveAll");
        _commands.Bind(btnNewForm, "File.NewForm");
        _commands.Bind(btnUndo, "Edit.Undo");
        _commands.Bind(btnRedo, "Edit.Redo");
        _commands.Bind(btnToggleCode, "View.ToggleFormCode");
        _commands.Bind(btnOpenInIde, "Project.OpenInIde");
        _commands.Bind(btnRun, "Run.Run");
        _commands.Bind(btnBuild, "Run.Build");
        _commands.Bind(btnNewApplication, "File.NewApplication");
        _commands.Bind(btnOpenProjectWelcome, "File.OpenProject");
        RebuildReopenMenu();
    }

    void RebuildReopenMenu()
    {
        if (_mniReopen == null) return;
        _mniReopen.Items.Clear();
        foreach (var p in _settings.RecentProjects)
            _mniReopen.Items.Add(_commands.CreateMenuItem("File.Reopen", Path.GetFileNameWithoutExtension(p), p));
        if (_mniReopen.Items.Count == 0)
        {
            var none = _commands.CreateMenuItem("File.Reopen", "(none)");
            none.Enabled = false;
            _mniReopen.Items.Add(none);
        }
    }

    /// <summary>Re-reads every command's enabled/checked state into the menus and buttons.</summary>
    void RefreshCommandState()
    {
        if (_mniReopen == null) return;
        _commands.Refresh(mnuMain.Items);
        foreach (var item in _mniReopen.Items) if (item.Caption == "(none)") item.Enabled = false;
        _commands.RefreshControls();
    }

    /// <summary>Command shortcuts come first: the registry matches them before any menu does.</summary>
    protected override bool ProcessShortcut(KeyEventArgs e) => _commands.ProcessShortcut(e) || base.ProcessShortcut(e);

    void CycleDocument(int delta)
    {
        var docs = _docs.Documents;
        if (docs.Count < 2) return;
        int i = _docs.Active is { } a ? docs.ToList().IndexOf(a) : 0;
        _docs.Activate(docs[(i + delta + docs.Count) % docs.Count]);
    }

    // ---- Context menus ----

    MenuItem Item(PopupMenu menu, string id, string? caption = null, object? parameter = null, bool? enabled = null)
    {
        var item = _commands.CreateMenuItem(id, caption, parameter);
        if (enabled is { } e) item.Enabled = e;
        menu.Items.Add(item);
        return item;
    }

    static void Separator(PopupMenu menu) => menu.Items.Add(new MenuItem { Caption = "-", Separator = true });

    /// <summary>The design surface's right-click menu: the same commands as the Edit/Component/Format menus,
    /// acting on the (already updated) selection.</summary>
    void ShowSurfaceContextMenu(DesignSurface surface, int x, int y)
    {
        if (Document is not { } doc) return;
        bool hasSelection = doc.Selection.Count > 0;
        bool hasMenu = doc.Primary is IMenuOwner || doc.Form.Descendants().OfType<IMenuOwner>().Any();
        bool canPaste = RadSharp.Text.Clipboard.GetText() is { } t && t.TrimStart().StartsWith('[');
        bool canAlign = Movable.Count >= 2;

        var menu = BuildSurfaceMenu(hasSelection, hasMenu, canPaste, canAlign, surface.Locked);
        menu.Popup(this, surface.AbsoluteLeft + x, surface.AbsoluteTop + y);
    }

    PopupMenu BuildSurfaceMenu(bool hasSelection, bool hasMenu, bool canPaste, bool canAlign, bool locked)
    {
        var menu = new PopupMenu();
        MenuItem Submenu(string caption, bool enabled, params (string Id, string Caption)[] children)
        {
            var item = new MenuItem(caption) { Enabled = enabled };
            foreach (var (id, c) in children) item.Items.Add(_commands.CreateMenuItem(id, c));
            menu.Items.Add(item);
            return item;
        }

        Item(menu, "Edit.Cut", enabled: hasSelection);
        Item(menu, "Edit.Copy", enabled: hasSelection);
        Item(menu, "Edit.Paste", enabled: canPaste);
        Item(menu, "Edit.Delete", enabled: hasSelection);
        Separator(menu);
        Item(menu, "Component.BringToFront", enabled: hasSelection);
        Item(menu, "Component.SendToBack", enabled: hasSelection);
        Separator(menu);
        Submenu("Align", canAlign,
            ("Format.AlignLefts", "Lefts"), ("Format.AlignCenters", "Centers"), ("Format.AlignRights", "Rights"),
            ("Format.AlignTops", "Tops"), ("Format.AlignMiddles", "Middles"), ("Format.AlignBottoms", "Bottoms"));
        Submenu("Make Same Size", canAlign,
            ("Format.SameWidth", "Width"), ("Format.SameHeight", "Height"), ("Format.SameSize", "Both"));
        Separator(menu);
        Item(menu, "Component.TabOrder", enabled: true);
        Item(menu, "Component.MenuEditor", enabled: hasMenu);
        Separator(menu);
        Item(menu, "Edit.SelectAll", enabled: true);
        Separator(menu);
        Item(menu, "Edit.LockControls").Checked = locked;
        Separator(menu);
        Item(menu, "View.ToggleFormCode", "View Code");
        return menu;
    }

    /// <summary>A document tab's right-click menu.</summary>
    PopupMenu BuildTabMenu(IDocumentView doc)
    {
        var menu = new PopupMenu();
        Item(menu, "File.Close", "Close", doc);
        Item(menu, "File.CloseOthers", "Close Others", doc, _docs.Documents.Count > 1);
        Item(menu, "File.CloseAll", "Close All");
        Separator(menu);
        Item(menu, "Window.KeepOpen", "Keep Open", doc, _docs.IsPreview(doc));
        Item(menu, "View.SplitFormCode");
        return menu;
    }

    /// <summary>A tool window tab's right-click menu.</summary>
    PopupMenu BuildToolWindowMenu(ToolWindow window)
    {
        var menu = new PopupMenu();
        foreach (var side in new[] { DockSide.Left, DockSide.Right, DockSide.Bottom })
            Item(menu, $"Window.Dock{side}", parameter: window, enabled: window.Region?.Side != side);
        Item(menu, "Window.Float", parameter: window);
        Separator(menu);
        Item(menu, "Window.Hide", parameter: window);
        return menu;
    }

    // ---- Tools ----

    void ShowOptions()
    {
        var dialog = new OptionsDialog(_settings, _ide, this);
        if (dialog.ShowModal() != ModalResult.Ok) return;
        dialog.Apply();
        SaveSettings();
        SaveIdeSettings();
        UpdateUi();
    }

    void ShowKeyboardShortcuts()
    {
        var dialog = new KeyboardShortcutsDialog(_commands, this);
        if (dialog.ShowModal() != ModalResult.Ok) return;
        dialog.Apply();
        SaveIdeSettings();
        RefreshCommandState();
    }
}
