using RadSharp.Drawing;
using RadSharp.Ide.Debug;

namespace RadSharp.Designer;

/// <summary>
/// The Breakpoints tool window: every breakpoint of the project with an enabled check box, file:line, condition and
/// hit count. Click the box (or press Space) to enable or disable one; double-click (or Enter) shows its line; Delete
/// removes it. The buttons delete the selected one, disable (or enable) them all, and edit the selected one's condition.
/// </summary>
public sealed class BreakpointsWindow : Control
{
    readonly BreakpointStore _store;
    readonly Button _delete = new() { Name = "btnDeleteBreakpoint", Caption = "Delete", Flat = true };
    readonly Button _toggleAll = new() { Name = "btnDisableAllBreakpoints", Caption = "Disable All", Flat = true };
    readonly Button _condition = new() { Name = "btnBreakpointCondition", Caption = "Condition…", Flat = true };
    readonly RowList _list = new() { Name = "lstBreakpoints", ItemHeight = 22, BorderStyle = BorderStyle.None };
    IReadOnlyList<SourceBreakpoint> _rows = [];
    bool _refreshPosted;

    public BreakpointsWindow(BreakpointStore store)
    {
        _store = store;
        Name = "pnlBreakpointsWindow";
        TabStop = false;
        foreach (var c in new Control[] { _delete, _toggleAll, _condition, _list }) Add(c);
        _delete.Click += _ => DeleteSelected();
        _toggleAll.Click += _ => ToggleAll();
        _condition.Click += _ => EditCondition(SelectedIndex);
        _list.DrawItem += DrawRow;
        _list.DoubleClick += _ => Activate(_list.ItemIndex);
        _list.EnterPressed += () => Activate(_list.ItemIndex);
        _list.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButton.Left && e.X < CheckWidth && _list.IndexAt(e.X, e.Y) is var i and >= 0) ToggleEnabled(i);
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.Modifiers != KeyModifiers.None) return;
            if (e.Key == Key.Space) { ToggleEnabled(_list.ItemIndex); e.Handled = true; }
            else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
        };
        _store.Changed += (_, _) => PostRefresh();
        Refresh();
    }

    /// <summary>Shows a breakpoint's line in the editor.</summary>
    public Action<SourceBreakpoint>? Navigate { get; set; }

    /// <summary>Shows a modal dialog (the condition editor); the designer passes itself as the owner.</summary>
    public Form? DialogOwner { get; set; }

    /// <summary>The project directory, to show paths relative to it.</summary>
    public Func<string?>? BaseDirectory { get; set; }

    /// <summary>Runs work on the UI thread (the store changes on session threads too).</summary>
    public Action<Action> Post { get; set; } = Application.Post;

    public IReadOnlyList<SourceBreakpoint> Rows => _rows;
    public ListBox List => _list;
    public int SelectedIndex { get => _list.ItemIndex; set => _list.ItemIndex = value; }

    int CheckWidth => Px(26);

    void PostRefresh()
    {
        if (_refreshPosted) return;
        _refreshPosted = true;
        Post(() => { _refreshPosted = false; Refresh(); });
    }

    /// <summary>Re-reads the store.</summary>
    public void Refresh()
    {
        var selected = SelectedIndex >= 0 && SelectedIndex < _rows.Count ? _rows[SelectedIndex].Id : Guid.Empty;
        _rows = _store.All;
        _list.Items = [.. _rows.Select(Describe)];
        int i = _rows.ToList().FindIndex(b => b.Id == selected);
        if (i >= 0) _list.ItemIndex = i;
        bool anyEnabled = _rows.Any(b => b.Enabled);
        _toggleAll.Caption = anyEnabled || _rows.Count == 0 ? "Disable All" : "Enable All";
        Invalidate();
    }

    string Place(SourceBreakpoint b)
    {
        var dir = BaseDirectory?.Invoke();
        var file = dir != null && b.File.StartsWith(dir, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(dir, b.File) : Path.GetFileName(b.File);
        return $"{file}:{b.Line}";
    }

    string Describe(SourceBreakpoint b) =>
        $"[{(b.Enabled ? "x" : " ")}] {Place(b)}{(b.Condition is { } c ? "  when " + c : "")}{(b.HitCondition is { } h ? "  hit " + h : "")}  hits: {b.TimesHit}";

    public void Activate(int index)
    {
        if (index >= 0 && index < _rows.Count) Navigate?.Invoke(_rows[index]);
    }

    public void ToggleEnabled(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        _store.SetEnabled(_rows[index].Id, !_rows[index].Enabled);
        Refresh();
    }

    public void DeleteSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= _rows.Count) return;
        int keep = SelectedIndex;
        _store.Remove(_rows[keep].Id);
        Refresh();
        if (_rows.Count > 0) _list.ItemIndex = Math.Min(keep, _rows.Count - 1);
    }

    /// <summary>Disables every breakpoint, or enables them all when none is enabled.</summary>
    public void ToggleAll()
    {
        bool enable = !_rows.Any(b => b.Enabled);
        foreach (var b in _store.All) _store.SetEnabled(b.Id, enable);
        Refresh();
    }

    /// <summary>Edits a breakpoint's condition and hit condition in a dialog.</summary>
    public void EditCondition(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        var b = _rows[index];
        var dialog = new BreakpointConditionDialog(Place(b), b.Condition, b.HitCondition, DialogOwner);
        if (dialog.ShowModal() != ModalResult.Ok) return;
        _store.SetConditions(b.Id, dialog.Condition, dialog.HitCondition);
        Refresh();
    }

    protected override void OnResize()
    {
        base.OnResize();
        int h = Px(26), x = Px(4);
        foreach (var (b, w) in new[] { (_delete, 70), (_toggleAll, 90), (_condition, 96) })
        {
            b.SetBounds(x, Px(2), Px(w), h);
            x += Px(w) + Px(4);
        }
        _list.SetBounds(0, h + Px(4), Width, Math.Max(0, Height - h - Px(4)));
    }

    void DrawRow(Control sender, Canvas canvas, int index, Rect row, bool selected)
    {
        if (index >= _rows.Count) return;
        var t = canvas.Theme;
        var b = _rows[index];
        if (selected) canvas.FillRect(row, _list.Focused ? t.Selection : t.SelectionInactive);
        int pad = canvas.Px(6), box = canvas.Px(13);
        var br = new Rect(row.X + pad, row.Y + (row.Height - box) / 2, box, box);
        canvas.FillRect(br, t.InputBackground);
        canvas.DrawBorder(br, t.InputBorder, canvas.Px(1));
        if (b.Enabled)
        {
            canvas.DrawLine(new PointF(br.X + box * 0.2f, br.Y + box * 0.5f), new PointF(br.X + box * 0.42f, br.Y + box * 0.75f), t.Text, canvas.Px(2));
            canvas.DrawLine(new PointF(br.X + box * 0.42f, br.Y + box * 0.75f), new PointF(br.X + box * 0.82f, br.Y + box * 0.25f), t.Text, canvas.Px(2));
        }
        int dot = canvas.Px(10);
        var dr = new Rect(br.X + box + pad, row.Y + (row.Height - dot) / 2, dot, dot);
        var red = Color.FromHex("#E5484D");
        if (!b.Enabled) canvas.DrawEllipseBorder(dr, red, canvas.Px(1));
        else canvas.FillEllipse(dr, b.Verified == false ? red with { A = 96 } : red);
        var font = _list.EffectiveFont;
        int x = dr.X + dot + pad, right = row.X + row.Width - pad;
        int hitsW = canvas.Px(70), placeW = canvas.Px(200);
        canvas.DrawText($"{b.TimesHit} hit{(b.TimesHit == 1 ? "" : "s")}", font, t.TextDisabled, new Rect(right - hitsW, row.Y, hitsW, row.Height), TextAlign.Right, VerticalAlign.Center);
        canvas.DrawText(Place(b), font, t.Text, new Rect(x, row.Y, placeW, row.Height), TextAlign.Left, VerticalAlign.Center, ellipsis: true);
        var cond = string.Join("  ", new[] { b.Condition is { } c ? "when " + c : null, b.HitCondition is { } h ? "hit " + h : null, b.Verified == false ? "(not bound)" : null }.Where(s => s != null));
        canvas.DrawText(cond, font, t.TextDisabled, new Rect(x + placeW, row.Y, Math.Max(0, right - hitsW - x - placeW - pad), row.Height), TextAlign.Left, VerticalAlign.Center, ellipsis: true);
    }
}

/// <summary>Edits a breakpoint's condition (a C# expression) and hit condition ("5", "&gt;=3", "%2").</summary>
public sealed class BreakpointConditionDialog : Form
{
    readonly Edit _condition, _hit;

    public BreakpointConditionDialog(string place, string? condition, string? hitCondition, Form? owner)
    {
        DialogLayout.Setup(this, "Breakpoint Condition", 460, 230, owner);
        DialogLayout.Put(this, new Label { Caption = $"Break at {place} only when:" }, 16, 14, 428, 24);
        _condition = DialogLayout.Put(this, new Edit { Name = "edtCondition", Text = condition ?? "" }, 16, 40, 428, 26);
        DialogLayout.Put(this, new Label { Caption = "Hit count (e.g. 5, >=3, %2; empty for every hit):" }, 16, 78, 428, 24);
        _hit = DialogLayout.Put(this, new Edit { Name = "edtHitCondition", Text = hitCondition ?? "" }, 16, 104, 428, 26);
        DialogLayout.Put(this, new Button { Name = "btnOk", Caption = "OK", Default = true, ModalResult = ModalResult.Ok }, 256, 180, 90, 30);
        DialogLayout.Put(this, new Button { Name = "btnCancel", Caption = "Cancel", Cancel = true, ModalResult = ModalResult.Cancel }, 354, 180, 90, 30);
        PerformLayout();
        Shown += _ => { _condition.Focus(); _condition.SelectAll(); };
    }

    public string Condition => _condition.Text.Trim();
    public string HitCondition => _hit.Text.Trim();
}
