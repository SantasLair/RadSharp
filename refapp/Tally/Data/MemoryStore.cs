using Tally.Model;

namespace Tally.Data;

/// <summary>An <see cref="IStore"/> that keeps everything in lists (demo data and tests).</summary>
public sealed class MemoryStore(DateOnly today) : IStore
{
    readonly List<Client> _clients = [];
    readonly Dictionary<int, Client> _clientsById = [];
    readonly List<Invoice> _invoices = [];
    readonly Dictionary<int, Invoice> _invoicesById = [];
    readonly Dictionary<int, List<InvoiceLine>> _lines = [];
    readonly Dictionary<int, decimal> _totals = [];
    int _lastClientId, _lastInvoiceId;

    public DateOnly Today { get; } = today;
    public int ClientCount => _clients.Count;
    public int InvoiceCount => _invoices.Count;
    public decimal DefaultTaxRate { get; set; } = 21m;
    public int PaymentTermsDays { get; set; } = 14;

    public void Add(Client client)
    {
        if (client.Id == 0) client.Id = _lastClientId + 1;
        _lastClientId = Math.Max(_lastClientId, client.Id);
        _clients.Add(client);
        _clientsById[client.Id] = client;
    }

    public void Add(Invoice invoice, IEnumerable<InvoiceLine> lines)
    {
        if (invoice.Id == 0) invoice.Id = _lastInvoiceId + 1;
        _lastInvoiceId = Math.Max(_lastInvoiceId, invoice.Id);
        _invoices.Add(invoice);
        _invoicesById[invoice.Id] = invoice;
        SetLines(invoice, lines);
    }

    void SetLines(Invoice invoice, IEnumerable<InvoiceLine> lines)
    {
        var list = lines.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            list[i].InvoiceId = invoice.Id;
            list[i].Position = i + 1;
        }
        _lines[invoice.Id] = list;
        _totals[invoice.Id] = list.Sum(l => l.Amount);
    }

    public IReadOnlyList<Client> Clients(ClientFilter filter, string search = "")
    {
        search = search.Trim();
        return [.. _clients.Where(c => filter switch
            {
                ClientFilter.Active => !c.Archived,
                ClientFilter.Archived => c.Archived,
                _ => true,
            } && (search.Length == 0 || Matches(c, search)))];
    }

    static bool Matches(Client c, string text) =>
        c.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
        || c.Company.Contains(text, StringComparison.CurrentCultureIgnoreCase)
        || c.Email.Contains(text, StringComparison.CurrentCultureIgnoreCase)
        || c.City.Contains(text, StringComparison.CurrentCultureIgnoreCase);

    public Client? Client(int id) => _clientsById.GetValueOrDefault(id);

    public IReadOnlyList<InvoiceRow> Invoices(InvoiceFilter filter) =>
        [.. _invoices
            .Where(i => filter switch
            {
                InvoiceFilter.Draft => i.Status == InvoiceStatus.Draft,
                InvoiceFilter.Sent => i.Status == InvoiceStatus.Sent && !i.IsOverdue(Today),
                InvoiceFilter.Overdue => i.IsOverdue(Today),
                InvoiceFilter.Paid => i.Status == InvoiceStatus.Paid,
                _ => true,
            })
            .Select(Row)];

    public IReadOnlyList<InvoiceRow> InvoicesOf(int clientId) =>
        [.. _invoices.Where(i => i.ClientId == clientId).OrderByDescending(i => i.IssueDate).ThenByDescending(i => i.Id).Select(Row)];

    InvoiceRow Row(Invoice i) =>
        new(i, _clientsById.TryGetValue(i.ClientId, out var c) ? c.Name : "?", _totals[i.Id], i.DisplayStatus(Today));

    public Invoice? Invoice(int id) => _invoicesById.GetValueOrDefault(id);

    public IReadOnlyList<InvoiceLine> Lines(int invoiceId) => _lines.TryGetValue(invoiceId, out var l) ? l : [];

    public void Save(Client client)
    {
        if (client.Id == 0 || !_clientsById.ContainsKey(client.Id)) Add(client);
        else if (!ReferenceEquals(_clientsById[client.Id], client))
        {
            _clients[_clients.IndexOf(_clientsById[client.Id])] = client;
            _clientsById[client.Id] = client;
        }
    }

    public void Delete(Client client)
    {
        foreach (var invoice in _invoices.Where(i => i.ClientId == client.Id).ToList())
        {
            _invoices.Remove(invoice);
            _invoicesById.Remove(invoice.Id);
            _lines.Remove(invoice.Id);
            _totals.Remove(invoice.Id);
        }
        _clients.Remove(client);
        _clientsById.Remove(client.Id);
    }

    public void Save(Invoice invoice, IReadOnlyList<InvoiceLine> lines)
    {
        if (invoice.Id == 0 || !_invoicesById.TryGetValue(invoice.Id, out var stored)) { Add(invoice, lines); return; }
        if (!ReferenceEquals(stored, invoice))
        {
            _invoices[_invoices.IndexOf(stored)] = invoice;
            _invoicesById[invoice.Id] = invoice;
        }
        SetLines(invoice, lines);
    }

    public string NextInvoiceNumber(int year)
    {
        var prefix = $"{year}-";
        int last = _invoices.Where(i => i.Number.StartsWith(prefix, StringComparison.Ordinal))
            .Select(i => int.TryParse(i.Number.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        return $"{prefix}{last + 1:D5}";
    }
}
