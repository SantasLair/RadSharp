using System.Globalization;
using RadSharp;
using RadSharp.Drawing;
using Tally.Data;
using Tally.Model;

namespace Tally;

/// <summary>
/// The main window: the navigation tree picks what the right side lists (clients, or invoices by status), both lists are
/// virtual (rows come from <see cref="lvClients_GetItem"/> and <see cref="lvInvoices_GetItem"/>), a header click sorts,
/// and the search box filters clients after a short pause (<c>tmrSearch</c>).
/// </summary>
public partial class MainForm : Form
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;   // Tally is English-only (docs/reference-app-scope.md §3)

    IStore _store = null!;
    IReadOnlyList<Client> _clients = [];
    IReadOnlyList<InvoiceRow> _invoices = [];
    ClientFilter _clientFilter;
    InvoiceFilter _invoiceFilter;
    string _search = "";

    public MainForm() => InitializeComponent();

    /// <summary>The data to show; when null at start-up, the window makes a demo store with 10 000 clients.</summary>
    public IStore? Store { get; set; }

    void frmMain_Show(Control sender)
    {
        _store = Store ??= DemoData.CreateStore(clients: 10_000, seed: 2026, today: DateOnly.FromDateTime(DateTime.Today));
        tvNav.Selected = tvNav.Nodes[0];   // "Clients": tvNav_Change fills the list
    }

    // ---- Navigation ----

    void tvNav_Change(Control sender)
    {
        if (tvNav.Selected is not { } node || _store == null) return;
        var branch = node.Parent ?? node;
        var leaf = node.Parent == null ? null : node.Text;
        if (branch.Text == "Clients")
        {
            _clientFilter = leaf switch { "Active" => ClientFilter.Active, "Archived" => ClientFilter.Archived, _ => ClientFilter.All };
            ShowPage(tsClients);
            LoadClients();
        }
        else if (branch.Text == "Invoices")
        {
            _invoiceFilter = leaf == null ? InvoiceFilter.All : Enum.Parse<InvoiceFilter>(leaf);
            ShowPage(tsInvoices);
            LoadInvoices();
        }
    }

    void ShowPage(TabSheet sheet) => pgMain.ActivePageIndex = pgMain.Pages.ToList().IndexOf(sheet);

    void pgMain_Change(Control sender) => UpdateStatus();

    // ---- Clients ----

    void LoadClients()
    {
        _clients = Sorted(_store.Clients(_clientFilter, _search), lvClients.SortColumn, lvClients.SortDescending);
        lvClients.ItemIndex = -1;
        lvClients.ItemCount = _clients.Count;
        lvClients.Invalidate();
        UpdateStatus();
    }

    static string ClientColumn(Client c, int column) => column switch
    {
        1 => c.Company,
        2 => c.Email,
        3 => c.Phone,
        4 => c.City,
        _ => c.Name,
    };

    static IReadOnlyList<Client> Sorted(IReadOnlyList<Client> clients, int column, bool descending)
    {
        if (column < 0) return clients;
        var ordered = descending
            ? clients.OrderByDescending(c => ClientColumn(c, column), StringComparer.CurrentCultureIgnoreCase)
            : clients.OrderBy(c => ClientColumn(c, column), StringComparer.CurrentCultureIgnoreCase);
        return [.. ordered.ThenBy(c => c.Id)];
    }

    void lvClients_GetItem(Control sender, ListViewItemEventArgs e)
    {
        var c = _clients[e.Index];
        e.Item.Caption = c.Name;
        e.Item.SubItems.AddRange([c.Company, c.Email, c.Phone, c.City]);
    }

    // AutoSort has already moved the arrow (SortColumn, SortDescending); a virtual list sorts its own data.
    void lvClients_ColumnClick(Control sender, int column) => LoadClients();

    // ---- Search (debounced) ----

    void edtSearch_Change(Control sender)
    {
        tmrSearch.Enabled = false;   // restart the pause
        tmrSearch.Enabled = true;
    }

    void tmrSearch_Timer(Control sender)
    {
        tmrSearch.Enabled = false;
        _search = edtSearch.Text.Trim();
        if (pgMain.ActivePage != tsClients) tvNav.Selected = tvNav.Nodes[0];
        else LoadClients();
    }

    // ---- Invoices ----

    void LoadInvoices()
    {
        _invoices = Sorted(_store.Invoices(_invoiceFilter), lvInvoices.SortColumn, lvInvoices.SortDescending);
        lvInvoices.ItemIndex = -1;
        lvInvoices.ItemCount = _invoices.Count;
        lvInvoices.Invalidate();
        UpdateStatus();
    }

    static IReadOnlyList<InvoiceRow> Sorted(IReadOnlyList<InvoiceRow> rows, int column, bool descending)
    {
        if (column < 0) return rows;
        IOrderedEnumerable<InvoiceRow> ordered = column switch
        {
            1 => Order(rows, r => r.ClientName, StringComparer.CurrentCultureIgnoreCase, descending),
            2 => Order(rows, r => r.Invoice.IssueDate, Comparer<DateOnly>.Default, descending),
            3 => Order(rows, r => r.Invoice.DueDate, Comparer<DateOnly>.Default, descending),
            4 => Order(rows, r => r.Status, StringComparer.Ordinal, descending),
            5 => Order(rows, r => r.Total, Comparer<decimal>.Default, descending),
            _ => Order(rows, r => r.Invoice.Number, StringComparer.Ordinal, descending),
        };
        return [.. ordered.ThenBy(r => r.Invoice.Id)];
    }

    static IOrderedEnumerable<InvoiceRow> Order<TKey>(IEnumerable<InvoiceRow> rows, Func<InvoiceRow, TKey> key, IComparer<TKey> comparer, bool descending) =>
        descending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);

    void lvInvoices_GetItem(Control sender, ListViewItemEventArgs e)
    {
        var row = _invoices[e.Index];
        var i = row.Invoice;
        e.Item.Caption = i.Number;
        e.Item.SubItems.AddRange([row.ClientName, Date(i.IssueDate), Date(i.DueDate), row.Status, Money(row.Total)]);
    }

    void lvInvoices_ColumnClick(Control sender, int column) => LoadInvoices();

    static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", Invariant);
    static string Money(decimal amount) => amount.ToString("N2", Invariant);

    // ---- Status bar ----

    void UpdateStatus()
    {
        if (_store == null) return;
        sbMain.SimpleText = pgMain.ActivePage == tsInvoices
            ? $"{_invoices.Count.ToString("N0", Invariant)} invoices ({FilterName(_invoiceFilter)})  ·  total {Money(_invoices.Sum(r => r.Total))} EUR"
            : $"{_clients.Count.ToString("N0", Invariant)} of {_store.ClientCount.ToString("N0", Invariant)} clients"
              + (_search.Length > 0 ? $" matching \"{_search}\"" : "");
    }

    static string FilterName(InvoiceFilter f) => f == InvoiceFilter.All ? "all" : f.ToString().ToLowerInvariant();

    // ---- Menu commands (the menu editor names these handlers but doesn't write them: see the M17 bug list) ----

    void mniExit_Click(MenuItem sender) => Close();

    void mniFind_Click(MenuItem sender)
    {
        edtSearch.Focus();
        edtSearch.SelectAll();
    }

    void mniViewClients_Click(MenuItem sender) => tvNav.Selected = tvNav.Nodes[0];

    void mniViewInvoices_Click(MenuItem sender) => tvNav.Selected = tvNav.Nodes[1];

    void mniDarkTheme_Click(MenuItem sender)
    {
        mniDarkTheme.Checked = !mniDarkTheme.Checked;
        Theme = mniDarkTheme.Checked ? Theme.Dark : Theme.Default;
        InvalidateAll();
    }

    void mniAbout_Click(MenuItem sender) =>
        Dialogs.MessageDlg("Tally 0.1\nA small invoicing app, the RadSharp reference application.", MessageType.Information, owner: this);
}
