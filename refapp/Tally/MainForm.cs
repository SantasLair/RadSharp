using System.Globalization;
using RadSharp;
using RadSharp.Drawing;
using Tally.Data;
using Tally.Model;

namespace Tally;

/// <summary>
/// The main window: the navigation tree picks what the right side lists (clients, or invoices by status), both lists are
/// virtual (rows come from <see cref="lvClients_GetItem"/> and <see cref="lvInvoices_GetItem"/>), a header click sorts,
/// and the search box filters clients after a short pause (<c>tmrSearch</c>). Below the client list, the selected client's
/// details are edited in place (copy-in/copy-out by hand, see <see cref="ShowClient"/>) next to their invoices.
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
    bool _selecting;   // the code is moving the client list's selection: not a pick by the user

    public MainForm() => InitializeComponent();

    /// <summary>The data to show; when null at start-up, the window makes a demo store with 10 000 clients.</summary>
    public IStore? Store { get; set; }

    void frmMain_Show(Control sender)
    {
        _store = Store ??= DemoData.CreateStore(clients: 10_000, seed: 2026, today: DateOnly.FromDateTime(DateTime.Today));
        ShowClient(null);
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

    /// <summary>Fills the client list from the store (filter, search, sort), keeping the client in the detail pane selected.</summary>
    void LoadClients()
    {
        _clients = Sorted(_store.Clients(_clientFilter, _search), lvClients.SortColumn, lvClients.SortDescending);
        _selecting = true;
        try
        {
            lvClients.ItemIndex = -1;
            lvClients.ItemCount = _clients.Count;
        }
        finally { _selecting = false; }
        SelectInList(_current);
        lvClients.Invalidate();
        UpdateStatus();
    }

    /// <summary>Selects a client's row (none for null or a client the list doesn't show) without treating it as a pick.</summary>
    void SelectInList(Client? client)
    {
        int index = -1;
        if (client is { Id: > 0 })
            for (int i = 0; i < _clients.Count; i++)
                if (_clients[i].Id == client.Id) { index = i; break; }
        _selecting = true;
        try { lvClients.ItemIndex = index; }
        finally { _selecting = false; }
    }

    Client? SelectedClient() => lvClients.ItemIndex >= 0 ? _clients[lvClients.ItemIndex] : null;

    /// <summary>A pick in the list shows that client, after asking about unsaved changes (Cancel puts the selection back).</summary>
    void lvClients_Change(Control sender)
    {
        if (_selecting || SelectedClient() is not { } next || next == _current) return;
        if (!ConfirmLeave()) { SelectInList(_current); return; }
        ShowClient(next);
        SelectInList(next);   // saving the other client first may have reloaded and re-sorted the list
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

    // ---- Client detail: copy-in/copy-out by hand (the Binder replaces this in stage 7) ----

    Client? _current;   // the client in the detail pane: a stored one, or a new one (Id 0) until it is saved
    bool _loading;      // ShowClient is filling the fields: their Change isn't an edit
    bool _dirty;        // the fields differ from _current

    /// <summary>Copy-in: shows <paramref name="client"/> (null: none, the pane is disabled) with no unsaved changes.</summary>
    void ShowClient(Client? client)
    {
        _current = client;
        _loading = true;
        try
        {
            edtName.Text = client?.Name ?? "";
            edtCompany.Text = client?.Company ?? "";
            edtEmail.Text = client?.Email ?? "";
            edtPhone.Text = client?.Phone ?? "";
            edtStreet.Text = client?.Street ?? "";
            edtCity.Text = client?.City ?? "";
            edtPostcode.Text = client?.Postcode ?? "";
            edtCountry.Text = client?.Country ?? "";
            edtTaxId.Text = client?.TaxId ?? "";
            chkArchived.Checked = client?.Archived ?? false;
            memNotes.Text = client?.Notes ?? "";
        }
        finally { _loading = false; }
        _dirty = false;
        pnlClient.Enabled = client != null;
        LoadClientInvoices();
        UpdateCommands();
    }

    /// <summary>Copy-out: the fields into <paramref name="client"/>.</summary>
    void StoreClient(Client client)
    {
        client.Name = edtName.Text.Trim();
        client.Company = edtCompany.Text.Trim();
        client.Email = edtEmail.Text.Trim();
        client.Phone = edtPhone.Text.Trim();
        client.Street = edtStreet.Text.Trim();
        client.City = edtCity.Text.Trim();
        client.Postcode = edtPostcode.Text.Trim();
        client.Country = edtCountry.Text.Trim();
        client.TaxId = edtTaxId.Text.Trim();
        client.Archived = chkArchived.Checked;
        client.Notes = memNotes.Text;
    }

    /// <summary>Every field's onChange (one shared handler): an edit by the user marks the client dirty.</summary>
    void ClientField_Change(Control sender)
    {
        if (_loading || _dirty) return;
        _dirty = true;
        UpdateCommands();
    }

    /// <summary>Saves the pane's client; false (nothing saved) when it has no name.</summary>
    bool SaveClient()
    {
        if (_current == null) return true;
        if (edtName.Text.Trim().Length == 0)
        {
            Dialogs.MessageDlg("A client needs a name.", MessageType.Warning, owner: this);
            edtName.Focus();
            return false;
        }
        StoreClient(_current);
        _store.Save(_current);
        _dirty = false;
        LoadClients();   // a new client joins the list, a renamed one moves; the selection follows it
        LoadClientInvoices();
        UpdateCommands();
        return true;
    }

    /// <summary>Before the pane shows another client: true to go on (saved or discarded), false to stay.</summary>
    bool ConfirmLeave()
    {
        if (!_dirty || _current == null) return true;
        var name = edtName.Text.Trim() is { Length: > 0 } n ? n : "the new client";
        var answer = Dialogs.MessageDlg($"Save changes to {name}?", MessageType.Confirmation, MessageButtons.YesNoCancel, this);
        if (answer == ModalResult.Yes) return SaveClient();
        return answer == ModalResult.No;
    }

    void NewClient()
    {
        if (!ConfirmLeave()) return;
        if (pgMain.ActivePage != tsClients) tvNav.Selected = tvNav.Nodes[0];
        ShowClient(new Client { CreatedAt = _store.Today });
        SelectInList(null);
        edtName.Focus();
    }

    void LoadClientInvoices()
    {
        lvClientInvoices.Items.Clear();
        var rows = _current is { Id: > 0 } c ? _store.InvoicesOf(c.Id) : [];
        foreach (var row in rows)
            lvClientInvoices.Items.Add(new ListViewItem(row.Invoice.Number, Date(row.Invoice.IssueDate), row.Status, Money(row.Total)) { Tag = row.Invoice });
        lvClientInvoices.ItemsChanged();
        lblClientInvoices.Caption = rows.Count == 0 ? "Invoices" : $"Invoices ({rows.Count.ToString(Invariant)})";
    }

    /// <summary>Enables the commands that depend on the detail pane, on every surface (menu, buttons, context menu).</summary>
    void UpdateCommands()
    {
        bool stored = _current is { Id: > 0 };
        mniSaveClient.Enabled = btnSaveClient.Enabled = _dirty;
        mniRevertClient.Enabled = btnRevertClient.Enabled = _dirty;
        mniDeleteClient.Enabled = pmiDeleteClient.Enabled = stored;
        mniNewInvoice.Enabled = btnNewInvoice.Enabled = pmiNewInvoice.Enabled = stored;
    }

    void mniNewClient_Click(MenuItem sender) => NewClient();

    void btnNewClient_Click(Control sender) => NewClient();

    void mniSaveClient_Click(MenuItem sender) => SaveClient();

    void btnSaveClient_Click(Control sender) => SaveClient();

    void mniRevertClient_Click(MenuItem sender) => ShowClient(_current);

    void btnRevertClient_Click(Control sender) => ShowClient(_current);

    async void mniDeleteClient_Click(MenuItem sender)
    {
        if (_current is not { Id: > 0 } client) return;
        int invoices = _store.InvoicesOf(client.Id).Count;
        var question = invoices switch
        {
            0 => $"Delete {client.Name}?",
            1 => $"Delete {client.Name} and their invoice?",
            _ => $"Delete {client.Name} and their {invoices.ToString(Invariant)} invoices?",
        };
        if (await Dialogs.MessageDlgAsync(question, MessageType.Confirmation, MessageButtons.YesNo, this) != ModalResult.Yes) return;
        _store.Delete(client);
        ShowClient(null);
        LoadClients();
        LoadInvoices();
    }

    // ---- Invoices: the invoice dialog, modal and asynchronous ----

    /// <summary>Opens the invoice dialog on a new invoice for the pane's (saved) client.</summary>
    async Task NewInvoice()
    {
        if (_current is not { Id: > 0 } client) return;
        await EditInvoice(InvoiceForm.NewFor(_store, client));
    }

    /// <summary>Shows the invoice dialog and, when it saved, refreshes what lists invoices.</summary>
    async Task EditInvoice(Invoice invoice)
    {
        var form = new InvoiceForm { Owner = this };
        form.Edit(_store, invoice);
        if (await form.ShowModalAsync() != ModalResult.Ok) return;
        LoadClientInvoices();
        LoadInvoices();
    }

    async void mniNewInvoice_Click(MenuItem sender) => await NewInvoice();

    async void btnNewInvoice_Click(Control sender) => await NewInvoice();

    async void lvClientInvoices_DblClick(Control sender)
    {
        if (lvClientInvoices.Selected?.Tag is Invoice invoice) await EditInvoice(invoice);
    }

    async void lvInvoices_DblClick(Control sender)
    {
        if (lvInvoices.ItemIndex >= 0) await EditInvoice(_invoices[lvInvoices.ItemIndex].Invoice);
    }

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

    // ---- Menu commands (stubs written by the menu editor) ----

    void mniExit_Click(MenuItem sender) => Close();

    void mniFind_Click(MenuItem sender)
    {
        edtSearch.Focus();
        edtSearch.SelectAll();
    }

    void mniViewClients_Click(MenuItem sender) => tvNav.Selected = tvNav.Nodes[0];

    void mniViewInvoices_Click(MenuItem sender) => tvNav.Selected = tvNav.Nodes[1];

    void mniAbout_Click(MenuItem sender) =>
        Dialogs.MessageDlg("Tally 0.1\nA small invoicing app, the RadSharp reference application.", MessageType.Information, owner: this);

    void mniDarkTheme_Click(MenuItem sender)
    {
        mniDarkTheme.Checked = !mniDarkTheme.Checked;
        Theme = mniDarkTheme.Checked ? Theme.Dark : Theme.Default;
        InvalidateAll();
    }
}
