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
    readonly Edit _caption, _name, _shortcut, _handler;
    readonly CheckBox _enabled, _checked;
    bool _loading;

    public Form Form { get; }

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
        EditorLayout.Put(f, new Label { Caption = "onClick" }, 268, 116, 90, 22);
        _handler = EditorLayout.Put(f, new Edit { Name = "edtHandler" }, 360, 114, 184, 26);
        _enabled = EditorLayout.Put(f, new CheckBox { Name = "chkEnabled", Caption = "Enabled" }, 360, 150, 184, 22);
        _checked = EditorLayout.Put(f, new CheckBox { Name = "chkChecked", Caption = "Checked" }, 360, 176, 184, 22);

        Button B(string name, string caption, int x, int y, Action action)
        {
            var b = EditorLayout.Put(f, new Button { Name = name, Caption = caption }, x, y, 88, 28);
            b.Click += _ => action();
            return b;
        }
        B("btnAdd", "Add", 268, 220, () => AddItem("New item"));
        B("btnAddSub", "Add sub", 360, 220, () => AddSubItem("New item"));
        B("btnDelete", "Delete", 452, 220, DeleteSelected);
        B("btnUp", "Up", 268, 256, () => MoveSelected(-1));
        B("btnDown", "Down", 360, 256, () => MoveSelected(1));
        var close = B("btnClose", "Close", 456, 356, () => { });
        close.ModalResult = ModalResult.Ok;
        close.Default = true;

        _tree.Change += _ => LoadFields();
        _caption.Change += _ => Store(i => i.Caption = _caption.Text);
        _name.Change += _ => Store(i => i.Name = _name.Text.Trim());
        _shortcut.Change += _ => Store(i => i.Shortcut = _shortcut.Text.Trim());
        _handler.Change += _ => Store(i => i.ClickHandler = _handler.Text.Trim());
        _enabled.Change += _ => Store(i => i.Enabled = _enabled.Checked);
        _checked.Change += _ => Store(i => i.Checked = _checked.Checked);

        RebuildTree(null);
        f.PerformLayout();
    }

    /// <summary>Opens the editor modally on <paramref name="menu"/>; the whole session is one undo step.</summary>
    public static void Edit(DesignDocument doc, IMenuOwner menu, Form? owner)
    {
        doc.Snapshot();
        new MenuEditor(doc, menu, owner).Form.ShowModal();
        doc.NotifyChanged();
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
        void Add(List<TreeNode> nodes, List<MenuItem> items, TreeNode? parent)
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

    static string Label(MenuItem i) => i.IsSeparator ? "────" : i.Caption + (i.Shortcut.Length > 0 ? "   " + i.Shortcut : "");

    void LoadFields()
    {
        _loading = true;
        var i = SelectedItem;
        _caption.Text = i?.Caption ?? "";
        _name.Text = i?.Name ?? "";
        _shortcut.Text = i?.Shortcut ?? "";
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
