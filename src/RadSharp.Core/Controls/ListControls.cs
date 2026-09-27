using RadSharp.Drawing;

namespace RadSharp;

public delegate void DrawItemEvent(Control sender, Canvas canvas, int index, Rect rect, bool selected);

/// <summary>A scrolling list of strings with one selected item.</summary>
public class ListBox : Control
{
    string[] _items = [];
    int _itemIndex = -1;
    int _itemHeight = 24;
    readonly ScrollAxis _axis = new() { PageSize = 0, Max = 0 };
    readonly ScrollBarDriver _driver;
    bool _barDrag, _mouseSelecting;

    public ListBox()
    {
        _driver = new ScrollBarDriver(_axis);
        SetDefaultSize(160, 120);
        TabStop = true;
    }

    [Published(Category = "Behavior")]
    public string[] Items
    {
        get => _items;
        set
        {
            _items = value ?? [];
            if (_itemIndex >= _items.Length) ItemIndex = _items.Length - 1;
            ClampScroll();
            Invalidate();
        }
    }

    /// <summary>The selected row, or -1.</summary>
    [Published(Category = "Behavior")]
    public int ItemIndex
    {
        get => _itemIndex;
        set
        {
            value = Math.Clamp(value, -1, _items.Length - 1);
            if (value == _itemIndex) return;
            _itemIndex = value;
            ScrollIntoView(value);
            Invalidate();
            Change?.Invoke(this);
        }
    }

    /// <summary>Row height at 100 %.</summary>
    [Published(Category = "Appearance")]
    public int ItemHeight { get => _itemHeight; set { _itemHeight = Math.Max(1, value); Invalidate(); } }

    [Published(Category = "Appearance")] public BorderStyle BorderStyle { get; set; } = BorderStyle.Single;

    [Published] public event NotifyEvent? Change;
    [Published("onDblClick")] public event NotifyEvent? DoubleClick;
    /// <summary>Paint rows yourself (the default drawing is skipped while this has handlers).</summary>
    [Published] public event DrawItemEvent? DrawItem;

    public string? SelectedItem => _itemIndex >= 0 ? _items[_itemIndex] : null;

    /// <summary>In a dropdown: the hovered row is selected, and a click or Enter chooses it.</summary>
    internal bool HotTrack { get; set; }
    internal event Action<int>? ItemChosen;
    internal event Action? PopupClosed;

    protected internal override void OnPopupClosed()
    {
        base.OnPopupClosed();
        PopupClosed?.Invoke();
    }
    internal Action<Canvas, int, Rect, Color>? ItemPainter { get; set; }

    int Border => BorderStyle == BorderStyle.Single ? Px(1) : 0;
    int RowHeight => Px(_itemHeight);
    int BarSize => Px(12);
    bool ShowBar => _items.Length * RowHeight > Height - 2 * Border;

    /// <summary>The rows area, in this control's coordinates.</summary>
    Rect RowsArea => new(Border, Border, Math.Max(0, Width - 2 * Border - (ShowBar ? BarSize : 0)), Math.Max(0, Height - 2 * Border));
    Rect BarRect => new(Width - Border - BarSize, Border, BarSize, Math.Max(0, Height - 2 * Border));

    public int TopOffset { get; private set; }

    void SyncAxis()
    {
        _axis.Max = Math.Max(_items.Length * RowHeight, RowsArea.Height);
        _axis.PageSize = RowsArea.Height;
        _axis.SetPosition(TopOffset);
    }

    void ClampScroll() => TopOffset = Math.Clamp(TopOffset, 0, Math.Max(0, _items.Length * RowHeight - RowsArea.Height));

    void SetTopOffset(int value)
    {
        int before = TopOffset;
        TopOffset = value;
        ClampScroll();
        if (TopOffset != before) Invalidate();
    }

    public void ScrollIntoView(int index)
    {
        if (index < 0) return;
        int y = index * RowHeight, h = RowsArea.Height;
        if (y < TopOffset) SetTopOffset(y);
        else if (y + RowHeight > TopOffset + h) SetTopOffset(y + RowHeight - h);
    }

    /// <summary>The row at a point in this control's coordinates, or -1.</summary>
    public int IndexAt(int x, int y)
    {
        var area = RowsArea;
        if (x < area.X || x >= area.X + area.Width || y < area.Y || y >= area.Y + area.Height) return -1;
        int i = (y - area.Y + TopOffset) / Math.Max(1, RowHeight);
        return i < _items.Length ? i : -1;
    }

    /// <summary>A row's rectangle in this control's coordinates.</summary>
    public Rect ItemRect(int index) => new(RowsArea.X, RowsArea.Y + index * RowHeight - TopOffset, RowsArea.Width, RowHeight);

    protected override void OnResize()
    {
        ClampScroll();
        base.OnResize();
    }

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (ShowBar && e.X >= BarRect.X && e.Button == MouseButton.Left)
        {
            SyncAxis();
            _barDrag = true;
            if (_driver.Press(e.Y - BarRect.Y, BarRect.Height, Px(16), RowsArea.Height)) SetTopOffset(_axis.Position);
            return;
        }
        int i = IndexAt(e.X, e.Y);
        if (i < 0) return;
        ItemIndex = i;  // right-click selects too, before a context menu opens
        if (e.Button != MouseButton.Left) return;
        _mouseSelecting = true;
        if (e.Clicks == 2) DoubleClick?.Invoke(this);
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_barDrag)
        {
            if (_driver.Move(e.Y - BarRect.Y, BarRect.Height, Px(16))) SetTopOffset(_axis.Position);
            return;
        }
        if (_mouseSelecting || HotTrack)
        {
            int i = IndexAt(e.X, Math.Clamp(e.Y, RowsArea.Y, RowsArea.Y + RowsArea.Height - 1));
            if (i >= 0) ItemIndex = i;
        }
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_barDrag) { _barDrag = false; _driver.Release(); Invalidate(); return; }
        _mouseSelecting = false;
        if (HotTrack && e.Button == MouseButton.Left && IndexAt(e.X, e.Y) is var i and >= 0) ItemChosen?.Invoke(i);
    }

    protected internal override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled || !ShowBar) return;
        SetTopOffset(TopOffset - (int)Math.Round(e.DeltaY * RowHeight * 3));
        e.Handled = true;
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Modifiers != KeyModifiers.None || _items.Length == 0) return;
        int page = Math.Max(1, RowsArea.Height / Math.Max(1, RowHeight) - 1);
        int? target = e.Key switch
        {
            Key.Up => Math.Max(0, _itemIndex - 1),
            Key.Down => Math.Min(_items.Length - 1, _itemIndex + 1),
            Key.Home => 0,
            Key.End => _items.Length - 1,
            Key.PageUp => Math.Max(0, _itemIndex - page),
            Key.PageDown => Math.Min(_items.Length - 1, _itemIndex + page),
            _ => null,
        };
        if (target is { } t) { ItemIndex = t; e.Handled = true; }
        else if (e.Key == Key.Enter && HotTrack && _itemIndex >= 0) { ItemChosen?.Invoke(_itemIndex); e.Handled = true; }
    }

    /// <summary>Typing a letter selects the next item that starts with it.</summary>
    protected internal override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || e.Text.Length == 0 || _items.Length == 0) return;
        for (int n = 1; n <= _items.Length; n++)
        {
            int i = (_itemIndex + n) % _items.Length;
            if (_items[i].StartsWith(e.Text, StringComparison.CurrentCultureIgnoreCase)) { ItemIndex = i; break; }
        }
        e.Handled = true;
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var r = new Rect(0, 0, Width, Height);
        canvas.FillRect(r, IsEffectivelyEnabled ? t.InputBackground : t.FormBackground);
        var area = RowsArea;
        canvas.Save();
        canvas.ClipRect(area);
        int first = TopOffset / Math.Max(1, RowHeight);
        for (int i = first; i < _items.Length; i++)
        {
            var row = ItemRect(i);
            if (row.Y >= area.Y + area.Height) break;
            bool selected = i == _itemIndex;
            if (DrawItem != null) { DrawItem(this, canvas, i, row, selected); continue; }
            if (selected) canvas.FillRect(row, Focused || HotTrack ? t.Selection : t.SelectionInactive);
            var textColor = IsEffectivelyEnabled ? t.Text : t.TextDisabled;
            var textRect = new Rect(row.X + canvas.Px(8), row.Y, row.Width - canvas.Px(12), row.Height);
            if (ItemPainter != null) ItemPainter(canvas, i, textRect, textColor);
            else canvas.DrawText(_items[i], EffectiveFont, textColor, textRect, TextAlign.Left, VerticalAlign.Center);
            if (selected && Focused && OwningForm is { ShowFocusCues: true }) canvas.DrawBorder(row, t.FocusRing, canvas.Px(1));
        }
        canvas.Restore();
        if (ShowBar)
        {
            SyncAxis();
            ScrollBarDriver.Paint(canvas, BarRect, vertical: true, _axis, _barDrag, Px(16));
        }
        if (BorderStyle == BorderStyle.Single) canvas.DrawBorder(r, Focused ? t.Accent : t.InputBorder, Border);
    }
}

/// <summary>A drop-down list (Delphi's csDropDownList combo box): shows the selected item, opens a list below.</summary>
public class ComboBox : Control
{
    string[] _items = [];
    int _itemIndex = -1;
    ListBox? _dropDown;

    public ComboBox()
    {
        SetDefaultSize(160, 26);
        TabStop = true;
    }

    [Published(Category = "Behavior")]
    public string[] Items
    {
        get => _items;
        set { _items = value ?? []; if (_itemIndex >= _items.Length) ItemIndex = _items.Length - 1; Invalidate(); }
    }

    [Published(Category = "Behavior")]
    public int ItemIndex
    {
        get => _itemIndex;
        set
        {
            value = Math.Clamp(value, -1, _items.Length - 1);
            if (value == _itemIndex) return;
            _itemIndex = value;
            Invalidate();
            OnChange();
        }
    }

    /// <summary>How many rows the drop-down shows before it scrolls.</summary>
    [Published(Category = "Behavior")] public int DropDownCount { get; set; } = 8;

    [Published] public event NotifyEvent? Change;

    public string Text => _itemIndex >= 0 ? _items[_itemIndex] : "";
    public bool IsDroppedDown => _dropDown != null;

    protected virtual void OnChange() => Change?.Invoke(this);

    /// <summary>Draws item <paramref name="index"/> (the closed face and the list rows use this).</summary>
    protected virtual void DrawItemContent(Canvas canvas, int index, Rect rect, Color textColor) =>
        canvas.DrawText(_items[index], EffectiveFont, textColor, rect, TextAlign.Left, VerticalAlign.Center);

    public void DropDown()
    {
        if (OwningForm is not { } form || _dropDown != null || _items.Length == 0) return;
        var list = new ListBox
        {
            Items = _items,
            HotTrack = true,
            ItemPainter = DrawItemContent,
        };
        list.ItemIndex = _itemIndex;
        int rows = Math.Clamp(_items.Length, 1, Math.Max(1, DropDownCount));
        list.SetBounds(0, 0, Width, rows * Px(24) + 2 * Px(1));
        list.ItemChosen += i =>
        {
            ItemIndex = i;
            CloseUp();
        };
        list.PopupClosed += () =>
        {
            if (_dropDown == list) _dropDown = null;
            Invalidate();
        };
        _dropDown = list;
        form.OpenPopup(list, AbsoluteLeft, AbsoluteTop + Height, this);
        list.ScrollIntoView(_itemIndex);
        Invalidate();
    }

    public void CloseUp()
    {
        if (_dropDown is { } list && OwningForm is { } form) form.ClosePopup(list);
        _dropDown = null;
        Invalidate();
    }

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButton.Left) DropDown();
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if ((e.Key == Key.Down && e.Modifiers == KeyModifiers.Alt) || (e.Key == Key.F4 && e.Modifiers == KeyModifiers.None))
        {
            DropDown();
            e.Handled = true;
            return;
        }
        if (e.Modifiers != KeyModifiers.None || _items.Length == 0) return;
        int? target = e.Key switch
        {
            Key.Up => Math.Max(0, _itemIndex - 1),
            Key.Down => Math.Min(_items.Length - 1, _itemIndex + 1),
            Key.Home => 0,
            Key.End => _items.Length - 1,
            _ => null,
        };
        if (target is { } t) { ItemIndex = t; e.Handled = true; }
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var r = new Rect(0, 0, Width, Height);
        bool enabled = IsEffectivelyEnabled;
        float radius = canvas.Px(3);
        canvas.FillRoundRect(r, radius, enabled ? t.InputBackground : t.FormBackground);
        canvas.DrawRoundBorder(r, radius, Focused || IsDroppedDown ? t.Accent : IsHovered && enabled ? t.ButtonBorder : t.InputBorder, canvas.Px(1));
        int arrowW = canvas.Px(22);
        var textRect = new Rect(canvas.Px(8), 0, Width - canvas.Px(8) - arrowW, Height);
        if (_itemIndex >= 0)
        {
            canvas.Save();
            canvas.ClipRect(textRect);
            DrawItemContent(canvas, _itemIndex, textRect, enabled ? t.Text : t.TextDisabled);
            canvas.Restore();
        }
        canvas.DrawText("⌄", canvas.DefaultFont, enabled ? t.Text : t.TextDisabled,
            new Rect(Width - arrowW, -canvas.Px(3), arrowW - canvas.Px(4), Height), TextAlign.Center, VerticalAlign.Center);
        if (Focused && OwningForm is { ShowFocusCues: true }) canvas.DrawFocusRing(r, radius);
    }
}

/// <summary>A drop-down of named colours with swatches (Delphi's TColorBox).</summary>
public class ColorBox : ComboBox
{
    static readonly (string Name, Color Color)[] Palette =
    [
        ("Black", Color.FromHex("#000000")), ("Maroon", Color.FromHex("#800000")), ("Green", Color.FromHex("#008000")),
        ("Olive", Color.FromHex("#808000")), ("Navy", Color.FromHex("#000080")), ("Purple", Color.FromHex("#800080")),
        ("Teal", Color.FromHex("#008080")), ("Gray", Color.FromHex("#808080")), ("Silver", Color.FromHex("#C0C0C0")),
        ("Red", Color.FromHex("#FF0000")), ("Lime", Color.FromHex("#00FF00")), ("Yellow", Color.FromHex("#FFFF00")),
        ("Blue", Color.FromHex("#0000FF")), ("Fuchsia", Color.FromHex("#FF00FF")), ("Aqua", Color.FromHex("#00FFFF")),
        ("White", Color.FromHex("#FFFFFF")),
    ];

    public ColorBox()
    {
        Items = [.. Palette.Select(p => p.Name)];
        ItemIndex = 0;
    }

    /// <summary>The selected colour (the nearest palette entry is selected when set).</summary>
    [Published(Category = "Behavior")]
    public Color Selected
    {
        get => ItemIndex >= 0 ? Palette[ItemIndex].Color : Color.Black;
        set
        {
            int i = Array.FindIndex(Palette, p => p.Color == value);
            if (i >= 0) ItemIndex = i;
        }
    }

    protected override void DrawItemContent(Canvas canvas, int index, Rect rect, Color textColor)
    {
        int s = canvas.Px(14);
        var swatch = new Rect(rect.X, rect.Y + (rect.Height - s) / 2, s, s);
        canvas.FillRect(swatch, Palette[index].Color);
        canvas.DrawBorder(swatch, canvas.Theme.InputBorder, canvas.Px(1));
        int gap = s + canvas.Px(8);
        canvas.DrawText(Palette[index].Name, EffectiveFont, textColor,
            new Rect(rect.X + gap, rect.Y, rect.Width - gap, rect.Height), TextAlign.Left, VerticalAlign.Center);
    }
}
