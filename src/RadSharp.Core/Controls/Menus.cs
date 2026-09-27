using System.Text.Json;
using RadSharp.Drawing;

namespace RadSharp;

public delegate void MenuItemEvent(MenuItem sender);

/// <summary>
/// One menu command, separator or submenu. Saved in form files as nested objects under the menu's
/// <c>"items"</c> (spec, Appendix A: caption, shortcut, altShortcut, onClick, enabled, checked, visible,
/// separator, items).
/// </summary>
public sealed class MenuItem
{
    public string Name { get; set; } = "";
    public string Caption { get; set; } = "";
    /// <summary>Shortcut text such as <c>"Mod+S"</c> or <c>"F4"</c> (spec, Appendix C).</summary>
    public string Shortcut { get; set; } = "";
    /// <summary>A second shortcut for the same command (e.g. Ctrl+Y and Ctrl+Shift+Z for Redo).</summary>
    public string AltShortcut { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Checked { get; set; }
    public bool Visible { get; set; } = true;
    public bool Separator { get; set; }
    /// <summary>The handler name from the form file (kept for the designer and for saving).</summary>
    public string ClickHandler { get; set; } = "";

    public List<MenuItem> Items { get; } = [];

    public bool IsSeparator => Separator || Caption == "-";
    public bool HasSubmenu => Items.Count > 0;

    [Published] public event MenuItemEvent? Click;

    public MenuItem() { }
    public MenuItem(string caption, MenuItemEvent? click = null, string shortcut = "")
    {
        Caption = caption;
        Shortcut = shortcut;
        if (click != null) Click += click;
    }

    /// <summary>Adds a sub-item and returns it (for building menus in code).</summary>
    public MenuItem Add(string caption, MenuItemEvent? click = null, string shortcut = "")
    {
        var item = new MenuItem(caption, click, shortcut);
        Items.Add(item);
        return item;
    }

    public void AddSeparator() => Items.Add(new MenuItem { Caption = "-", Separator = true });

    /// <summary>Runs the command if the item is enabled, visible and not a separator or submenu.</summary>
    public bool PerformClick()
    {
        if (!Enabled || !Visible || IsSeparator || HasSubmenu) return false;
        Click?.Invoke(this);
        return true;
    }

    internal bool Matches(KeyEventArgs e) =>
        (RadSharp.Shortcut.TryParse(Shortcut, out var s) && s.Matches(e))
        || (RadSharp.Shortcut.TryParse(AltShortcut, out var a) && a.Matches(e));

    /// <summary>Depth-first search for the enabled command a key press triggers.</summary>
    internal static MenuItem? FindShortcut(IEnumerable<MenuItem> items, KeyEventArgs e)
    {
        foreach (var item in items)
        {
            if (!item.Visible || !item.Enabled) continue;
            if (!item.HasSubmenu && item.Matches(e)) return item;
            if (FindShortcut(item.Items, e) is { } found) return found;
        }
        return null;
    }

    // ---- Form files ----

    internal static MenuItem Read(JsonElement json, List<string> warnings, string path)
    {
        var item = new MenuItem();
        if (json.ValueKind != JsonValueKind.Object) { warnings.Add($"{path}: a menu item must be an object; skipped."); return item; }
        foreach (var p in json.EnumerateObject())
        {
            try
            {
                switch (p.Name)
                {
                    case "name": item.Name = Converters.String.Read(p.Value); break;
                    case "caption": item.Caption = Converters.String.Read(p.Value); break;
                    case "shortcut": item.Shortcut = Converters.String.Read(p.Value); break;
                    case "altShortcut": item.AltShortcut = Converters.String.Read(p.Value); break;
                    case "enabled": item.Enabled = Converters.Bool.Read(p.Value); break;
                    case "checked": item.Checked = Converters.Bool.Read(p.Value); break;
                    case "visible": item.Visible = Converters.Bool.Read(p.Value); break;
                    case "separator": item.Separator = Converters.Bool.Read(p.Value); break;
                    case "onClick": item.ClickHandler = Converters.String.Read(p.Value); break;
                    case "items":
                        int i = 0;
                        foreach (var child in p.Value.EnumerateArray()) item.Items.Add(Read(child, warnings, $"{path}.items[{i++}]"));
                        break;
                    default: warnings.Add($"{path}: menu items have no property \"{p.Name}\"; ignored."); break;
                }
            }
            catch (FormatException e) { warnings.Add($"{path}.{p.Name}: {e.Message}; ignored."); }
        }
        return item;
    }

    internal void Write(FormJsonWriter w)
    {
        w.BeginObject();
        if (Name.Length > 0) { w.Key("name"); w.WriteString(Name); }
        if (Caption.Length > 0) { w.Key("caption"); w.WriteString(Caption); }
        if (AltShortcut.Length > 0) { w.Key("altShortcut"); w.WriteString(AltShortcut); }
        if (Checked) { w.Key("checked"); w.WriteBool(true); }
        if (!Enabled) { w.Key("enabled"); w.WriteBool(false); }
        if (ClickHandler.Length > 0) { w.Key("onClick"); w.WriteString(ClickHandler); }
        if (Separator) { w.Key("separator"); w.WriteBool(true); }
        if (Shortcut.Length > 0) { w.Key("shortcut"); w.WriteString(Shortcut); }
        if (!Visible) { w.Key("visible"); w.WriteBool(false); }
        WriteItems(w, Items);
        w.EndObject();
    }

    internal static void WriteItems(FormJsonWriter w, List<MenuItem> items)
    {
        if (items.Count == 0) return;
        w.Key("items");
        w.BeginArray();
        foreach (var item in items) { w.NextElement(); item.Write(w); }
        w.EndArray();
    }
}

/// <summary>A control that owns a menu item tree saved under <c>"items"</c>.</summary>
public interface IMenuOwner
{
    List<MenuItem> Items { get; }
}

/// <summary>The menu bar along the top of a form (Delphi's TMainMenu).</summary>
public class MainMenu : Control, IMenuOwner
{
    int _openIndex = -1;

    public MainMenu()
    {
        SetDefaultSize(200, 26);
        Align = Align.Top;
    }

    public List<MenuItem> Items { get; } = [];

    /// <summary>Adds a top-level menu and returns it.</summary>
    public MenuItem Add(string caption)
    {
        var item = new MenuItem(caption);
        Items.Add(item);
        return item;
    }

    RenderFont BarFont => new(FontFace.Resolve(EffectiveFont), (float)(EffectiveFont.Size * UiScale));

    /// <summary>The bar-relative rectangle of each visible top-level item.</summary>
    public List<(MenuItem Item, Rect Rect)> ItemRects()
    {
        var list = new List<(MenuItem, Rect)>();
        int x = Px(4), pad = Px(10);
        foreach (var item in Items)
        {
            if (!item.Visible) continue;
            int w = (int)Math.Ceiling(BarFont.MeasureWidth(item.Caption)) + 2 * pad;
            list.Add((item, new Rect(x, 0, w, Height)));
            x += w;
        }
        return list;
    }

    int IndexAt(int x)
    {
        var rects = ItemRects();
        for (int i = 0; i < rects.Count; i++)
            if (x >= rects[i].Rect.X && x < rects[i].Rect.X + rects[i].Rect.Width) return i;
        return -1;
    }

    /// <summary>Opens the dropdown of visible top-level item <paramref name="index"/>.</summary>
    public void OpenMenu(int index, bool selectFirst = false)
    {
        var rects = ItemRects();
        if (OwningForm is not { } form || index < 0 || index >= rects.Count) return;
        var (item, rect) = rects[index];
        form.CloseAllPopups();
        _openIndex = index;
        var popup = new MenuPopup(item.Items, this, null);
        popup.Closed += () => { if (_openIndex == index) { _openIndex = -1; Invalidate(); } };
        popup.ShowAt(form, AbsoluteLeft + rect.X, AbsoluteTop + Height, this);
        if (selectFirst) popup.SelectFirst();
        Invalidate();
    }

    /// <summary>Left/Right in an open top-level dropdown switch to the neighbouring menu.</summary>
    internal void OpenNeighbour(int delta)
    {
        int count = ItemRects().Count;
        if (count == 0 || _openIndex < 0) return;
        OpenMenu((_openIndex + delta + count) % count, selectFirst: true);
    }

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButton.Left) return;
        int i = IndexAt(e.X);
        if (i >= 0) OpenMenu(i);
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_openIndex < 0) return;
        int i = IndexAt(e.X);
        if (i >= 0 && i != _openIndex) OpenMenu(i);
    }

    protected internal override bool ProcessShortcut(KeyEventArgs e)
    {
        if (MenuItem.FindShortcut(Items, e) is not { } item) return false;
        OwningForm?.CloseAllPopups();
        return item.PerformClick();
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        canvas.FillRect(new Rect(0, 0, Width, Height), t.PanelBackground);
        canvas.FillRect(new Rect(0, Height - canvas.Px(1), Width, canvas.Px(1)), t.PanelBorder);
        var rects = ItemRects();
        for (int i = 0; i < rects.Count; i++)
        {
            var (item, r) = rects[i];
            if (i == _openIndex) canvas.FillRoundRect(Canvas.Inflate(r, -canvas.Px(2)), canvas.Px(4), t.Selection);
            canvas.DrawText(item.Caption, EffectiveFont, item.Enabled ? t.Text : t.TextDisabled, r, TextAlign.Center, VerticalAlign.Center);
        }
    }
}

/// <summary>A context menu (Delphi's TPopupMenu): shown at the pointer on right-click, or from code.</summary>
public class PopupMenu : Component, IMenuOwner
{
    public List<MenuItem> Items { get; } = [];

    public MenuItem Add(string caption, MenuItemEvent? click = null, string shortcut = "")
    {
        var item = new MenuItem(caption, click, shortcut);
        Items.Add(item);
        return item;
    }

    /// <summary>The control that was right-clicked, while the menu is open.</summary>
    public Control? PopupComponent { get; private set; }

    [Published("onPopup")] public event NotifyEvent? Opening;

    /// <summary>Shows the menu at a form position.</summary>
    public void Popup(Form form, int x, int y, Control? invoker = null)
    {
        PopupComponent = invoker;
        Opening?.Invoke(this);
        form.CloseAllPopups();
        new MenuPopup(Items, null, null).ShowAt(form, x, y, invoker ?? this);
    }
}

/// <summary>The dropdown list of a menu, shown in the form's popup layer.</summary>
public class MenuPopup : Control
{
    readonly List<MenuItem> _items;
    readonly MainMenu? _bar;
    readonly MenuPopup? _parentPopup;
    MenuPopup? _child;
    int _hot = -1;

    internal MenuPopup(List<MenuItem> items, MainMenu? bar, MenuPopup? parentPopup)
    {
        _items = [.. items.Where(i => i.Visible)];
        _bar = bar;
        _parentPopup = parentPopup;
    }

    internal event Action? Closed;

    public IReadOnlyList<MenuItem> Items => _items;
    public int HotIndex => _hot;

    int RowHeight(MenuItem item) => item.IsSeparator ? Px(9) : Px(26);

    RenderFont ItemFont => new(FontFace.Resolve(EffectiveFont), (float)(EffectiveFont.Size * UiScale));

    /// <summary>The popup-relative rectangle of item <paramref name="index"/>.</summary>
    public Rect ItemRect(int index)
    {
        int y = Px(4);
        for (int i = 0; i < index; i++) y += RowHeight(_items[i]);
        return new Rect(Px(4), y, Width - Px(8), RowHeight(_items[index]));
    }

    internal void ShowAt(Form form, int x, int y, Control owner)
    {
        PopupForm = form;  // lets size calculations use the form's theme and scale
        var font = ItemFont;
        int captionW = 0, shortcutW = 0;
        foreach (var item in _items)
        {
            captionW = Math.Max(captionW, (int)Math.Ceiling(font.MeasureWidth(item.Caption)));
            if (RadSharp.Shortcut.TryParse(item.Shortcut, out var s))
                shortcutW = Math.Max(shortcutW, (int)Math.Ceiling(font.MeasureWidth(s.ToString())));
        }
        int width = Math.Max(Px(160), Px(4) + Px(28) + captionW + (shortcutW > 0 ? Px(32) + shortcutW : 0) + Px(24) + Px(4));
        int height = Px(8) + _items.Sum(RowHeight);
        SetBounds(0, 0, width, height);
        form.OpenPopup(this, x, y, owner);
    }

    internal void SelectFirst() => MoveHot(1, from: -1);

    int IndexAt(int y)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var r = ItemRect(i);
            if (y >= r.Y && y < r.Y + r.Height) return i;
        }
        return -1;
    }

    bool Selectable(int i) => i >= 0 && i < _items.Count && !_items[i].IsSeparator && _items[i].Enabled;

    void SetHot(int index)
    {
        if (index == _hot) return;
        _hot = index;
        Invalidate();
        // Hovering another item closes an open submenu; hovering a submenu item opens it.
        if (_child != null && (index < 0 || _items[index].Items != _childItems)) CloseChild();
        if (Selectable(index) && _items[index].HasSubmenu && _child == null) OpenChild(index);
    }

    List<MenuItem>? _childItems;

    void OpenChild(int index)
    {
        if (OwningForm is not { } form) return;
        var r = ItemRect(index);
        _childItems = _items[index].Items;
        _child = new MenuPopup(_items[index].Items, _bar, this);
        _child.Closed += () => { _child = null; _childItems = null; };
        _child.ShowAt(form, Left + Width - Px(4), Top + r.Y - Px(4), this);
    }

    void CloseChild()
    {
        if (_child != null && OwningForm is { } form) form.ClosePopup(_child);
        _child = null;
        _childItems = null;
    }

    void MoveHot(int delta, int? from = null)
    {
        if (_items.Count == 0) return;
        int i = from ?? _hot;
        for (int n = 0; n < _items.Count; n++)
        {
            i = (i + delta + _items.Count) % _items.Count;
            if (Selectable(i)) { _hot = i; Invalidate(); return; }
        }
    }

    /// <summary>Runs an item: opens its submenu, or closes all menus and clicks it.</summary>
    void Activate(int index)
    {
        if (!Selectable(index)) return;
        var item = _items[index];
        if (item.HasSubmenu)
        {
            SetHot(index);
            if (_child == null) OpenChild(index);
            _child?.SelectFirst();
            return;
        }
        OwningForm?.CloseAllPopups();
        item.PerformClick();
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.Y);
        if (i >= 0) SetHot(i);
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButton.Left) return;
        int i = IndexAt(e.Y);
        if (i >= 0 && e.X >= 0 && e.X < Width) Activate(i);
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        switch (e.Key)
        {
            case Key.Down: MoveHot(1); break;
            case Key.Up: MoveHot(-1); break;
            case Key.Home: MoveHot(1, from: -1); break;
            case Key.End: MoveHot(-1, from: _items.Count); break;
            case Key.Enter or Key.Space: Activate(_hot); break;
            case Key.Right:
                if (Selectable(_hot) && _items[_hot].HasSubmenu) Activate(_hot);
                else if (_bar != null) _bar.OpenNeighbour(1);
                break;
            case Key.Left:
                if (_parentPopup != null) OwningForm?.ClosePopup(this);
                else if (_bar != null) _bar.OpenNeighbour(-1);
                break;
            default: return;
        }
        e.Handled = true;
    }

    protected internal override void OnPopupClosed()
    {
        base.OnPopupClosed();
        Closed?.Invoke();
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var r = new Rect(0, 0, Width, Height);
        canvas.FillRect(r, t.PopupBackground);
        canvas.DrawBorder(r, t.PopupBorder, canvas.Px(1));
        var font = EffectiveFont;
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var row = ItemRect(i);
            if (item.IsSeparator)
            {
                canvas.FillRect(new Rect(row.X + canvas.Px(8), row.Y + row.Height / 2, row.Width - canvas.Px(16), canvas.Px(1)), t.PanelBorder);
                continue;
            }
            bool enabled = item.Enabled;
            if (i == _hot && enabled) canvas.FillRoundRect(row, canvas.Px(4), t.Selection);
            var color = enabled ? t.Text : t.TextDisabled;
            if (item.Checked)
            {
                int s = canvas.Px(14);
                canvas.DrawCheckMark(new Rect(row.X + canvas.Px(7), row.Y + (row.Height - s) / 2, s, s), color, Math.Max(1.5f, canvas.Px(2)));
            }
            var textRect = new Rect(row.X + canvas.Px(28), row.Y, row.Width - canvas.Px(28) - canvas.Px(24), row.Height);
            canvas.DrawText(item.Caption, font, color, textRect, TextAlign.Left, VerticalAlign.Center);
            if (RadSharp.Shortcut.TryParse(item.Shortcut, out var sc))
                canvas.DrawText(sc.ToString(), font, t.TextDisabled, textRect, TextAlign.Right, VerticalAlign.Center);
            if (item.HasSubmenu)
                canvas.DrawText("›", font, color, new Rect(row.X + row.Width - canvas.Px(20), row.Y, canvas.Px(14), row.Height), TextAlign.Center, VerticalAlign.Center);
        }
    }
}
