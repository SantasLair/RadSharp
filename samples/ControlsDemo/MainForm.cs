using System.Globalization;
using RadSharp;
using RadSharp.Drawing;
using SkiaSharp;

namespace ControlsDemo;

/// <summary>
/// Shows the list, tree and grid controls. Controls, columns, nodes and items are laid out in MainForm.radform.json;
/// this file holds only what has to be generated (100 000 virtual rows, lazy children, icons) and the event handlers.
/// </summary>
public partial class MainForm : Form
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public MainForm()
    {
        InitializeComponent();
        imgIcons.Images = [Icon(new SKColor(230, 180, 40), 0), Icon(new SKColor(60, 130, 230), 1), Icon(new SKColor(120, 160, 120), 2), Icon(new SKColor(200, 80, 80), 3)];
        SetUpListView();
        lblChecks.Caption = "Checked: Syntax highlighting, Line numbers";
        lblCombos.Caption = "Pick or type a fruit; autoComplete fills in the rest.";
        calDemo_Change(calDemo);
        dtp_Change(dtpShort);
    }

    /// <summary>A 16x16 PNG data URI (an ImageList takes file paths, resources or data URIs): shape 0 folder, 1 disc, 2 page, 3 diamond.</summary>
    static string Icon(SKColor color, int shape)
    {
        using var bmp = new SKBitmap(16, 16);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.Transparent);
            using var fill = new SKPaint { Color = color, IsAntialias = true };
            switch (shape)
            {
                case 0: c.DrawRect(1, 4, 14, 10, fill); c.DrawRect(1, 2, 6, 3, fill); break;
                case 1: c.DrawCircle(8, 8, 7, fill); break;
                case 2: c.DrawRect(3, 1, 10, 14, fill); break;
                default: c.DrawRoundRect(2, 2, 12, 12, 3, 3, fill); break;
            }
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return Picture.ToDataUri(data.ToArray());
    }

    // ---- ListView tab: 100 000 virtual rows -------------------------------------------------------------------

    const int Count = 100_000;
    static readonly string[] Kinds = ["Folder", "Image", "Document", "Archive"];
    static readonly string[] Stems = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima"];

    readonly string[] _name = new string[Count];
    readonly int[] _kind = new int[Count], _size = new int[Count];
    readonly DateOnly[] _date = new DateOnly[Count];
    readonly HashSet<int> _checked = [];
    int[] _order = [];   // list row r shows item _order[r]

    void SetUpListView()
    {
        var rnd = new Random(14);
        for (int i = 0; i < Count; i++)
        {
            _name[i] = $"{Stems[rnd.Next(Stems.Length)]}-{rnd.Next(10_000):D4}";
            _kind[i] = rnd.Next(Kinds.Length);
            _size[i] = rnd.Next(1, 50_000);
            _date[i] = new DateOnly(2022, 1, 1).AddDays(rnd.Next(1500));
        }
        SortBy(0, false);   // matches sortColumn 0 from the form file
        lvFiles.ItemCount = Count;
        UpdateListStatus();
    }

    void lvFiles_GetItem(Control sender, ListViewItemEventArgs e)
    {
        int i = _order[e.Index];
        e.Item.Caption = _name[i];
        e.Item.SubItems.AddRange([Kinds[_kind[i]], _size[i].ToString("N0", Inv), _date[i].ToString("yyyy-MM-dd", Inv), i.ToString(Inv)]);
        e.Item.ImageIndex = _kind[i];
        e.Item.Checked = _checked.Contains(i);
    }

    // Type-ahead matches captions, so scan the Name column forward from the current row, wrapping around.
    void lvFiles_FindItem(Control sender, ListViewFindEventArgs e)
    {
        for (int n = 0; n < Count; n++)
        {
            int row = (e.StartIndex + n) % Count;
            if (_name[_order[row]].StartsWith(e.Text, StringComparison.OrdinalIgnoreCase)) { e.Index = row; return; }
        }
    }

    // AutoSort has already moved the arrow; in virtual mode the handler reorders the data.
    void lvFiles_ColumnClick(Control sender, int column)
    {
        SortBy(column, lvFiles.SortDescending);
        lvFiles.Invalidate();
        UpdateListStatus();
    }

    void SortBy(int column, bool descending)
    {
        Comparison<int> by = column switch
        {
            0 => (a, b) => string.Compare(_name[a], _name[b], StringComparison.OrdinalIgnoreCase),
            1 => (a, b) => string.Compare(Kinds[_kind[a]], Kinds[_kind[b]], StringComparison.Ordinal),
            2 => (a, b) => _size[a].CompareTo(_size[b]),
            3 => (a, b) => _date[a].CompareTo(_date[b]),
            _ => (a, b) => a.CompareTo(b),
        };
        // Ties break on the id, so the order is deterministic.
        var ids = new int[Count];
        for (int r = 0; r < Count; r++) ids[r] = r;
        Array.Sort(ids, (a, b) => { int c = by(a, b); if (descending) c = -c; return c != 0 ? c : a.CompareTo(b); });
        _order = ids;
    }

    void lvFiles_ItemChecked(Control sender, int index, bool isChecked)
    {
        int i = _order[index];
        if (isChecked) _checked.Add(i); else _checked.Remove(i);
        UpdateListStatus();
    }

    void lvFiles_Change(Control sender) => UpdateListStatus();

    void UpdateListStatus()
    {
        string sort = lvFiles.SortColumn >= 0 ? $"{lvFiles.Columns[lvFiles.SortColumn].Title} {(lvFiles.SortDescending ? "descending" : "ascending")}" : "none";
        lblLvStatus.Caption = $"{Count:N0} rows | selected {lvFiles.SelectedCount:N0} | checked {_checked.Count:N0} | sorted by {sort}";
    }

    // ---- TreeView tab -----------------------------------------------------------------------------------------

    void tvTree_Expanding(Control sender, TreeNodeCancelEventArgs e)
    {
        var node = e.Node;
        if (node.Nodes.Count > 0) return;                    // already loaded
        if (node.Text.StartsWith("Empty drive")) return;     // adds nothing, so the arrow disappears
        int level = node.Level;
        for (int i = 1; i <= 4; i++)
        {
            bool folder = level < 3 && i <= 2;
            var child = node.Add(folder ? $"Folder {level + 1}.{i}" : $"file{i}.dat");
            child.ImageIndex = folder ? 0 : 2;
            child.HasChildren = folder;   // folders load their own children when expanded
        }
    }

    void tvTree_NodeChecked(Control sender, TreeNode node) =>
        lblTvStatus.Caption = $"{PathOf(node)} is now {(node.Checked ? "checked" : "unchecked")}";

    void tvTree_Change(Control sender) =>
        lblTvStatus.Caption = tvTree.Selected is { } n ? $"Selected: {PathOf(n)}" : "Selected: (none)";

    void tvTree_Edited(Control sender, TreeEditEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Text)) return;
        e.Accept = false;   // the editor stays open
        lblTvStatus.Caption = "A node needs a name; type one or press Esc";
    }

    static string PathOf(TreeNode n) => n.Parent == null ? n.Text : $"{PathOf(n.Parent)} > {n.Text}";

    // ---- Lists tab --------------------------------------------------------------------------------------------

    void clbOptions_ClickCheck(Control sender)
    {
        var on = clbOptions.CheckedIndices.Select(i => clbOptions.Items[i]).ToList();
        int grayed = Enumerable.Range(0, clbOptions.Count).Count(i => clbOptions.States[i] == CheckState.Grayed);
        lblChecks.Caption = $"Checked: {(on.Count == 0 ? "none" : string.Join(", ", on))}; {grayed} grayed";
    }

    void combo_Change(Control sender) =>
        lblCombos.Caption = $"Fruit: \"{cboFruit.Text}\"  Country: {cboCountry.Text}";

    static bool IsHeader(string item) => item.StartsWith("# ");   // "# " marks a section header row

    void lstSections_MeasureItem(Control sender, MeasureItemEventArgs e)
    {
        if (IsHeader(lstSections.Items[e.Index])) e.Height = 36;
    }

    void lstSections_DrawItem(Control sender, Canvas canvas, int index, Rect rect, bool selected)
    {
        var t = canvas.Theme;
        string text = lstSections.Items[index];
        bool header = IsHeader(text);
        if (selected) canvas.FillRect(rect, t.Selection);
        else if (header) canvas.FillRect(rect, t.SplitterFace);
        int pad = canvas.Px(header ? 8 : 22);
        canvas.DrawText(header ? text[2..] : text, header ? canvas.DefaultBoldFont : canvas.DefaultFont, t.Text,
            new Rect(rect.X + pad, rect.Y, rect.Width - pad, rect.Height), TextAlign.Left, VerticalAlign.Center);
    }

    void lstMulti_Change(Control sender) =>
        lblMulti2.Caption = $"{lstMulti.SelectedCount} selected: {string.Join(", ", lstMulti.SelectedItems)}";

    // ---- DrawGrid tab -----------------------------------------------------------------------------------------

    // A hue per column, lightness per row.
    static Color Swatch(int col, int row)
    {
        var c = SKColor.FromHsl(col * 45f, 70, 85 - row * 13);
        return new Color(c.Red, c.Green, c.Blue);
    }

    void dgPalette_DrawCell(Control sender, Canvas canvas, int col, int row, Rect rect, GridDrawState state)
    {
        int inset = canvas.Px(3);
        var swatch = new Rect(rect.X + inset, rect.Y + inset, rect.Width - 2 * inset, rect.Height - 2 * inset);
        canvas.FillRoundRect(swatch, canvas.Px(4), Swatch(col, row));
        if (state.HasFlag(GridDrawState.Selected))
            canvas.DrawRoundBorder(swatch, canvas.Px(4), canvas.Theme.Accent, canvas.Px(3));
    }

    void dgPalette_Change(Control sender)
    {
        var ranges = dgPalette.SelectedRanges.ToList();
        long cells = ranges.Sum(r => (long)r.ColCount * r.RowCount);
        var c = Swatch(dgPalette.Col, dgPalette.Row);
        lblDgStatus.Caption = $"Current cell ({dgPalette.Col}, {dgPalette.Row}) #{c.R:X2}{c.G:X2}{c.B:X2} | {cells} cell(s) in {ranges.Count} range(s)";
    }

    // ---- Dates tab --------------------------------------------------------------------------------------------

    void calDemo_Change(Control sender) =>
        lblDtCal.Caption = $"Calendar: {(calDemo.SelectedDate is { } d ? d.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture) : "(none)")}";

    void dtp_Change(Control sender) =>
        lblDtPick.Caption = $"Short: {dtpShort.Value:yyyy-MM-dd}   Long: {dtpLong.Value:yyyy-MM-dd}   Optional: {(dtpOptional.Value is { } d ? d.ToString("yyyy-MM-dd", Inv) : "(no date)")}";
}
