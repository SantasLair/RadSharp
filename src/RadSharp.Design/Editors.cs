using RadSharp.Drawing;

namespace RadSharp.Design;

/// <summary>Helpers for building small editor forms in code at the current UI scale.</summary>
internal static class EditorLayout
{
    public static int S(int v) => LayoutMath(v * Form.DefaultScale);
    static int LayoutMath(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    public static T Put<T>(Control parent, T control, int x, int y, int w, int h) where T : Control
    {
        control.SetBounds(S(x), S(y), S(w), S(h));
        parent.Add(control);
        return control;
    }

    public static Form NewForm(string caption, int w, int h, Form? owner)
    {
        var f = new Form { Caption = caption, Owner = owner, Scale = Form.DefaultScale };
        f.SetBounds(0, 0, S(w), S(h));
        f.DesignWidth = f.Width;
        f.DesignHeight = f.Height;
        return f;
    }
}

/// <summary>
/// The menu editor (Delphi's menu designer): a tree of the menu's items and fields for the selected
/// one. Changes apply to the menu immediately; the whole session is one undo step.
/// </summary>
public sealed class MenuEditor
{
    readonly DesignDocument _doc;
    readonly IMenuOwner _menu;
    readonly TreeView _tree;
    readonly Edit _caption, _name, _shortcut, _altShortcut, _handler;
    readonly CheckBox _enabled, _checked;
    readonly HashSet<MenuItem> _handlerEdited = [];
    bool _loading;

    public Form Form { get; }

    /// <summary>
    /// The code side, wired by the designer (the editor itself can't reach the code-behind). Called with the items whose
    /// <see cref="MenuItem.ClickHandler"/> is set and whether to open the handler in the code editor: <c>false</c> when
    /// the editor closes after fields were edited (create the stubs quietly, all in one call), <c>true</c> with the one
    /// item of a double-click.
    /// </summary>
    public Action<IReadOnlyList<MenuItem>, bool>? HandlerRequested { get; set; }

    /// <summary>The conventional handler name, as the Object Inspector proposes: <c>mniExit_Click</c> (the caption's letters when the item has no name).</summary>
    public static string DefaultHandlerName(MenuItem item)
    {
        var name = item.Name.Length > 0 ? item.Name : "mni" + new string([.. item.Caption.Where(char.IsLetterOrDigit)]);
        return name + "_Click";
    }

    /// <summary>
    /// Double-click on an item or on the onClick field: gives the item a handler (the default name if it has none) and asks
    /// the designer to create the stub and show it. Does nothing for separators.
    /// </summary>
    public void EditHandler(MenuItem? item = null)
    {
        item ??= SelectedItem;
        if (item == null || item.IsSeparator) return;
        if (item.ClickHandler.Length == 0)
        {
            item.ClickHandler = DefaultHandlerName(item);
            if (item == SelectedItem) LoadFields();
            _doc.NotifyChanged();
        }
        _handlerEdited.Remove(item);
        HandlerRequested?.Invoke([item], true);
    }

    /// <summary>Creates the stubs of the handlers typed during the session (on close).</summary>
    void FlushHandlers()
    {
        var items = _handlerEdited.Where(i => i.ClickHandler.Length > 0).ToList();
        _handlerEdited.Clear();
        if (items.Count > 0) HandlerRequested?.Invoke(items, false);
    }

    public MenuEditor(DesignDocument doc, IMenuOwner menu, Form? owner = null)
    {
        _doc = doc;
        _menu = menu;
        Form = EditorLayout.NewForm($"Menu: {((Control)menu).Name}", 560, 400, owner);
        var f = Form;
        _tree = EditorLayout.Put(f, new TreeView { Name = "tvItems" }, 12, 12, 240, 330);
        EditorLayout.Put(f, new Label { Caption = "Caption" }, 268, 14, 90, 22);
        _caption = EditorLayout.Put(f, new Edit { Name = "edtCaption" }, 360, 12, 184, 26);
        EditorLayout.Put(f, new Label { Caption = "Name" }, 268, 48, 90, 22);
        _name = EditorLayout.Put(f, new Edit { Name = "edtName" }, 360, 46, 184, 26);
        EditorLayout.Put(f, new Label { Caption = "Shortcut" }, 268, 82, 90, 22);
        _shortcut = EditorLayout.Put(f, new Edit { Name = "edtShortcut", Placeholder = "Mod+S" }, 360, 80, 184, 26);
        EditorLayout.Put(f, new Label { Caption = "Alt shortcut" }, 268, 116, 90, 22);
        _altShortcut = EditorLayout.Put(f, new Edit { Name = "edtAltShortcut", Placeholder = "Ctrl+Y" }, 360, 114, 184, 26);
        EditorLayout.Put(f, new Label { Caption = "onClick" }, 268, 150, 90, 22);
        _handler = EditorLayout.Put(f, new Edit { Name = "edtHandler" }, 360, 148, 184, 26);
        _enabled = EditorLayout.Put(f, new CheckBox { Name = "chkEnabled", Caption = "Enabled" }, 360, 182, 184, 22);
        _checked = EditorLayout.Put(f, new CheckBox { Name = "chkChecked", Caption = "Checked" }, 360, 208, 184, 22);

        Button B(string name, string caption, int x, int y, Action action)
        {
            var b = EditorLayout.Put(f, new Button { Name = name, Caption = caption }, x, y, 88, 28);
            b.Click += _ => action();
            return b;
        }
        B("btnAdd", "Add", 268, 244, () => AddItem("New item"));
        B("btnAddSub", "Add sub", 360, 244, () => AddSubItem("New item"));
        B("btnDelete", "Delete", 452, 244, DeleteSelected);
        B("btnUp", "Up", 268, 280, () => MoveSelected(-1));
        B("btnDown", "Down", 360, 280, () => MoveSelected(1));
        B("btnSeparator", "Separator", 452, 280, () => AddSeparator());
        var close = B("btnClose", "Close", 456, 356, () => { });
        close.ModalResult = ModalResult.Ok;
        close.Default = true;

        _tree.Change += _ => LoadFields();
        _caption.Change += _ => Store(i => { i.Caption = _caption.Text; i.Separator = i.Caption == "-"; });
        _name.Change += _ => Store(i => i.Name = _name.Text.Trim());
        _shortcut.Change += _ => Store(i => i.Shortcut = _shortcut.Text.Trim());
        _altShortcut.Change += _ => Store(i => i.AltShortcut = _altShortcut.Text.Trim());
        _handler.Change += _ => Store(i => { i.ClickHandler = _handler.Text.Trim(); _handlerEdited.Add(i); });
        _handler.MouseDown += (_, e) => { if (e.Clicks == 2) EditHandler(); };
        _tree.DoubleClick += _ => EditHandler();
        _enabled.Change += _ => Store(i => i.Enabled = _enabled.Checked);
        _checked.Change += _ => Store(i => i.Checked = _checked.Checked);

        RebuildTree(null);
        f.PerformLayout();
    }

    /// <summary>Opens the editor modally on <paramref name="menu"/>; the whole session is one undo step.</summary>
    /// <param name="handlers">Creates a handler stub in the code-behind (see <see cref="HandlerRequested"/>); null when there is no code side.</param>
    public static void Edit(DesignDocument doc, IMenuOwner menu, Form? owner, Action<IReadOnlyList<MenuItem>, bool>? handlers = null)
    {
        doc.Snapshot();
        var editor = new MenuEditor(doc, menu, owner) { HandlerRequested = handlers };
        editor.Form.ShowModal();
        doc.NotifyChanged();
        editor.FlushHandlers();
    }

    public MenuItem? SelectedItem => _tree.Selected?.Tag as MenuItem;

    /// <summary>The list that holds <paramref name="item"/> (the menu's top level or a parent's items).</summary>
    List<MenuItem> OwnerList(MenuItem item)
    {
        List<MenuItem>? Find(List<MenuItem> list)
        {
            if (list.Contains(item)) return list;
            foreach (var i in list) if (Find(i.Items) is { } l) return l;
            return null;
        }
        return Find(_menu.Items) ?? _menu.Items;
    }

    /// <summary>Adds an item after the selected one (or at the end of the top level).</summary>
    public MenuItem AddItem(string caption)
    {
        var item = new MenuItem { Caption = caption };
        if (SelectedItem is { } sel)
        {
            var list = OwnerList(sel);
            list.Insert(list.IndexOf(sel) + 1, item);
        }
        else _menu.Items.Add(item);
        Changed(item);
        return item;
    }

    /// <summary>Adds a separator (caption <c>-</c>, <c>"separator": true</c> in the form file) after the selected item.</summary>
    public MenuItem AddSeparator()
    {
        var item = new MenuItem { Caption = "-", Separator = true };
        if (SelectedItem is { } sel)
        {
            var list = OwnerList(sel);
            list.Insert(list.IndexOf(sel) + 1, item);
        }
        else _menu.Items.Add(item);
        Changed(item);
        return item;
    }

    public MenuItem AddSubItem(string caption)
    {
        if (SelectedItem is not { } sel) return AddItem(caption);
        var item = new MenuItem { Caption = caption };
        sel.Items.Add(item);
        Changed(item);
        return item;
    }

    public void DeleteSelected()
    {
        if (SelectedItem is not { } sel) return;
        var list = OwnerList(sel);
        int i = list.IndexOf(sel);
        list.Remove(sel);
        Changed(list.Count > 0 ? list[Math.Min(i, list.Count - 1)] : null);
    }

    public void MoveSelected(int delta)
    {
        if (SelectedItem is not { } sel) return;
        var list = OwnerList(sel);
        int i = list.IndexOf(sel), j = Math.Clamp(i + delta, 0, list.Count - 1);
        if (i == j) return;
        list.RemoveAt(i);
        list.Insert(j, sel);
        Changed(sel);
    }

    public void Select(MenuItem item)
    {
        foreach (var n in AllNodes(_tree.Nodes))
            if (n.Tag == item) { _tree.Selected = n; return; }
    }

    static IEnumerable<TreeNode> AllNodes(IEnumerable<TreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in AllNodes(n.Nodes)) yield return c;
        }
    }

    void Changed(MenuItem? select)
    {
        RebuildTree(select);
        _doc.NotifyChanged();
    }

    void RebuildTree(MenuItem? select)
    {
        _tree.Nodes.Clear();
        void Add(IList<TreeNode> nodes, List<MenuItem> items, TreeNode? parent)
        {
            foreach (var item in items)
            {
                var node = parent == null ? new TreeNode(Label(item)) : parent.Add(Label(item));
                node.Tag = item;
                node.Expanded = true;
                if (parent == null) nodes.Add(node);
                Add(node.Nodes, item.Items, node);
            }
        }
        Add(_tree.Nodes, _menu.Items, null);
        _tree.Selected = null;
        if (select != null) Select(select);
        else if (_tree.Nodes.Count > 0) _tree.Selected = _tree.Nodes[0];
        LoadFields();
        _tree.Invalidate();
    }

    static string Label(MenuItem i) => i.IsSeparator ? "────" : i.Caption + (i.Shortcut.Length > 0 ? "   " + i.Shortcut : "") + (i.AltShortcut.Length > 0 ? "   (" + i.AltShortcut + ")" : "");

    void LoadFields()
    {
        _loading = true;
        var i = SelectedItem;
        _caption.Text = i?.Caption ?? "";
        _name.Text = i?.Name ?? "";
        _shortcut.Text = i?.Shortcut ?? "";
        _altShortcut.Text = i?.AltShortcut ?? "";
        _handler.Text = i?.ClickHandler ?? "";
        _enabled.Checked = i?.Enabled ?? true;
        _checked.Checked = i?.Checked ?? false;
        _loading = false;
    }

    void Store(Action<MenuItem> set)
    {
        if (_loading || SelectedItem is not { } item) return;
        set(item);
        if (_tree.Selected is { } node) node.Text = Label(item);
        _tree.Invalidate();
        _doc.NotifyChanged();
    }
}

/// <summary>
/// The <c>Columns</c> editor of a grid or a <see cref="ListView"/> (opened from the Object Inspector's <c>columns</c>
/// row), modelled on the <see cref="MenuEditor"/>: a list of the columns and fields for the selected one. Changes apply
/// to the control immediately; the whole session is one undo step.
/// </summary>
public sealed class GridColumnsEditor
{
    readonly DesignDocument _doc;
    readonly CustomGrid? _grid;        // null for a list view
    readonly GridColumns _columns;
    readonly ListBox _list;
    readonly Edit _title;
    readonly SpinEdit _width, _minWidth;
    readonly ComboBox _alignment;
    readonly Label _hint;
    bool _loading;

    public Form Form { get; }

    public GridColumnsEditor(DesignDocument doc, CustomGrid grid, Form? owner = null) : this(doc, grid, grid.Columns, grid, owner) { }

    /// <summary>The editor for a list view's columns.</summary>
    public GridColumnsEditor(DesignDocument doc, ListView view, Form? owner = null) : this(doc, view, view.Columns, null, owner) { }

    GridColumnsEditor(DesignDocument doc, Control control, GridColumns columns, CustomGrid? grid, Form? owner)
    {
        _doc = doc;
        _grid = grid;
        _columns = columns;
        Form = EditorLayout.NewForm($"Columns: {control.Name}", 520, 380, owner);
        var f = Form;
        _list = EditorLayout.Put(f, new ListBox { Name = "lstColumns" }, 12, 12, 220, 300);
        EditorLayout.Put(f, new Label { Caption = "Title" }, 248, 14, 80, 22);
        _title = EditorLayout.Put(f, new Edit { Name = "edtTitle" }, 332, 12, 172, 26);
        EditorLayout.Put(f, new Label { Caption = "Width" }, 248, 48, 80, 22);
        _width = EditorLayout.Put(f, new SpinEdit { Name = "spnWidth", MinValue = 0, MaxValue = 4000 }, 332, 46, 172, 26);
        EditorLayout.Put(f, new Label { Caption = "Alignment" }, 248, 82, 80, 22);
        _alignment = EditorLayout.Put(f, new ComboBox { Name = "cboAlignment", Items = [.. Enum.GetNames<TextAlign>().Select(n => char.ToLowerInvariant(n[0]) + n[1..])] }, 332, 80, 172, 26);
        EditorLayout.Put(f, new Label { Caption = "MinWidth" }, 248, 116, 80, 22);
        _minWidth = EditorLayout.Put(f, new SpinEdit { Name = "spnMinWidth", MinValue = 0, MaxValue = 4000 }, 332, 114, 172, 26);
        _hint = EditorLayout.Put(f, new Label { Name = "lblHint", Caption = "" }, 248, 150, 256, 60);

        Button B(string name, string caption, int x, int y, Action action)
        {
            var b = EditorLayout.Put(f, new Button { Name = name, Caption = caption }, x, y, 76, 28);
            b.Click += _ => action();
            return b;
        }
        B("btnAdd", "Add", 12, 322, () => AddColumn("Column"));
        B("btnDelete", "Delete", 94, 322, DeleteSelected);
        B("btnUp", "Up", 176, 322, () => MoveSelected(-1));
        B("btnDown", "Down", 258, 322, () => MoveSelected(1));
        var close = B("btnClose", "Close", 428, 336, () => { });
        close.ModalResult = ModalResult.Ok;
        close.Default = true;

        _list.Change += _ => LoadFields();
        _title.Change += _ => Store(c => c.Title = _title.Text);
        _width.ValueChanged += _ => Store(c => c.Width = _width.Value);
        _minWidth.ValueChanged += _ => Store(c => c.MinWidth = _minWidth.Value);
        _alignment.Change += _ => Store(c => { if (_alignment.ItemIndex >= 0) c.Alignment = (TextAlign)_alignment.ItemIndex; });

        Rebuild(0);
        f.PerformLayout();
    }

    /// <summary>Opens the editor modally on <paramref name="grid"/>; the whole session is one undo step.</summary>
    public static void Edit(DesignDocument doc, CustomGrid grid, Form? owner) => Run(doc, new GridColumnsEditor(doc, grid, owner));

    /// <summary>Opens the editor modally on a list view's columns; the whole session is one undo step.</summary>
    public static void Edit(DesignDocument doc, ListView view, Form? owner) => Run(doc, new GridColumnsEditor(doc, view, owner));

    static void Run(DesignDocument doc, GridColumnsEditor editor)
    {
        doc.Snapshot();
        editor.Form.ShowModal();
        doc.NotifyChanged();
    }

    public IReadOnlyList<GridColumn> Columns => _columns;

    public GridColumn? SelectedColumn => _list.ItemIndex >= 0 && _list.ItemIndex < _columns.Count ? _columns[_list.ItemIndex] : null;

    public int SelectedIndex => SelectedColumn == null ? -1 : _list.ItemIndex;

    public void Select(int index) => _list.ItemIndex = index;

    /// <summary>
    /// Adds a column after the selected one (or at the end). A grid that has no <c>Columns</c> yet first gets one blank
    /// column per existing grid column, so adding doesn't shrink it.
    /// </summary>
    public GridColumn AddColumn(string title)
    {
        if (_grid != null && _columns.Count == 0)
            for (int i = 0, n = _grid.ColCount; i < n; i++) _columns.Add("");   // (the first Add changes ColCount)
        var column = new GridColumn(title);
        int at = SelectedColumn == null ? _columns.Count : _list.ItemIndex + 1;
        _columns.Insert(at, column);
        Changed(at);
        return column;
    }

    public void DeleteSelected()
    {
        if (SelectedColumn == null) return;
        int i = _list.ItemIndex;
        _columns.RemoveAt(i);
        Changed(Math.Min(i, _columns.Count - 1));
    }

    public void MoveSelected(int delta)
    {
        if (SelectedColumn == null) return;
        int i = _list.ItemIndex, j = Math.Clamp(i + delta, 0, _columns.Count - 1);
        if (i == j) return;
        _columns.Move(i, j);
        Changed(j);
    }

    void Changed(int select)
    {
        Rebuild(select);
        _doc.NotifyChanged();
    }

    static string Label(int i, GridColumn c) => c.Title.Length > 0 ? $"{i}  {c.Title}" : $"{i}  (untitled)";

    void Rebuild(int select)
    {
        _list.Items = [.. _columns.Select((c, i) => Label(i, c))];
        _list.ItemIndex = Math.Min(select, _columns.Count - 1);
        _hint.Caption = _columns.Count > 0 ? ""
            : _grid != null ? $"No columns are defined: the grid has {_grid.ColCount} untitled columns. Add creates them, then the new one."
            : "No columns are defined: the list shows one untitled column.";
        LoadFields();
        _list.Invalidate();
    }

    void LoadFields()
    {
        _loading = true;
        var c = SelectedColumn;
        _title.Text = c?.Title ?? "";
        _width.Value = c?.Width ?? 0;
        _minWidth.Value = c?.MinWidth ?? 0;
        _alignment.ItemIndex = c == null ? -1 : (int)c.Alignment;
        _loading = false;
    }

    void Store(Action<GridColumn> set)
    {
        if (_loading || SelectedColumn is not { } column) return;
        set(column);
        int i = _list.ItemIndex;
        var items = _list.Items;
        if (i < items.Length) { items[i] = Label(i, column); _list.Items = items; _list.ItemIndex = i; }
        _doc.NotifyChanged();
    }
}

/// <summary>
/// The TreeView <c>Nodes</c> editor (Delphi's Items editor), opened from the Object Inspector's <c>nodes</c> row and
/// modelled on the <see cref="MenuEditor"/>: a tree of the nodes and fields for the selected one, with buttons to add
/// siblings and children, delete, move and indent. Changes apply to the tree immediately; the whole session is one
/// undo step.
/// </summary>
public sealed class TreeNodesEditor
{
    readonly DesignDocument _doc;
    readonly TreeView _target;
    readonly TreeView _tree;
    readonly Edit _text;
    readonly SpinEdit _imageIndex;
    readonly CheckBox _expanded;
    bool _loading;

    public Form Form { get; }

    public TreeNodesEditor(DesignDocument doc, TreeView tree, Form? owner = null)
    {
        _doc = doc;
        _target = tree;
        Form = EditorLayout.NewForm($"Nodes: {tree.Name}", 560, 400, owner);
        var f = Form;
        _tree = EditorLayout.Put(f, new TreeView { Name = "tvNodes" }, 12, 12, 260, 330);
        EditorLayout.Put(f, new Label { Caption = "Text" }, 288, 14, 88, 22);
        _text = EditorLayout.Put(f, new Edit { Name = "edtText" }, 380, 12, 164, 26);
        EditorLayout.Put(f, new Label { Caption = "ImageIndex" }, 288, 48, 88, 22);
        _imageIndex = EditorLayout.Put(f, new SpinEdit { Name = "spnImageIndex", MinValue = -1, MaxValue = 9999, Value = -1 }, 380, 46, 164, 26);
        _expanded = EditorLayout.Put(f, new CheckBox { Name = "chkExpanded", Caption = "Expanded" }, 380, 82, 164, 22);

        Button B(string name, string caption, int x, int y, Action action)
        {
            var b = EditorLayout.Put(f, new Button { Name = name, Caption = caption }, x, y, 84, 28);
            b.Click += _ => action();
            return b;
        }
        B("btnAdd", "Add", 288, 124, () => AddSibling("New node"));
        B("btnAddChild", "Add child", 378, 124, () => AddChild("New node"));
        B("btnDelete", "Delete", 468, 124, DeleteSelected);
        B("btnUp", "Up", 288, 160, () => MoveSelected(-1));
        B("btnDown", "Down", 378, 160, () => MoveSelected(1));
        B("btnIndent", "Indent", 288, 196, Indent);
        B("btnOutdent", "Outdent", 378, 196, Outdent);
        var close = B("btnClose", "Close", 460, 356, () => { });
        close.ModalResult = ModalResult.Ok;
        close.Default = true;

        _tree.Change += _ => LoadFields();
        _text.Change += _ => Store(n => n.Text = _text.Text, relabel: true);
        _imageIndex.ValueChanged += _ => Store(n => n.ImageIndex = _imageIndex.Value);
        _expanded.Change += _ => Store(n => n.Expanded = _expanded.Checked);

        Rebuild(null);
        f.PerformLayout();
    }

    /// <summary>Opens the editor modally on <paramref name="tree"/>'s nodes; the whole session is one undo step.</summary>
    public static void Edit(DesignDocument doc, TreeView tree, Form? owner)
    {
        doc.Snapshot();
        new TreeNodesEditor(doc, tree, owner).Form.ShowModal();
        doc.NotifyChanged();
    }

    /// <summary>The edited tree's node selected in the editor, or null.</summary>
    public TreeNode? SelectedNode => _tree.Selected?.Tag as TreeNode;

    TreeNodes ListOf(TreeNode node) => node.Parent?.Nodes ?? _target.Nodes;

    /// <summary>Adds a node after the selected one (or at the end of the top level) and selects it.</summary>
    public TreeNode AddSibling(string text)
    {
        var node = new TreeNode(text);
        if (SelectedNode is { } sel)
        {
            var list = ListOf(sel);
            list.Insert(list.IndexOf(sel) + 1, node);
        }
        else _target.Nodes.Add(node);
        Changed(node);
        return node;
    }

    /// <summary>Adds a node as the selected node's last child (or at the top level when nothing is selected).</summary>
    public TreeNode AddChild(string text)
    {
        if (SelectedNode is not { } sel) return AddSibling(text);
        var node = sel.Add(text);
        Changed(node);
        return node;
    }

    /// <summary>Deletes the selected node and its children; the next sibling (or the previous, or the parent) is selected.</summary>
    public void DeleteSelected()
    {
        if (SelectedNode is not { } sel) return;
        var list = ListOf(sel);
        int i = list.IndexOf(sel);
        var parent = sel.Parent;
        list.RemoveAt(i);
        Changed(list.Count > 0 ? list[Math.Min(i, list.Count - 1)] : parent);
    }

    /// <summary>Moves the selected node among its siblings.</summary>
    public void MoveSelected(int delta)
    {
        if (SelectedNode is not { } sel) return;
        var list = ListOf(sel);
        int i = list.IndexOf(sel), j = Math.Clamp(i + delta, 0, list.Count - 1);
        if (i == j) return;
        list.Move(i, j);
        Changed(sel);
    }

    /// <summary>Makes the selected node the last child of the sibling above it.</summary>
    public void Indent()
    {
        if (SelectedNode is not { } sel) return;
        var list = ListOf(sel);
        int i = list.IndexOf(sel);
        if (i <= 0) return;
        list[i - 1].Nodes.Add(sel);   // moves it
        Changed(sel);
    }

    /// <summary>Moves the selected node out of its parent, to just after it.</summary>
    public void Outdent()
    {
        if (SelectedNode is not { } sel || sel.Parent is not { } parent) return;
        var outer = ListOf(parent);
        outer.Insert(outer.IndexOf(parent) + 1, sel);   // moves it
        Changed(sel);
    }

    public void Select(TreeNode node)
    {
        foreach (var n in AllNodes(_tree.Nodes))
            if (n.Tag == node) { _tree.Selected = n; return; }
    }

    static IEnumerable<TreeNode> AllNodes(IEnumerable<TreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in AllNodes(n.Nodes)) yield return c;
        }
    }

    void Changed(TreeNode? select)
    {
        Rebuild(select);
        _doc.NotifyChanged();
    }

    static string Label(TreeNode n) => n.Text.Length > 0 ? n.Text : "(no text)";

    /// <summary>Mirrors the edited tree's nodes (all expanded) in the editor's own tree, each tagged with its node.</summary>
    void Rebuild(TreeNode? select)
    {
        _tree.Nodes.Clear();
        void Add(TreeNodes into, TreeNodes from)
        {
            foreach (var node in from)
            {
                var mirror = new TreeNode(Label(node)) { Tag = node, Expanded = true };
                Add(mirror.Nodes, node.Nodes);
                into.Add(mirror);
            }
        }
        Add(_tree.Nodes, _target.Nodes);
        _tree.Selected = null;
        if (select != null) Select(select);
        else if (_tree.Nodes.Count > 0) _tree.Selected = _tree.Nodes[0];
        LoadFields();
    }

    void LoadFields()
    {
        _loading = true;
        var n = SelectedNode;
        _text.Text = n?.Text ?? "";
        _imageIndex.Value = n?.ImageIndex ?? -1;
        _expanded.Checked = n?.Expanded ?? false;
        _loading = false;
    }

    void Store(Action<TreeNode> set, bool relabel = false)
    {
        if (_loading || SelectedNode is not { } node) return;
        set(node);
        if (relabel && _tree.Selected is { } mirror) mirror.Text = Label(node);
        _doc.NotifyChanged();
    }
}

/// <summary>The tab-order editor: lists a container's tab stops in order; Up/Down reorder them.</summary>
public sealed class TabOrderEditor
{
    readonly List<Control> _order;
    readonly ListBox _list;

    public Form Form { get; }

    public TabOrderEditor(Control container, Form? owner = null)
    {
        _order = [.. container.Children
            .Select((c, i) => (c, i))
            .Where(t => t.c.TabStop)
            .OrderBy(t => t.c.TabOrder < 0 ? 1 : 0).ThenBy(t => t.c.TabOrder < 0 ? t.i : t.c.TabOrder).ThenBy(t => t.i)
            .Select(t => t.c)];
        Form = EditorLayout.NewForm($"Tab order: {container.Name}", 360, 320, owner);
        _list = EditorLayout.Put(Form, new ListBox { Name = "lstOrder" }, 12, 12, 230, 290);
        var up = EditorLayout.Put(Form, new Button { Name = "btnUp", Caption = "Up" }, 256, 12, 90, 28);
        var down = EditorLayout.Put(Form, new Button { Name = "btnDown", Caption = "Down" }, 256, 48, 90, 28);
        var ok = EditorLayout.Put(Form, new Button { Name = "btnOk", Caption = "OK", ModalResult = ModalResult.Ok, Default = true }, 256, 238, 90, 28);
        EditorLayout.Put(Form, new Button { Name = "btnCancel", Caption = "Cancel", ModalResult = ModalResult.Cancel, Cancel = true }, 256, 274, 90, 28);
        up.Click += _ => Move(-1);
        down.Click += _ => Move(1);
        _ = ok;
        Refresh(0);
        Form.PerformLayout();
    }

    public IReadOnlyList<Control> Order => _order;

    public void Move(int delta)
    {
        int i = _list.ItemIndex, j = Math.Clamp(i + delta, 0, _order.Count - 1);
        if (i < 0 || i == j) return;
        var c = _order[i];
        _order.RemoveAt(i);
        _order.Insert(j, c);
        Refresh(j);
    }

    public void SelectIndex(int i) => _list.ItemIndex = i;

    void Refresh(int select)
    {
        _list.Items = [.. _order.Select(c => $"{c.Name}  ({ComponentRegistry.Of(c).Name})")];
        _list.ItemIndex = Math.Min(select, _order.Count - 1);
    }

    /// <summary>Writes the order as explicit tabOrder values.</summary>
    public void Apply()
    {
        for (int i = 0; i < _order.Count; i++) _order[i].TabOrder = i;
    }

    /// <summary>Opens the editor modally for <paramref name="container"/>; applies the order on OK.</summary>
    public static void Edit(DesignDocument doc, Control container, Form? owner)
    {
        var editor = new TabOrderEditor(container, owner);
        if (editor.Form.ShowModal() != ModalResult.Ok) return;
        doc.Snapshot();
        editor.Apply();
        doc.NotifyChanged();
    }
}
