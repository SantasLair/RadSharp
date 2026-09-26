using RadSharp.Drawing;

namespace RadSharp;

public enum PropertyGridMode { Properties, Events }

public delegate void PropertyChangedEvent(PropertyGrid sender, string propertyName);
public delegate void EventRowEvent(PropertyGrid sender, EventMeta ev);

/// <summary>
/// The Object Inspector: lists a control's published properties (or events) from the generated
/// metadata and edits them in place. Text, numbers, colours and structs are typed into an inline
/// editor; enums and booleans pick from a list; string lists open a multi-line editor.
/// </summary>
public class PropertyGrid : Control
{
    Control? _target;
    ComponentClass? _class;
    readonly List<(string Name, PropertyMeta? Prop, EventMeta? Event)> _rows = [];
    int _selected = -1, _top;
    PropertyGridMode _mode;
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

    /// <summary>Raised after the user changed a value.</summary>
    [Published] public event NotifyEvent? Change;
    public event PropertyChangedEvent? PropertyChanged;
    /// <summary>Raised when an event row is double-clicked (the designer creates the handler).</summary>
    public event EventRowEvent? EventDoubleClick;

    /// <summary>The control being inspected.</summary>
    public Control? Target
    {
        get => _target;
        set
        {
            EndEdit(commit: true);
            _target = value;
            _class = value == null ? null : ComponentRegistry.Of(value);
            Rebuild();
        }
    }

    public int RowCount => _rows.Count;
    public string RowName(int i) => _rows[i].Name;
    public int SelectedIndex { get => _selected; set { _selected = Math.Clamp(value, -1, _rows.Count - 1); ScrollIntoView(_selected); Invalidate(); } }
    public bool IsEditing => _editor != null;

    /// <summary>The value text shown in row <paramref name="i"/>.</summary>
    public string RowValue(int i)
    {
        var (_, prop, ev) = _rows[i];
        if (_target == null) return "";
        if (ev != null) return _target.EventHandlers.GetValueOrDefault(ev.Name, "");
        if (prop!.Type == PropertyType.Strings) return $"({((string[])prop.GetValue(_target)!).Length} items)";
        return prop.FormatValue(_target);
    }

    public int IndexOf(string name) => _rows.FindIndex(r => r.Name == name);

    /// <summary>Repaints the values (after the target changed from code).</summary>
    public void RefreshValues() => Invalidate();

    void Rebuild()
    {
        _rows.Clear();
        if (_class != null)
        {
            // Non-visual components show only their own properties and events: the inherited
            // layout and input members of Control mean nothing for them (as in Delphi).
            var hidden = _target is Component && ComponentRegistry.Find(typeof(Control)) is { } control
                ? control.OwnProperties.Select(p => p.Name).Concat(control.OwnEvents.Select(e => e.Name)).ToHashSet()
                : [];
            if (_mode == PropertyGridMode.Properties)
            {
                foreach (var p in _class.Properties)
                    if (!hidden.Contains(p.Name) || _class.FindProperty(p.Name) != ComponentRegistry.Find(typeof(Control))!.FindProperty(p.Name))
                        _rows.Add((p.Name, p, null));
            }
            else
            {
                foreach (var e in _class.Events)
                    if (!hidden.Contains(e.Name)) _rows.Add((e.Name, null, e));
            }
        }
        _selected = _rows.Count > 0 ? Math.Clamp(_selected, 0, _rows.Count - 1) : -1;
        _top = 0;
        Invalidate();
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
        if (_target == null || i < 0 || i >= _rows.Count || OwningForm is not { } form) return;
        EndEdit(commit: true);
        SelectedIndex = i;
        var (_, prop, ev) = _rows[i];
        var cell = ValueRect(i);
        int x = AbsoluteLeft + cell.X, y = AbsoluteTop + cell.Y;

        if (prop is { Type: PropertyType.Bool })
        {
            Apply(i, prop.FormatValue(_target) == "true" ? "false" : "true");
            return;
        }
        if (prop is { Values: { Count: > 0 } values })
        {
            var list = new ListBox { Items = [.. values], HotTrack = true };
            list.ItemIndex = Array.IndexOf(list.Items, prop.FormatValue(_target));
            list.SetBounds(0, 0, cell.Width, Math.Min(values.Count, 8) * Px(24) + 2 * Px(1));
            list.ItemChosen += n => { Apply(i, list.Items[n]); form.ClosePopup(list); };
            OpenEditor(list, x, y + cell.Height, commitOnClose: null);
            return;
        }
        if (prop is { Type: PropertyType.Strings })
        {
            var memo = new Memo { Text = string.Join("\n", (string[])prop.GetValue(_target)!), WordWrap = false };
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

    /// <summary>Sets row <paramref name="i"/> from text; returns false (and changes nothing) if the text isn't valid.</summary>
    public bool Apply(int i, string text)
    {
        if (_target == null || i < 0 || i >= _rows.Count) return false;
        var (name, prop, ev) = _rows[i];
        if (ev != null)
        {
            text = text.Trim();
            if (_target.EventHandlers.GetValueOrDefault(ev.Name, "") == text) return true;
            if (text.Length == 0) _target.EventHandlers.Remove(ev.Name);
            else _target.EventHandlers[ev.Name] = text;
        }
        else
        {
            var before = prop!.FormatValue(_target);
            if (!prop.TryParseValue(_target, text)) return false;
            if (prop.FormatValue(_target) == before) return true;
        }
        _target.OwningForm?.InvalidateAll();
        Invalidate();
        PropertyChanged?.Invoke(this, name);
        Change?.Invoke(this);
        return true;
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
        if (i < 0) return;
        if (e.Clicks == 2 && _rows[i].Event is { } ev) { EventDoubleClick?.Invoke(this, ev); return; }
        bool wasSelected = i == _selected;
        SelectedIndex = i;
        // Event rows: a click selects, a double-click creates the handler, Enter/F2 edits the name.
        if (_rows[i].Event == null && e.X >= RowsArea.X + NameColumn && (wasSelected || _rows[i].Prop is { Type: PropertyType.Bool }))
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
            case Key.Up: SelectedIndex = Math.Max(0, _selected - 1); break;
            case Key.Down: SelectedIndex = Math.Min(_rows.Count - 1, _selected + 1); break;
            case Key.Home: SelectedIndex = 0; break;
            case Key.End: SelectedIndex = _rows.Count - 1; break;
            case Key.Enter or Key.F2: BeginEdit(_selected); break;
            case Key.Space when _rows[Math.Max(0, _selected)].Prop is { Type: PropertyType.Bool }: BeginEdit(_selected); break;
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
            bool selected = i == _selected;
            var nameRect = new Rect(row.X, row.Y, NameColumn, row.Height);
            if (selected) canvas.FillRect(nameRect, Focused || IsEditing ? t.Selection : t.SelectionInactive);
            canvas.DrawText(_rows[i].Name, font, t.Text, new Rect(nameRect.X + canvas.Px(8), row.Y, nameRect.Width - canvas.Px(10), row.Height), TextAlign.Left, VerticalAlign.Center);

            var cell = ValueRect(i);
            var valueRect = new Rect(cell.X + canvas.Px(8), cell.Y, cell.Width - canvas.Px(10), cell.Height);
            if (_target != null)
            {
                var prop = _rows[i].Prop;
                if (prop is { Type: PropertyType.Bool })
                {
                    int s = canvas.Px(14);
                    var box = new Rect(valueRect.X, row.Y + (row.Height - s) / 2, s, s);
                    bool on = prop.FormatValue(_target) == "true";
                    canvas.FillRoundRect(box, canvas.Px(3), on ? t.Accent : t.InputBackground);
                    if (on) canvas.DrawCheckMark(box, t.OnAccent, Math.Max(1.5f, canvas.Px(2)));
                    else canvas.DrawRoundBorder(box, canvas.Px(3), t.InputBorder, canvas.Px(1));
                    valueRect = new Rect(box.X + s + canvas.Px(6), row.Y, valueRect.Width - s - canvas.Px(6), row.Height);
                }
                else if (prop is { Type: PropertyType.Color } && Color.TryParse(prop.FormatValue(_target), out var c))
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
