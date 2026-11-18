using Tally.Model;

namespace Tally.Data;

/// <summary>
/// Where Tally's data lives. <see cref="MemoryStore"/> for now; a SQLite store follows once F9 can load packages
/// (docs/reference-app-scope.md §5, stage 4, which also makes these calls asynchronous).
/// </summary>
public interface IStore
{
    /// <summary>The date "overdue" is measured against.</summary>
    DateOnly Today { get; }

    int ClientCount { get; }
    int InvoiceCount { get; }

    /// <summary>Clients matching the filter and, if not empty, containing <paramref name="search"/> in name, company, email or city.</summary>
    IReadOnlyList<Client> Clients(ClientFilter filter, string search = "");

    Client? Client(int id);

    IReadOnlyList<InvoiceRow> Invoices(InvoiceFilter filter);

    IReadOnlyList<InvoiceLine> Lines(int invoiceId);
}
