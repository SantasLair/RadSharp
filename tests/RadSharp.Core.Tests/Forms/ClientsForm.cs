using System.Globalization;

namespace RadSharp.Tests.Forms;

public sealed class Client
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public bool Active { get; set; } = true;
    public decimal Rate { get; set; }
    public DateOnly Since { get; set; } = new(2026, 1, 1);
    public int TermsDays { get; set; } = 30;
}

/// <summary>
/// The M15 done-when as a form: a master list, detail controls bound to the selected client with no copy-in/copy-out, and a
/// Save action (button, tool button, menu item, Mod+S) enabled by the binder's dirty flag. Its other half is generated from
/// ClientsForm.radform.json.
/// </summary>
public partial class ClientsForm : Form
{
    public readonly List<Client> Clients =
    [
        new() { Name = "Acme Ltd", Email = "office@acme.test", Rate = 95m, TermsDays = 30 },
        new() { Name = "Globex", Email = "ap@globex.test", Active = false, Rate = 120.5m, TermsDays = 14, Since = new(2025, 6, 1) },
    ];

    public readonly Binder<Client> Binder;   // owned by the form: disposed when it closes
    public int Saves;

    public ClientsForm()
    {
        InitializeComponent();
        Binder = new Binder<Client>(this);
        Binder.Bind(edtName, c => c.Name, (c, v) => c.Name = v).Validate(v => v.Trim().Length > 0 ? null : "Enter a name.");
        Binder.Bind(edtEmail, c => c.Email, (c, v) => c.Email = v);
        Binder.Bind(chkActive, c => c.Active, (c, v) => c.Active = v);
        Binder.Bind(edtRate, c => c.Rate, (c, v) => c.Rate = v, BindFormat.Decimal("0.00", CultureInfo.InvariantCulture));
        Binder.Bind(dtpSince, c => c.Since, (c, v) => c.Since = v);
        Binder.Bind(cboTerms, c => c.TermsDays, (c, v) => c.TermsDays = v, [14, 30, 60]);
        ShowList();
        lstClients.ItemIndex = 0;
    }

    void ShowList()
    {
        int at = lstClients.ItemIndex;
        lstClients.Items = [.. Clients.Select(c => c.Name)];
        lstClients.ItemIndex = Math.Min(at, Clients.Count - 1);
    }

    void lstClients_Change(Control sender) =>
        Binder.Current = lstClients.ItemIndex >= 0 && lstClients.ItemIndex < Clients.Count ? Clients[lstClients.ItemIndex] : null;

    void actSave_Update(Control sender) => actSave.Enabled = Binder.IsDirty;

    void actSave_Execute(Control sender)
    {
        if (!Binder.Commit()) { Binder.FocusFirstError(); return; }
        Saves++;
        ShowList();
    }

    void actRevert_Update(Control sender) => actRevert.Enabled = Binder.IsDirty;
    void actRevert_Execute(Control sender) => Binder.Revert();

    void actNew_Execute(Control sender)
    {
        Clients.Add(new Client { Name = "New client" });
        ShowList();
        lstClients.ItemIndex = Clients.Count - 1;
        edtName.Focus();
        edtName.SelectAll();
    }

    void actDelete_Update(Control sender) => actDelete.Enabled = Binder.Current != null;

    void actDelete_Execute(Control sender)
    {
        if (Binder.Current is not { } c) return;
        Clients.Remove(c);
        ShowList();
        lstClients_Change(lstClients);   // the index may not have changed, the client under it has
    }
}
