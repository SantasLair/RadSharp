using RadSharp.Tests.Forms;

namespace RadSharp.Tests;

/// <summary>
/// M15's done-when, as cut for 1.0: a master/detail form with no copy-in/copy-out code and a Save action enabled by a dirty
/// flag (Forms/ClientsForm), generated from its form file and driven through simulated input and the idle loop.
/// </summary>
[Collection("PlatformHost")]   // raises Application.Idle, which is process-wide
public class BindingMasterDetailTests
{
    static ClientsForm Open()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new ClientsForm();
        f.Resize(640, 420);
        f.NotifyShown();
        Application.RaiseIdle();
        return f;
    }

    static string ProjectDir([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;

    static void ClickRow(Form f, ListBox list, int index)
    {
        var r = list.ItemRect(index);
        f.Simulate.Click(list.AbsoluteLeft + 20, list.AbsoluteTop + r.Y + r.Height / 2);
    }

    [Fact]
    public void Editing_enables_Save_and_saving_writes_the_client_and_refreshes_the_list()
    {
        var f = Open();
        try
        {
            Assert.Equal("Acme Ltd", f.edtName.Text);
            Assert.Equal(("&Save", "Save the client"), (f.btnSave.Caption, f.tbSave.Hint));
            Assert.Equal("Mod+S", f.mnuSave.Shortcut);
            Assert.False(f.btnSave.Enabled || f.tbSave.Enabled || f.mnuSave.Enabled);   // clean: Save is off

            f.Simulate.Click(f.edtName);
            f.edtName.SelectAll();
            f.Simulate.Type("Acme Group");
            Assert.True(f.Binder.IsDirty);
            Application.RaiseIdle();                       // onUpdate: Save follows the dirty flag
            Assert.True(f.btnSave.Enabled && f.tbSave.Enabled && f.mnuSave.Enabled && f.btnRevert.Enabled);
            Assert.Equal("Acme Ltd", f.Clients[0].Name);   // nothing written yet

            f.Simulate.Click(f.btnSave);
            Assert.Equal(1, f.Saves);
            Assert.Equal("Acme Group", f.Clients[0].Name);
            Assert.Equal("Acme Group", f.lstClients.Items[0]);
            Application.RaiseIdle();
            Assert.False(f.btnSave.Enabled || f.tbSave.Enabled);
        }
        finally { f.Close(); }
    }

    [Fact]
    public void Selecting_another_client_shows_it_and_the_shortcut_saves_through_the_action()
    {
        var f = Open();
        try
        {
            ClickRow(f, f.lstClients, 1);
            Assert.Same(f.Clients[1], f.Binder.Current);
            Assert.Equal(("Globex", "120.50", false, 0), (f.edtName.Text, f.edtRate.Text, f.chkActive.Checked, f.cboTerms.ItemIndex));

            f.Simulate.Click(f.chkActive);
            f.Simulate.Click(f.edtRate);
            f.edtRate.SelectAll();
            f.Simulate.Type("99.5");
            // No idle pass since the edit, so Save still looks disabled: the action's shortcut runs onUpdate first, which enables it.
            f.Simulate.Key("Mod+S");
            Assert.Equal(1, f.Saves);
            Assert.Equal((true, 99.5m), (f.Clients[1].Active, f.Clients[1].Rate));
            Assert.Equal("99.50", f.edtRate.Text);
            Assert.False(f.Binder.IsDirty);
        }
        finally { f.Close(); }
    }

    [Fact]
    public void Invalid_input_keeps_the_form_dirty_and_Revert_restores_it()
    {
        var f = Open();
        try
        {
            f.Simulate.Click(f.edtRate);
            f.edtRate.SelectAll();
            f.Simulate.Type("abc");
            Application.RaiseIdle();
            f.Simulate.Click(f.tbSave);
            Assert.Equal(0, f.Saves);
            Assert.True(f.edtRate.Focused);                // FocusFirstError
            Assert.Equal("Enter a number.", f.Binder.Errors.Single().Message);
            Assert.Equal(95m, f.Clients[0].Rate);

            f.Simulate.Key(Key.R, KeyModifiers.Alt);         // &Revert on the linked button
            Assert.Equal("95.00", f.edtRate.Text);
            Assert.False(f.Binder.IsDirty);
        }
        finally { f.Close(); }
    }

    [Fact]
    public void New_and_Delete_through_the_context_menu_and_shortcuts_leave_no_client_disabled_detail()
    {
        var f = Open();
        try
        {
            f.Simulate.Key("Mod+N");                         // an action shortcut with no menu-bar item for it
            Assert.Equal(3, f.Clients.Count);
            Assert.Same(f.Clients[2], f.Binder.Current);
            Assert.True(f.edtName.Focused);

            // Right-click the list, choose Delete (the popup menu item is linked to actDelete).
            ClickRow(f, f.lstClients, 0);
            var row = f.lstClients.ItemRect(0);
            int x = f.lstClients.AbsoluteLeft + 20, y = f.lstClients.AbsoluteTop + row.Y + row.Height / 2;
            f.Simulate.Click(x, y, MouseButton.Right);
            var menu = Assert.IsType<MenuPopup>(f.Popups.Single());
            Assert.Equal(new[] { "&New client", "&Delete" }, menu.Items.Select(i => i.Caption));
            var item = menu.ItemRect(1);
            f.Simulate.MoveTo(menu.Left + item.X + 10, menu.Top + item.Y + 5);
            f.Simulate.Click(menu.Left + item.X + 10, menu.Top + item.Y + 5);
            Assert.Equal(new[] { "Globex", "New client" }, f.Clients.Select(c => c.Name));
            Assert.Same(f.Clients[0], f.Binder.Current);

            f.Simulate.Click(f.lstClients);                  // focus the list so Mod+Delete isn't taken by an edit
            f.Simulate.Key("Mod+Delete");
            f.Simulate.Key("Mod+Delete");
            Assert.Empty(f.Clients);
            Assert.Null(f.Binder.Current);
            Assert.False(f.edtName.Enabled || f.dtpSince.Enabled);   // no client: the detail is disabled
            Application.RaiseIdle();
            Assert.False(f.tbDelete.Enabled);
        }
        finally { f.Close(); }
    }

    [Fact]
    public void The_form_file_round_trips_with_the_actions_and_links()
    {
        // Loaded without its code-behind (as the designer does): the binder's values and the list's items aren't in the file.
        var source = File.ReadAllText(Path.Combine(ProjectDir(), "Forms", "ClientsForm.radform.json")).ReplaceLineEndings("\n");
        var (form, warnings) = FormReader.Load(source);
        Assert.Empty(warnings);
        Assert.Equal("&Save", ((Button)form.FindControl("btnSave")!).Caption);
        Assert.Equal(source, FormWriter.Save(form));
    }
}
