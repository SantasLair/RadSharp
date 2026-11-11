using RadSharp.Design.Code;
using RadSharp.Drawing;

namespace RadSharp.Designer;

/// <summary>
/// The Problems tool window: the messages of every <see cref="IProblemSource"/> in a <see cref="ProblemStore"/>
/// (build results, later live diagnostics), one row each with a severity icon, code, message and file:line.
/// Filtered by severity and by "current document only"; double-click (or Enter) navigates through
/// <see cref="Navigate"/>, which the designer points at its <see cref="ICodeNavigator"/>.
/// </summary>
public sealed class ProblemsWindow : Control
{
    readonly ProblemStore _store;
    readonly CheckBox _errors = new() { Name = "chkErrors", Checked = true };
    readonly CheckBox _warnings = new() { Name = "chkWarnings", Checked = true };
    readonly CheckBox _info = new() { Name = "chkInfo", Checked = true };
    readonly CheckBox _current = new() { Name = "chkCurrentDocument", Caption = "Current document only" };
    readonly RowList _list = new() { Name = "lstProblemRows", ItemHeight = 22, BorderStyle = BorderStyle.None };
    IReadOnlyList<Problem> _rows = [];

    public ProblemsWindow(ProblemStore store)
    {
        _store = store;
        Name = "pnlProblemsWindow";
        TabStop = false;
        foreach (var c in new Control[] { _errors, _warnings, _info, _current, _list }) Add(c);
        _store.Changed += Refresh;
        foreach (var chk in new[] { _errors, _warnings, _info, _current }) chk.Change += _ => Refresh();
        _list.DrawItem += DrawRow;
        _list.DoubleClick += _ => Activate(_list.ItemIndex);
        _list.EnterPressed += () => Activate(_list.ItemIndex);
        Refresh();
    }

    /// <summary>Shows a place in the code (the designer's <see cref="ICodeNavigator"/>); returns whether it could.</summary>
    public Func<BuildMessage, bool>? Navigate { get; set; }

    /// <summary>The file of the active document, for "current document only".</summary>
    public Func<string?>? CurrentFile { get; set; }

    public bool ShowErrors { get => _errors.Checked; set => _errors.Checked = value; }
    public bool ShowWarnings { get => _warnings.Checked; set => _warnings.Checked = value; }
    public bool ShowInfo { get => _info.Checked; set => _info.Checked = value; }
    public bool CurrentDocumentOnly { get => _current.Checked; set => _current.Checked = value; }

    /// <summary>The rows shown (after filtering), in order.</summary>
    public IReadOnlyList<Problem> Rows => _rows;
    public int SelectedIndex { get => _list.ItemIndex; set => _list.ItemIndex = value; }
    public ListBox List => _list;

    /// <summary>Raised after the rows were rebuilt.</summary>
    public event Action? RowsChanged;

    /// <summary>Re-reads the store and the filters (also when the active document changed).</summary>
    public void Refresh()
    {
        var current = CurrentDocumentOnly ? CurrentFile?.Invoke() : null;
        string? full = current != null ? Path.GetFullPath(current) : null;
        _rows =
        [
            .. _store.All.Where(p => p.Message.SeverityOf() switch
            {
                ProblemSeverity.Error => ShowErrors,
                ProblemSeverity.Warning => ShowWarnings,
                _ => ShowInfo,
            } && (!CurrentDocumentOnly || (full != null && p.Message.File.Length > 0
                && string.Equals(Path.GetFullPath(p.Message.File), full, StringComparison.OrdinalIgnoreCase)))),
        ];
        int selected = _list.ItemIndex;
        _list.Items = [.. _rows.Select(Describe)];
        if (selected >= _rows.Count) _list.ItemIndex = _rows.Count - 1;
        _errors.Caption = $"Errors ({_store.Count(ProblemSeverity.Error)})";
        _warnings.Caption = $"Warnings ({_store.Count(ProblemSeverity.Warning)})";
        _info.Caption = $"Info ({_store.Count(ProblemSeverity.Info)})";
        Invalidate();
        RowsChanged?.Invoke();
    }

    static string Describe(Problem p)
    {
        var m = p.Message;
        var place = m.File.Length > 0 ? $"{Path.GetFileName(m.File)}({m.Line})" : "";
        return $"{m.SeverityOf()} {m.Id}: {m.Message} {place}".TrimEnd();
    }

    /// <summary>Navigates to the row's file and line (what double-click does). False if the row has no file or navigation failed.</summary>
    public bool Activate(int index)
    {
        if (index < 0 || index >= _rows.Count) return false;
        var m = _rows[index].Message;
        return m.File.Length > 0 && Navigate?.Invoke(m) == true;
    }

    protected override void OnResize()
    {
        base.OnResize();
        int h = Px(24), x = Px(6);
        foreach (var (chk, w) in new[] { (_errors, 100), (_warnings, 112), (_info, 80), (_current, 180) })
        {
            chk.SetBounds(x, Px(3), Px(w), h);
            x += Px(w) + Px(8);
        }
        _list.SetBounds(0, Px(30), Width, Math.Max(0, Height - Px(30)));
    }

    void DrawRow(Control sender, Canvas canvas, int index, Rect row, bool selected)
    {
        if (index >= _rows.Count) return;
        var t = canvas.Theme;
        var m = _rows[index].Message;
        if (selected) canvas.FillRect(row, _list.Focused ? t.Selection : t.SelectionInactive);
        int pad = canvas.Px(6), icon = canvas.Px(12);
        var ir = new Rect(row.X + pad, row.Y + (row.Height - icon) / 2, icon, icon);
        switch (m.SeverityOf())
        {
            case ProblemSeverity.Error:
                canvas.FillEllipse(ir, Color.FromHex("#D13438"));
                canvas.DrawLine(new PointF(ir.X + icon * 0.3f, ir.Y + icon * 0.3f), new PointF(ir.X + icon * 0.7f, ir.Y + icon * 0.7f), Color.FromHex("#FFFFFF"), canvas.Px(1));
                canvas.DrawLine(new PointF(ir.X + icon * 0.7f, ir.Y + icon * 0.3f), new PointF(ir.X + icon * 0.3f, ir.Y + icon * 0.7f), Color.FromHex("#FFFFFF"), canvas.Px(1));
                break;
            case ProblemSeverity.Warning:
                canvas.FillPolygon([new PointF(ir.X + icon / 2f, ir.Y), new PointF(ir.X + icon, ir.Y + icon), new PointF(ir.X, ir.Y + icon)], Color.FromHex("#E8A200"));
                break;
            default:
                canvas.FillEllipse(ir, Color.FromHex("#2F7FD8"));
                break;
        }
        int x = ir.X + icon + pad;
        var font = _list.EffectiveFont;
        int codeW = canvas.Px(70), placeW = canvas.Px(190);
        canvas.DrawText(m.Id, font, t.TextDisabled, new Rect(x, row.Y, codeW, row.Height), TextAlign.Left, VerticalAlign.Center, ellipsis: true);
        x += codeW;
        int right = row.X + row.Width - pad;
        var place = m.File.Length > 0 ? $"{Path.GetFileName(m.File)}:{m.Line}" : "";
        canvas.DrawText(place, font, t.TextDisabled, new Rect(right - placeW, row.Y, placeW, row.Height), TextAlign.Right, VerticalAlign.Center, ellipsis: true);
        canvas.DrawText(m.Message, font, t.Text, new Rect(x, row.Y, Math.Max(0, right - placeW - x - pad), row.Height), TextAlign.Left, VerticalAlign.Center, ellipsis: true);
    }
}

/// <summary>A list box that also reports Enter (the Problems window navigates on it).</summary>
sealed class RowList : ListBox
{
    public event Action? EnterPressed;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Enter && e.Modifiers == KeyModifiers.None) { EnterPressed?.Invoke(); e.Handled = true; }
    }
}
