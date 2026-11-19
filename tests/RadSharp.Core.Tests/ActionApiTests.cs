using System.Runtime.CompilerServices;

namespace RadSharp.Tests;

/// <summary>
/// M15's pre-1.0 changes to actions: the ActionItem type published as "Action", explicit reference properties in the
/// inspector (pick or type), a linked menu item repainting, the shortcut's single onUpdate, and which forms get idle updates.
/// </summary>
[Collection("PlatformHost")]   // the idle tests raise Application.Idle, which is process-wide
public class ActionApiTests
{
    const string Json = """
        { "format": "radform", "version": "1.0", "root": { "class": "Form", "name": "frmActions", "width": 400, "height": 300,
          "children": [
            { "class": "Action", "name": "actSave", "caption": "&Save", "shortcut": "Mod+S", "left": 10, "top": 200 }
          ] } }
        """;

    static Form Load(string json = Json)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var (form, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);
        return form;
    }

    static T Find<T>(Form f, string name) where T : Control => Assert.IsAssignableFrom<T>(f.FindControl(name));

    // ---- The type and its form-file name ----

    [Fact]
    public void The_class_is_ActionItem_in_C_and_Action_in_the_registry_and_form_files()
    {
        var cls = ComponentRegistry.Find("Action")!;
        Assert.Equal(typeof(ActionItem), cls.Type);
        Assert.Same(cls, ComponentRegistry.Find(typeof(ActionItem)));
        Assert.Equal("Action", ComponentRegistry.Of(new ActionItem()).Name);
        Assert.Null(ComponentRegistry.Find("ActionItem"));
        Assert.Contains(ComponentRegistry.All, c => c.Name == "Action");
        var saved = FormWriter.Save(Load());
        Assert.Contains("\"class\": \"Action\"", saved);
        Assert.DoesNotContain("ActionItem", saved);
    }

    [Fact]
    public void A_generated_form_has_ActionItem_fields_for_Action_nodes()
    {
        var f = new Forms.ClientsForm();
        ActionItem save = f.actSave;                          // typed by the form generator
        Assert.Equal("actSave", save.Name);
    }

    // ---- Reference properties ----

    [Fact]
    public void Reference_properties_are_marked_explicitly_not_found_by_name()
    {
        var button = ComponentRegistry.Find("Button")!;
        Assert.Equal(typeof(ActionItem), button.FindProperty("action")!.ReferenceType);
        Assert.Equal(typeof(ImageList), button.FindProperty("images")!.ReferenceType);
        Assert.Equal(typeof(PopupMenu), button.FindProperty("popupMenu")!.ReferenceType);
        Assert.Null(button.FindProperty("caption")!.ReferenceType);
        var marked = ComponentRegistry.All.SelectMany(c => c.OwnProperties).Where(p => p.ReferenceType != null).ToList();
        Assert.NotEmpty(marked);
        Assert.All(marked, p => Assert.Equal(PropertyType.String, p.Type));
        Assert.All(marked, p => Assert.Contains(p.Name, new[] { "action", "images", "popupMenu" }));
    }

    static (Form Host, PropertyGrid Grid, Form Designed, Button Target) InspectorOnLoadedForm()
    {
        var designed = Load();
        var target = new Button { Name = "btnNew" };
        designed.Add(target);
        designed.Add(new ImageList { Name = "imlIcons" });
        designed.Add(new PopupMenu { Name = "pmFile" });
        var host = new Form();
        host.SetBounds(0, 0, 600, 700);
        var grid = new PropertyGrid();
        grid.SetBounds(10, 10, 300, 660);
        host.Add(grid);
        host.PerformLayout();
        grid.Target = target;
        return (host, grid, designed, target);
    }

    [Fact]
    public void Object_inspector_reference_row_lets_the_user_type_a_name_that_is_not_on_the_form_yet()
    {
        var (host, grid, designed, target) = InspectorOnLoadedForm();
        int row = grid.IndexOf("action");
        grid.BeginEdit(row);
        Assert.Equal(2, host.Popups.Count);                   // the list under a text box: pick or type
        host.Simulate.Type("actLater");
        host.Simulate.Key(Key.Enter);
        Assert.Empty(host.Popups);
        Assert.Equal("actLater", target.ActionName);
        Assert.Equal("actLater", grid.RowValue(row));
        Assert.Null(target.Action);                           // not there yet

        var later = new ActionItem { Name = "actLater", Caption = "Later" };
        designed.Add(later);
        Assert.Same(later, target.Action);                    // resolved once it exists

        grid.BeginEdit(row);                                  // the text box starts with the current name
        host.Simulate.Key(Key.Escape);
        Assert.Empty(host.Popups);
        Assert.Equal("actLater", target.ActionName);

        grid.BeginEdit(row);
        host.Simulate.Type("ignored");
        host.Simulate.Key(Key.Escape);                        // Escape drops what was typed
        Assert.Empty(host.Popups);
        Assert.Equal("actLater", target.ActionName);

        grid.BeginEdit(row);                                  // an emptied text box clears the reference
        host.Simulate.Key(Key.A, KeyModifiers.Ctrl);
        host.Simulate.Key(Key.Delete);
        host.Simulate.Key(Key.Enter);
        Assert.Equal("", target.ActionName);
        Assert.Null(target.Action);
    }

    [Fact]
    public void Clicking_outside_a_reference_editor_keeps_what_was_typed_and_closes_both_popups()
    {
        var (host, grid, _, target) = InspectorOnLoadedForm();
        grid.BeginEdit(grid.IndexOf("action"));
        host.Simulate.Type("actTyped");
        host.Simulate.Click(500, 650);
        Assert.Empty(host.Popups);
        Assert.Equal("actTyped", target.ActionName);
    }

    [Fact]
    public void Reference_rows_offer_the_kind_the_property_is_marked_with_and_other_string_rows_stay_plain_text_boxes()
    {
        var (host, grid, _, _) = InspectorOnLoadedForm();
        grid.BeginEdit(grid.IndexOf("images"));
        Assert.Equal(new[] { PropertyGrid.NoReference, "imlIcons" }, Assert.IsType<ListBox>(host.Popups[0]).Items);
        grid.EndEdit(commit: false);
        Assert.Empty(host.Popups);

        grid.BeginEdit(grid.IndexOf("popupMenu"));
        Assert.Equal(new[] { PropertyGrid.NoReference, "pmFile" }, Assert.IsType<ListBox>(host.Popups[0]).Items);
        grid.EndEdit(commit: false);
        Assert.Empty(host.Popups);

        grid.BeginEdit(grid.IndexOf("caption"));              // not a reference: just a text box
        Assert.IsAssignableFrom<Edit>(host.Popups.Single());
    }

    // ---- A linked menu item repaints ----

    const string MenuOnlyJson = """
        { "format": "radform", "version": "1.0", "root": { "class": "Form", "name": "frmMenu", "width": 300, "height": 200,
          "children": [
            { "class": "MainMenu", "name": "mnuMain", "items": [ { "caption": "&File", "items": [ { "name": "mnuSave", "action": "actSave" } ] } ] },
            { "class": "Action", "name": "actSave", "caption": "&Save", "left": 10, "top": 100 }
          ] } }
        """;

    [Theory]
    [InlineData("caption")]
    [InlineData("enabled")]
    [InlineData("checked")]
    [InlineData("visible")]
    [InlineData("shortcut")]
    public void A_linked_menu_item_repaints_the_menu_when_the_action_changes_it(string member)
    {
        var f = Load(MenuOnlyJson);
        f.PerformLayout();
        var save = Find<ActionItem>(f, "actSave");
        var bar = Find<MainMenu>(f, "mnuMain");
        Assert.NotNull(bar.Items[0].Items[0].Action);         // linked (the walk ran with the layout)
        bar.OpenMenu(0);                                      // the dropdown is open too
        Assert.NotEmpty(f.Popups);
        f.TryTakeDirtyRect(out _);                            // painted: clean
        Assert.False(f.TryTakeDirtyRect(out _));

        switch (member)
        {
            case "caption": save.Caption = "&Store"; break;
            case "enabled": save.Enabled = false; break;
            case "checked": save.Checked = true; break;
            case "visible": save.Visible = false; break;
            case "shortcut": save.Shortcut = "Mod+Q"; break;
        }
        Assert.True(f.TryTakeDirtyRect(out _));
    }

    // ---- Shortcut and onUpdate ----

    [Fact]
    public void A_shortcut_runs_onUpdate_once()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form();
        f.SetBounds(0, 0, 300, 200);
        var act = new ActionItem { Name = "actGo", Shortcut = "Mod+G" };
        f.Add(act);
        f.PerformLayout();
        int updates = 0, runs = 0;
        act.Update += _ => updates++;
        act.Executed += _ => runs++;
        f.Simulate.Key("Mod+G");
        Assert.Equal((1, 1), (updates, runs));

        Assert.True(act.Execute());                           // running it from code also updates once
        Assert.Equal((2, 2), (updates, runs));

        act.Update += _ => act.Enabled = false;               // onUpdate disables it: one update, no run
        f.Simulate.Key("Mod+G");
        Assert.Equal((3, 2), (updates, runs));
    }

    // ---- Idle updates: which forms take part ----

    /// <summary>A host that only answers whether a form is blocked by a modal one.</summary>
    sealed class ModalHost : IPlatformServices
    {
        public readonly HashSet<Form> Blocked = [];
        public void ShowForm(Form form) => form.NotifyShown();
        public void RunModal(Form form) { }
        public void CloseForm(Form form) { }
        public void ProcessEventsUntil(Func<bool> done) { }
        public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) => done(null);
        public void PostToUiThread(Action action) => action();
        public void ShowModal(Form form, Action onClosed) { }
        public bool IsFormBlocked(Form form) => Blocked.Contains(form);
    }

    static (Form Form, Func<int> Updates) ShownFormWithUpdates()
    {
        var f = Load();
        int updates = 0;
        Find<ActionItem>(f, "actSave").Update += _ => updates++;
        f.NotifyShown();
        return (f, () => updates);
    }

    [Fact]
    public void A_hidden_form_gets_no_idle_updates_until_it_is_visible_again()
    {
        var (f, updates) = ShownFormWithUpdates();
        try
        {
            Application.RaiseIdle();
            Assert.Equal(1, updates());
            f.Visible = false;
            Application.RaiseIdle();
            Assert.Equal(1, updates());
            f.Visible = true;
            Application.RaiseIdle();
            Assert.Equal(2, updates());
        }
        finally { f.Close(); }
    }

    [Fact]
    public void A_form_the_host_closed_without_Close_stops_updating()
    {
        var (f, updates) = ShownFormWithUpdates();
        Application.RaiseIdle();
        Assert.Equal(1, updates());
        f.NotifyHostClosed();                                 // what SdlHost.CloseForm and the host shutdown do
        Application.RaiseIdle();
        Assert.Equal(1, updates());
    }

    [Fact]
    public void A_form_below_a_modal_form_gets_no_idle_updates()
    {
        var old = PlatformHost.Current;
        var host = new ModalHost();
        PlatformHost.Current = host;
        var (f, updates) = ShownFormWithUpdates();
        try
        {
            Application.RaiseIdle();
            Assert.Equal(1, updates());
            host.Blocked.Add(f);                              // a modal dialog opened over it
            Application.RaiseIdle();
            Assert.Equal(1, updates());
            host.Blocked.Clear();                             // closed again
            Application.RaiseIdle();
            Assert.Equal(2, updates());
        }
        finally
        {
            f.Close();
            PlatformHost.Current = old;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference ShowAndLetGo(bool hostClose)
    {
        var (f, _) = ShownFormWithUpdates();
        Application.RaiseIdle();
        if (hostClose) f.NotifyHostClosed();
        else f.Close();
        return new WeakReference(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_idle_list_does_not_keep_a_closed_form_alive(bool closedByHost)
    {
        var weak = ShowAndLetGo(closedByHost);
        for (int i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive);
    }
}
