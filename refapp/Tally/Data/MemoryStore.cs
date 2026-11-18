using Tally.Model;

namespace Tally.Data;

/// <summary>An <see cref="IStore"/> that keeps everything in lists (demo data and tests).</summary>
public sealed class MemoryStore(DateOnly today) : IStore
{
    readonly List<Client> _clients = [];
    readonly Dictionary<int, Client> _clientsById = [];
    readonly List<Invoice> _invoices = [];
    readonly Dictionary<int, List<InvoiceLine>> _lines = [];
    readonly Dictionary<int, decimal> _totals = [];

    public DateOnly Today { get; } = today;
    public int ClientCount => _clients.Count;
    public int InvoiceCount => _invoices.Count;

    public void Add(Client client)
    {
        if (client.Id == 0) client.Id = _clients.Count + 1;
        _clients.Add(client);
        _clientsById[client.Id] = client;
    }

    public void Add(Invoice invoice, IEnumerable<InvoiceLine> lines)
    {
        if (invoice.Id == 0) invoice.Id = _invoices.Count + 1;
        _invoices.Add(invoice);
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
            .Select(i => new InvoiceRow(i, _clientsById.TryGetValue(i.ClientId, out var c) ? c.Name : "?", _totals[i.Id], i.DisplayStatus(Today)))];

    public IReadOnlyList<InvoiceLine> Lines(int invoiceId) => _lines.TryGetValue(invoiceId, out var l) ? l : [];
}
