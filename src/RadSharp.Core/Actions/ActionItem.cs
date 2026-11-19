namespace RadSharp;

/// <summary>
/// A command shared by buttons, tool buttons and menu items (a light Delphi <c>TAction</c>; docs/binding-and-actions.md).
/// It is a non-visual component on the form, like a <see cref="Timer"/>. A control links to it by name (its
/// <c>"action"</c> member, like <c>"popupMenu"</c>) or from code (<see cref="Button.Action"/>, <see cref="MenuItem.Action"/>):
/// the control then shows the action's caption, hint, enabled, checked and visible state and image, follows later changes to
/// them, and clicking it runs <see cref="Executed"/> (<c>onExecute</c>). <see cref="Update"/> (<c>onUpdate</c>) runs while
/// the application is idle, for actions on forms that are shown, so it can set <see cref="Enabled"/> or <see cref="Checked"/>
/// from the application's state in one place.
/// </summary>
/// <remarks>
/// The C# type is <c>ActionItem</c> because <c>RadSharp.Action</c> would clash with <see cref="System.Action"/> in every file
/// that has <c>using RadSharp;</c>. Form files, the palette and the component registry still call it <c>Action</c>, as Delphi's
/// <c>TAction</c> shows as Action: <see cref="ComponentAttribute"/> publishes the class under that name, and the form generator
/// emits <c>global::RadSharp.ActionItem</c> fields for <c>"class": "Action"</c> nodes.
/// </remarks>
[Component("Action")]
public class ActionItem : Component
{
    string _caption = "", _hint = "", _shortcut = "";
    bool _enabled = true, _checked, _visible = true;
    int _imageIndex = -1;
    readonly List<ActionLink> _links = [];

    public ActionItem() => ActionSlot.Used = true;

    /// <summary>The caption linked controls show; <c>&amp;</c> marks a mnemonic, which works through the linked buttons and menu items.</summary>
    [Published(Category = "Action")]
    public string Caption { get => _caption; set => Set(ActionMember.Caption, ref _caption, value ?? ""); }

    /// <summary>The tooltip of linked buttons and tool buttons.</summary>
    [Published("hint", Category = "Action")]
    public new string Hint { get => _hint; set => Set(ActionMember.Hint, ref _hint, value ?? ""); }

    /// <summary>
    /// Shortcut text such as <c>"Mod+S"</c> or <c>"F5"</c> (spec, Appendix C). Pressing it runs the action when no control or
    /// menu bar took the key (also while the action is hidden); linked menu items show it.
    /// </summary>
    [Published(Category = "Action")]
    public string Shortcut { get => _shortcut; set => Set(ActionMember.Shortcut, ref _shortcut, value ?? ""); }

    /// <summary>Whether the command can run now; linked controls are enabled and disabled with it.</summary>
    [Published("enabled", Category = "Action")]
    public new bool Enabled { get => _enabled; set => Set(ActionMember.Enabled, ref _enabled, value); }

    /// <summary>A toggle's state: linked menu items show a check mark and tool buttons stay down.</summary>
    [Published(Category = "Action")]
    public bool Checked { get => _checked; set => Set(ActionMember.Checked, ref _checked, value); }

    /// <summary>Whether linked controls are shown. A hidden action's shortcut still works.</summary>
    [Published("visible", Category = "Action")]
    public new bool Visible { get => _visible; set => Set(ActionMember.Visible, ref _visible, value); }

    /// <summary>The picture linked buttons show from their own (or their tool bar's) image list; -1 shows none.</summary>
    [Published(Category = "Action")]
    public int ImageIndex { get => _imageIndex; set => Set(ActionMember.ImageIndex, ref _imageIndex, value); }

    /// <summary>Running the action toggles <see cref="Checked"/> first (Delphi's <c>AutoCheck</c>).</summary>
    [Published(Category = "Action")] public bool AutoCheck { get; set; }

    /// <summary>The command itself (<c>onExecute</c>). The sender is the action; <see cref="ActionComponent"/> is what was clicked.</summary>
    [Published("onExecute")] public event NotifyEvent? Executed;

    /// <summary>
    /// Raised while the application is idle, for an action on a shown form (<c>onUpdate</c>), and just before the action runs:
    /// set <see cref="Enabled"/>, <see cref="Checked"/> or <see cref="Caption"/> from the application's state here. Keep it cheap.
    /// </summary>
    [Published("onUpdate")] public event NotifyEvent? Update;

    /// <summary>While <see cref="Executed"/> runs: the button or <see cref="MenuItem"/> that ran it, or null (a shortcut, or code).</summary>
    public object? ActionComponent { get; private set; }

    /// <summary>The buttons and menu items linked to this action now.</summary>
    public IReadOnlyList<object> Clients => [.. _links.Select(l => l.Client)];

    /// <summary>
    /// Runs the command: raises <see cref="Update"/>, then, if the action is enabled, toggles <see cref="Checked"/> when
    /// <see cref="AutoCheck"/> is set and raises <see cref="Executed"/>. Returns false if the action is disabled or has no
    /// <see cref="Executed"/> handler.
    /// </summary>
    public bool Execute() => Execute(null);

    /// <param name="invoker">The button or menu item that ran it, or null.</param>
    /// <param name="update">False when <see cref="Update"/> just ran (a shortcut checks the state first), so it doesn't run twice.</param>
    internal bool Execute(object? invoker, bool update = true)
    {
        if (update) UpdateState();
        if (!_enabled) return false;
        if (AutoCheck) Checked = !_checked;
        if (Executed is not { } handler) return false;
        var previous = ActionComponent;
        ActionComponent = invoker;
        try { handler(this); }
        finally { ActionComponent = previous; }
        return true;
    }

    /// <summary>Raises <see cref="Update"/> now. Returns false if it has no handler.</summary>
    public bool UpdateState()
    {
        if (Update is not { } handler) return false;
        handler(this);
        return true;
    }

    internal bool HasUpdateHandler => Update != null;

    internal bool MatchesShortcut(KeyEventArgs e) => RadSharp.Shortcut.TryParse(_shortcut, out var s) && s.Matches(e);

    // ---- Links ----

    internal object? Get(ActionMember m) => m switch
    {
        ActionMember.Caption => _caption,
        ActionMember.Hint => _hint,
        ActionMember.Shortcut => _shortcut,
        ActionMember.Enabled => _enabled,
        ActionMember.Checked => _checked,
        ActionMember.Visible => _visible,
        ActionMember.ImageIndex => _imageIndex,
        _ => null,
    };

    void Set<TValue>(ActionMember m, ref TValue field, TValue value)
    {
        if (EqualityComparer<TValue>.Default.Equals(field, value)) return;
        object? old = field;
        field = value;
        // Delphi's rule: a client follows the change only while its value still equals the action's (it is "linked").
        foreach (var link in _links.ToArray())
            if (link.Supports(m) && Equals(link.Get(m), old)) link.Set(m, value);
    }

    internal void AddLink(ActionLink link)
    {
        if (!_links.Contains(link)) _links.Add(link);
    }

    internal void RemoveLink(ActionLink link) => _links.Remove(link);

    /// <summary>Drops name-resolved links whose client wasn't found on the form in walk <paramref name="stamp"/> (it was removed).</summary>
    internal void SweepLinks(int stamp)
    {
        for (int i = _links.Count - 1; i >= 0; i--)
            if (!_links[i].Explicit && _links[i].Stamp != stamp) _links.RemoveAt(i);
    }
}

/// <summary>The properties an action shares with its linked controls.</summary>
internal enum ActionMember { Caption, Hint, Shortcut, Enabled, Checked, Visible, ImageIndex }

/// <summary>One client (button or menu item) linked to an action: reads and writes the client's side of each member.</summary>
internal abstract class ActionLink(ActionItem action)
{
    static readonly ActionMember[] Members =
    [
        ActionMember.Caption, ActionMember.Hint, ActionMember.Shortcut, ActionMember.Enabled,
        ActionMember.Checked, ActionMember.Visible, ActionMember.ImageIndex,
    ];

    public ActionItem Action { get; } = action;
    public abstract object Client { get; }

    /// <summary>Linked from code (by object): kept even when the action is on another form, until unlinked from code.</summary>
    public bool Explicit { get; set; }

    /// <summary>The form walk that last found this link's client (see <see cref="ActionItem.SweepLinks"/>).</summary>
    public int Stamp { get; set; }

    public abstract bool Supports(ActionMember m);
    public abstract object? Get(ActionMember m);
    public abstract void Set(ActionMember m, object? value);

    /// <summary>Whether the client's value is its class default (so a link made while loading may replace it).</summary>
    public abstract bool IsDefault(ActionMember m);

    /// <summary>True if the client's value for <paramref name="m"/> comes from the action: it equals the action's.</summary>
    public bool IsLinked(ActionMember m) => Supports(m) && Equals(Get(m), Action.Get(m));

    /// <summary>The members that were linked when the link was suspended; null while the link is live.</summary>
    ActionMember[]? _suspendedLinked;

    public bool IsSuspended => _suspendedLinked != null;

    /// <summary>Remembers which members follow the action, for <see cref="Restore"/>.</summary>
    public void Snapshot() => _suspendedLinked = [.. Members.Where(IsLinked)];

    /// <summary>Copies the action's current value to the members that were linked at <see cref="Snapshot"/>; the client's overrides stay.</summary>
    public void Restore()
    {
        var linked = _suspendedLinked;
        _suspendedLinked = null;
        if (linked == null) return;
        foreach (var m in linked) Set(m, Action.Get(m));
    }

    /// <summary>
    /// Copies the action's values to the client. With <paramref name="onlyDefaults"/> (a link made while a form loads) a value
    /// the form file set explicitly is kept, as Delphi does when loading.
    /// </summary>
    public void CopyFromAction(bool onlyDefaults)
    {
        foreach (var m in Members)
            if (Supports(m) && (!onlyDefaults || IsDefault(m))) Set(m, Action.Get(m));
    }
}

/// <summary>
/// The action side of a client: its <c>"action"</c> name and, once resolved, its link. Shared by <see cref="Button"/> and
/// <see cref="MenuItem"/>.
/// </summary>
internal sealed class ActionSlot(Func<ActionItem, ActionLink> createLink)
{
    string _name = "";
    ActionItem? _explicit;   // the action given from code: kept while the client is off a form, linked only while it is on an open one
    bool _keepOwnValues;   // the pending name came from a form file: the file's explicit values win when it resolves

    /// <summary>Set once any action exists or any client names one: until then forms skip the link walk entirely.</summary>
    internal static bool Used;

    /// <summary>Bumped whenever a name is set that can't be resolved yet, so every form walks its links again.</summary>
    internal static int PendingVersion;

    public ActionLink? Link { get; private set; }

    public string Name => Link?.Action.Name ?? _explicit?.Name ?? _name;

    public ActionItem? Action => Link?.Action ?? _explicit;

    /// <summary>
    /// Sets the name; links at once when <paramref name="form"/> has that action, else when the form next walks its links.
    /// Set from code the client adopts every value of the action; with <paramref name="loading"/> (a form file is being read)
    /// the values the file set explicitly are kept and only defaults come from the action, as Delphi does when loading.
    /// </summary>
    public void SetName(string? name, Form? form, bool loading = false)
    {
        name ??= "";
        if (Link != null && Link.Action.Name == name) return;
        Unlink();
        _name = name;
        _explicit = null;
        _keepOwnValues = loading;
        if (name.Length == 0) return;
        Used = true;
        if (form?.FindControl(name) is ActionItem a) Attach(a, explicitLink: false, onlyDefaults: loading);
        else Interlocked.Increment(ref PendingVersion);
    }

    /// <summary>Links to <paramref name="action"/> from code (copying every value), or unlinks with null.</summary>
    public void SetAction(ActionItem? action)
    {
        if (action == null) { Unlink(); _explicit = null; _name = ""; return; }   // also cancels a name that never resolved
        if (_explicit == action && Link?.Action == action) return;
        Unlink();
        _explicit = action;
        _name = action.Name;
        Attach(action, explicitLink: true, onlyDefaults: false);
    }

    /// <summary>
    /// The client left its form's tree or the form closed: an explicit (code) link is taken off the action's client list, so the
    /// action doesn't keep a dead or hidden client alive and doesn't write to it (Delphi's <c>FreeNotification</c>). The
    /// reference and the link object stay (with a note of which members followed the action), so <see cref="Resume"/> links
    /// again when the client is back on an open form.
    /// </summary>
    public void Suspend()
    {
        if (Link is not { Explicit: true, IsSuspended: false } link) return;
        link.Snapshot();
        link.Action.RemoveLink(link);
    }

    /// <summary>
    /// The client is on an open form again: the link rejoins the action, and only the members that followed it take its current
    /// value, so a caption or other value the client was given meanwhile is kept.
    /// </summary>
    public void Resume()
    {
        if (Link is not { IsSuspended: true } link) return;
        link.Restore();
        link.Action.AddLink(link);
    }

    void Attach(ActionItem action, bool explicitLink, bool onlyDefaults)
    {
        var link = createLink(action);
        link.Explicit = explicitLink;
        Link = link;
        action.AddLink(link);
        link.CopyFromAction(onlyDefaults);
    }

    void Unlink()
    {
        if (Link == null) return;
        if (Link.Explicit) _explicit = null;
        _name = Link.Action.Name;
        Link.Action.RemoveLink(Link);
        Link = null;
    }

    /// <summary>Part of a form's link walk: keeps a valid link, drops one to an action that left the form, resolves a pending name.</summary>
    public void Resolve(Form form, int stamp)
    {
        if (_explicit != null)   // a code link: not name-resolved; it follows its client on and off open forms
        {
            if (!form.HostClosed) Resume();
            if (Link is { IsSuspended: false }) { Link.Stamp = stamp; Link.Action.AddLink(Link); }
            return;
        }
        if (Link != null)
        {
            if (Link.Explicit || Link.Action.OwningForm == form)
            {
                Link.Stamp = stamp;
                Link.Action.AddLink(Link);
                return;
            }
            Unlink();
        }
        if (_name.Length > 0 && form.FindControl(_name) is ActionItem a)
        {
            Attach(a, explicitLink: false, onlyDefaults: _keepOwnValues);
            Link!.Stamp = stamp;
        }
    }
}
