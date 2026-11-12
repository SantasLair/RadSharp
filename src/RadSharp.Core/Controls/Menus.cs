using System.Text.Json;
using RadSharp.Drawing;

namespace RadSharp;

public delegate void MenuItemEvent(MenuItem sender);

/// <summary>
/// One menu command, separator or submenu. Saved in form files as nested objects under the menu's
/// <c>"items"</c> (spec, Appendix A: caption, shortcut, altShortcut, onClick, enabled, checked, visible,
/// separator, radioItem, items).
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
    /// <summary>Draws the checked state as a filled dot instead of a check mark. The app unchecks the other items of the group.</summary>
    public bool RadioItem { get; set; }
    public bool Visible { get; set; } = true;
    public bool Separator { get; set; }
    /// <summary>The handler name from the form file (kept for the designer and for saving).</summary>
    public string ClickHandler { get; set; } = "";

    public List<MenuItem> Items { get; } = [];

    public bool IsSeparator => Separator || Caption == "-";
    public bool HasSubmenu => Items.Count > 0;

    [Published] public event MenuItemEvent? Click;

    /// <summary>
    /// Raised on a submenu item (top-level or nested) just before its dropdown is built and shown, so an app can
    /// update the children's Enabled / Checked / Caption first.
    /// </summary>
    public event MenuItemEvent? Opening;

    internal void RaiseOpening() => Opening?.Invoke(this);

    /// <summary>The access key marked with <c>&amp;</c> in the caption, upper-cased; null if none.</summary>
    public char? AccessKey
    {
        get
        {
            var text = StripAccessKey(Caption, out int i);
            return i >= 0 ? char.ToUpperInvariant(text[i]) : null;
        }
    }

    /// <summary>The caption without its <c>&amp;</c> markers (the text to measure and draw).</summary>
    public string DisplayText => StripAccessKey(Caption, out _);

    /// <summary>
    /// Removes the access-key markers from a caption: <c>"&amp;File"</c> gives <c>"File"</c> with
    /// <paramref name="index"/> 0 (the character to underline), <c>"&amp;&amp;"</c> is a literal <c>&amp;</c>.
    /// <paramref name="index"/> is -1 when the caption has no access key.
    /// </summary>
    public static string StripAccessKey(string caption, out int index)
    {
        index = -1;
        if (caption.IndexOf('&') < 0) return caption;
        var sb = new System.Text.StringBuilder(caption.Length);
        for (int i = 0; i < caption.Length; i++)
        {
            char c = caption[i];
            if (c != '&') { sb.Append(c); continue; }
            if (i + 1 >= caption.Length) break;                 // a trailing & is dropped
            if (caption[i + 1] == '&') { sb.Append('&'); i++; continue; }
            if (index < 0) index = sb.Length;                   // the next character is the access key
        }
        return sb.ToString();
    }

    /// <summary>Draws a caption with its access key underlined (1 px, just below the baseline), centred or left-aligned in <paramref name="r"/>.</summary>
    internal static void DrawCaption(Canvas canvas, Font font, string caption, Color color, Rect r, TextAlign align)
    {
        var text = StripAccessKey(caption, out int index);
        if (text.Length == 0) return;
        var render = canvas.Resolve(font);
        float width = render.MeasureWidth(text);
        float x = align == TextAlign.Center ? r.X + (r.Width - width) / 2 : r.X;
        float baseline = r.Y + (r.Height - (render.Ascent + render.Descent)) / 2 + render.Ascent;
        canvas.DrawText(text, render, color, x, baseline);
        if (index < 0) return;
        float ux = MathF.Round(x, MidpointRounding.AwayFromZero) + render.MeasureWidth(text[..index]);
        int uw = Math.Max(1, (int)MathF.Round(render.MeasureWidth(text[index].ToString())));
        canvas.FillRect(new Rect((int)MathF.Round(ux), (int)MathF.Round(baseline, MidpointRounding.AwayFromZero) + canvas.Px(1), uw, canvas.Px(1)), color);
    }

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
                    case "radioItem": item.RadioItem = Converters.Bool.Read(p.Value); break;
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
        if (RadioItem) { w.Key("radioItem"); w.WriteBool(true); }
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
    int _hot = -1;                  // the highlighted top-level item in menu mode
    bool _menuMode;
    bool _active;                   // menu mode or a dropdown from the bar is in use: focus is remembered
    bool _switching;                // a dropdown is being replaced by a neighbour: don't end the session
    Control? _restoreFocus;

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
            int w = (int)Math.Ceiling(BarFont.MeasureWidth(item.DisplayText)) + 2 * pad;
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
        BeginSession(form);
        _switching = true;
        form.CloseAllPopups();
        _switching = false;
        var item = rects[index].Item;
        item.RaiseOpening();
        var after = ItemRects();
        var rect = index < after.Count ? after[index].Rect : rects[index].Rect;
        _openIndex = index;
        if (_menuMode) _hot = index;
        var popup = new MenuPopup(item.Items, this, null);
        popup.Closed += () =>
        {
            if (_openIndex == index) { _openIndex = -1; Invalidate(); }
            if (!_switching) EndSession(restoreFocus: true);
        };
        popup.ShowAt(form, AbsoluteLeft + rect.X, AbsoluteTop + Height, this);
        if (selectFirst) popup.SelectFirst();
        Invalidate();
    }

    /// <summary>True while the bar is driven from the keyboard (after Alt or F10): one item is highlighted.</summary>
    public bool InMenuMode => _menuMode;

    /// <summary>The highlighted top-level item in menu mode (an index among the visible items), or -1.</summary>
    public int HotIndex => _menuMode ? _hot : -1;

    /// <summary>Enters menu mode: highlights the first item and takes the keyboard focus (remembered, and given back on exit).</summary>
    public void EnterMenuMode()
    {
        if (_menuMode || OwningForm is not { } form || ItemRects().Count == 0) return;
        form.CloseAllPopups();
        BeginSession(form);
        _menuMode = true;
        _hot = 0;
        if (!form.SetFocus(this)) { EndSession(restoreFocus: false); return; }
        Invalidate();
    }

    /// <summary>Leaves menu mode (closing any dropdown) and gives the focus back to the control that had it.</summary>
    public void ExitMenuMode()
    {
        _switching = false;
        OwningForm?.CloseAllPopups();
        EndSession(restoreFocus: true);
    }

    internal void ToggleMenuMode()
    {
        if (_openIndex >= 0 || _menuMode) ExitMenuMode();
        else EnterMenuMode();
    }

    void BeginSession(Form form)
    {
        if (_active) return;
        _active = true;
        _restoreFocus = form.FocusedControl == this ? null : form.FocusedControl;
    }

    void EndSession(bool restoreFocus)
    {
        if (!_active) return;
        _active = false;
        _menuMode = false;
        _hot = -1;
        var target = _restoreFocus;
        _restoreFocus = null;
        Invalidate();
        if (!restoreFocus || OwningForm is not { } form) return;
        if (target != null && form.SetFocus(target)) return;
        if (form.FocusedControl == this) form.SetFocus(null);
    }

    protected internal override void OnLostFocus()
    {
        base.OnLostFocus();
        if (_menuMode && _openIndex < 0) EndSession(restoreFocus: false);   // focus went elsewhere (e.g. a click)
    }

    int IndexOfAccessKey(char c)
    {
        var rects = ItemRects();
        for (int i = 0; i < rects.Count; i++)
            if (rects[i].Item.Enabled && rects[i].Item.AccessKey == c) return i;
        return -1;
    }

    internal static char? LetterOf(Key key) =>
        key is >= Key.A and <= Key.Z ? (char)('A' + (key - Key.A))
        : key is >= Key.D0 and <= Key.D9 ? (char)('0' + (key - Key.D0)) : null;

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !_menuMode) return;
        int count = ItemRects().Count;
        if (count == 0) { ExitMenuMode(); e.Handled = true; return; }
        if ((e.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Meta)) != 0) return;
        switch (e.Key)
        {
            case Key.Left: _hot = (_hot - 1 + count) % count; Invalidate(); break;
            case Key.Right: _hot = (_hot + 1) % count; Invalidate(); break;
            case Key.Down or Key.Enter or Key.Space: OpenMenu(_hot, selectFirst: true); break;
            case Key.Escape or Key.F10: ExitMenuMode(); break;
            default:
                if (LetterOf(e.Key) is { } c && IndexOfAccessKey(c) is var i and >= 0) { OpenMenu(i, selectFirst: true); break; }
                return;
        }
        e.Handled = true;
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
        if (MenuItem.FindShortcut(Items, e) is { } item)
        {
            OwningForm?.CloseAllPopups();
            return item.PerformClick();
        }
        if (e.Key == Key.F10 && e.Modifiers == KeyModifiers.None) { EnterMenuMode(); return _menuMode; }
        if (e.Modifiers == KeyModifiers.Alt && LetterOf(e.Key) is { } c && IndexOfAccessKey(c) is var i and >= 0)
        {
            OpenMenu(i, selectFirst: true);
            return true;
        }
        return false;
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
            if (i == _openIndex || (_menuMode && i == _hot)) canvas.FillRoundRect(Canvas.Inflate(r, -canvas.Px(2)), canvas.Px(4), t.Selection);
            MenuItem.DrawCaption(canvas, EffectiveFont, item.Caption, item.Enabled ? t.Text : t.TextDisabled, r, TextAlign.Center);
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
            captionW = Math.Max(captionW, (int)Math.Ceiling(font.MeasureWidth(item.DisplayText)));
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
        _items[index].RaiseOpening();
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
            default:
                // A letter runs the item with that access key (with or without Alt).
                if ((e.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Meta)) != 0 || MainMenu.LetterOf(e.Key) is not { } c) return;
                int hit = -1;
                for (int n = 1; n <= _items.Count && hit < 0; n++)
                {
                    int i = (_hot + n + _items.Count) % _items.Count;
                    if (Selectable(i) && _items[i].AccessKey == c) hit = i;
                }
                if (hit < 0) return;
                Activate(hit);
                break;
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
                var mark = new Rect(row.X + canvas.Px(7), row.Y + (row.Height - s) / 2, s, s);
                if (item.RadioItem)
                {
                    int d = canvas.Px(7);
                    canvas.FillEllipse(new Rect(mark.X + (s - d) / 2, mark.Y + (s - d) / 2, d, d), color);
                }
                else canvas.DrawCheckMark(mark, color, Math.Max(1.5f, canvas.Px(2)));
            }
            var textRect = new Rect(row.X + canvas.Px(28), row.Y, row.Width - canvas.Px(28) - canvas.Px(24), row.Height);
            MenuItem.DrawCaption(canvas, font, item.Caption, color, textRect, TextAlign.Left);
            if (RadSharp.Shortcut.TryParse(item.Shortcut, out var sc))
                canvas.DrawText(sc.ToString(), font, t.TextDisabled, textRect, TextAlign.Right, VerticalAlign.Center);
            if (item.HasSubmenu)
                canvas.DrawText("›", font, color, new Rect(row.X + row.Width - canvas.Px(20), row.Y, canvas.Px(14), row.Height), TextAlign.Center, VerticalAlign.Center);
        }
    }
}
