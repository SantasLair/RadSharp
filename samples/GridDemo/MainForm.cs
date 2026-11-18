using System.Diagnostics;
using System.Globalization;
using RadSharp;
using RadSharp.Drawing;

namespace GridDemo;

/// <summary>
/// A 100 000-row StringGrid in virtual mode: the grid stores nothing per row. The data lives in plain arrays here;
/// <c>GetCell</c> supplies each visible cell's text, <c>ColumnClick</c> sorts a row-order array (AutoSort has already
/// moved the sort arrow), and <c>SetEditText</c> validates and stores edits.
/// </summary>
public partial class MainForm : Form
{
    const int Count = 100_000;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly string[] _names = new string[Count], _cities = new string[Count];
    readonly int[] _qty = new int[Count];
    readonly decimal[] _price = new decimal[Count];
    readonly DateOnly[] _date = new DateOnly[Count];
    int[] _order = [.. Enumerable.Range(0, Count)];   // grid row r shows item _order[r - 1]

    public MainForm()
    {
        InitializeComponent();
        Generate();
        grdData.Columns.Add("#", 70, TextAlign.Right).MinWidth = 40;
        grdData.Columns.Add("Name", 160).MinWidth = 60;
        grdData.Columns.Add("City", 130);
        grdData.Columns.Add("Qty", 70, TextAlign.Right);
        grdData.Columns.Add("Price", 90, TextAlign.Right);
        grdData.Columns.Add("Date", 110);
        ShowStatus($"{Count:N0} rows; the grid stores none of them");
    }

    void Generate()
    {
        string[] first = ["Ada", "Alan", "Barbara", "Claude", "Donald", "Edsger", "Frances", "Grace", "Ken", "Linus", "Margaret", "Niklaus", "Radia", "Tim"];
        string[] last = ["Lovelace", "Turing", "Liskov", "Shannon", "Knuth", "Dijkstra", "Allen", "Hopper", "Thompson", "Torvalds", "Hamilton", "Wirth", "Perlman", "Berners-Lee"];
        string[] cities = ["Amsterdam", "Berlin", "Cairo", "Dublin", "Helsinki", "Lagos", "Lima", "Oslo", "Osaka", "Quebec", "Seoul", "Sydney", "Toronto", "Zurich"];
        var rnd = new Random(14);
        var start = new DateOnly(2020, 1, 1);
        for (int i = 0; i < Count; i++)
        {
            _names[i] = $"{first[rnd.Next(first.Length)]} {last[rnd.Next(last.Length)]}";
            _cities[i] = cities[rnd.Next(cities.Length)];
            _qty[i] = rnd.Next(1, 500);
            _price[i] = rnd.Next(100, 100_000) / 100m;
            _date[i] = start.AddDays(rnd.Next(0, 2000));
        }
    }

    string CellText(int item, int col) => col switch
    {
        1 => _names[item],
        2 => _cities[item],
        3 => _qty[item].ToString(Inv),
        4 => _price[item].ToString("0.00", Inv),
        5 => _date[item].ToString("yyyy-MM-dd", Inv),
        _ => "",
    };

    void grdData_GetCell(Control sender, GridTextEventArgs e)
    {
        if (e.Row == 0) return;   // the header titles come from Columns
        e.Text = e.Col == 0 ? e.Row.ToString("N0", Inv) : CellText(_order[e.Row - 1], e.Col);
    }

    void grdData_SetEditText(Control sender, GridTextEventArgs e)
    {
        int item = _order[e.Row - 1];
        string text = e.Text.Trim();
        switch (e.Col)
        {
            case 1: _names[item] = text; break;
            case 2: _cities[item] = text; break;
            case 3 when int.TryParse(text, NumberStyles.Integer, Inv, out var q) && q >= 0: _qty[item] = q; break;
            case 4 when decimal.TryParse(text, NumberStyles.Number, Inv, out var p) && p >= 0: _price[item] = p; break;
            case 5 when DateOnly.TryParseExact(text, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d): _date[item] = d; break;
            default:
                e.Accept = false;   // keeps the editor open
                ShowStatus($"\"{text}\" is not a valid {grdData.Columns[e.Col].Title}; fix it or press Esc");
                return;
        }
        ShowStatus($"Row {e.Row:N0}: {grdData.Columns[e.Col].Title} set to \"{text}\"");
    }

    void grdData_ColumnClick(Control sender, int column)
    {
        if (column < 1) return;
        var sw = Stopwatch.StartNew();
        bool descending = grdData.SortDescending;
        Comparison<int> by = column switch
        {
            1 => (a, b) => string.Compare(_names[a], _names[b], StringComparison.CurrentCultureIgnoreCase),
            2 => (a, b) => string.Compare(_cities[a], _cities[b], StringComparison.CurrentCultureIgnoreCase),
            3 => (a, b) => _qty[a].CompareTo(_qty[b]),
            4 => (a, b) => _price[a].CompareTo(_price[b]),
            _ => (a, b) => _date[a].CompareTo(_date[b]),
        };
        // Stable: ties keep their current order.
        var rank = new int[Count];
        for (int r = 0; r < Count; r++) rank[_order[r]] = r;
        var next = (int[])_order.Clone();
        Array.Sort(next, (a, b) =>
        {
            int c = by(a, b);
            if (descending) c = -c;
            return c != 0 ? c : rank[a].CompareTo(rank[b]);
        });
        _order = next;
        grdData.Invalidate();
        ShowStatus($"Sorted {Count:N0} rows by {grdData.Columns[column].Title}, {(descending ? "descending" : "ascending")}, in {sw.ElapsedMilliseconds} ms");
    }

    void grdData_Change(Control sender)
    {
        if (grdData.Col < 0) return;
        long cells = grdData.SelectedRanges.Sum(r => (long)r.ColCount * r.RowCount);
        ShowStatus($"Row {grdData.Row:N0}, {grdData.Columns[grdData.Col].Title}: {cells:N0} cell(s) in {grdData.Selection.Ranges.Count} range(s)");
    }

    void ShowStatus(string text) => lblStatus.Caption = text;
}
