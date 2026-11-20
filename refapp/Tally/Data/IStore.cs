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

    /// <summary>One client's invoices, newest first.</summary>
    IReadOnlyList<InvoiceRow> InvoicesOf(int clientId);

    Invoice? Invoice(int id);

    IReadOnlyList<InvoiceLine> Lines(int invoiceId);

    /// <summary>Adds a new client (<see cref="Model.Client.Id"/> 0 gets the next id) or keeps the changes of a stored one.</summary>
    void Save(Client client);

    /// <summary>Removes a client and its invoices.</summary>
    void Delete(Client client);

    /// <summary>Adds a new invoice (<see cref="Model.Invoice.Id"/> 0) or replaces a stored one's header and lines.</summary>
    void Save(Invoice invoice, IReadOnlyList<InvoiceLine> lines);

    /// <summary>The next free number for an invoice issued in <paramref name="year"/>: <c>2026-00043</c>.</summary>
    string NextInvoiceNumber(int year);

    /// <summary>The tax rate (percent) of a new invoice line (a setting in stage 5).</summary>
    decimal DefaultTaxRate { get; }

    /// <summary>Days from issue to due date for a new invoice (a setting in stage 5).</summary>
    int PaymentTermsDays { get; }
}
