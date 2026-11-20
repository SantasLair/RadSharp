namespace RadSharp;

// The form's part in actions (docs/binding-and-actions.md): resolving "action" names to links, action shortcuts,
// and running onUpdate while the application is idle.
public partial class Form
{
    List<ActionItem>? _actions;
    bool _actionLinksDirty = true;
    int _actionVersion = -1;
    int _actionStamp;

    // Shown forms that have actions: updated while the application is idle. Weak, so a form whose window the host took away
    // without telling it (a torn-down live-run session) isn't kept alive, and with it the collectible load context of its type.
    static readonly List<WeakReference<Form>> ShownForms = [];
    int _uiThread = Environment.CurrentManagedThreadId;   // the thread that shows the form owns it (set again on Shown)
    internal int ActionListBuilds;   // how often the action list was rebuilt (a test counts it)
    static bool _idleHooked;
    bool _windowShown, _idleTracked;

    /// <summary>Called from the constructor: a shown form with actions takes part in the idle update.</summary>
    void InitActions()
    {
        Shown += _ => { _uiThread = Environment.CurrentManagedThreadId; _windowShown = true; HostClosed = false; ForEachActionSlot(this, s => s.Resume()); SyncIdleTracking(); };
        Closed += _ => NotifyHostClosed();
    }

    /// <summary>
    /// Called by the host when it has taken the form's window away (also when it did so without <see cref="Close"/>, e.g. the
    /// host shutting down): the form stops taking part in the idle update and is no longer referenced by it.
    /// </summary>
    public void NotifyHostClosed()
    {
        _windowShown = false;
        HostClosed = true;
        ForEachActionSlot(this, s => s.Suspend());   // links made from code stop holding this form's buttons and menu items
        SyncIdleTracking();
    }

    /// <summary>True from the host taking the window away until the form is shown again: explicit action links are suspended.</summary>
    internal bool HostClosed { get; private set; }

    /// <summary>A control (and its subtree) was added to this form: its code links to actions are live again.</summary>
    internal void ControlAdded(Control child)
    {
        if (ActionSlot.Used && !HostClosed) ForEachActionSlot(child, s => s.Resume());
    }

    /// <summary>A control (and its subtree) left this form: its code links to actions stop listing it as a client.</summary>
    internal void ControlRemoved(Control child)
    {
        if (ActionSlot.Used) ForEachActionSlot(child, s => s.Suspend());
    }

    /// <summary>Visits the action slot of every button and menu item at or below <paramref name="root"/>.</summary>
    static void ForEachActionSlot(Control root, Action<ActionSlot> visit)
    {
        if (!ActionSlot.Used) return;
        Visit(root);

        void Visit(Control c)
        {
            if (c is Button { PeekActionSlot: { } slot }) visit(slot);
            if (c is IMenuOwner menu)
                foreach (var item in menu.Items) VisitItem(item);
            foreach (var child in c.Children) Visit(child);
        }

        void VisitItem(MenuItem item)
        {
            if (item.PeekActionSlot is { } slot) visit(slot);
            foreach (var sub in item.Items) VisitItem(sub);
        }
    }

    /// <summary>Whether idle updates apply now: shown, visible and not blocked by a modal form (Delphi doesn't update disabled windows).</summary>
    bool TakesIdleUpdates => _windowShown && Visible && !(PlatformHost.Current?.IsFormBlocked(this) ?? false);

    void SyncIdleTracking()
    {
        // Tracked whenever shown and actions are in use; whether this form has any is looked at lazily, in the idle pass, so
        // adding controls never rescans the tree here.
        bool track = _windowShown && ActionSlot.Used;
        _idleTracked = track;
        lock (ShownForms)   // UI code is single-threaded, but test runners show forms from several threads
        {
            ShownForms.RemoveAll(w => !w.TryGetTarget(out var f) || f == this);
            if (track) ShownForms.Add(new WeakReference<Form>(this));
            if (ShownForms.Count > 0 && !_idleHooked) { Application.Idle += OnApplicationIdle; _idleHooked = true; }
            else if (ShownForms.Count == 0 && _idleHooked) { Application.Idle -= OnApplicationIdle; _idleHooked = false; }
        }
    }

    /// <summary>Runs onUpdate for the actions of every shown, visible form that no modal form blocks. Never asks for another idle call at once (no busy loop).</summary>
    static void OnApplicationIdle(object? sender, IdleEventArgs e)
    {
        var forms = new List<Form>();
        lock (ShownForms)
        {
            ShownForms.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in ShownForms)
                if (w.TryGetTarget(out var f)) forms.Add(f);
            if (ShownForms.Count == 0 && _idleHooked) { Application.Idle -= OnApplicationIdle; _idleHooked = false; }
        }
        int thread = Environment.CurrentManagedThreadId;
        foreach (var form in forms)   // UI-thread-only: a form made on another thread is that thread's to update
            if (form._uiThread == thread && form.TakesIdleUpdates && form.ActionList.Count > 0) form.UpdateActions();
    }

    /// <summary>Called when controls are added or removed anywhere on the form.</summary>
    void ActionTreeChanged()
    {
        _actions = null;
        _actionLinksDirty = true;
        if (_windowShown && ActionSlot.Used && !_idleTracked) SyncIdleTracking();   // the first action may just have been added
    }

    /// <summary>The form's actions, in tree order.</summary>
    List<ActionItem> ActionList => _actions ??= BuildActionList();

    List<ActionItem> BuildActionList()
    {
        ActionListBuilds++;
        return [.. Descendants().OfType<ActionItem>()];
    }

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

    /// <summary>A shortcut nothing else took: runs the first enabled action on this form with that shortcut that has a handler.</summary>
    bool ProcessActionShortcut(KeyEventArgs e)
    {
        if (!ActionSlot.Used) return false;
        EnsureActionLinks();
        foreach (var action in ActionList.ToArray())
        {
            if (!action.MatchesShortcut(e)) continue;
            action.UpdateState();
            if (!action.Enabled) continue;
            if (!action.Execute(null, update: false)) continue;   // onUpdate just ran above; no handler: the key isn't taken (Delphi's IsShortCut)
            e.Handled = true;
            return true;
        }
        return false;
    }
}
