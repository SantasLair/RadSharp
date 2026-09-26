using RadSharp.Drawing;

namespace RadSharp;

/// <summary>A row of a <see cref="ListView"/>: a caption and one text per further column.</summary>
public sealed class ListViewItem(string caption, params string[] subItems)
{
    public string Caption { get; set; } = caption;
    public List<string> SubItems { get; } = [.. subItems];
    public object? Tag { get; set; }

    public string Text(int column) => column == 0 ? Caption : column - 1 < SubItems.Count ? SubItems[column - 1] : "";
}

public delegate void ColumnClickEvent(Control sender, int column);

/// <summary>
/// A multi-column list with a header (Delphi's TListView in report style). Columns are published as
/// <c>"Caption:width"</c> strings; rows are set from code.
/// </summary>
public class ListView : Control
{
    string[] _columns = [];
    int[] _widths = [];
    int _itemIndex = -1, _top;
    int _resizing = -1, _resizeStartX, _resizeStartWidth;

    public ListView()
    {
        SetDefaultSize(300, 180);
        TabStop = true;
    }

    /// <summary>Column headers as <c>"Caption:width"</c> (width at 100 %; default 100).</summary>
    [Published(Category = "Appearance")]
    public string[] Columns
    {
        get => _columns;
        set
        {
            _columns = value ?? [];
            _widths = [.. _columns.Select(c => c.LastIndexOf(':') is var i and >= 0 && int.TryParse(c[(i + 1)..], out var w) ? w : 100)];
            Invalidate();
        }
    }

    public string ColumnCaption(int i) => _columns[i].LastIndexOf(':') is var c and >= 0 ? _columns[i][..c] : _columns[i];
    /// <summary>A column's current width at 100 % (the user can drag header borders).</summary>
    public int ColumnWidth(int i) => _widths[i];

    public List<ListViewItem> Items { get; } = [];

    public int ItemIndex
    {
        get => _itemIndex;
        set
        {
            value = Math.Clamp(value, -1, Items.Count - 1);
            if (value == _itemIndex) return;
            _itemIndex = value;
            ScrollIntoView(value);
            Invalidate();
            Change?.Invoke(this);
        }
    }

    public ListViewItem? Selected => _itemIndex >= 0 && _itemIndex < Items.Count ? Items[_itemIndex] : null;

    [Published] public event NotifyEvent? Change;
    [Published("onDblClick")] public event NotifyEvent? DoubleClick;
    [Published] public event ColumnClickEvent? ColumnClick;

    /// <summary>Call after changing <see cref="Items"/> from code.</summary>
    public void ItemsChanged()
    {
        if (_itemIndex >= Items.Count) _itemIndex = Items.Count - 1;
        Invalidate();
    }

    int HeaderHeight => Px(26);
    int RowHeight => Px(24);
    Rect RowsArea => new(Px(1), Px(1) + HeaderHeight, Math.Max(0, Width - Px(2)), Math.Max(0, Height - Px(2) - HeaderHeight));

    void ScrollIntoView(int i)
    {
        if (i < 0) return;
        int y = i * RowHeight, h = RowsArea.Height;
        if (y < _top) _top = y;
        else if (y + RowHeight > _top + h) _top = y + RowHeight - h;
    }

    int ColumnX(int i)
    {
        int x = Px(1);
        for (int c = 0; c < i; c++) x += Px(_widths[c]);
        return x;
    }

    /// <summary>The column whose right border is near <paramref name="x"/>, for resizing.</summary>
    int BorderAt(int x)
    {
        for (int c = 0; c < _columns.Length; c++)
            if (Math.Abs(ColumnX(c + 1) - x) <= Px(4)) return c;
        return -1;
    }

    public int IndexAt(int y)
    {
        var a = RowsArea;
        if (y < a.Y || y >= a.Y + a.Height) return -1;
        int i = (y - a.Y + _top) / RowHeight;
        return i < Items.Count ? i : -1;
    }

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButton.Left) return;
        if (e.Y < Px(1) + HeaderHeight)
        {
            int b = BorderAt(e.X);
            if (b >= 0) { _resizing = b; _resizeStartX = e.X; _resizeStartWidth = _widths[b]; return; }
            for (int c = 0; c < _columns.Length; c++)
                if (e.X >= ColumnX(c) && e.X < ColumnX(c + 1)) { ColumnClick?.Invoke(this, c); return; }
            return;
        }
        int i = IndexAt(e.Y);
        if (i < 0) return;
        ItemIndex = i;
        if (e.Clicks == 2) DoubleClick?.Invoke(this);
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_resizing >= 0)
        {
            _widths[_resizing] = Math.Max(20, _resizeStartWidth + (int)Math.Round((e.X - _resizeStartX) / UiScale));
            Invalidate();
            return;
        }
        Cursor = e.Y < Px(1) + HeaderHeight && BorderAt(e.X) >= 0 ? Cursor.SizeWE : Cursor.Default;
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _resizing = -1;
    }

    protected internal override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled) return;
        _top = Math.Clamp(_top - (int)Math.Round(e.DeltaY * RowHeight * 3), 0, Math.Max(0, Items.Count * RowHeight - RowsArea.Height));
        Invalidate();
        e.Handled = true;
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Modifiers != KeyModifiers.None || Items.Count == 0) return;
        int? t = e.Key switch
        {
            Key.Up => Math.Max(0, _itemIndex - 1),
            Key.Down => Math.Min(Items.Count - 1, _itemIndex + 1),
            Key.Home => 0,
            Key.End => Items.Count - 1,
            _ => null,
        };
        if (t is { } v) { ItemIndex = v; e.Handled = true; }
        else if (e.Key == Key.Enter && _itemIndex >= 0) { DoubleClick?.Invoke(this); e.Handled = true; }
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var r = new Rect(0, 0, Width, Height);
        canvas.FillRect(r, t.InputBackground);
        var font = canvas.DefaultFont;
        var header = new Rect(canvas.Px(1), canvas.Px(1), Width - canvas.Px(2), HeaderHeight);
        canvas.FillRect(header, t.PanelBackground);
        canvas.FillRect(new Rect(header.X, header.Y + header.Height - canvas.Px(1), header.Width, canvas.Px(1)), t.PanelBorder);
        for (int c = 0; c < _columns.Length; c++)
        {
            int x0 = ColumnX(c), x1 = ColumnX(c + 1);
            canvas.DrawText(ColumnCaption(c), canvas.DefaultBoldFont, t.Text, new Rect(x0 + canvas.Px(8), header.Y, x1 - x0 - canvas.Px(12), header.Height), TextAlign.Left, VerticalAlign.Center);
            canvas.FillRect(new Rect(x1 - canvas.Px(1), header.Y + canvas.Px(4), canvas.Px(1), header.Height - canvas.Px(8)), t.PanelBorder);
        }
        var area = RowsArea;
        canvas.Save();
        canvas.ClipRect(area);
        for (int i = _top / RowHeight; i < Items.Count; i++)
        {
            int y = area.Y + i * RowHeight - _top;
            if (y >= area.Y + area.Height) break;
            var row = new Rect(area.X, y, area.Width, RowHeight);
            if (i == _itemIndex) canvas.FillRect(row, Focused ? t.Selection : t.SelectionInactive);
            for (int c = 0; c < Math.Max(1, _columns.Length); c++)
            {
                int x0 = _columns.Length == 0 ? area.X : ColumnX(c), x1 = _columns.Length == 0 ? area.X + area.Width : ColumnX(c + 1);
                canvas.Save();
                canvas.ClipRect(new Rect(x0, y, x1 - x0, RowHeight));
                canvas.DrawText(Items[i].Text(c), font, t.Text, new Rect(x0 + canvas.Px(8), y, x1 - x0 - canvas.Px(12), RowHeight), TextAlign.Left, VerticalAlign.Center);
                canvas.Restore();
            }
        }
        canvas.Restore();
        canvas.DrawBorder(r, Focused ? t.Accent : t.InputBorder, canvas.Px(1));
    }
}

/// <summary>A node of a <see cref="TreeView"/>.</summary>
public sealed class TreeNode(string text)
{
    public string Text { get; set; } = text;
    public List<TreeNode> Nodes { get; } = [];
    public bool Expanded { get; set; }
    public object? Tag { get; set; }
    public TreeNode? Parent { get; private set; }

    public TreeNode Add(string text)
    {
        var node = new TreeNode(text) { Parent = this };
        Nodes.Add(node);
        return node;
    }

    public int Level => Parent == null ? 0 : Parent.Level + 1;
}

/// <summary>A hierarchical list with expandable nodes (Delphi's TTreeView). Nodes are set from code.</summary>
public class TreeView : Control
{
    TreeNode? _selected;
    int _top;

    public TreeView()
    {
        SetDefaultSize(200, 200);
        TabStop = true;
    }

    public List<TreeNode> Nodes { get; } = [];

    public TreeNode Add(string text)
    {
        var node = new TreeNode(text);
        Nodes.Add(node);
        Invalidate();
        return node;
    }

    public TreeNode? Selected
    {
        get => _selected;
        set
        {
            if (value == _selected) return;
            _selected = value;
            for (var p = value?.Parent; p != null; p = p.Parent) p.Expanded = true;
            ScrollIntoView();
            Invalidate();
            Change?.Invoke(this);
        }
    }

    [Published] public event NotifyEvent? Change;
    [Published("onDblClick")] public event NotifyEvent? DoubleClick;

    /// <summary>The rows currently visible (expanded branches), top to bottom.</summary>
    public List<TreeNode> VisibleNodes()
    {
        var list = new List<TreeNode>();
        void Walk(IEnumerable<TreeNode> nodes)
        {
            foreach (var n in nodes)
            {
                list.Add(n);
                if (n.Expanded) Walk(n.Nodes);
            }
        }
        Walk(Nodes);
        return list;
    }

    int RowHeight => Px(24);
    int Indent => Px(18);
    Rect Area => new(Px(1), Px(1), Math.Max(0, Width - Px(2)), Math.Max(0, Height - Px(2)));

    void ScrollIntoView()
    {
        if (_selected == null) return;
        int i = VisibleNodes().IndexOf(_selected);
        if (i < 0) return;
        int y = i * RowHeight, h = Area.Height;
        if (y < _top) _top = y;
        else if (y + RowHeight > _top + h) _top = y + RowHeight - h;
    }

    public void Toggle(TreeNode node)
    {
        if (node.Nodes.Count == 0) return;
        node.Expanded = !node.Expanded;
        if (!node.Expanded && _selected != null)
            for (var p = _selected.Parent; p != null; p = p.Parent)
                if (p == node) { Selected = node; break; }
        Invalidate();
    }

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButton.Left) return;
        var rows = VisibleNodes();
        int i = (e.Y - Area.Y + _top) / RowHeight;
        if (e.Y < Area.Y || i < 0 || i >= rows.Count) return;
        var node = rows[i];
        int expanderX = Area.X + node.Level * Indent;
        if (e.X >= expanderX && e.X < expanderX + Indent) { Toggle(node); return; }
        Selected = node;
        if (e.Clicks == 2) { Toggle(node); DoubleClick?.Invoke(this); }
    }

    protected internal override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled) return;
        _top = Math.Clamp(_top - (int)Math.Round(e.DeltaY * RowHeight * 3), 0, Math.Max(0, VisibleNodes().Count * RowHeight - Area.Height));
        Invalidate();
        e.Handled = true;
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Modifiers != KeyModifiers.None) return;
        var rows = VisibleNodes();
        if (rows.Count == 0) return;
        int i = _selected == null ? -1 : rows.IndexOf(_selected);
        switch (e.Key)
        {
            case Key.Up: Selected = rows[Math.Max(0, i - 1)]; break;
            case Key.Down: Selected = rows[Math.Min(rows.Count - 1, i + 1)]; break;
            case Key.Home: Selected = rows[0]; break;
            case Key.End: Selected = rows[^1]; break;
            case Key.Right when _selected != null:
                if (!_selected.Expanded && _selected.Nodes.Count > 0) Toggle(_selected);
                else if (_selected.Nodes.Count > 0) Selected = _selected.Nodes[0];
                break;
            case Key.Left when _selected != null:
                if (_selected.Expanded) Toggle(_selected);
                else if (_selected.Parent != null) Selected = _selected.Parent;
                break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var r = new Rect(0, 0, Width, Height);
        canvas.FillRect(r, t.InputBackground);
        var area = Area;
        var rows = VisibleNodes();
        canvas.Save();
        canvas.ClipRect(area);
        for (int i = _top / RowHeight; i < rows.Count; i++)
        {
            int y = area.Y + i * RowHeight - _top;
            if (y >= area.Y + area.Height) break;
            var node = rows[i];
            int x = area.X + node.Level * Indent;
            if (node == _selected) canvas.FillRect(new Rect(area.X, y, area.Width, RowHeight), Focused ? t.Selection : t.SelectionInactive);
            if (node.Nodes.Count > 0)
                canvas.DrawText(node.Expanded ? "▾" : "▸", canvas.DefaultFont, t.TextDisabled, new Rect(x, y, Indent, RowHeight), TextAlign.Center, VerticalAlign.Center);
            canvas.DrawText(node.Text, canvas.DefaultFont, t.Text, new Rect(x + Indent, y, area.Width - x - Indent, RowHeight), TextAlign.Left, VerticalAlign.Center);
        }
        canvas.Restore();
        canvas.DrawBorder(r, Focused ? t.Accent : t.InputBorder, canvas.Px(1));
    }
}
