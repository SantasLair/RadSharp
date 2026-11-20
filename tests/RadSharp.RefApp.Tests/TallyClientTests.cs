using static RadSharp.RefApp.LiveTally;

namespace RadSharp.RefApp;

/// <summary>
/// Workflow 2 (docs/reference-app-scope.md §1.6): the client detail pane under the client list, used through simulated
/// input on the F9-run main window. Add a client, edit, Save (Mod+S); Revert discards; switching to another client with
/// unsaved changes asks first (Cancel stays, No discards, Yes saves); the list's context menu deletes with confirmation.
/// </summary>
public class TallyClientTests
{
    static string Field(Form live, string name) => Get<Control>(live, name) switch
    {
        Edit e => e.Text,
        Memo m => m.Text,
        CheckBox c => c.Checked ? "true" : "false",
        var c => throw new InvalidOperationException($"{c.Name} isn't a field."),
    };

    [Fact]
    public void Workflow_2_add_edit_save_revert_and_switching_asks_first()
    {
        var (d, live) = RunTally();
        var clients = Get<ListView>(live, "lvClients");
        var status = Get<StatusBar>(live, "sbMain");
        var save = Get<Button>(live, "btnSaveClient");
        var revert = Get<Button>(live, "btnRevertClient");
        var pane = Get<Panel>(live, "pnlClient");

        // Nothing selected yet: the pane is off.
        Assert.False(pane.Enabled);
        Assert.Equal(-1, clients.ItemIndex);

        // Client › New Client (Mod+N): an empty pane, the caret in Name.
        live.Simulate.Key("Mod+N");
        Assert.True(pane.Enabled);
        Assert.Same(Get<Edit>(live, "edtName"), live.FocusedControl);
        Assert.False(save.Enabled);
        live.Simulate.Type("Zelda Quint");
        Assert.True(save.Enabled);
        Assert.True(revert.Enabled);
        DesignerDriver.TypeInto(live, live.FindControl("edtCompany"), "Quint Labs");
        DesignerDriver.TypeInto(live, live.FindControl("edtEmail"), "zelda@quint.example");
        DesignerDriver.TypeInto(live, live.FindControl("edtCity"), "Lisbon");
        DesignerDriver.TypeInto(live, live.FindControl("edtCountry"), "Portugal");
        DesignerDriver.TypeInto(live, live.FindControl("memNotes"), "Pays on time.");
        RefApp.Render(live, "MainForm.run.client-new@100%");

        // Save (Mod+S): the client joins the list, selected; the commands calm down.
        live.Simulate.Key("Mod+S");
        Assert.Equal("10,001 of 10,001 clients", status.SimpleText);
        Assert.Equal(10_000, clients.ItemIndex);
        Assert.Equal(["Zelda Quint", "Quint Labs", "zelda@quint.example", "", "Lisbon"], Enumerable.Range(0, 5).Select(c => clients.Selected!.Text(c)));
        Assert.False(save.Enabled);
        Assert.False(revert.Enabled);
        Assert.Equal("Invoices", Get<Label>(live, "lblClientInvoices").Caption);

        // Revert: an edit, then the button puts the saved value back.
        DesignerDriver.TypeInto(live, live.FindControl("edtCity"), "Porto");
        Assert.True(save.Enabled);
        ClickButton(live, "btnRevertClient");
        Assert.Equal("Lisbon", Field(live, "edtCity"));
        Assert.False(save.Enabled);

        // Switching with unsaved changes asks. Cancel: the selection goes back and the edit stays.
        DesignerDriver.TypeInto(live, live.FindControl("edtPhone"), "+351 555 0101");
        int other = clients.TopIndex + 1;
        Assert.NotEqual(10_000, other);
        string? question = null;
        d.Host.Expect("Save changes", f => { question = DriverHost.Describe(f); return "btnCancel"; });
        ClickRow(live, clients, other);
        Assert.Equal(0, d.Host.PendingModals);
        Assert.StartsWith("Save changes to Zelda Quint?", question);
        Assert.Equal(10_000, clients.ItemIndex);
        Assert.Equal("+351 555 0101", Field(live, "edtPhone"));
        RefApp.Render(live, "MainForm.run.client-dirty@100%");

        // No: the other client is shown; Zelda's phone was never saved.
        d.Host.Expect("Save changes", _ => "btnNo");
        ClickRow(live, clients, other);
        Assert.Equal(other, clients.ItemIndex);
        string otherName = clients.Selected!.Text(0);
        Assert.Equal(otherName, Field(live, "edtName"));
        Assert.False(save.Enabled);

        // Yes: an edit of the other client is saved before Zelda is shown again.
        DesignerDriver.TypeInto(live, live.FindControl("edtCity"), "Faro");
        d.Host.Expect("Save changes", _ => "btnYes");
        ClickRow(live, clients, other);                  // the same row: focuses the list, asks nothing
        Assert.Equal(1, d.Host.PendingModals);
        live.Simulate.Key(Key.End);                      // to the last row, Zelda's
        Assert.Equal(0, d.Host.PendingModals);
        Assert.Equal("Zelda Quint", Field(live, "edtName"));
        Assert.Equal("", Field(live, "edtPhone"));
        Assert.Equal(10_000, clients.ItemIndex);
        ClickRow(live, clients, other);
        Assert.Equal("Faro", Field(live, "edtCity"));
        Assert.Equal("Faro", clients.Selected!.Text(4));
    }

    [Fact]
    public void A_new_client_needs_a_name_and_switching_away_from_an_untouched_one_doesnt_ask()
    {
        var (d, live) = RunTally();
        var clients = Get<ListView>(live, "lvClients");
        ClickButton(live, "btnNewClient");   // the toolbar button does what the menu item does
        Assert.True(Get<Panel>(live, "pnlClient").Enabled);

        // Untouched: picking a client doesn't ask (an unexpected dialog would fail the test).
        ClickRow(live, clients, clients.TopIndex);
        Assert.Equal(clients.Selected!.Text(0), Field(live, "edtName"));

        // A new client with only an email can't be saved: a warning, and nothing is added.
        live.Simulate.Key("Mod+N");
        DesignerDriver.TypeInto(live, live.FindControl("edtEmail"), "nobody@example.com");
        string? warning = null;
        d.Host.Expect("Warning", f => { warning = DriverHost.Describe(f); return "btnOk"; });
        ClickButton(live, "btnSaveClient");
        Assert.StartsWith("A client needs a name.", warning);
        Assert.Equal("10,000 of 10,000 clients", Get<StatusBar>(live, "sbMain").SimpleText);
        Assert.Same(Get<Edit>(live, "edtName"), live.FocusedControl);

        // Leaving it with Yes tries to save, fails the same way, and stays.
        d.Host.Expect("Save changes", _ => "btnYes");
        d.Host.Expect("Warning", _ => "btnOk");
        ClickRow(live, clients, clients.TopIndex + 2);
        Assert.Equal(-1, clients.ItemIndex);
        Assert.Equal("nobody@example.com", Field(live, "edtEmail"));
    }

    [Fact]
    public void The_client_lists_context_menu_deletes_a_client_and_their_invoices_after_asking()
    {
        var (d, live) = RunTally();
        var clients = Get<ListView>(live, "lvClients");
        var status = Get<StatusBar>(live, "sbMain");

        // Right-click selects the row, then opens the menu; the client's invoices show beside the pane.
        int row = clients.TopIndex + 3;
        ClickRow(live, clients, row, MouseButton.Right);
        Assert.Equal(row, clients.ItemIndex);
        var name = clients.Selected!.Text(0);
        var invoices = Get<ListView>(live, "lvClientInvoices");
        int count = invoices.ItemCount;
        Assert.Equal(count == 0 ? "Invoices" : $"Invoices ({count})", Get<Label>(live, "lblClientInvoices").Caption);
        RefApp.Render(live, "MainForm.run.client-menu@100%");

        // No keeps the client.
        string? question = null;
        d.Host.Expect("Delete", f => { question = DriverHost.Describe(f); return "btnNo"; });
        ClickPopupItem(live, "Delete Client…");
        Assert.StartsWith($"Delete {name}", question);
        Assert.Equal("10,000 of 10,000 clients", status.SimpleText);

        // Yes deletes it; the pane empties and switches off.
        ClickRow(live, clients, row, MouseButton.Right);
        d.Host.Expect("Delete", _ => "btnYes");
        ClickPopupItem(live, "Delete Client…");
        Assert.Equal("9,999 of 9,999 clients", status.SimpleText);
        Assert.False(Get<Panel>(live, "pnlClient").Enabled);
        Assert.Equal("", Field(live, "edtName"));
        Assert.Equal(-1, clients.ItemIndex);

        // The context menu's New Client is the menu's command.
        ClickRow(live, clients, row, MouseButton.Right);
        ClickPopupItem(live, "New Client");
        Assert.Equal("", Field(live, "edtName"));
        Assert.True(Get<Panel>(live, "pnlClient").Enabled);
    }
}
