using RadSharp.Drawing;
using static RadSharp.RefApp.DesignerDriver;

namespace RadSharp.RefApp.Recipes;

/// <summary>
/// Tally's invoice dialog (docs/reference-app-scope.md §1.2), shown modally with <c>ShowModalAsync</c>: the header (client,
/// number, issue and due dates, status), a grid of lines edited in place (description, quantity, unit price, tax rate;
/// the amount is computed), the totals, notes, and Save/Cancel. The grid and the bottom row follow the window's size.
/// </summary>
public static class InvoiceFormRecipe
{
    public static void Build(DesignerDriver d)
    {
        d.NewForm("InvoiceForm");
        d.SelectForm();
        d.Set("caption", "Invoice");
        d.Set("width", "760");
        d.Set("height", "560");
        d.Set("startPosition", "centerOwner");
        d.Handler("onCloseQuery");                             // frmInvoice_CloseQuery: asks before discarding changes

        // ---- Header: every field shares one onChange handler, which sets the dirty flag ----
        void Caption(string name, string caption, int x, int y, int width)
        {
            d.Place("Label", "frmInvoice", x, y + 4, width, 20);
            d.Name(name);
            d.Set("caption", caption);
        }
        Caption("lblClient", "Client", 12, 12, 70);
        d.Place("ComboBox", "frmInvoice", 88, 12, 300, 26);
        d.Name("cboClient");
        d.Set("style", "dropDown");
        d.Set("dropDownCount", "12");
        d.Handler("onChange", "Header_Change");
        Caption("lblNumber", "Number", 412, 12, 64);
        d.Place("Edit", "frmInvoice", 480, 12, 128, 26);
        d.Name("edtNumber");
        d.Handler("onChange", "Header_Change");

        Caption("lblIssued", "Issued", 12, 44, 70);
        d.Place("DateTimePicker", "frmInvoice", 88, 44, 136, 26);
        d.Name("dtpIssued");
        d.Handler("onChange", "Header_Change");
        Caption("lblDue", "Due", 240, 44, 40);
        d.Place("DateTimePicker", "frmInvoice", 284, 44, 136, 26);   // 4 px right of the 8 px grid: Alt held, as for any free point
        d.Name("dtpDue");
        d.Handler("onChange", "Header_Change");
        Caption("lblStatus", "Status", 436, 44, 40);
        d.Place("ComboBox", "frmInvoice", 480, 44, 128, 26);
        d.Name("cboStatus");
        d.Strings("items", "Draft", "Sent", "Paid");
        d.Handler("onChange", "Header_Change");

        // ---- Lines: a string grid edited in place; the fixed column numbers the lines, the last row adds one ----
        d.Place("StringGrid", "frmInvoice", 12, 84, 736, 280);
        d.Name("grdLines");
        d.Anchors("left", "top", "right", "bottom");
        d.Columns(new("#", 36, TextAlign.Right), new("Description", 340), new("Qty", 72, TextAlign.Right),
            new("Unit price", 100, TextAlign.Right), new("Tax %", 64, TextAlign.Right), new("Amount", 110, TextAlign.Right));
        d.Set("rowCount", "2");
        d.Set("editing", "true");
        d.Set("wantTabs", "true");
        d.Set("colSizing", "true");
        d.Handler("onGetEditText");
        d.Handler("onSetEditText");

        d.Place("Button", "frmInvoice", 12, 372, 104, 28);
        d.Name("btnDeleteLine");
        d.Set("caption", "Delete Line");
        d.Anchors("left", "bottom");
        d.Handler("onClick");
        d.Place("Label", "frmInvoice", 128, 376, 360, 20);   // what was wrong with the last value typed into the grid
        d.Name("lblProblem");
        d.Set("caption", "");
        d.Set("ellipsis", "true");
        d.Anchors("left", "right", "bottom");

        // ---- Totals (right) and notes (left) ----
        void Total(string name, string caption, int y)
        {
            d.Place("Label", "frmInvoice", 504, y, 100, 20);
            d.Name(name + "Caption");
            d.Set("caption", caption);
            d.Set("alignment", "right");
            d.Anchors("right", "bottom");
            d.Place("Label", "frmInvoice", 608, y, 140, 20);
            d.Name(name);
            d.Set("caption", "0.00");
            d.Set("alignment", "right");
            d.Anchors("right", "bottom");
        }
        Total("lblSubtotal", "Subtotal", 412);
        Total("lblTax", "Tax", 436);
        Total("lblTotal", "Total", 460);
        d.Select("lblTotal");
        d.Set("fontBold", "true");
        d.Select("lblTotalCaption");
        d.Set("fontBold", "true");

        Caption("lblNotes", "Notes", 12, 408, 70);
        d.Anchors("left", "bottom");
        d.Place("Memo", "frmInvoice", 88, 412, 400, 88);
        d.Name("memNotes");
        d.Anchors("left", "right", "bottom");
        d.Handler("onChange", "Header_Change");

        // ---- Buttons ----
        d.Place("Button", "frmInvoice", 12, 520, 104, 28);
        d.Name("btnExport");
        d.Set("caption", "Export…");
        d.Set("enabled", "false");                             // HTML export arrives in stage 5
        d.Anchors("left", "bottom");
        d.Place("Button", "frmInvoice", 560, 520, 88, 28);
        d.Name("btnSave");
        d.Set("caption", "Save");
        d.Anchors("right", "bottom");
        d.Handler("onClick");
        d.Place("Button", "frmInvoice", 660, 520, 88, 28);
        d.Name("btnCancel");
        d.Set("caption", "Cancel");
        d.Set("cancel", "true");
        d.Set("modalResult", "cancel");
        d.Anchors("right", "bottom");
    }
}
