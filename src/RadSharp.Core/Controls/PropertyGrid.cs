using RadSharp.Drawing;

namespace RadSharp;

public enum PropertyGridMode { Properties, Events }

/// <summary>
/// How <see cref="PropertyGrid"/> orders its rows. <see cref="Declared"/> (the default) is the framework's own
/// metadata order and is exactly the pre-existing behaviour — every caller that never sets
/// <see cref="PropertyGrid.Arrangement"/> keeps it, byte-for-byte. <see cref="Alphabetical"/> and
/// <see cref="Categorized"/> are the two states of the designer's own categorized/alphabetical toggle.
/// </summary>
public enum PropertyGridArrangement { Declared, Alphabetical, Categorized }

public delegate void PropertyChangedEvent(PropertyGrid sender, string propertyName);
public delegate void EventRowEvent(PropertyGrid sender, EventMeta ev);

/// <summary>
/// The Object Inspector: lists a control's published properties (or events) from the generated
/// metadata and edits them in place. Text, numbers, colours and structs are typed into an inline
/// editor; enums and booleans pick from a list; string lists open a multi-line editor.
/// </summary>
public class PropertyGrid : Control
{
    IReadOnlyList<Control> _targets = [];
    readonly List<(string Name, PropertyMeta? Prop, EventMeta? Event, bool IsHeader, Anchors? Flag)> _rows = [];
    readonly HashSet<string> _expandedAnchors = [];
    int _selected = -1, _top;
    PropertyGridMode _mode;
    PropertyGridArrangement _arrangement;
    string _filter = "";
    Control? _editor;
    bool _cancelEdit;
    readonly ScrollAxis _axis = new() { PageSize = 0, Max = 0 };
    readonly ScrollBarDriver _driver;
    bool _barDrag;

    public PropertyGrid()
    {
        _driver = new ScrollBarDriver(_axis);
        SetDefaultSize(260, 300);
        TabStop = true;
    }

    /// <summary>Width of the name column at 100 %.</summary>
    [Published(Category = "Appearance")] public int NameWidth { get; set; } = 120;

    [Published(Category = "Behavior")]
    public PropertyGridMode Mode { get => _mode; set { _mode = value; Rebuild(); } }

    /// <summary>Declaration order (the default, unchanged), alphabetical, or grouped by category.</summary>
    [Published(Category = "Behavior")]
    public PropertyGridArrangement Arrangement { get => _arrangement; set { _arrangement = value; Rebuild(); } }

    /// <summary>Only rows whose name contains this text (case-insensitive) are shown; empty shows every row.</summary>
    [Published(Category = "Behavior")]
    public string Filter
    {
        get => _filter;
        set { value = value.Trim(); if (_filter == value) return; _filter = value; Rebuild(); }
    }

    /// <summary>Raised after the user changed a value.</summary>
    [Published] public event NotifyEvent? Change;
    /// <summary>Raised just before the user's edit is applied (the designer records an undo step here).</summary>
    public event PropertyChangedEvent? PropertyChanging;
    public event PropertyChangedEvent? PropertyChanged;
    /// <summary>Raised when an event row is double-clicked (the designer creates the handler).</summary>
    public event EventRowEvent? EventDoubleClick;

    /// <summary>The control being inspected; null when there is none or more than one (see <see cref="Targets"/>).</summary>
    public Control? Target
    {
        get => _targets.Count == 1 ? _targets[0] : null;
        set => SetTargets(value == null ? [] : [value]);
    }

    /// <summary>The controls being inspected together.</summary>
    public IReadOnlyList<Control> Targets => _targets;

    /// <summary>
    /// Shows the properties (or events) common to every control in <paramref name="targets"/>, blank where
    /// their values differ; editing a row applies the new value to every target that has it. An empty list
    /// clears the grid.
    /// </summary>
    public void SetTargets(IReadOnlyList<Control> targets)
    {
        EndEdit(commit: true);
        _targets = targets;
        Rebuild();
    }

    public int RowCount => _rows.Count;
    public string RowName(int i) => _rows[i].Name;
    public int SelectedIndex { get => _selected; set { _selected = Math.Clamp(value, -1, _rows.Count - 1); ScrollIntoView(_selected); Invalidate(); } }
    public bool IsEditing => _editor != null;

    /// <summary>True if row <paramref name="i"/> is a category header (<see cref="Arrangement"/> categorized), not an editable row.</summary>
    public bool IsHeader(int i) => _rows[i].IsHeader;

    /// <summary>
    /// True if row <paramref name="i"/> is one flag of an expanded <c>Anchors</c> property (Delphi's Object
    /// Inspector style: click the parent row's disclosure arrow to reveal akLeft/akTop/akRight/akBottom as
    /// their own checkbox rows, rather than a graphical widget).
    /// </summary>
    public bool IsAnchorFlag(int i) => _rows[i].Flag != null;

    /// <summary>The value text shown in row <paramref name="i"/>: blank if the targets' values differ.</summary>
    public string RowValue(int i)
    {
        var (_, prop, ev, isHeader, flag) = _rows[i];
        if (_targets.Count == 0 || isHeader) return "";
        if (flag is { } f) return Common(_targets.Select(t => ((Anchors)prop!.GetValue(t)!).HasFlag(f) ? "true" : "false"));
        if (ev != null) return Common(_targets.Select(t => t.EventHandlers.GetValueOrDefault(ev.Name, "")));
        if (prop!.Type == PropertyType.Strings)
        {
            var counts = _targets.Select(t => ((string[])prop.GetValue(t)!).Length).Distinct().ToList();
            return counts.Count == 1 ? $"({counts[0]} items)" : "";
        }
        return Common(_targets.Select(prop.FormatValue));
    }

    static string Common(IEnumerable<string> values)
    {
        using var e = values.GetEnumerator();
        if (!e.MoveNext()) return "";
        var first = e.Current;
        while (e.MoveNext()) if (e.Current != first) return "";
        return first;
    }

    public int IndexOf(string name) => _rows.FindIndex(r => !r.IsHeader && r.Name == name);

    /// <summary>Repaints the values (after a target changed from code).</summary>
    public void RefreshValues() => Invalidate();

    const string MiscCategory = "Misc";

    void Rebuild()
    {
        _rows.Clear();
        if (_targets.Count > 0)
        {
            var classes = _targets.Select(ComponentRegistry.Of).ToList();
            var controlClass = ComponentRegistry.Find(typeof(Control))!;
            // Non-visual components show only their own properties and events: the inherited layout and
            // input members of Control mean nothing for them (as in Delphi). Only applies when every
            // selected control is a component — mixing kinds can't happen from a rubber-band or Shift-click
            // selection, but stay conservative rather than hide members that do apply to a visual control.
            bool allComponents = _targets.All(t => t is Component);
            var hiddenNames = allComponents
                ? controlClass.OwnProperties.Select(p => p.Name).Concat(controlClass.OwnEvents.Select(e => e.Name)).ToHashSet()
                : [];

            List<(string Name, PropertyMeta? Prop, EventMeta? Event, string Category)> items = [];
            if (_mode == PropertyGridMode.Properties)
            {
                foreach (var p in classes[0].Properties)
                {
                    bool excluded = hiddenNames.Contains(p.Name) && classes.All(c => c.FindProperty(p.Name) == controlClass.FindProperty(p.Name));
                    if (!excluded && classes.All(c => c.FindProperty(p.Name) is { } q && q.Type == p.Type))
                        items.Add((p.Name, p, null, string.IsNullOrEmpty(p.Category) ? MiscCategory : p.Category));
                }
            }
            else
            {
                foreach (var e in classes[0].Events)
                    if (!hiddenNames.Contains(e.Name) && classes.All(c => c.FindEvent(e.Name) != null))
                        items.Add((e.Name, null, e, MiscCategory));
            }
            if (_filter.Length > 0) items = [.. items.Where(r => r.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))];

            // Events aren't grouped by category (Delphi and Visual Studio don't either): Categorized then
            // behaves like Alphabetical for the Events tab.
            if (_arrangement == PropertyGridArrangement.Categorized && _mode == PropertyGridMode.Properties)
            {
                foreach (var group in items.GroupBy(r => r.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    _rows.Add((group.Key, null, null, true, null));
                    foreach (var r in group.OrderBy(r => r.Name, StringComparer.Ordinal)) AddRow(r);
                }
            }
            else
            {
                var ordered = _arrangement == PropertyGridArrangement.Declared ? items.AsEnumerable() : items.OrderBy(r => r.Name, StringComparer.Ordinal);
                foreach (var r in ordered) AddRow(r);
            }
        }
        _selected = _rows.Count > 0 ? Math.Clamp(_selected, 0, _rows.Count - 1) : -1;
        if (_selected >= 0 && _rows[_selected].IsHeader) _selected = FindSelectable(_selected, 1) ?? FindSelectable(_selected, -1) ?? -1;
        _top = 0;
        Invalidate();
    }

    static readonly (Anchors Flag, string Suffix)[] AnchorFlags =
        [(Anchors.Left, "left"), (Anchors.Top, "top"), (Anchors.Right, "right"), (Anchors.Bottom, "bottom")];

    /// <summary>Adds one row for <paramref name="item"/>, plus its four flag rows if it's an expanded Anchors property.</summary>
    void AddRow((string Name, PropertyMeta? Prop, EventMeta? Event, string Category) item)
    {
        _rows.Add((item.Name, item.Prop, item.Event, false, null));
        if (item.Prop is { Type: PropertyType.Anchors } && _expandedAnchors.Contains(item.Name))
            foreach (var (flag, suffix) in AnchorFlags)
                _rows.Add(($"{item.Name}.{suffix}", item.Prop, null, false, flag));
    }

    /// <summary>Expands or collapses an Anchors property's flag rows (its disclosure arrow was clicked).</summary>
    public void ToggleAnchorsExpanded(int i)
    {
        if (i < 0 || i >= _rows.Count || _rows[i] is not { Flag: null, Prop.Type: PropertyType.Anchors } row) return;
        if (!_expandedAnchors.Add(row.Name)) _expandedAnchors.Remove(row.Name);
        Rebuild();
    }

    /// <summary>The nearest non-header row from <paramref name="from"/> (inclusive) stepping by <paramref name="delta"/>.</summary>
    int? FindSelectable(int from, int delta)
    {
        for (int i = from; i >= 0 && i < _rows.Count; i += delta)
            if (!_rows[i].IsHeader) return i;
        return null;
    }

    // ---- Geometry ----

    int RowHeight => Px(24);
    int Border => Px(1);
    int NameColumn => Px(NameWidth);
    bool ShowBar => _rows.Count * RowHeight > Height - 2 * Border;
    int BarSize => Px(12);
    Rect RowsArea => new(Border, Border, Math.Max(0, Width - 2 * Border - (ShowBar ? BarSize : 0)), Math.Max(0, Height - 2 * Border));
    Rect BarRect => new(Width - Border - BarSize, Border, BarSize, Math.Max(0, Height - 2 * Border));

    public Rect RowRect(int i) => new(RowsArea.X, RowsArea.Y + i * RowHeight - _top, RowsArea.Width, RowHeight);
    public Rect ValueRect(int i)
    {
        var r = RowRect(i);
        return new Rect(r.X + NameColumn, r.Y, Math.Max(0, r.Width - NameColumn), r.Height);
    }

    int IndexAt(int y)
    {
        var area = RowsArea;
        if (y < area.Y || y >= area.Y + area.Height) return -1;
        int i = (y - area.Y + _top) / Math.Max(1, RowHeight);
        return i < _rows.Count ? i : -1;
    }

    void ScrollIntoView(int i)
    {
        if (i < 0) return;
        int y = i * RowHeight, h = RowsArea.Height;
        if (y < _top) _top = y;
        else if (y + RowHeight > _top + h) _top = y + RowHeight - h;
        _top = Math.Clamp(_top, 0, Math.Max(0, _rows.Count * RowHeight - h));
    }

    // ---- Editing ----

    /// <summary>Starts editing row <paramref name="i"/> (bools toggle immediately).</summary>
    public void BeginEdit(int i)
    {
        if (_targets.Count == 0 || i < 0 || i >= _rows.Count || _rows[i].IsHeader || OwningForm is not { } form) return;
        EndEdit(commit: true);
        SelectedIndex = i;
        var (_, prop, ev, _, flag) = _rows[i];
        var cell = ValueRect(i);
        int x = AbsoluteLeft + cell.X, y = AbsoluteTop + cell.Y;

        if (flag is { } f)
        {
            SetAnchorFlag(prop!, f, RowValue(i) != "true");
            return;
        }
        if (prop is { Type: PropertyType.Bool })
        {
            Apply(i, RowValue(i) == "true" ? "false" : "true");
            return;
        }
        if (prop is { Values: { Count: > 0 } values })
        {
            var list = new ListBox { Items = [.. values], HotTrack = true };
            list.ItemIndex = Array.IndexOf(list.Items, RowValue(i));
            list.SetBounds(0, 0, cell.Width, Math.Min(values.Count, 8) * Px(24) + 2 * Px(1));
            list.ItemChosen += n => { Apply(i, list.Items[n]); form.ClosePopup(list); };
            OpenEditor(list, x, y + cell.Height, commitOnClose: null);
            return;
        }
        if (prop is { Type: PropertyType.Strings })
        {
            // Multi-select seeds the editor from the first target only; committing still applies to all.
            var memo = new Memo { Text = string.Join("\n", (string[])prop.GetValue(_targets[0])!), WordWrap = false };
            memo.SetBounds(0, 0, Math.Max(cell.Width, Px(200)), Px(140));
            OpenEditor(memo, x, y, commitOnClose: () => Apply(i, memo.Text));
            return;
        }
        var edit = new InlineEdit { Text = RowValue(i), BorderStyle = BorderStyle.Single };
        edit.SetBounds(0, 0, cell.Width, cell.Height);
        edit.Commit += () => EndEdit(commit: true);
        edit.CancelEdit += () => EndEdit(commit: false);
        OpenEditor(edit, x, y, commitOnClose: () => Apply(i, edit.Text));
        edit.SelectAll();
    }

    Action? _commitOnClose;

    void OpenEditor(Control editor, int x, int y, Action? commitOnClose)
    {
        if (OwningForm is not { } form) return;
        _editor = editor;
        _cancelEdit = false;
        _commitOnClose = commitOnClose;
        if (editor is InlineEdit ie) ie.Closed += OnEditorClosed;
        else if (editor is Memo m) m.PopupClosedHook = OnEditorClosed;
        else if (editor is ListBox lb) lb.PopupClosed += OnEditorClosed;
        form.OpenPopup(editor, x, y, this);
        editor.Focus();
    }

    void OnEditorClosed()
    {
        var commit = _commitOnClose;
        _commitOnClose = null;
        _editor = null;
        if (!_cancelEdit) commit?.Invoke();
        Invalidate();
        Focus();
    }

    /// <summary>Closes the inline editor, keeping (or dropping) what was typed.</summary>
    public void EndEdit(bool commit)
    {
        if (_editor == null || OwningForm is not { } form) { _editor = null; return; }
        _cancelEdit = !commit;
        form.ClosePopup(_editor);
    }

    /// <summary>
    /// Sets row <paramref name="i"/> from text on every target that accepts it; returns false (and changes
    /// nothing) if none of them do. With one target this is exactly the single-control behaviour.
    /// </summary>
    public bool Apply(int i, string text)
    {
        if (_targets.Count == 0 || i < 0 || i >= _rows.Count || _rows[i].IsHeader) return false;
        var (name, prop, ev, _, flag) = _rows[i];
        if (flag is { } f)
        {
            if (!bool.TryParse(text.Trim(), out var on)) return false;
            SetAnchorFlag(prop!, f, on);
            return true;
        }
        if (ev != null)
        {
            text = text.Trim();
            if (_targets.All(t => t.EventHandlers.GetValueOrDefault(ev.Name, "") == text)) return true;
            PropertyChanging?.Invoke(this, name);
            foreach (var t in _targets)
            {
                if (text.Length == 0) t.EventHandlers.Remove(ev.Name);
                else t.EventHandlers[ev.Name] = text;
            }
        }
        else
        {
            if (_targets.All(t => prop!.FormatValue(t) == text)) return true;
            PropertyChanging?.Invoke(this, name);
            bool any = false;
            foreach (var t in _targets) if (prop!.TryParseValue(t, text)) any = true;
            if (!any) return false;
        }
        foreach (var t in _targets) t.OwningForm?.InvalidateAll();
        Invalidate();
        PropertyChanged?.Invoke(this, name);
        Change?.Invoke(this);
        return true;
    }

    /// <summary>Sets or clears one Anchors flag on every target, keeping the property's other flags as they are.</summary>
    void SetAnchorFlag(PropertyMeta prop, Anchors flag, bool on)
    {
        if (_targets.All(t => ((Anchors)prop.GetValue(t)!).HasFlag(flag) == on)) return;
        PropertyChanging?.Invoke(this, prop.Name);
        foreach (var t in _targets)
        {
            var current = (Anchors)prop.GetValue(t)!;
            prop.SetValue(t, on ? current | flag : current & ~flag);
        }
        foreach (var t in _targets) t.OwningForm?.InvalidateAll();
        Invalidate();
        PropertyChanged?.Invoke(this, prop.Name);
        Change?.Invoke(this);
    }

    // ---- Input ----

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButton.Left) return;
        if (ShowBar && e.X >= BarRect.X)
        {
            SyncAxis();
            _barDrag = true;
            if (_driver.Press(e.Y - BarRect.Y, BarRect.Height, Px(16), RowsArea.Height)) { _top = _axis.Position; Invalidate(); }
            return;
        }
        int i = IndexAt(e.Y);
        if (i < 0 || _rows[i].IsHeader) return;
        // The disclosure arrow of an Anchors row (Delphi-style expand, not a graphical widget) lives in the
        // name column; clicking it toggles its akLeft/akTop/akRight/akBottom rows instead of selecting/editing.
        if (_rows[i].Flag == null && _rows[i].Prop is { Type: PropertyType.Anchors } && e.X < RowsArea.X + NameColumn)
        {
            SelectedIndex = i;
            ToggleAnchorsExpanded(i);
            return;
        }
        if (e.Clicks == 2 && _rows[i].Event is { } ev) { EventDoubleClick?.Invoke(this, ev); return; }
        bool wasSelected = i == _selected;
        SelectedIndex = i;
        // Event rows: a click selects, a double-click creates the handler, Enter/F2 edits the name.
        bool checkbox = _rows[i].Prop is { Type: PropertyType.Bool } || _rows[i].Flag != null;
        if (_rows[i].Event == null && e.X >= RowsArea.X + NameColumn && (wasSelected || checkbox))
            BeginEdit(i);
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_barDrag && _driver.Move(e.Y - BarRect.Y, BarRect.Height, Px(16))) { _top = _axis.Position; Invalidate(); }
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_barDrag) { _barDrag = false; _driver.Release(); }
    }

    protected internal override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled || !ShowBar) return;
        _top = Math.Clamp(_top - (int)Math.Round(e.DeltaY * RowHeight * 3), 0, Math.Max(0, _rows.Count * RowHeight - RowsArea.Height));
        Invalidate();
        e.Handled = true;
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Modifiers != KeyModifiers.None || _rows.Count == 0) return;
        switch (e.Key)
        {
            case Key.Up: SelectedIndex = FindSelectable(Math.Max(0, _selected - 1), -1) ?? _selected; break;
            case Key.Down: SelectedIndex = FindSelectable(Math.Min(_rows.Count - 1, _selected + 1), 1) ?? _selected; break;
            case Key.Home: SelectedIndex = FindSelectable(0, 1) ?? _selected; break;
            case Key.End: SelectedIndex = FindSelectable(_rows.Count - 1, -1) ?? _selected; break;
            case Key.Enter or Key.F2: BeginEdit(_selected); break;
            case Key.Space when _selected >= 0 && (_rows[_selected].Prop is { Type: PropertyType.Bool } || _rows[_selected].Flag != null):
                BeginEdit(_selected);
                break;
            case Key.Right or Key.Left when _selected >= 0 && _rows[_selected] is { Flag: null, Prop.Type: PropertyType.Anchors }:
                ToggleAnchorsExpanded(_selected);
                break;
            default: return;
        }
        e.Handled = true;
    }

    void SyncAxis()
    {
        _axis.Max = Math.Max(_rows.Count * RowHeight, RowsArea.Height);
        _axis.PageSize = RowsArea.Height;
        _axis.SetPosition(_top);
    }

    // ---- Painting ----

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var r = new Rect(0, 0, Width, Height);
        canvas.FillRect(r, t.InputBackground);
        var area = RowsArea;
        var font = canvas.DefaultFont;
        canvas.Save();
        canvas.ClipRect(area);
        for (int i = _top / Math.Max(1, RowHeight); i < _rows.Count; i++)
        {
            var row = RowRect(i);
            if (row.Y >= area.Y + area.Height) break;
            if (_rows[i].IsHeader)
            {
                canvas.FillRect(row, t.SelectionInactive);
                canvas.DrawText(_rows[i].Name, canvas.DefaultBoldFont, t.Text, new Rect(row.X + canvas.Px(8), row.Y, row.Width - canvas.Px(10), row.Height), TextAlign.Left, VerticalAlign.Center);
                canvas.FillRect(new Rect(row.X, row.Y + row.Height - canvas.Px(1), row.Width, canvas.Px(1)), t.PanelBorder);
                continue;
            }
            bool selected = i == _selected;
            bool isAnchorFlag = _rows[i].Flag != null;
            bool isAnchorsParent = !isAnchorFlag && _rows[i].Prop is { Type: PropertyType.Anchors };
            var nameRect = new Rect(row.X, row.Y, NameColumn, row.Height);
            if (selected) canvas.FillRect(nameRect, Focused || IsEditing ? t.Selection : t.SelectionInactive);
            int nameIndent = canvas.Px(8) + (isAnchorFlag ? canvas.Px(14) : 0);
            string nameText = _rows[i].Name;
            if (isAnchorsParent) nameText = (_expandedAnchors.Contains(nameText) ? "▾ " : "▸ ") + nameText;
            else if (isAnchorFlag) nameText = nameText[(nameText.LastIndexOf('.') + 1)..];   // "anchors.left" → "left"
            canvas.DrawText(nameText, font, t.Text, new Rect(nameRect.X + nameIndent, row.Y, nameRect.Width - nameIndent - canvas.Px(2), row.Height), TextAlign.Left, VerticalAlign.Center);

            var cell = ValueRect(i);
            var valueRect = new Rect(cell.X + canvas.Px(8), cell.Y, cell.Width - canvas.Px(10), cell.Height);
            if (_targets.Count > 0)
            {
                var prop = _rows[i].Prop;
                if (prop is { Type: PropertyType.Bool } || isAnchorFlag)
                {
                    int s = canvas.Px(14);
                    var box = new Rect(valueRect.X, row.Y + (row.Height - s) / 2, s, s);
                    bool on = RowValue(i) == "true";
                    canvas.FillRoundRect(box, canvas.Px(3), on ? t.Accent : t.InputBackground);
                    if (on) canvas.DrawCheckMark(box, t.OnAccent, Math.Max(1.5f, canvas.Px(2)));
                    else canvas.DrawRoundBorder(box, canvas.Px(3), t.InputBorder, canvas.Px(1));
                    valueRect = new Rect(box.X + s + canvas.Px(6), row.Y, valueRect.Width - s - canvas.Px(6), row.Height);
                }
                else if (prop is { Type: PropertyType.Color } && Color.TryParse(RowValue(i), out var c))
                {
                    int s = canvas.Px(14);
                    var swatch = new Rect(valueRect.X, row.Y + (row.Height - s) / 2, s, s);
                    canvas.FillRect(swatch, c);
                    canvas.DrawBorder(swatch, t.InputBorder, canvas.Px(1));
                    valueRect = new Rect(swatch.X + s + canvas.Px(6), row.Y, valueRect.Width - s - canvas.Px(6), row.Height);
                }
                canvas.Save();
                canvas.ClipRect(cell);
                canvas.DrawText(RowValue(i), font, t.Text, valueRect, TextAlign.Left, VerticalAlign.Center);
                canvas.Restore();
            }
            canvas.FillRect(new Rect(row.X, row.Y + row.Height - canvas.Px(1), row.Width, canvas.Px(1)), t.SelectionInactive);
            canvas.FillRect(new Rect(cell.X - canvas.Px(1), row.Y, canvas.Px(1), row.Height), t.SelectionInactive);
        }
        canvas.Restore();
        if (ShowBar)
        {
            SyncAxis();
            ScrollBarDriver.Paint(canvas, BarRect, vertical: true, _axis, _barDrag, Px(16));
        }
        canvas.DrawBorder(r, Focused ? t.Accent : t.InputBorder, Border);
    }

    /// <summary>The inline text editor: Enter commits, Escape cancels.</summary>
    sealed class InlineEdit : Edit
    {
        public event Action? Commit;
        public event Action? CancelEdit;
        public event Action? Closed;

        protected internal override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Modifiers == KeyModifiers.None && e.Key == Key.Enter) { e.Handled = true; Commit?.Invoke(); return; }
            if (e.Modifiers == KeyModifiers.None && e.Key == Key.Escape) { e.Handled = true; CancelEdit?.Invoke(); return; }
            base.OnKeyDown(e);
        }

        protected internal override void OnPopupClosed()
        {
            base.OnPopupClosed();
            Closed?.Invoke();
        }
    }
}
