using System.Text.Json;
using System.Text.Json.Nodes;

namespace RadSharp.Designer;

/// <summary>
/// One IDE action (Delphi's <c>TAction</c>): an id, a title, a category, a default shortcut, enabled/checked
/// predicates and a handler. Menus, tool buttons and context menus are views of commands; they never carry
/// their own logic.
/// </summary>
public sealed class Command
{
    readonly Action<object?> _execute;

    public Command(string id, string title, string category, Action execute, string defaultShortcut = "",
        Func<bool>? canExecute = null, Func<bool>? isChecked = null)
        : this(id, title, category, _ => execute(), defaultShortcut, canExecute, isChecked) { }

    /// <summary>A command whose handler takes a parameter (the recent project of File › Reopen, the tab a context
    /// menu was opened on); without one it acts on the current state.</summary>
    public Command(string id, string title, string category, Action<object?> execute, string defaultShortcut = "",
        Func<bool>? canExecute = null, Func<bool>? isChecked = null)
    {
        Id = id;
        Title = title;
        Category = category;
        _execute = execute;
        DefaultShortcut = defaultShortcut;
        CanExecute = canExecute;
        IsChecked = isChecked;
    }

    /// <summary>Stable identifier, e.g. <c>File.Save</c>; the key of keymap overrides.</summary>
    public string Id { get; }
    public string Title { get; }
    public string Category { get; }
    public string DefaultShortcut { get; }
    /// <summary>A second, fixed shortcut (Ctrl+Y beside Mod+Shift+Z for Redo); not rebindable.</summary>
    public string AltShortcut { get; init; } = "";
    public Func<bool>? CanExecute { get; }
    public Func<bool>? IsChecked { get; }

    /// <summary>The shortcut in effect: the user's binding, or <see cref="DefaultShortcut"/>. Empty for none.</summary>
    public string Shortcut { get; internal set; } = "";

    public bool Enabled => CanExecute?.Invoke() ?? true;
    public bool Checked => IsChecked?.Invoke() ?? false;

    /// <summary>Runs the handler if the command is enabled; returns whether it ran.</summary>
    public bool Execute(object? parameter = null)
    {
        if (!Enabled) return false;
        _execute(parameter);
        return true;
    }

    public override string ToString() => $"{Id} ({Title})";
}

/// <summary>
/// The IDE's commands and its keymap. Every menu item and tool button is created or bound here, so it resolves
/// to a command (<see cref="CommandOf(MenuItem)"/>); shortcuts are matched here (the form offers each shortcut
/// candidate to <see cref="ProcessShortcut"/> before any menu sees it) and can be rebound and persisted as a
/// small JSON keymap of overrides (<see cref="SaveKeymap"/>).
/// </summary>
public sealed class CommandRegistry
{
    readonly Dictionary<string, Command> _byId = new(StringComparer.Ordinal);
    readonly List<Command> _order = [];
    readonly Dictionary<string, string> _overrides = new(StringComparer.Ordinal);
    readonly Dictionary<MenuItem, (string Id, object? Parameter)> _menuItems = [];
    readonly Dictionary<Control, string> _controls = [];

    public IReadOnlyList<Command> All => _order;

    /// <summary>User bindings that differ from the defaults: command id → shortcut text ("" = no shortcut).</summary>
    public IReadOnlyDictionary<string, string> Overrides => _overrides;

    /// <summary>Raised after a shortcut was rebound or reset (menus refresh their shortcut text).</summary>
    public event Action? ShortcutsChanged;

    public Command Register(Command command)
    {
        if (_byId.ContainsKey(command.Id)) throw new InvalidOperationException($"Command {command.Id} is already registered.");
        _byId[command.Id] = command;
        _order.Add(command);
        command.Shortcut = _overrides.TryGetValue(command.Id, out var s) ? s : command.DefaultShortcut;
        return command;
    }

    public Command? Find(string id) => _byId.GetValueOrDefault(id);

    public Command this[string id] => _byId.TryGetValue(id, out var c) ? c : throw new KeyNotFoundException($"No command {id}.");

    public bool Execute(string id, object? parameter = null) => Find(id)?.Execute(parameter) ?? false;

    // ---- Shortcuts ----

    /// <summary>
    /// Binds <paramref name="shortcut"/> (e.g. <c>"Mod+Shift+K"</c>; empty removes the shortcut) to a command.
    /// Any other command that had the same key loses it. Returns the commands that lost it.
    /// </summary>
    public IReadOnlyList<Command> Rebind(string id, string shortcut)
    {
        var command = this[id];
        shortcut = Normalize(shortcut);
        var losers = new List<Command>();
        if (shortcut.Length > 0)
        {
            var key = RadSharp.Shortcut.Parse(shortcut);
            foreach (var other in _order)
                if (other != command && SameKey(other.Shortcut, key))
                {
                    SetShortcut(other, "");
                    losers.Add(other);
                }
        }
        SetShortcut(command, shortcut);
        ShortcutsChanged?.Invoke();
        return losers;
    }

    /// <summary>Restores a command's default shortcut.</summary>
    public void ResetShortcut(string id)
    {
        var command = this[id];
        _overrides.Remove(id);
        command.Shortcut = command.DefaultShortcut;
        ShortcutsChanged?.Invoke();
    }

    /// <summary>Restores every default shortcut.</summary>
    public void ResetAllShortcuts()
    {
        _overrides.Clear();
        foreach (var c in _order) c.Shortcut = c.DefaultShortcut;
        ShortcutsChanged?.Invoke();
    }

    void SetShortcut(Command command, string shortcut)
    {
        command.Shortcut = shortcut;
        if (shortcut == command.DefaultShortcut || (SameText(shortcut, command.DefaultShortcut))) _overrides.Remove(command.Id);
        else _overrides[command.Id] = shortcut;
    }

    static bool SameText(string a, string b) =>
        RadSharp.Shortcut.TryParse(a, out var x) && RadSharp.Shortcut.TryParse(b, out var y) && x == y;

    static bool SameKey(string text, Shortcut key) => RadSharp.Shortcut.TryParse(text, out var s) && s == key;

    /// <summary>Canonical text for a shortcut: modifiers in a fixed order, the platform command key as <c>Mod</c>
    /// (so a keymap moves between Windows and macOS). Throws <see cref="FormatException"/> for invalid text.</summary>
    public static string Normalize(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return "";
        return Format(RadSharp.Shortcut.Parse(shortcut));
    }

    /// <summary>Canonical text for a key combination (see <see cref="Normalize"/>).</summary>
    public static string Format(Shortcut s)
    {
        var parts = new List<string>();
        var mods = s.Modifiers;
        var cmd = RadSharp.Shortcut.CommandModifier;
        if ((mods & cmd) == cmd && cmd != KeyModifiers.None) { parts.Add("Mod"); mods &= ~cmd; }
        if (mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Meta");
        // The key's own name, as Shortcut prints it (A, F9, Del, PgUp, -), then made parseable.
        var display = new Shortcut(s.Key, KeyModifiers.None).ToString();
        parts.Add(display);
        return string.Join("+", parts);
    }

    /// <summary>The enabled command bound to the key press, if any.</summary>
    public Command? FindByShortcut(KeyEventArgs e)
    {
        foreach (var c in _order)
            if ((Matches(c.Shortcut, e) || Matches(c.AltShortcut, e)) && c.Enabled) return c;
        return null;
    }

    static bool Matches(string shortcut, KeyEventArgs e) => RadSharp.Shortcut.TryParse(shortcut, out var s) && s.Matches(e);

    /// <summary>Runs the command bound to a key press; true if one ran.</summary>
    public bool ProcessShortcut(KeyEventArgs e) => FindByShortcut(e)?.Execute() ?? false;

    // ---- Menus and controls ----

    /// <summary>A menu item for a command: its caption (the command's title unless given), shortcut text,
    /// enabled/checked state (see <see cref="Refresh(IEnumerable{MenuItem})"/>) and a click that runs it.</summary>
    public MenuItem CreateMenuItem(string id, string? caption = null, object? parameter = null)
    {
        var command = this[id];
        var item = new MenuItem(caption ?? command.Title) { Name = "" };
        item.Click += _ => command.Execute(parameter);
        _menuItems[item] = (id, parameter);
        Refresh(item);
        return item;
    }

    /// <summary>The command id a menu item runs; null for separators, submenus and items made elsewhere.</summary>
    public string? CommandOf(MenuItem item) => _menuItems.TryGetValue(item, out var b) ? b.Id : null;

    /// <summary>Makes a button (a tool button, a welcome-page button) run a command, and keeps its Enabled in step.</summary>
    public void Bind(Button button, string id)
    {
        var command = this[id];
        button.Click += _ => command.Execute();
        _controls[button] = id;
        if (button.Hint.Length == 0) button.Hint = HintFor(command);
    }

    static string HintFor(Command c) =>
        c.Shortcut.Length > 0 && RadSharp.Shortcut.TryParse(c.Shortcut, out var s) ? $"{c.Title} ({s})" : c.Title;

    public string? CommandOf(Control control) => _controls.GetValueOrDefault(control);

    /// <summary>Re-reads enabled/checked state and shortcut text for menu items (recursively).</summary>
    public void Refresh(IEnumerable<MenuItem> items)
    {
        foreach (var item in items)
        {
            Refresh(item);
            if (item.HasSubmenu) Refresh(item.Items);
        }
    }

    void Refresh(MenuItem item)
    {
        if (!_menuItems.TryGetValue(item, out var b) || Find(b.Id) is not { } c) return;
        item.Enabled = c.Enabled;
        item.Checked = c.Checked;
        item.Shortcut = c.Shortcut;
        item.AltShortcut = c.AltShortcut;
    }

    /// <summary>Updates bound buttons' Enabled from their commands.</summary>
    public void RefreshControls()
    {
        foreach (var (control, id) in _controls)
        {
            control.Enabled = Find(id)?.Enabled ?? false;
            if (control is Button b && Find(id) is { } c) b.Hint = HintFor(c);
        }
    }

    // ---- Keymap file ----

    /// <summary>Reads keymap overrides (<c>{"shortcuts": {"File.Save": "Mod+S"}}</c>); unknown ids are kept for
    /// commands registered later; invalid shortcuts are skipped. A missing or broken file means defaults.</summary>
    public void LoadKeymap(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path))?["shortcuts"] is not JsonObject map) return;
            foreach (var (id, value) in map)
            {
                var text = value?.GetValue<string>() ?? "";
                try { text = Normalize(text); }
                catch (FormatException) { continue; }
                _overrides[id] = text;
                if (Find(id) is { } c) c.Shortcut = text;
            }
            ShortcutsChanged?.Invoke();
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or FormatException) { }
    }

    public void SaveKeymap(string path)
    {
        var map = new JsonObject();
        foreach (var (id, s) in _overrides.OrderBy(p => p.Key, StringComparer.Ordinal)) map[id] = s;
        var root = new JsonObject { ["shortcuts"] = map };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
