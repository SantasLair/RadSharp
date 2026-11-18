namespace RadSharp;

// The form's part in actions (docs/binding-and-actions.md): resolving "action" names to links, action shortcuts,
// and running onUpdate while the application is idle.
public partial class Form
{
    List<ActionItem>? _actions;
    bool _actionLinksDirty = true;
    int _actionVersion = -1;
    int _actionStamp;

    static readonly List<Form> ShownForms = [];   // shown forms that have actions: updated while the application is idle
    static bool _idleHooked;
    bool _windowShown;

    /// <summary>Called from the constructor: a shown form with actions takes part in the idle update.</summary>
    void InitActions()
    {
        Shown += _ => { _windowShown = true; SyncIdleTracking(); };
        Closed += _ => NotifyHostClosed();
    }

    /// <summary>
    /// Called by the host when it has taken the form's window away (also when it did so without <see cref="Close"/>, e.g. the
    /// host shutting down): the form stops taking part in the idle update and is no longer referenced by it.
    /// </summary>
    public void NotifyHostClosed()
    {
        _windowShown = false;
        SyncIdleTracking();
    }

    /// <summary>Whether idle updates apply now: shown, visible and not blocked by a modal form (Delphi doesn't update disabled windows).</summary>
    bool TakesIdleUpdates => _windowShown && Visible && !(PlatformHost.Current?.IsFormBlocked(this) ?? false);

    void SyncIdleTracking()
    {
        bool track = _windowShown && ActionSlot.Used && ActionList.Count > 0;
        lock (ShownForms)   // UI code is single-threaded, but test runners show forms from several threads
        {
            if (track) { if (!ShownForms.Contains(this)) ShownForms.Add(this); }
            else ShownForms.Remove(this);
            if (ShownForms.Count > 0 && !_idleHooked) { Application.Idle += OnApplicationIdle; _idleHooked = true; }
            else if (ShownForms.Count == 0 && _idleHooked) { Application.Idle -= OnApplicationIdle; _idleHooked = false; }
        }
    }

    /// <summary>Runs onUpdate for the actions of every shown, visible form that no modal form blocks. Never asks for another idle call at once (no busy loop).</summary>
    static void OnApplicationIdle(object? sender, IdleEventArgs e)
    {
        Form[] forms;
        lock (ShownForms) forms = [.. ShownForms];
        foreach (var form in forms)
            if (form.TakesIdleUpdates) form.UpdateActions();
    }

    /// <summary>Called when controls are added or removed anywhere on the form.</summary>
    void ActionTreeChanged()
    {
        _actions = null;
        _actionLinksDirty = true;
        if (_windowShown && ActionSlot.Used) SyncIdleTracking();   // an action may have been added or removed
    }

    /// <summary>The form's actions, in tree order.</summary>
    List<ActionItem> ActionList => _actions ??= [.. Descendants().OfType<ActionItem>()];

    /// <summary>
    /// Runs <see cref="ActionItem.Update"/> (onUpdate) for every action on this form, as the application does while idle
    /// for shown forms (Delphi's <c>UpdateActions</c>). An exception in one handler is reported and the others still run.
    /// </summary>
    public void UpdateActions()
    {
        EnsureActionLinks();
        foreach (var action in ActionList.ToArray())
        {
            if (!action.HasUpdateHandler) continue;
            try { action.UpdateState(); }
            catch (Exception ex) { Application.ReportException(ex); }
        }
    }

    /// <summary>
    /// Links controls and menu items to the actions they name, if anything changed since the last walk: a control or action was
    /// added or removed, or a name was set that couldn't be resolved then. Cheap when nothing changed.
    /// </summary>
    internal void EnsureActionLinks()
    {
        if (!ActionSlot.Used) return;
        if (!_actionLinksDirty && _actionVersion == ActionSlot.PendingVersion) return;
        _actionLinksDirty = false;
        _actionVersion = ActionSlot.PendingVersion;
        int stamp = ++_actionStamp;
        Walk(this);
        foreach (var action in ActionList) action.SweepLinks(stamp);

        void Walk(Control c)
        {
            foreach (var child in c.Children)
            {
                if (child is Button b) b.ResolveAction(this, stamp);
                if (child is IMenuOwner menu)
                    foreach (var item in menu.Items) item.ResolveAction(this, stamp);
                Walk(child);
            }
        }
    }

    /// <summary>A shortcut nothing else took: runs the first enabled action on this form with that shortcut.</summary>
    bool ProcessActionShortcut(KeyEventArgs e)
    {
        if (!ActionSlot.Used) return false;
        EnsureActionLinks();
        foreach (var action in ActionList.ToArray())
        {
            if (!action.MatchesShortcut(e)) continue;
            action.UpdateState();
            if (!action.Enabled) continue;
            action.Execute(null, update: false);   // onUpdate just ran above
            e.Handled = true;
            return true;
        }
        return false;
    }
}
