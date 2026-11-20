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
        d.Set("width", "1140");
        d.Set("height", "760");
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
                new("E&xit", Name: "mniExit", Shortcut: "Mod+Q", Handler: Item.Click),
            ]),
            new Item("&Edit", Items:
            [
                new("&Find", Name: "mniFind", Shortcut: "Mod+F", Handler: Item.Click),
            ]),
            new Item("&Client", Items:
            [
                new("&New Client", Name: "mniNewClient", Shortcut: "Mod+N", Handler: Item.Click),
                new("&Save Client", Name: "mniSaveClient", Shortcut: "Mod+S", Handler: Item.Click, Enabled: false),
                new("&Revert Changes", Name: "mniRevertClient", Handler: Item.Click, Enabled: false),
                Item.Separator,
                new("&Delete Client…", Name: "mniDeleteClient", Handler: Item.Click, Enabled: false),
            ]),
            new Item("&Invoice", Items:
            [
                new("&New Invoice…", Name: "mniNewInvoice", Shortcut: "Mod+Shift+N", Handler: Item.Click, Enabled: false),
                new("Mark &Paid", Enabled: false),
                Item.Separator,
                new("&Delete Invoice", Enabled: false),
            ]),
            new Item("&View", Items:
            [
                new("&Clients", Name: "mniViewClients", Shortcut: "Mod+1", Handler: Item.Click),
                new("&Invoices", Name: "mniViewInvoices", Shortcut: "Mod+2", Handler: Item.Click),
                Item.Separator,
                new("&Dark Theme", Name: "mniDarkTheme", Handler: "mniDarkTheme_Click"),
            ]),
            new Item("&Help", Items:
            [
                new("&About Tally…", Name: "mniAbout", Handler: Item.Click),
            ]));

        // ---- Toolbar: commands and the search box (debounced by a timer) ----
        d.Place("ToolBar", "frmMain", 16, 200, 400, 36);
        d.Name("tbMain");
        d.Place("ToolButton", "tbMain", 8, 4, 96, 28);
        d.Name("btnNewClient");
        d.Set("caption", "New Client");
        d.Handler("onClick");
        d.Place("ToolButton", "tbMain", 120, 4, 96, 28);
        d.Name("btnNewInvoice");
        d.Set("caption", "New Invoice");
        d.Set("enabled", "false");
        d.Handler("onClick");
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

        // Clients: the list over the detail pane (bottom-aligned first, then its splitter, then the list in what is left).
        d.ShowPage("tsClients");
        ClientDetail(d);
        d.Place("Splitter", "tsClients", 400, 120, 200, 4);
        d.Name("splClient");
        d.Set("align", "bottom");
        d.Set("height", "4");
        d.Place("ListView", "tsClients", 16, 16, 400, 200);
        d.Name("lvClients");
        d.Set("align", "client");
        d.Set("autoSort", "true");
        d.Columns(new("Name", 170), new("Company", 150), new("Email", 200), new("Phone", 110), new("City", 120));
        d.Handler("onGetItem");
        d.Handler("onColumnClick");
        d.Handler("onChange");

        d.ShowPage("tsInvoices");
        d.Place("ListView", "tsInvoices", 16, 16, 400, 300);
        d.Name("lvInvoices");
        d.Set("align", "client");
        d.Set("autoSort", "true");
        d.Columns(new("Number", 100), new("Client", 200), new("Issued", 100), new("Due", 100), new("Status", 90),
            new("Total", 110, TextAlign.Right));
        d.Handler("onGetItem");
        d.Handler("onColumnClick");
        d.Handler("onDblClick");                               // opens the invoice
        d.ShowPage("tsClients");

        // ---- The client list's context menu: shares the Client and Invoice menus' handlers (typed into onClick) ----
        d.Place("PopupMenu", "frmMain", 1000, 680);
        d.Name("pmClients");
        d.Menu(
            new Item("&New Client", Handler: "mniNewClient_Click"),
            new Item("New &Invoice…", Name: "pmiNewInvoice", Handler: "mniNewInvoice_Click", Enabled: false),
            Item.Separator,
            new Item("&Delete Client…", Name: "pmiDeleteClient", Handler: "mniDeleteClient_Click"));
        d.Select("lvClients");
        d.Set("popupMenu", "pmClients");

        // ---- Non-visual: the search debounce ----
        d.Place("Timer", "frmMain", 936, 560);
        d.Name("tmrSearch");
        d.Set("interval", "300");
        d.Set("enabled", "false");
        d.Handler("onTimer");
    }

    /// <summary>
    /// The selected client's details (copy-in/copy-out by hand until the Binder, stage 7) and invoices: two columns of
    /// labelled fields, notes, Save/Revert, and the client's invoices on the right. Every field shares one onChange
    /// handler, which sets the dirty flag.
    /// </summary>
    static void ClientDetail(DesignerDriver d)
    {
        d.Place("Panel", "tsClients", 16, 300, 400, 100);
        d.Name("pnlClient");
        d.Set("align", "bottom");
        d.Set("height", "284");
        d.Set("borderStyle", "none");

        void Field(string label, string name, int x, int y, int labelWidth, int width)
        {
            d.Place("Label", "pnlClient", x, y + 4, labelWidth, 20);
            d.Name("lbl" + name[3..]);
            d.Set("caption", label);
            d.Place("Edit", "pnlClient", x + labelWidth + 4, y, width, 26);
            d.Name(name);
            d.Handler("onChange", "ClientField_Change");
        }
        Field("Name", "edtName", 12, 10, 76, 200);
        Field("Company", "edtCompany", 12, 42, 76, 200);
        Field("Email", "edtEmail", 12, 74, 76, 200);
        Field("Phone", "edtPhone", 12, 106, 76, 200);
        Field("Street", "edtStreet", 308, 10, 66, 170);
        Field("City", "edtCity", 308, 42, 66, 170);
        Field("Postcode", "edtPostcode", 308, 74, 66, 170);
        Field("Country", "edtCountry", 308, 106, 66, 170);
        Field("Tax ID", "edtTaxId", 308, 138, 66, 170);
        d.Place("CheckBox", "pnlClient", 92, 140, 120, 22);
        d.Name("chkArchived");
        d.Set("caption", "Archived");
        d.Handler("onChange", "ClientField_Change");
        d.Place("Label", "pnlClient", 12, 174, 76, 20);
        d.Name("lblNotes");
        d.Set("caption", "Notes");
        d.Place("Memo", "pnlClient", 92, 170, 456, 64);
        d.Name("memNotes");
        d.Handler("onChange", "ClientField_Change");

        d.Place("Button", "pnlClient", 92, 246, 88, 28);
        d.Name("btnSaveClient");
        d.Set("caption", "Save");
        d.Set("enabled", "false");
        d.Handler("onClick");
        d.Place("Button", "pnlClient", 188, 246, 88, 28);
        d.Name("btnRevertClient");
        d.Set("caption", "Revert");
        d.Set("enabled", "false");
        d.Handler("onClick");

        d.Place("Label", "pnlClient", 568, 12, 200, 20);
        d.Name("lblClientInvoices");
        d.Set("caption", "Invoices");
        d.Place("ListView", "pnlClient", 568, 36, 352, 238);
        d.Name("lvClientInvoices");
        d.Anchors("left", "top", "right", "bottom");
        d.Columns(new("Number", 92), new("Issued", 92), new("Status", 74), new("Total", 80, TextAlign.Right));
        d.Handler("onDblClick");                               // opens the invoice
    }
}
