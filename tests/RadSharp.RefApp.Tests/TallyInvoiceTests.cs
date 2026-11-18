using static RadSharp.RefApp.LiveTally;

namespace RadSharp.RefApp;

/// <summary>
/// Workflow 3 (docs/reference-app-scope.md §1.6) through simulated input: the invoice dialog, opened with
/// <c>ShowModalAsync</c> from the F9-run main window. Lines are typed into the grid (validated in onSetEditText), the totals
/// follow decimal quantities, Save stores the invoice, and Cancel with changes asks first. Each test pins the culture,
/// because the dialog reads and shows numbers and dates in the current one.
/// </summary>
public class TallyInvoiceTests
{
    const int Description = 1, Quantity = 2, UnitPrice = 3, TaxRate = 4, Amount = 5;

    static void ClickCell(Form form, StringGrid grid, int col, int row)
    {
        form.PerformLayout();
        var r = grid.CellRect(col, row);
        form.Simulate.Click(grid.AbsoluteLeft + r.X + r.Width / 2, grid.AbsoluteTop + r.Y + r.Height / 2);
    }

    /// <summary>Clicks a line's description cell and types a line, Tab between the cells, Enter at the end.</summary>
    static void TypeLine(Form form, StringGrid grid, int row, params string[] cells)
    {
        ClickCell(form, grid, Description, row);
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0) form.Simulate.Key(Key.Tab);
            if (cells[i].Length > 0) form.Simulate.Type(cells[i]);
        }
        form.Simulate.Key(Key.Enter);
        Assert.False(grid.IsEditing, $"Line {row}: the grid refused a value ({((Label)form.FindControl("lblProblem")!).Caption}).");
    }

    /// <summary>Clicks a date picker's first segment and types the date's digits (the segments advance by themselves).</summary>
    static void TypeDate(Form form, DateTimePicker picker, string digits)
    {
        form.PerformLayout();
        form.Simulate.Click(picker.AbsoluteLeft + picker.Px(10), picker.AbsoluteTop + picker.Height / 2);
        form.Simulate.Type(digits);
    }

    static string Total(Form f, string label) => ((Label)f.FindControl(label)!).Caption;

    /// <summary>Selects a client with a click in the list; returns the row.</summary>
    static int PickClient(Form live, int offset = 1)
    {
        var clients = Get<ListView>(live, "lvClients");
        int row = clients.TopIndex + offset;
        ClickRow(live, clients, row);
        return row;
    }

    [Fact]
    public void Workflow_3_new_invoice_with_three_lines_decimal_quantities_right_totals_and_save() => WithCulture("en-US", () =>
    {
        var (d, live) = RunTally();
        PickClient(live);
        var client = Get<Edit>(live, "edtName").Text;
        var invoices = Get<ListView>(live, "lvClientInvoices");
        int before = invoices.ItemCount;
        Assert.True(Get<Button>(live, "btnNewInvoice").Enabled);

        string? number = null;
        d.Host.Expect("Invoice", f =>
        {
            Assert.Equal("New Invoice", f.Caption);
            Assert.StartsWith(client, ((ComboBox)f.FindControl("cboClient")!).Text);
            number = ((Edit)f.FindControl("edtNumber")!).Text;
            Assert.Matches(@"^\d{4}-\d{5}$", number);
            Assert.Equal("Draft", ((ComboBox)f.FindControl("cboStatus")!).Text);

            // Dates by typing into the pickers (M/d/yyyy in en-US).
            var issued = (DateTimePicker)f.FindControl("dtpIssued")!;
            var due = (DateTimePicker)f.FindControl("dtpDue")!;
            TypeDate(f, issued, "11202026");
            TypeDate(f, due, "12042026");
            Assert.Equal(new DateOnly(2026, 11, 20), issued.Date);
            Assert.Equal(new DateOnly(2026, 12, 4), due.Date);

            // Three lines; the last row is always the empty one that adds a line.
            var grid = (StringGrid)f.FindControl("grdLines")!;
            Assert.Equal(2, grid.RowCount);
            TypeLine(f, grid, 1, "Logo design", "1", "450");
            TypeLine(f, grid, 2, "Design consultation (hour)", "2.5", "85");
            TypeLine(f, grid, 3, "Travel expenses", "1", "38.50", "0");
            Assert.Equal(5, grid.RowCount);
            Assert.Equal(["1", "2", "3", "*"], Enumerable.Range(1, 4).Select(r => grid.Cells[0, r]));
            Assert.Equal("21", grid.Cells[TaxRate, 1]);                // the default rate
            Assert.Equal("38.50", grid.Cells[UnitPrice, 3]);
            Assert.Equal("257.13", grid.Cells[Amount, 2]);             // 212.50 + 44.625 tax, rounded away from zero
            Assert.Equal("840.13 EUR", Total(f, "lblTotal"));

            // Change the first quantity to 1.5: the line and the totals follow.
            ClickCell(f, grid, Quantity, 1);
            f.Simulate.Type("1.5");
            f.Simulate.Key(Key.Enter);
            Assert.Equal("1.5", grid.Cells[Quantity, 1]);
            Assert.Equal("816.75", grid.Cells[Amount, 1]);
            Assert.Equal("926.00 EUR", Total(f, "lblSubtotal"));
            Assert.Equal("186.38 EUR", Total(f, "lblTax"));
            Assert.Equal("1,112.38 EUR", Total(f, "lblTotal"));
            RefApp.Render(f, "InvoiceForm.run@100%");

            // Validation keeps the editor open with the text and says why; Esc gives the old value back.
            var problem = (Label)f.FindControl("lblProblem")!;
            foreach (var (col, text, message) in new[]
            {
                (UnitPrice, "abc", "\"abc\" isn't a number."),
                (UnitPrice, "12.345", "A unit price has at most two decimals."),
                (Quantity, "0", "The quantity must be more than 0."),
                (Quantity, "1,5", "\"1,5\" isn't a number."),          // no thousands separators: 1,5 isn't 15
                (TaxRate, "120", "The tax rate is a percentage from 0 to 100."),
            })
            {
                ClickCell(f, grid, col, 2);
                f.Simulate.Type(text);
                f.Simulate.Key(Key.Enter);
                Assert.True(grid.IsEditing, $"{text} was accepted.");
                Assert.Equal(text, grid.InplaceEditor!.Text);
                Assert.Equal(message, problem.Caption);
                f.Simulate.Key(Key.Escape);
                Assert.False(grid.IsEditing);
            }
            RefApp.Render(f, "InvoiceForm.run.problem@100%");
            Assert.Equal("2.5", grid.Cells[Quantity, 2]);
            Assert.Equal("1,112.38 EUR", Total(f, "lblTotal"));

            // The amount is computed: neither a double-click nor typing opens an editor on it.
            ClickCell(f, grid, Amount, 1);
            f.Simulate.Type("5");
            Assert.False(grid.IsEditing);
            return "btnSave";
        });
        live.Simulate.Key("Mod+Shift+N");
        Assert.Equal(0, d.Host.PendingModals);

        // Saved: the client's invoice list has it, with its total.
        Assert.Equal(before + 1, invoices.ItemCount);
        var row = invoices.Items.Single(i => i.Caption == number);
        Assert.Equal(["2026-11-20", "Draft", "1,112.38"], row.SubItems);
        Assert.Equal($"Invoices ({before + 1})", Get<Label>(live, "lblClientInvoices").Caption);
    });

    [Fact]
    public void Cancel_with_changes_asks_first_and_a_saved_invoice_reopens_with_its_lines() => WithCulture("de-DE", () =>
    {
        var (d, live) = RunTally();
        PickClient(live, offset: 2);
        var invoices = Get<ListView>(live, "lvClientInvoices");
        int before = invoices.ItemCount;

        // Esc on an untouched invoice closes it without a question.
        d.Host.Expect("Invoice", f => { f.Simulate.Key(Key.Escape); return null; });
        live.Simulate.Key("Mod+Shift+N");
        Assert.Equal(0, d.Host.PendingModals);
        Assert.Equal(before, invoices.ItemCount);

        // A line typed with a decimal comma (de-DE), then Cancel: "No" keeps the dialog open, "Yes" drops the invoice.
        string? question = null;
        d.Host.Expect("Invoice", f =>
        {
            var grid = (StringGrid)f.FindControl("grdLines")!;
            TypeLine(f, grid, 1, "Workshop facilitation (day)", "1,5", "900");
            Assert.Equal("1,5", grid.Cells[Quantity, 1]);
            Assert.Equal("1.633,50 EUR", Total(f, "lblTotal"));   // 1 350,00 + 21 % tax
            RefApp.Render(f, "InvoiceForm.run.de-DE@100%");

            d.Host.Expect("Discard", q => { question = DriverHost.Describe(q); return "btnNo"; });
            ClickButton(f, "btnCancel");
            Assert.True(f.IsModal, "The dialog closed although the question was answered No.");
            Assert.Equal("1.633,50 EUR", Total(f, "lblTotal"));

            d.Host.Expect("Discard", _ => "btnYes");
            ClickButton(f, "btnCancel");
            Assert.False(f.IsModal);
            return null;
        });
        live.Simulate.Key("Mod+Shift+N");
        Assert.Equal(0, d.Host.PendingModals);
        Assert.StartsWith("Discard the changes to this new invoice?", question);
        Assert.Equal(before, invoices.ItemCount);

        // Saved through the toolbar button, then reopened with a double-click: the lines come back; a change saves.
        string? number = null;
        d.Host.Expect("Invoice", f =>
        {
            number = ((Edit)f.FindControl("edtNumber")!).Text;
            var grid = (StringGrid)f.FindControl("grdLines")!;
            TypeLine(f, grid, 1, "Hosting (year)", "1", "96");
            TypeLine(f, grid, 2, "Social media kit", "2", "310");
            return "btnSave";
        });
        ClickButton(live, "btnNewInvoice");
        Assert.Equal(before + 1, invoices.ItemCount);
        int index = invoices.Items.FindIndex(i => i.Caption == number);
        Assert.Equal("866.36", invoices.Items[index].SubItems[2]);   // the main window shows invariant numbers: (96 + 620) × 1.21

        d.Host.Expect("Invoice", f =>
        {
            Assert.Equal($"Invoice {number}", f.Caption);
            var grid = (StringGrid)f.FindControl("grdLines")!;
            Assert.Equal(["Hosting (year)", "Social media kit"], new[] { grid.Cells[Description, 1], grid.Cells[Description, 2] });
            Assert.Equal("866,36 EUR", Total(f, "lblTotal"));
            ClickCell(f, grid, Description, 1);
            ClickButton(f, "btnDeleteLine");
            Assert.Equal("Social media kit", grid.Cells[Description, 1]);
            Assert.Equal("750,20 EUR", Total(f, "lblTotal"));
            return "btnSave";
        });
        live.PerformLayout();
        var r = invoices.ItemRect(index);
        live.Simulate.DoubleClick(invoices.AbsoluteLeft + r.X + invoices.Px(30), invoices.AbsoluteTop + r.Y + r.Height / 2);
        Assert.Equal(0, d.Host.PendingModals);
        Assert.Equal("750.20", invoices.Items.Single(i => i.Caption == number).SubItems[2]);
    });

    [Fact]
    public void At_150_percent_the_client_pane_and_the_invoice_dialog_scale() => WithCulture("en-US", () =>
    {
        var (d, live) = RunTally(scale: 1.5);
        Form.DefaultScale = 1.5;   // the dialog opens on the same monitor
        try
        {
            PickClient(live);
            DesignerDriver.TypeInto(live, live.FindControl("memNotes"), "Prefers invoices by email.");
            RefApp.Render(live, "MainForm.run.client@150%");
            d.Host.Expect("Invoice", f =>
            {
                Assert.Equal(1.5, f.Scale);
                var grid = (StringGrid)f.FindControl("grdLines")!;
                TypeLine(f, grid, 1, "Logo design", "1.5", "450");
                TypeLine(f, grid, 2, "Hosting (year)", "1", "96");
                Assert.Equal("816.75", grid.Cells[Amount, 1]);
                ClickCell(f, grid, UnitPrice, 2);
                f.Simulate.Type("x");
                f.Simulate.Key(Key.Enter);
                RefApp.Render(f, "InvoiceForm.run@150%");
                f.Simulate.Key(Key.Escape);
                d.Host.Expect("Discard", q => { RefApp.Render(q, "InvoiceForm.discard@150%"); return "btnYes"; });
                ClickButton(f, "btnCancel");
                return null;
            });
            live.Simulate.Key("Mod+Shift+N");
            d.Host.Expect("Save changes", _ => "btnCancel");   // the pane's notes are still unsaved when the next client is picked
            PickClient(live, offset: 2);
            Assert.Equal(0, d.Host.PendingModals);
        }
        finally { Form.DefaultScale = 1; }
    });

    [Fact]
    public void Double_clicking_an_overdue_invoice_opens_it_and_marking_it_paid_moves_it_out_of_the_list() => WithCulture("en-US", () =>
    {
        var (d, live) = RunTally();
        ClickNav(live, "Overdue");
        var list = Get<ListView>(live, "lvInvoices");
        int overdue = list.ItemCount;
        live.PerformLayout();
        var r = list.ItemRect(0);
        string? number = null;
        d.Host.Expect("Invoice ", f =>
        {
            number = ((Edit)f.FindControl("edtNumber")!).Text;
            Assert.Equal("Sent", ((ComboBox)f.FindControl("cboStatus")!).Text);
            DesignerDriver.PickCombo(f, (ComboBox)f.FindControl("cboStatus")!, 2);   // Paid
            return "btnSave";
        });
        live.Simulate.DoubleClick(list.AbsoluteLeft + r.X + list.Px(30), list.AbsoluteTop + r.Y + r.Height / 2);
        Assert.Equal(0, d.Host.PendingModals);
        Assert.Equal(overdue - 1, list.ItemCount);
        ClickNav(live, "Paid");
        Assert.Contains(Enumerable.Range(0, list.ItemCount), i => Cell(list, i, 0) == number);
    });

    [Fact]
    public void Saving_needs_a_line_and_dates_in_order() => WithCulture("en-US", () =>
    {
        var (d, live) = RunTally();
        PickClient(live);
        var warnings = new List<string>();
        d.Host.Expect("Invoice", f =>
        {
            d.Host.Expect("Warning", w => { warnings.Add(DriverHost.Describe(w)); return "btnOk"; });
            ClickButton(f, "btnSave");
            Assert.True(f.IsModal);

            var grid = (StringGrid)f.FindControl("grdLines")!;
            TypeLine(f, grid, 1, "Print layout (page)", "4", "45");
            TypeDate(f, (DateTimePicker)f.FindControl("dtpIssued")!, "12012026");
            TypeDate(f, (DateTimePicker)f.FindControl("dtpDue")!, "11012026");
            d.Host.Expect("Warning", w => { warnings.Add(DriverHost.Describe(w)); return "btnOk"; });
            ClickButton(f, "btnSave");
            Assert.True(f.IsModal);

            TypeDate(f, (DateTimePicker)f.FindControl("dtpDue")!, "12152026");
            ClickButton(f, "btnSave");
            Assert.False(f.IsModal);
            return null;
        });
        live.Simulate.Key("Mod+Shift+N");
        Assert.Equal(0, d.Host.PendingModals);
        Assert.Collection(warnings,
            w => Assert.StartsWith("Add at least one line with a description.", w),
            w => Assert.StartsWith("The due date is before the issue date.", w));
    });
}
