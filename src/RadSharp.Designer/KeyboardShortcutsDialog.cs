using RadSharp.Drawing;

namespace RadSharp.Designer;

/// <summary>
/// Tools › Keyboard Shortcuts: every command with its shortcut. Pick a command, press the new keys in the
/// shortcut box and Assign (a command that had those keys loses them); Remove clears a shortcut, Reset restores
/// the default. Nothing changes until OK (<see cref="Apply"/>).
/// </summary>
public sealed class KeyboardShortcutsDialog : Form
{
    readonly CommandRegistry _registry;
    readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
    readonly Edit _filter;
    readonly ListBox _list;
    readonly ShortcutBox _box;
    readonly Label _status;
    List<Command> _shown = [];

    public KeyboardShortcutsDialog(CommandRegistry registry, Form? owner)
    {
        _registry = registry;
        foreach (var c in registry.All) _pending[c.Id] = c.Shortcut;
        DialogLayout.Setup(this, "Keyboard Shortcuts", 640, 500, owner);
        _filter = DialogLayout.Put(this, new Edit { Name = "edtFilter", Placeholder = "Filter commands" }, 16, 16, 608, 26);
        _list = DialogLayout.Put(this, new ListBox { Name = "lstCommands" }, 16, 50, 608, 300);
        DialogLayout.Put(this, new Label { Caption = "New shortcut" }, 16, 364, 100, 26).Layout = VerticalAlign.Center;
        _box = DialogLayout.Put(this, new ShortcutBox { Name = "edtShortcut" }, 120, 364, 200, 26);
        DialogLayout.Put(this, new Button { Name = "btnAssign", Caption = "Assign" }, 330, 362, 90, 30).Click += _ => Assign();
        DialogLayout.Put(this, new Button { Name = "btnRemove", Caption = "Remove" }, 428, 362, 90, 30).Click += _ => SetPending("");
        DialogLayout.Put(this, new Button { Name = "btnReset", Caption = "Reset" }, 526, 362, 98, 30).Click += _ =>
        {
            if (Selected is { } c) SetPending(c.DefaultShortcut);
        };
        _status = DialogLayout.Put(this, new Label { Name = "lblStatus", FontColor = Drawing.Color.FromHex("#6B7280") }, 16, 402, 608, 24);
        DialogLayout.Put(this, new Button { Name = "btnResetAll", Caption = "Reset All" }, 16, 450, 110, 30).Click += _ =>
        {
            foreach (var c in _registry.All) _pending[c.Id] = c.DefaultShortcut;
            Fill();
        };
        DialogLayout.Put(this, new Button { Name = "btnOk", Caption = "OK", Default = true, ModalResult = ModalResult.Ok }, 440, 450, 86, 30);
        DialogLayout.Put(this, new Button { Name = "btnCancel", Caption = "Cancel", Cancel = true, ModalResult = ModalResult.Cancel }, 538, 450, 86, 30);
        _filter.Change += _ => Fill();
        _list.Change += _ => _status.Caption = Selected is { } c ? $"{c.Id}   (default: {Display(c.DefaultShortcut)})" : "";
        Fill();
        PerformLayout();
    }

    /// <summary>The shortcut each command will have after OK.</summary>
    public IReadOnlyDictionary<string, string> Pending => _pending;

    Command? Selected => _list.ItemIndex >= 0 && _list.ItemIndex < _shown.Count ? _shown[_list.ItemIndex] : null;

    /// <summary>Selects a command's row (clearing the filter if it hides it).</summary>
    public void SelectCommand(string id)
    {
        int i = _shown.FindIndex(c => c.Id == id);
        if (i < 0) { _filter.Text = ""; Fill(); i = _shown.FindIndex(c => c.Id == id); }
        _list.ItemIndex = i;
    }

    static string Display(string shortcut) =>
        shortcut.Length > 0 && Shortcut.TryParse(shortcut, out var s) ? s.ToString() : "(none)";

    void Fill()
    {
        var keep = Selected?.Id;
        var f = _filter.Text.Trim();
        _shown = [.. _registry.All.Where(c => f.Length == 0
            || c.Title.Contains(f, StringComparison.OrdinalIgnoreCase) || c.Category.Contains(f, StringComparison.OrdinalIgnoreCase)
            || Display(_pending[c.Id]).Contains(f, StringComparison.OrdinalIgnoreCase))];
        _list.Items = [.. _shown.Select(c => $"{c.Category} › {c.Title.TrimEnd('…')}    {(_pending[c.Id].Length > 0 ? Display(_pending[c.Id]) : "")}")];
        _list.ItemIndex = keep == null ? -1 : _shown.FindIndex(c => c.Id == keep);
    }

    void Assign()
    {
        if (_box.Shortcut.Length == 0) { _status.Caption = "Press a key combination in the box first."; return; }
        SetPending(_box.Shortcut);
    }

    void SetPending(string shortcut)
    {
        if (Selected is not { } command) { _status.Caption = "Choose a command first."; return; }
        var lost = new List<string>();
        if (shortcut.Length > 0 && Shortcut.TryParse(shortcut, out var key))
            foreach (var other in _registry.All)
                if (other != command && Shortcut.TryParse(_pending[other.Id], out var o) && o == key)
                {
                    _pending[other.Id] = "";
                    lost.Add(other.Title);
                }
        _pending[command.Id] = shortcut;
        Fill();
        _status.Caption = lost.Count > 0 ? $"{Display(shortcut)} was taken from {string.Join(", ", lost)}." : $"{command.Title}: {Display(shortcut)}";
    }

    /// <summary>Applies the pending shortcuts to the registry.</summary>
    public void Apply()
    {
        // Clear first, so swapping two commands' keys doesn't take one from the other.
        foreach (var c in _registry.All)
            if (!string.Equals(CommandRegistry.Normalize(c.Shortcut), CommandRegistry.Normalize(_pending[c.Id]), StringComparison.Ordinal))
                _registry.Rebind(c.Id, "");
        foreach (var c in _registry.All)
            if (_pending[c.Id].Length > 0 && c.Shortcut != CommandRegistry.Normalize(_pending[c.Id]))
                _registry.Rebind(c.Id, _pending[c.Id]);
    }
}

/// <summary>A box that records the next key combination pressed in it (Backspace clears it).</summary>
public sealed class ShortcutBox : Control
{
    string _shortcut = "";

    public ShortcutBox()
    {
        TabStop = true;
        SetDefaultSize(160, 26);
    }

    /// <summary>The recorded shortcut in <see cref="CommandRegistry.Normalize"/> form, or empty.</summary>
    public string Shortcut
    {
        get => _shortcut;
        set { _shortcut = value; Invalidate(); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key is Key.Unknown or Key.Alt) return;
        if (e.Key == Key.Backspace && e.Modifiers == KeyModifiers.None) Shortcut = "";
        else
        {
            var text = CommandRegistry.Format(new RadSharp.Shortcut(e.Key, e.Modifiers));
            if (RadSharp.Shortcut.TryParse(text, out _)) Shortcut = text;
        }
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var r = new Rect(0, 0, Width, Height);
        canvas.FillRect(r, t.FormBackground);
        canvas.DrawBorder(r, Focused ? t.Accent : t.PanelBorder, canvas.Px(1));
        bool empty = _shortcut.Length == 0;
        var text = empty ? "Press a shortcut" : RadSharp.Shortcut.Parse(_shortcut).ToString();
        canvas.DrawText(text, EffectiveFont, empty ? t.TextDisabled : t.Text, new Rect(canvas.Px(6), 0, Width - canvas.Px(12), Height), TextAlign.Left, VerticalAlign.Center);
    }
}
