using RadSharp.Drawing;
using static RadSharp.RefApp.DesignerDriver;

namespace RadSharp.RefApp.Recipes;

/// <summary>
/// Tally's main window (docs/reference-app-scope.md §1.2): menu, toolbar with the search box, navigation tree, splitter,
/// and a page control with the client and invoice lists (virtual ListViews), over a status bar. Coordinates are design
/// pixels in the parent; aligned controls are dragged out anywhere free in their parent and then aligned. Order matters:
/// controls align in the order they are placed (menu, toolbar and status bar first, then the tree, its splitter, and the
/// page control in what is left).
/// </summary>
public static class MainFormRecipe
{
    public static void Build(DesignerDriver d)
    {
        d.SelectForm();
        d.Set("caption", "Tally");
        d.Set("width", "1000");
        d.Set("height", "640");
        d.Handler("onShow");                                   // frmMain_Show: opens the store

        // ---- Menu ----
        d.Place("MainMenu", "frmMain", 16, 200);
        d.Name("mnuMain");
        d.Menu(
            new Item("&File", Items:
            [
                new("&Import Clients…", Enabled: false),
                new("&Generate Demo Data…", Enabled: false),
                Item.Separator,
                new("&Settings…", Enabled: false),
                Item.Separator,
                new("E&xit", Shortcut: "Mod+Q", Handler: "mniExit_Click"),
            ]),
            new Item("&Edit", Items:
            [
                new("&Find", Shortcut: "Mod+F", Handler: "mniFind_Click"),
            ]),
            new Item("&Client", Items:
            [
                new("&New Client", Shortcut: "Mod+N", Enabled: false),
                new("&Delete Client", Enabled: false),
            ]),
            new Item("&Invoice", Items:
            [
                new("&New Invoice", Shortcut: "Mod+Shift+N", Enabled: false),
                new("Mark &Paid", Enabled: false),
                Item.Separator,
                new("&Delete Invoice", Enabled: false),
            ]),
            new Item("&View", Items:
            [
                new("&Clients", Shortcut: "Mod+1", Handler: "mniViewClients_Click"),
                new("&Invoices", Shortcut: "Mod+2", Handler: "mniViewInvoices_Click"),
                Item.Separator,
                new("&Dark Theme", Name: "mniDarkTheme", Handler: "mniDarkTheme_Click"),
            ]),
            new Item("&Help", Items:
            [
                new("&About Tally…", Handler: "mniAbout_Click"),
            ]));

        // ---- Toolbar: commands and the search box (debounced by a timer) ----
        d.Place("ToolBar", "frmMain", 16, 200, 400, 36);
        d.Name("tbMain");
        d.Place("ToolButton", "tbMain", 8, 4, 96, 28);
        d.Name("btnNewClient");
        d.Set("caption", "New Client");
        d.Set("enabled", "false");
        d.Place("ToolButton", "tbMain", 120, 4, 96, 28);
        d.Name("btnNewInvoice");
        d.Set("caption", "New Invoice");
        d.Set("enabled", "false");
        d.Place("ToolSeparator", "tbMain", 240, 4);
        d.Name("sepTools");
        d.Place("Edit", "tbMain", 280, 5, 280, 26);
        d.Name("edtSearch");
        d.Set("placeholder", "Find clients (name, company, email, city)");
        d.Handler("onChange");

        // ---- Status bar ----
        d.Place("StatusBar", "frmMain", 16, 400, 304, 24);
        d.Name("sbMain");
        d.Set("simpleText", "Ready");

        // ---- Navigation tree and its splitter ----
        d.Place("TreeView", "frmMain", 16, 96, 200, 400);
        d.Name("tvNav");
        d.Set("align", "left");
        d.Nodes(
            new Node("Clients", Expanded: true, new Node("Active"), new Node("Archived")),
            new Node("Invoices", Expanded: true, new Node("Draft"), new Node("Sent"), new Node("Overdue"), new Node("Paid")));
        d.Handler("onChange");
        d.Place("Splitter", "frmMain", 240, 96, 4, 304);
        d.Name("splNav");

        // ---- Pages: clients and invoices ----
        d.Place("PageControl", "frmMain", 280, 96, 600, 400);
        d.Name("pgMain");
        d.Set("align", "client");
        d.Handler("onChange");
        d.Place("TabSheet", "pgMain", 200, 200);
        d.Name("tsClients");
        d.Set("caption", "Clients");
        d.Place("TabSheet", "pgMain", 200, 200);
        d.Name("tsInvoices");
        d.Set("caption", "Invoices");

        d.ShowPage("tsClients");
        d.Place("ListView", "tsClients", 16, 16, 400, 300);
        d.Name("lvClients");
        d.Set("align", "client");
        d.Set("autoSort", "true");
        d.Columns(new("Name", 170), new("Company", 150), new("Email", 200), new("Phone", 110), new("City", 120));
        d.Handler("onGetItem");
        d.Handler("onColumnClick");

        d.ShowPage("tsInvoices");
        d.Place("ListView", "tsInvoices", 16, 16, 400, 300);
        d.Name("lvInvoices");
        d.Set("align", "client");
        d.Set("autoSort", "true");
        d.Columns(new("Number", 100), new("Client", 200), new("Issued", 100), new("Due", 100), new("Status", 90),
            new("Total", 110, TextAlign.Right));
        d.Handler("onGetItem");
        d.Handler("onColumnClick");
        d.ShowPage("tsClients");

        // ---- Non-visual: the search debounce ----
        d.Place("Timer", "frmMain", 936, 560);
        d.Name("tmrSearch");
        d.Set("interval", "300");
        d.Set("enabled", "false");
        d.Handler("onTimer");
    }
}
