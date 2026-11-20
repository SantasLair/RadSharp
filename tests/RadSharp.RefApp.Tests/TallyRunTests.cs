using static RadSharp.RefApp.LiveTally;

namespace RadSharp.RefApp;

/// <summary>
/// The committed app (refapp/Tally, forms and code) run with F9 in the designer, through the driver, and used with
/// simulated input on the live form (<see cref="LiveTally"/>). The live form's renders go to
/// TestResults/refapp/MainForm.run*.png.
/// </summary>
public class TallyRunTests
{
    [Fact]
    public void F9_shows_the_main_form()
    {
        var (d, live) = RunTally();
        Assert.Equal("MainForm", live.GetType().Name);
        Assert.Equal("Tally", live.Caption);
        Assert.Contains(d.Designer.OutputLines, l => l.StartsWith("Running MainForm", StringComparison.Ordinal));
    }

    [Fact]
    public void F9_run_lists_10000_clients_sorts_on_a_header_click_and_filters_from_the_tree()
    {
        var (_, live) = RunTally();
        var clients = Get<ListView>(live, "lvClients");
        var status = Get<StatusBar>(live, "sbMain");
        var pages = Get<PageControl>(live, "pgMain");

        // Start-up: Shown filled the demo store and selected "Clients".
        Assert.Equal(10_000, clients.ItemCount);
        Assert.Equal("10,000 of 10,000 clients", status.SimpleText);
        RefApp.Render(live, "MainForm.run@100%");
        Assert.NotEqual("", Cell(clients, 9_999, 0));

        // A click on the Name header sorts ascending, a second one descending; City sorts by city.
        ClickHeader(live, clients, 0);
        Assert.Equal((0, false), (clients.SortColumn, clients.SortDescending));
        AssertOrdered(clients, 0, 200, descending: false);
        ClickHeader(live, clients, 0);
        Assert.Equal((0, true), (clients.SortColumn, clients.SortDescending));
        AssertOrdered(clients, 0, 200, descending: true);
        ClickHeader(live, clients, 4);
        Assert.Equal((4, false), (clients.SortColumn, clients.SortDescending));
        AssertOrdered(clients, 4, 200, descending: false);
        Assert.Equal(10_000, clients.ItemCount);

        // The tree filters: Active + Archived = all clients, and the sort survives the filter.
        ClickNav(live, "Archived");
        int archived = clients.ItemCount;
        ClickNav(live, "Active");
        int active = clients.ItemCount;
        Assert.InRange(archived, 1, 2_000);
        Assert.Equal(10_000, active + archived);
        Assert.Equal(active.ToString("N0", Invariant) + " of 10,000 clients", status.SimpleText);
        AssertOrdered(clients, 4, 100, descending: false);

        // Invoices › Overdue switches to the invoice page and lists only overdue ones; Paid only paid ones.
        var invoices = Get<ListView>(live, "lvInvoices");
        ClickNav(live, "Overdue");
        Assert.Equal("tsInvoices", pages.ActivePage!.Name);
        int overdue = invoices.ItemCount;
        Assert.True(overdue > 0);
        Assert.All(Enumerable.Range(0, Math.Min(overdue, 300)), r => Assert.Equal("Overdue", Cell(invoices, r, 4)));
        Assert.StartsWith(overdue.ToString("N0", Invariant) + " invoices (overdue)", status.SimpleText);
        ClickNav(live, "Paid");
        Assert.All(Enumerable.Range(0, Math.Min(invoices.ItemCount, 300)), r => Assert.Equal("Paid", Cell(invoices, r, 4)));
        ClickNav(live, "Invoices");
        Assert.True(invoices.ItemCount > overdue);
        ClickHeader(live, invoices, 5);   // Total, a number: sorted by value, not as text
        var totals = Enumerable.Range(0, 100).Select(r => decimal.Parse(Cell(invoices, r, 5), Invariant)).ToList();
        Assert.Equal(totals.Order(), totals);
        invoices.ItemIndex = 0;   // back to the top for the picture
        invoices.ItemIndex = -1;
        RefApp.Render(live, "MainForm.run.invoices@100%");
    }

    [Fact]
    public void Typing_in_the_search_box_filters_clients_after_the_pause()
    {
        var (_, live) = RunTally();
        var clients = Get<ListView>(live, "lvClients");
        DesignerDriver.TypeInto(live, live.FindControl("edtSearch"), "Lisbon");
        Assert.Equal(10_000, clients.ItemCount);   // not yet: the timer waits for a pause in typing
        live.ProcessTick(1_000);
        live.ProcessTick(1_400);
        Assert.InRange(clients.ItemCount, 1, 9_999);
        Assert.All(Enumerable.Range(0, Math.Min(clients.ItemCount, 100)), r => Assert.Contains("Lisbon", Cell(clients, r, 4)));
        Assert.EndsWith("matching \"Lisbon\"", Get<StatusBar>(live, "sbMain").SimpleText);
    }

    [Fact]
    public void Menu_commands_switch_views_change_the_theme_and_show_the_about_box()
    {
        var (d, live) = RunTally();
        var menu = Get<MainMenu>(live, "mnuMain");
        var pages = Get<PageControl>(live, "pgMain");

        DesignerDriver.ClickMenu(live, menu, "View", "Invoices");
        Assert.Equal("tsInvoices", pages.ActivePage!.Name);
        live.Simulate.Key("Mod+1");
        Assert.Equal("tsClients", pages.ActivePage!.Name);

        DesignerDriver.ClickMenu(live, menu, "View", "Dark Theme");
        Assert.Same(Drawing.Theme.Dark, live.Theme);
        RefApp.Render(live, "MainForm.run.dark@100%");

        string? text = null;
        d.Host.Expect("Tally 0.1", f => { text = DriverHost.Describe(f); return "btnOk"; });
        DesignerDriver.ClickMenu(live, menu, "Help", "About Tally…");
        Assert.Contains("Tally 0.1", text);

        var edit = Get<Edit>(live, "edtSearch");
        live.Simulate.Key("Mod+F");
        Assert.Same(edit, live.FocusedControl);
    }

    [Fact]
    public void F9_run_at_150_percent_scales_the_whole_window()
    {
        var (_, live) = RunTally(scale: 1.5);
        Assert.Equal(1.5, live.Scale);
        Assert.Equal((1710, 1140), (live.Width, live.Height));
        var tree = Get<TreeView>(live, "tvNav");
        Assert.Equal(300, tree.Width);
        Assert.Equal(10_000, Get<ListView>(live, "lvClients").ItemCount);
        RefApp.Render(live, "MainForm.run@150%");
    }
}
