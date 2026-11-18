using System.Globalization;
using RadSharp;

namespace ClientsDemo;

/// <summary>One client; a plain class (the binder never clones it and writes it only on Save).</summary>
public sealed class Client
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public bool Active { get; set; } = true;
    public decimal Rate { get; set; }
    public DateOnly Since { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public int TermsDays { get; set; } = 30;
    public string Notes { get; set; } = "";
}

/// <summary>
/// Master/detail with <see cref="Binder{T}"/> and actions (docs/binding-and-actions.md): no copy-in/copy-out code, and the
/// Save/Revert/New/Delete commands live in four actions shared by the menu, the tool bar, the buttons and the list's
/// context menu, enabled from one onUpdate each.
/// </summary>
public partial class MainForm : Form
{
    readonly List<Client> _clients =
    [
        new() { Name = "Acme Ltd", Email = "office@acme.example", Rate = 95m, Since = new(2021, 3, 1), Notes = "Pays on time." },
        new() { Name = "Globex", Email = "ap@globex.example", Active = false, Rate = 120.5m, TermsDays = 14, Since = new(2025, 6, 1) },
        new() { Name = "Initech", Email = "billing@initech.example", Rate = 80m, TermsDays = 60, Since = new(2019, 11, 20) },
    ];

    readonly Binder<Client> _binder = new();
    int _shown = -1;   // the list row the binder shows

    public MainForm()
    {
        InitializeComponent();
        _binder.Bind(edtName, c => c.Name, (c, v) => c.Name = v.Trim()).Validate(v => v.Trim().Length > 0 ? null : "Enter a name.");
        _binder.Bind(edtEmail, c => c.Email, (c, v) => c.Email = v.Trim())
               .Validate(v => v.Length == 0 || v.Contains('@') ? null : "Enter an e-mail address, or nothing.");
        _binder.Bind(chkActive, c => c.Active, (c, v) => c.Active = v);
        _binder.Bind(edtRate, c => c.Rate, (c, v) => c.Rate = v, BindFormat.Decimal("0.00", CultureInfo.InvariantCulture))
               .Validate(v => v >= 0 ? null : "The rate can't be negative.");
        _binder.Bind(dtpSince, c => c.Since, (c, v) => c.Since = v);
        _binder.Bind(cboTerms, c => c.TermsDays, (c, v) => c.TermsDays = v, [14, 30, 60]);
        _binder.Bind(memNotes, c => c.Notes, (c, v) => c.Notes = v);
        _binder.DirtyChanged += (_, _) => sbMain.SimpleText = _binder.IsDirty ? "Edited — Save (Ctrl+S) or Revert" : "Ready";
        ShowList();
        lstClients.ItemIndex = 0;
    }

    void ShowList()
    {
        int at = lstClients.ItemIndex;
        lstClients.Items = [.. _clients.Select(c => c.Name)];
        lstClients.ItemIndex = Math.Min(at, _clients.Count - 1);
    }

    void lstClients_Change(Control sender)
    {
        if (lstClients.ItemIndex == _shown && _binder.Current != null) return;
        // Moving to another client saves the edits first; if they don't validate, stay on this one.
        if (_binder.IsDirty && !_binder.Commit())
        {
            lstClients.ItemIndex = _shown;
            Dialogs.ShowMessage(_binder.Errors[0].Message, this);
            _binder.FocusFirstError();
            return;
        }
        if (_binder.Current != null) RefreshNames();
        _shown = lstClients.ItemIndex;
        _binder.Current = _shown >= 0 && _shown < _clients.Count ? _clients[_shown] : null;
    }

    void RefreshNames()
    {
        for (int i = 0; i < _clients.Count; i++)
            if (lstClients.Items[i] != _clients[i].Name) { lstClients.Items = [.. _clients.Select(c => c.Name)]; return; }
    }

    void actSave_Update(Control sender) => actSave.Enabled = _binder.IsDirty;

    void actSave_Execute(Control sender)
    {
        if (!_binder.Commit())
        {
            sbMain.SimpleText = _binder.Errors[0].Message;
            _binder.FocusFirstError();
            return;
        }
        RefreshNames();
        sbMain.SimpleText = "Saved.";
    }

    void actRevert_Update(Control sender) => actRevert.Enabled = _binder.IsDirty;
    void actRevert_Execute(Control sender) => _binder.Revert();

    void actNew_Execute(Control sender)
    {
        if (_binder.IsDirty && !_binder.Commit()) { _binder.FocusFirstError(); return; }
        _clients.Add(new Client { Name = "New client" });
        ShowList();
        lstClients.ItemIndex = _clients.Count - 1;
        edtName.Focus();
        edtName.SelectAll();
    }

    void actDelete_Update(Control sender) => actDelete.Enabled = _binder.Current != null;

    void actDelete_Execute(Control sender)
    {
        if (_binder.Current is not { } c) return;
        _clients.Remove(c);
        _shown = -1;
        _binder.Current = null;   // discard its edits
        ShowList();
        lstClients_Change(lstClients);
    }
}
