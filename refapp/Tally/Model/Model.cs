namespace Tally.Model;

/// <summary>A customer: a person or a company that gets invoices.</summary>
public sealed class Client
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Company { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public string Postcode { get; set; } = "";
    public string Country { get; set; } = "";
    public string TaxId { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool Archived { get; set; }
    public DateOnly CreatedAt { get; set; }
}

/// <summary>What is stored about an invoice's state. Overdue isn't stored: it is a sent invoice past its due date.</summary>
public enum InvoiceStatus { Draft, Sent, Paid }

public sealed class Invoice
{
    public int Id { get; set; }
    /// <summary>The invoice number people see, e.g. <c>2026-0042</c>.</summary>
    public string Number { get; set; } = "";
    public int ClientId { get; set; }
    public DateOnly IssueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public InvoiceStatus Status { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Notes { get; set; } = "";
    public DateOnly? PaidOn { get; set; }

    /// <summary>Sent and past its due date on <paramref name="today"/>.</summary>
    public bool IsOverdue(DateOnly today) => Status == InvoiceStatus.Sent && DueDate < today;

    /// <summary>The status as shown: Draft, Sent, Overdue or Paid.</summary>
    public string DisplayStatus(DateOnly today) => IsOverdue(today) ? "Overdue" : Status.ToString();
}

public sealed class InvoiceLine
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public int Position { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    /// <summary>Tax rate in percent, e.g. 19.</summary>
    public decimal TaxRate { get; set; }

    public decimal Net => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
    public decimal Tax => Math.Round(Net * TaxRate / 100m, 2, MidpointRounding.AwayFromZero);
    public decimal Amount => Net + Tax;
}

/// <summary>Which clients a list shows (the navigation tree's Clients branch).</summary>
public enum ClientFilter { All, Active, Archived }

/// <summary>Which invoices a list shows (the navigation tree's Invoices branch).</summary>
public enum InvoiceFilter { All, Draft, Sent, Overdue, Paid }

/// <summary>An invoice with what its list row shows: the client's name, the total and the displayed status.</summary>
public sealed record InvoiceRow(Invoice Invoice, string ClientName, decimal Total, string Status);
