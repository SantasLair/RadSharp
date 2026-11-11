using RadSharp.Design;
using RadSharp.Design.Code;
using RadSharp.DesignHost.Protocol;
using RadSharp.Drawing;

namespace RadSharp.DesignHost;

/// <summary>
/// An open form file: its <see cref="DesignDocument"/> and the three pane forms that edit it (design surface,
/// Object Inspector, component palette), wired the way the standalone designer wires them.
/// </summary>
internal sealed class HostDocument
{
    readonly DesignHostServer _server;
    readonly List<ComponentClass> _palette = ComponentPalette.Classes();
    bool _syncing;

    public HostDocument(DesignHostServer server, int docId, string path, DesignDocument doc)
    {
        _server = server;
        DocId = docId;
        Path = path;
        Doc = doc;
        // Selection changes never alter whether the document is modified, so they do not mark it stale.
        doc.SelectionChanged += () => { UpdateInspector(); StateDirty = true; };
        doc.FormReplaced += () => { UseDesignedTheme(); UpdateInspector(); StateDirty = ModifiedStale = true; };
        UseDesignedTheme();
        doc.Changed += () =>
        {
            GridProps?.RefreshValues();
            GridEvents?.RefreshValues();
            StateDirty = ModifiedStale = true;
        };
    }

    /// <summary>
    /// The form being designed is drawn in the theme it will run with (the default), not the designer's own theme:
    /// <see cref="Theme.Current"/> may be dark to match the IDE, which would put light theme text on a form's
    /// explicitly light panels. Applied after every load, undo, redo and reload, since each creates a new form.
    /// </summary>
    void UseDesignedTheme()
    {
        if (ReferenceEquals(Doc.Form.Theme, Theme.Default)) return;
        Doc.Form.Theme = Theme.Default;
        Doc.Form.InvalidateAll();
        Surface?.Invalidate();
    }

    public int DocId { get; }
    public string Path { get; set; }
    public DesignDocument Doc { get; }

    public HostWindow? SurfaceWindow { get; set; }
    public HostWindow? InspectorWindow { get; set; }
    public HostWindow? PaletteWindow { get; set; }
    public IEnumerable<HostWindow> Windows => new[] { SurfaceWindow, InspectorWindow, PaletteWindow }.OfType<HostWindow>();

    public DesignSurface? Surface { get; private set; }
    public PageControl? InspectorPages { get; private set; }
    public PropertyGrid? GridProps { get; private set; }
    public PropertyGrid? GridEvents { get; private set; }
    public TreeView? PaletteTree { get; private set; }
    public Edit? PaletteFilter { get; private set; }

    /// <summary>Something that <see cref="DocumentState"/> reports may have changed.</summary>
    public bool StateDirty { get; set; } = true;

    /// <summary>The form changed since <see cref="DesignDocument.Modified"/> was last evaluated (it serialises the whole form).</summary>
    public bool ModifiedStale { get; set; } = true;
    public long LastModifiedCheck { get; set; }

    /// <summary>How often <see cref="DesignDocument.Modified"/> was evaluated (for tests).</summary>
    public int ModifiedEvaluations { get; set; }
    public (bool Dirty, bool CanUndo, bool CanRedo, bool HasSelection)? SentState { get; set; }

    public bool IsFormFile => Path.EndsWith(".radform.json", StringComparison.OrdinalIgnoreCase);
    public string Title => IsFormFile ? RadProject.FormClassName(Path) : System.IO.Path.GetFileName(Path);

    // ---- Panes ----

    /// <summary>Builds (or rebuilds, e.g. for a new DPI scale) the pane form for <paramref name="role"/>.</summary>
    public Form BuildPane(WindowRole role, double scale) => role switch
    {
        WindowRole.Surface => BuildSurface(scale),
        WindowRole.Inspector => BuildInspector(scale),
        WindowRole.Palette => BuildPalette(scale),
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    static Form NewPane(string name, string caption)
    {
        // Laid out at 100 % and scaled once by ApplyScale, like a loaded form file.
        var form = new Form { Name = name, Caption = caption };
        form.SetBounds(0, 0, 400, 300);
        return form;
    }

    Form BuildSurface(double scale)
    {
        var old = Surface;
        var form = NewPane("frmSurface", Title);
        var surface = new DesignSurface { Name = "surface", Align = Align.Client, ShowGrid = old?.ShowGrid ?? true, Locked = old?.Locked ?? false };
        form.Add(surface);
        form.ApplyScale(scale);
        if (old != null) old.Document = null;   // unhook the old surface from the document's events
        surface.Document = Doc;
        surface.PendingClass = old?.PendingClass;
        surface.Created += _ =>
        {
            _syncing = true;
            if (PaletteTree != null) PaletteTree.Selected = null;
            _syncing = false;
        };
        surface.DoubleClicked += c => { if (CodeIntegration.DefaultEvent(c) is { } ev) EditHandler(c, ev); };
        surface.ContextMenuRequested += (x, y) => ShowContextMenu(form, surface, x, y);
        Surface = surface;
        surface.Focus();
        return form;
    }

    Form BuildInspector(double scale)
    {
        int page = InspectorPages?.ActivePageIndex ?? 0;
        var form = NewPane("frmInspector", "Object Inspector");
        var pages = new PageControl { Name = "pgInspector", Align = Align.Client };
        var props = new TabSheet { Name = "tsProperties", Caption = "Properties" };
        var events = new TabSheet { Name = "tsEvents", Caption = "Events" };
        pages.Add(props);
        pages.Add(events);
        var gridProps = new PropertyGrid { Name = "gridProps", Align = Align.Client, Arrangement = PropertyGridArrangement.Categorized };
        var gridEvents = new PropertyGrid { Name = "gridEvents", Align = Align.Client, Mode = PropertyGridMode.Events };
        props.Add(gridProps);
        events.Add(gridEvents);
        form.Add(pages);
        form.ApplyScale(scale);
        pages.ActivePageIndex = page;

        foreach (var grid in new[] { gridProps, gridEvents })
        {
            grid.PropertyChanging += (_, _) => Doc.Snapshot();
            grid.PropertyChanged += (_, _) => Doc.NotifyChanged();
        }
        gridEvents.EventDoubleClick += (_, ev) => EditHandler(Doc.Primary, ev);
        GridProps?.SetTargets([]);
        GridEvents?.SetTargets([]);
        InspectorPages = pages;
        GridProps = gridProps;
        GridEvents = gridEvents;
        UpdateInspector();
        return form;
    }

    Form BuildPalette(double scale)
    {
        string filterText = PaletteFilter?.Text ?? "";
        var form = NewPane("frmPalette", "Palette");
        var filter = new Edit { Name = "edtPaletteFilter", Align = Align.Top, Placeholder = "Search components", Text = filterText };
        var tree = new TreeView { Name = "tvPalette", Align = Align.Client };
        form.Add(filter);
        form.Add(tree);
        form.ApplyScale(scale);
        PaletteFilter = filter;
        PaletteTree = tree;
        FillPalette();
        filter.Change += _ => FillPalette();
        tree.Change += _ =>
        {
            if (_syncing || Surface == null) return;
            Surface.PendingClass = tree.Selected?.Tag as ComponentClass;
        };
        return form;
    }

    void FillPalette()
    {
        if (PaletteTree == null) return;
        _syncing = true;
        ComponentPalette.Fill(PaletteTree, _palette, PaletteFilter?.Text ?? "");
        _syncing = false;
        if (Surface != null) Surface.PendingClass = null;
    }

    void UpdateInspector()
    {
        if (GridProps == null || GridEvents == null) return;
        IReadOnlyList<Control> targets = Doc.Selection.Count > 0 ? Doc.Selection : [Doc.Primary];
        GridProps.SetTargets(targets);
        GridEvents.SetTargets(targets);
        Surface?.Invalidate();
    }

    void ShowContextMenu(Form form, DesignSurface surface, int x, int y)
    {
        bool hasSelection = Doc.Selection.Count > 0;
        bool canPaste = RadSharp.Text.Clipboard.GetText() is { } t && t.TrimStart().StartsWith('[');
        bool hasMenu = FindMenuOwner() != null;
        var menu = new PopupMenu();
        void Add(string caption, string command, bool enabled = true)
        {
            var item = menu.Add(caption);
            item.Enabled = enabled;
            item.Click += _ => Execute(command);
        }
        void Sep() => menu.Items.Add(new MenuItem { Separator = true });
        Add("Cut", "Cut", hasSelection);
        Add("Copy", "Copy", hasSelection);
        Add("Paste", "Paste", canPaste);
        Add("Delete", "Delete", hasSelection);
        Sep();
        Add("Bring to Front", "BringToFront", hasSelection);
        Add("Send to Back", "SendToBack", hasSelection);
        Sep();
        Add("Tab Order…", "TabOrder");
        Add("Menu Editor…", "MenuEditor", hasMenu);
        Sep();
        Add("Select All", "SelectAll");
        menu.Popup(form, surface.AbsoluteLeft + x, surface.AbsoluteTop + y);
    }

    /// <summary>The menu the Menu Editor edits: the selected MainMenu/PopupMenu, else the form's first one.</summary>
    IMenuOwner? FindMenuOwner() =>
        Doc.Primary as IMenuOwner ?? Doc.Form.Descendants().OfType<IMenuOwner>().FirstOrDefault();

    /// <summary>Moves the selected controls to the top (or bottom) of their parents' z-order, as one undo step.</summary>
    void Reorder(bool toFront)
    {
        var targets = Doc.Selection.Where(c => c.Parent != null).ToList();
        if (targets.Count == 0) return;
        Doc.Snapshot();
        foreach (var c in targets)
        {
            var parent = c.Parent!;
            parent.Remove(c);
            parent.Insert(toFront ? parent.Children.Count : 0, c);
        }
        Doc.SelectMany(targets);
        Doc.NotifyChanged();
    }

    // ---- Commands ----

    /// <summary>Runs an editor command (the protocol's <c>Command</c> names); false if the name is unknown.</summary>
    public bool Execute(string name)
    {
        switch (name)
        {
            case "Undo": Doc.Undo(); break;
            case "Redo": Doc.Redo(); break;
            case "Cut": Surface?.CopySelection(); Doc.DeleteSelection(); break;
            case "Copy": Surface?.CopySelection(); break;
            case "Paste": Surface?.PasteClipboard(); break;
            case "Delete": Doc.DeleteSelection(); break;
            case "SelectAll": Doc.SelectMany(Doc.PasteTarget().Children.Where(c => c.IsShown || c is Component)); break;
            case "BringToFront": Reorder(toFront: true); break;
            case "SendToBack": Reorder(toFront: false); break;
            // The editors are modal RADSharp forms; HeadlessPlatform shows them as Dialog windows owned by the surface.
            case "TabOrder": TabOrderEditor.Edit(Doc, Doc.PasteTarget(), Surface?.OwningForm); break;
            case "MenuEditor":
                if (FindMenuOwner() is not { } menu) return true;
                MenuEditor.Edit(Doc, menu, Surface?.OwningForm);
                break;
            default: return false;
        }
        Surface?.Invalidate();
        StateDirty = ModifiedStale = true;
        return true;
    }

    // ---- Handlers in the code-behind ----

    /// <summary>
    /// Double-click on a control or an event row: as in the standalone designer, gives the event a handler (the
    /// conventional name if it has none) and makes sure the method exists in the code-behind, working on the
    /// client's editor buffer (<see cref="CodeRequest"/>), never on the file. The client then applies the new text
    /// (<see cref="ApplyCodeEdit"/>) or, if the handler already existed, just shows it (<see cref="NavigateToCode"/>). The form file itself is
    /// not saved: the document just becomes dirty, and the IDE saves it.
    /// </summary>
    public void EditHandler(Control target, EventMeta ev)
    {
        var code = IsFormFile ? RadProject.CodeFileFor(Path) : null;
        if (code == null || !File.Exists(code) || FindProjectFile(System.IO.Path.GetDirectoryName(Path)) == null)
        {
            _server.SendError(DocId, $"Can't create an event handler: {System.IO.Path.GetFileName(Path)} is not part of a RADSharp project "
                + $"(it needs a .csproj in its folder or above and its code-behind {(code != null ? System.IO.Path.GetFileName(code) : "<Name>.cs")} next to it).");
            return;
        }
        bool added = !target.EventHandlers.TryGetValue(ev.Name, out var handler);
        if (added)
        {
            handler = CodeIntegration.HandlerName(target, Doc.Form, ev);
            Doc.Snapshot();
            target.EventHandlers[ev.Name] = handler!;
            Doc.NotifyChanged();
        }
        var className = RadProject.FormClassName(Path);
        var targetName = target.Name;
        var isForm = target is Form;

        // The handler name was assigned before the code was touched: take it back if the code side fails.
        void RollBack()
        {
            if (!added) return;
            var current = isForm ? Doc.Form : Doc.Form.FindControl(targetName);
            if (current != null && current.EventHandlers.TryGetValue(ev.Name, out var now) && now == handler)
            {
                current.EventHandlers.Remove(ev.Name);
                Doc.NotifyChanged();
            }
        }

        // The code file may be open in the client with unsaved edits: ask for its buffer instead of touching the disk.
        _server.RequestCode(DocId, code, buffer =>
        {
            try
            {
                var original = buffer ?? File.ReadAllText(code);
                var (source, line) = CodeIntegration.EnsureHandler(original, className, handler!, ev.Parameters);
                if (source != original)
                    _server.Send(new ApplyCodeEdit { DocId = DocId, File = code, Text = source, Line = line, BaseHash = HashText(original) });
                else _server.Send(new NavigateToCode { DocId = DocId, File = code, Line = line });
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                RollBack();
                _server.SendError(DocId, $"Can't create the handler {handler} in {System.IO.Path.GetFileName(code)}: {e.Message}");
            }
        }, () =>
        {
            RollBack();
            _server.SendError(DocId, "code editor did not respond; try again");
        });
    }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 text (the <see cref="ApplyCodeEdit.BaseHash"/>).</summary>
    internal static string HashText(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>The nearest .csproj in <paramref name="dir"/> or a folder above it.</summary>
    static string? FindProjectFile(string? dir)
    {
        for (var d = dir; !string.IsNullOrEmpty(d); d = System.IO.Path.GetDirectoryName(d))
        {
            try
            {
                if (Directory.EnumerateFiles(d, "*.csproj").FirstOrDefault() is { } project) return project;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        return null;
    }
}
