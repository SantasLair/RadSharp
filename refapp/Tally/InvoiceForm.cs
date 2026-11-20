using System.Globalization;
using RadSharp;
using Tally.Data;
using Tally.Model;

namespace Tally;

/// <summary>
/// One invoice, shown modally (<c>ShowModalAsync</c>): the header (client, number, dates, status, notes), the lines in a
/// grid edited in place, and the totals. Nothing reaches the store until Save; closing with changes asks first
/// (<see cref="frmInvoice_CloseQuery"/>). Numbers are typed and shown in the current culture.
/// </summary>
/// <remarks>
/// Grid layout: row 0 is the header, rows 1..n the lines, and the last row is always empty: typing into it adds a line.
/// Column 0 (fixed) numbers the lines; <see cref="Amount"/> is computed and can't be edited.
/// </remarks>
public partial class InvoiceForm : Form
{
    const int Description = 1, Quantity = 2, UnitPrice = 3, TaxRate = 4, Amount = 5;

    IStore _store = null!;
    Invoice _invoice = null!;
    List<InvoiceLine> _lines = [];
    List<Client> _clients = [];   // cboClient's items, in order
    bool _loading;                // Edit is filling the fields: their Change isn't an edit
    bool _dirty;                  // something differs from the stored invoice
    bool _saved;                  // Save stored it: closing needs no question

    public InvoiceForm() => InitializeComponent();

    static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>A new draft for <paramref name="client"/>: today, the next number, due after the payment terms.</summary>
    public static Invoice NewFor(IStore store, Client client) => new()
    {
        ClientId = client.Id,
        Number = store.NextInvoiceNumber(store.Today.Year),
        IssueDate = store.Today,
        DueDate = store.Today.AddDays(store.PaymentTermsDays),
        Status = InvoiceStatus.Draft,
    };

    /// <summary>Copy-in: shows <paramref name="invoice"/> (a stored one, or a new one from <see cref="NewFor"/>) and its lines.</summary>
    public void Edit(IStore store, Invoice invoice)
    {
        _store = store;
        _invoice = invoice;
        _lines = [.. store.Lines(invoice.Id).Select(Copy)];
        _clients = [.. store.Clients(ClientFilter.Active).Append(store.Client(invoice.ClientId)).OfType<Client>().DistinctBy(c => c.Id)
            .OrderBy(ClientText, StringComparer.CurrentCultureIgnoreCase)];
        Caption = invoice.Id == 0 ? "New Invoice" : $"Invoice {invoice.Number}";
        _loading = true;
        try
        {
            cboClient.Items = [.. _clients.Select(ClientText)];
            cboClient.ItemIndex = _clients.FindIndex(c => c.Id == invoice.ClientId);
            edtNumber.Text = invoice.Number;
            dtpIssued.Date = invoice.IssueDate;
            dtpDue.Date = invoice.DueDate;
            cboStatus.ItemIndex = (int)invoice.Status;
            memNotes.Text = invoice.Notes;
        }
        finally { _loading = false; }
        FillGrid();
        _dirty = false;
    }

    static string ClientText(Client c) => c.Company.Length > 0 ? $"{c.Name} ({c.Company})" : c.Name;

    static InvoiceLine Copy(InvoiceLine l) => new()
    {
        Id = l.Id, InvoiceId = l.InvoiceId, Position = l.Position, Description = l.Description,
        Quantity = l.Quantity, UnitPrice = l.UnitPrice, TaxRate = l.TaxRate,
    };

    /// <summary>Copy-out of the header; false (with a message) when it can't be stored.</summary>
    bool StoreHeader()
    {
        string? problem =
            cboClient.ItemIndex < 0 ? "Choose the client from the list."
            : edtNumber.Text.Trim().Length == 0 ? "The invoice needs a number."
            : dtpDue.Date < dtpIssued.Date ? "The due date is before the issue date."
            : !_lines.Any(l => l.Description.Length > 0) ? "Add at least one line with a description."
            : null;
        if (problem != null)
        {
            Dialogs.MessageDlg(problem, MessageType.Warning, owner: this);
            return false;
        }
        _invoice.ClientId = _clients[cboClient.ItemIndex].Id;
        _invoice.Number = edtNumber.Text.Trim();
        _invoice.IssueDate = dtpIssued.Date;
        _invoice.DueDate = dtpDue.Date;
        _invoice.Status = (InvoiceStatus)Math.Max(0, cboStatus.ItemIndex);
        _invoice.Notes = memNotes.Text;
        return true;
    }

    /// <summary>The header fields' shared onChange.</summary>
    void Header_Change(Control sender)
    {
        if (!_loading) _dirty = true;
    }

    // ---- Lines ----

    void FillGrid()
    {
        grdLines.RowCount = _lines.Count + 2;
        for (int i = 0; i < _lines.Count; i++) ShowLine(i + 1);
        ClearRow(_lines.Count + 1);
        UpdateTotals();
    }

    void ShowLine(int row)
    {
        var line = _lines[row - 1];
        grdLines.Cells[0, row] = row.ToString(Culture);
        grdLines.Cells[Description, row] = line.Description;
        grdLines.Cells[Quantity, row] = line.Quantity.ToString("0.###", Culture);
        grdLines.Cells[UnitPrice, row] = line.UnitPrice.ToString("0.00", Culture);
        grdLines.Cells[TaxRate, row] = line.TaxRate.ToString("0.##", Culture);
        grdLines.Cells[Amount, row] = line.Amount.ToString("N2", Culture);
    }

    void ClearRow(int row)
    {
        grdLines.Cells[0, row] = "*";
        for (int col = Description; col <= Amount; col++) grdLines.Cells[col, row] = "";
    }

    void UpdateTotals()
    {
        var currency = " " + _invoice.Currency;
        lblSubtotal.Caption = _lines.Sum(l => l.Net).ToString("N2", Culture) + currency;
        lblTax.Caption = _lines.Sum(l => l.Tax).ToString("N2", Culture) + currency;
        lblTotal.Caption = _lines.Sum(l => l.Amount).ToString("N2", Culture) + currency;
    }

    /// <summary>The amount is computed: its cells don't open an editor. A new editor clears the last complaint.</summary>
    void grdLines_GetEditText(Control sender, GridTextEventArgs e)
    {
        if (e.Col == Amount) e.Accept = false;
        else lblProblem.Caption = "";
    }

    /// <summary>
    /// Validates and stores a typed cell: numbers in the current culture (no thousands separators), quantity above 0, at
    /// most two decimals in a price, a tax rate from 0 to 100. A refused value keeps the editor open and says why below the
    /// grid. Typing into the empty last row adds a line (quantity 1, the default tax rate).
    /// </summary>
    void grdLines_SetEditText(Control sender, GridTextEventArgs e)
    {
        int row = e.Row;
        bool isNew = row == _lines.Count + 1;
        var text = e.Text.Trim();
        if (isNew && text.Length == 0) return;   // nothing typed into the new row
        decimal value = 0;
        if (e.Col != Description)
        {
            string? problem = !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, Culture, out value)
                ? $"\"{text}\" isn't a number."
                : e.Col == Quantity && value <= 0 ? "The quantity must be more than 0."
                : e.Col == UnitPrice && value < 0 ? "The unit price can't be negative."
                : e.Col == UnitPrice && value != Math.Round(value, 2) ? "A unit price has at most two decimals."
                : e.Col == TaxRate && (value < 0 || value > 100) ? "The tax rate is a percentage from 0 to 100."
                : null;
            if (problem != null)
            {
                lblProblem.Caption = problem;
                e.Accept = false;
                return;
            }
        }
        lblProblem.Caption = "";

        if (isNew)
        {
            _lines.Add(new InvoiceLine { Quantity = 1, TaxRate = _store.DefaultTaxRate });
            grdLines.RowCount = _lines.Count + 2;
            ClearRow(_lines.Count + 1);
        }
        var line = _lines[row - 1];
        switch (e.Col)
        {
            case Description: line.Description = text; break;
            case Quantity: line.Quantity = value; break;
            case UnitPrice: line.UnitPrice = value; break;
            case TaxRate: line.TaxRate = value; break;
        }
        ShowLine(row);
        e.Text = grdLines.Cells[e.Col, row];   // the grid stores the typed cell as shown (e.g. "2" → "2.00")
        UpdateTotals();
        _dirty = true;
    }

    void btnDeleteLine_Click(Control sender)
    {
        int row = grdLines.Row;
        if (row < 1 || row > _lines.Count) return;
        _lines.RemoveAt(row - 1);
        FillGrid();
        grdLines.FocusCell(Description, Math.Min(row, _lines.Count + 1));
        grdLines.Focus();
        _dirty = true;
    }

    // ---- Closing ----

    void btnSave_Click(Control sender)
    {
        if (!grdLines.EndEdit(commit: true)) return;   // a value still being typed that the grid refuses
        if (!StoreHeader()) return;
        var lines = _lines.Where(l => l.Description.Length > 0 || l.UnitPrice != 0).ToList();
        _store.Save(_invoice, lines);
        _saved = true;
        ModalResult = ModalResult.Ok;
    }

    /// <summary>Cancel, Esc or the title bar: unsaved changes are only dropped after asking.</summary>
    void frmInvoice_CloseQuery(Control sender, CloseQueryEventArgs e)
    {
        if (_saved || !_dirty) return;
        var what = _invoice.Id == 0 ? "this new invoice" : $"invoice {_invoice.Number}";
        e.CanClose = Dialogs.MessageDlg($"Discard the changes to {what}?", MessageType.Confirmation, MessageButtons.YesNo, this) == ModalResult.Yes;
    }
}
