using System.Text.Json;

namespace RadSharp.Tests;

/// <summary>The Action component (docs/binding-and-actions.md, section 2), driven through simulated input.</summary>
[Collection("PlatformHost")]   // the idle tests raise Application.Idle, which is process-wide
public class ActionTests
{
    // Clients come before the actions in the file, so names are resolved after loading, as in any designed form.
    const string Json = """
        { "format": "radform", "version": "1.0", "root": { "class": "Form", "name": "frmActions", "width": 400, "height": 300,
          "children": [
            { "class": "MainMenu", "name": "mnuMain", "items": [
              { "caption": "&File", "items": [ { "name": "mnuSave", "action": "actSave" }, { "name": "mnuSaveAs", "caption": "Save &as", "action": "actSave" } ] } ] },
            { "class": "ToolBar", "name": "tbMain", "children": [ { "class": "ToolButton", "name": "tbBold", "action": "actBold" } ] },
            { "class": "Button", "name": "btnSave", "action": "actSave", "left": 10, "top": 80 },
            { "class": "Button", "name": "btnOwn", "caption": "Keep", "action": "actSave", "left": 100, "top": 80 },
            { "class": "Action", "name": "actSave", "caption": "&Save", "hint": "Save it", "shortcut": "Mod+S", "imageIndex": 2, "left": 10, "top": 200 },
            { "class": "Action", "name": "actBold", "caption": "&Bold", "autoCheck": true, "checked": true, "left": 50, "top": 200 }
          ] } }
        """;

    internal static Form Load(string json = Json)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var (form, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);
        return form;
    }

    internal static T Find<T>(Form f, string name) where T : Control => Assert.IsAssignableFrom<T>(f.FindControl(name));

    static MenuItem Item(Form f, int top, int sub) => Find<MainMenu>(f, "mnuMain").Items[top].Items[sub];

    [Fact]
    public void Loading_links_by_name_copies_the_action_and_keeps_values_the_file_sets()
    {
        var f = Load();
        var save = Find<ActionItem>(f, "actSave");
        var btn = Find<Button>(f, "btnSave");
        Assert.Same(save, btn.Action);
        Assert.Equal(("&Save", "Save it", 2), (btn.Caption, btn.Hint, btn.ImageIndex));
        var own = Find<Button>(f, "btnOwn");
        Assert.Equal(("Keep", "Save it"), (own.Caption, own.Hint));          // the file's caption wins over the action's
        Assert.Equal(("&Save", "Mod+S"), (Item(f, 0, 0).Caption, Item(f, 0, 0).Shortcut));
        Assert.Equal(("Save &as", "Mod+S"), (Item(f, 0, 1).Caption, Item(f, 0, 1).Shortcut));
        Assert.True(Find<ToolButton>(f, "tbBold").Down);                       // checked -> down
        Assert.Equal(4, save.Clients.Count);
    }

    [Fact]
    public void Values_that_come_from_the_action_are_not_saved_and_the_file_round_trips()
    {
        var f = Load();
        var saved = FormWriter.Save(f);
        Assert.Equal(saved, FormWriter.Save(Load(saved)));
        using var doc = JsonDocument.Parse(saved);
        var children = doc.RootElement.GetProperty("root").GetProperty("children");
        JsonElement Node(string name) => children.EnumerateArray().First(c => c.GetProperty("name").GetString() == name);
        var btn = Node("btnSave");
        Assert.Equal("actSave", btn.GetProperty("action").GetString());
        Assert.False(btn.TryGetProperty("caption", out _));
        Assert.False(btn.TryGetProperty("hint", out _));
        Assert.False(btn.TryGetProperty("imageIndex", out _));
        Assert.Equal("Keep", Node("btnOwn").GetProperty("caption").GetString());
        var items = Node("mnuMain").GetProperty("items")[0].GetProperty("items");
        Assert.Equal(new[] { "name", "action" }, items[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("Save &as", items[1].GetProperty("caption").GetString());
        Assert.False(items[1].TryGetProperty("shortcut", out _));
    }

    [Fact]
    public void Later_changes_reach_controls_whose_value_still_matches_the_action()
    {
        var f = Load();
        var save = Find<ActionItem>(f, "actSave");
        var btn = Find<Button>(f, "btnSave");
        var own = Find<Button>(f, "btnOwn");
        save.Caption = "S&tore";
        Assert.Equal(("S&tore", "Keep"), (btn.Caption, own.Caption));
        Assert.Equal("S&tore", Item(f, 0, 0).Caption);
        Assert.Equal("Save &as", Item(f, 0, 1).Caption);

        save.Enabled = false;
        Assert.False(btn.Enabled || own.Enabled || Item(f, 0, 0).Enabled || Item(f, 0, 1).Enabled);
        save.Visible = false;
        Assert.False(btn.Visible);
        save.Visible = true;
        btn.Hint = "Mine";                // the button no longer follows the hint
        save.Hint = "Write it";
        Assert.Equal(("Mine", "Write it"), (btn.Hint, own.Hint));
    }

    [Fact]
    public void Clicking_a_linked_button_or_choosing_a_menu_item_runs_the_action()
    {
        var f = Load();
        var save = Find<ActionItem>(f, "actSave");
        var log = new List<object?>();
        save.Executed += a => log.Add(((ActionItem)a).ActionComponent);
        var btn = Find<Button>(f, "btnSave");

        f.Simulate.Click(btn);
        f.Simulate.Key("Mod+S");                          // the menu bar matches the item's (copied) shortcut first
        Assert.Equal(new object?[] { btn, Item(f, 0, 0) }, log);

        var own = Find<Button>(f, "btnOwn");
        int ownClicks = 0;
        own.Click += _ => ownClicks++;                    // code subscribed to Click: that runs instead (Delphi's rule)
        f.Simulate.Click(own);
        Assert.Equal((2, 1), (log.Count, ownClicks));
        Assert.Null(save.ActionComponent);
    }

    [Fact]
    public void A_disabled_action_disables_its_controls_and_its_shortcut()
    {
        var f = Load();
        var save = Find<ActionItem>(f, "actSave");
        int runs = 0;
        save.Executed += _ => runs++;
        save.Enabled = false;
        f.Simulate.Click(Find<Button>(f, "btnSave"));
        f.Simulate.Key("Mod+S");
        Assert.False(save.Execute());
        Assert.Equal(0, runs);
    }

    [Fact]
    public void Action_shortcuts_work_without_a_menu_even_when_hidden_and_respect_onUpdate()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form();
        f.SetBounds(0, 0, 300, 200);
        var edit = new Edit();
        edit.SetBounds(10, 10, 100, 24);
        var act = new ActionItem { Name = "actNew", Shortcut = "Mod+N", Visible = false };
        f.Add(edit);
        f.Add(act);
        f.PerformLayout();
        edit.Focus();
        int runs = 0;
        bool allowed = true;
        act.Executed += _ => runs++;
        act.Update += _ => act.Enabled = allowed;

        f.Simulate.Key("Mod+N");
        Assert.Equal(1, runs);
        allowed = false;                               // onUpdate runs before a shortcut executes
        f.Simulate.Key("Mod+N");
        Assert.Equal(1, runs);
        Assert.False(act.Enabled);
    }

    [Fact]
    public void AutoCheck_toggles_checked_and_the_tool_button_follows()
    {
        var f = Load();
        var bold = Find<ActionItem>(f, "actBold");
        var tb = Find<ToolButton>(f, "tbBold");
        bold.Executed += _ => { };
        tb.AllowToggle = true;   // ignored while linked: Down follows the action
        f.Simulate.Click(tb);
        Assert.False(bold.Checked);
        Assert.False(tb.Down);
        f.Simulate.Click(tb);
        Assert.True(bold.Checked && tb.Down);
        Assert.Equal("&Bold", tb.Caption);
    }

    [Fact]
    public void OnUpdate_runs_while_idle_for_shown_forms_only_and_never_asks_for_more()
    {
        var f = Load();
        var save = Find<ActionItem>(f, "actSave");
        bool dirty = false;
        int updates = 0;
        save.Update += _ => { updates++; save.Enabled = dirty; };
        try
        {
            Assert.False(Application.RaiseIdle());
            Assert.Equal(0, updates);                       // not shown yet

            f.NotifyShown();
            Assert.False(Application.RaiseIdle());          // Done stays true: the host may sleep
            Assert.Equal(1, updates);
            Assert.False(Find<Button>(f, "btnSave").Enabled);
            dirty = true;
            Application.RaiseIdle();
            Assert.True(Find<Button>(f, "btnSave").Enabled && Item(f, 0, 0).Enabled);
        }
        finally { f.Close(); }
        Application.RaiseIdle();
        Assert.Equal(2, updates);                           // closed: no more updates
    }

    [Fact]
    public void An_exception_in_one_onUpdate_is_reported_and_the_others_still_update()
    {
        var f = Load();
        var errors = new List<Exception>();
        void Handler(object? s, ThreadExceptionEventArgs e) { errors.Add(e.Exception); e.Handled = true; }
        Application.ThreadException += Handler;
        try
        {
            Find<ActionItem>(f, "actSave").Update += _ => throw new InvalidOperationException("boom");
            bool boldUpdated = false;
            Find<ActionItem>(f, "actBold").Update += _ => boldUpdated = true;
            f.UpdateActions();
            Assert.True(boldUpdated);
            Assert.Equal("boom", Assert.Single(errors).Message);
        }
        finally { Application.ThreadException -= Handler; }
    }

    [Fact]
    public void Linking_from_code_copies_everything_and_unlinking_keeps_the_values()
    {
        var f = Load();
        var save = Find<ActionItem>(f, "actSave");
        var b = new Button { Caption = "Old", Hint = "Old hint" };
        f.Add(b);
        b.ActionName = "actSave";                           // on the form already: like setting it in the Object Inspector
        Assert.Equal(("&Save", "Save it"), (b.Caption, b.Hint));
        b.ActionName = "";
        Assert.Null(b.Action);
        save.Caption = "Changed";
        Assert.Equal("&Save", b.Caption);

        // An explicit link to an action on another form survives that form's link walks.
        var other = new Form();
        var shared = new ActionItem { Name = "actShared", Caption = "Shared" };
        other.Add(shared);
        var c = new Button();
        f.Add(c);
        c.Action = shared;
        f.Resize(500, 300);
        Assert.Same(shared, c.Action);
        shared.Caption = "Still shared";
        Assert.Equal("Still shared", c.Caption);
    }

    [Fact]
    public void Removing_renaming_and_re_adding_the_action()
    {
        var f = Load();
        var save = Find<ActionItem>(f, "actSave");
        var btn = Find<Button>(f, "btnSave");
        save.Name = "actStore";
        Assert.Equal("actStore", btn.ActionName);           // the link follows the rename
        f.Remove(save);
        Assert.Null(btn.Action);
        Assert.Equal("&Save", btn.Caption);                 // keeps what it shows, and the name
        Assert.Equal("actStore", btn.ActionName);
        Assert.DoesNotContain(btn, save.Clients);
        f.Add(save);
        Assert.Same(save, btn.Action);
    }

    [Fact]
    public void Object_inspector_action_row_offers_the_forms_actions()
    {
        var designed = Load();
        var target = new Button { Name = "btnNew" };
        designed.Add(target);
        var host = new Form();
        host.SetBounds(0, 0, 600, 700);
        var grid = new PropertyGrid();
        grid.SetBounds(10, 10, 300, 660);
        host.Add(grid);
        host.PerformLayout();
        grid.Target = target;

        int row = grid.IndexOf("action");
        Assert.True(row >= 0);
        grid.BeginEdit(row);
        Assert.Equal(2, host.Popups.Count);                   // the names under a text box (pick or type)
        var list = Assert.IsType<ListBox>(host.Popups[0]);
        Assert.IsAssignableFrom<Edit>(host.Popups[1]);
        Assert.Equal(new[] { PropertyGrid.NoReference, "actBold", "actSave" }, list.Items);
        var r = list.ItemRect(2);
        host.Simulate.MoveTo(list.Left + 10, list.Top + r.Y + 5);
        host.Simulate.Click(list.Left + 10, list.Top + r.Y + 5);
        Assert.Empty(host.Popups);                            // picking closes the list and the text box
        Assert.Equal("actSave", target.ActionName);
        Assert.Equal("&Save", target.Caption);
        Assert.Equal("actSave", grid.RowValue(row));

        grid.BeginEdit(row);
        list = Assert.IsType<ListBox>(host.Popups[0]);
        host.Simulate.MoveTo(list.Left + 10, list.Top + list.ItemRect(0).Y + 5);
        host.Simulate.Click(list.Left + 10, list.Top + list.ItemRect(0).Y + 5);
        Assert.Equal("", target.ActionName);
        Assert.Null(target.Action);
    }
}

/// <summary>Action captions with mnemonics. Separate from <see cref="ActionTests"/> because <see cref="Mnemonics.Enabled"/> is
/// process-wide and off by default on macOS, so it is turned on only inside the "Mnemonics" collection (see MnemonicTests).</summary>
[Collection("Mnemonics")]
public class ActionMnemonicTests : IDisposable
{
    readonly bool _wasEnabled = Mnemonics.Enabled;

    public ActionMnemonicTests() => Mnemonics.Enabled = true;

    public void Dispose() => Mnemonics.Enabled = _wasEnabled;

    [Fact]
    public void A_mnemonic_in_the_action_caption_works_through_the_linked_button()
    {
        var f = ActionTests.Load();
        int runs = 0;
        ActionTests.Find<ActionItem>(f, "actSave").Executed += _ => runs++;
        f.Simulate.Key(Key.S, KeyModifiers.Alt);
        Assert.Equal(1, runs);
        Assert.True(ActionTests.Find<Button>(f, "btnSave").Focused);
    }
}
