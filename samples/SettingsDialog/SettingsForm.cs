using RadSharp;

namespace SettingsDialog;

/// <summary>A second form in the same project; its OK/Cancel buttons set ModalResult, which closes it.</summary>
public partial class SettingsForm : Form
{
    public SettingsForm() => InitializeComponent();

    public void Load(Settings s)
    {
        edtUser.Text = s.User;
        cboTheme.ItemIndex = Array.IndexOf(cboTheme.Items, s.Theme);
        chkAutosave.Checked = s.Autosave;
        trkVolume.Position = s.Volume;
        spnHistory.Value = s.History;
    }

    public Settings Result => new(edtUser.Text.Trim(), cboTheme.Text, chkAutosave.Checked, trkVolume.Position, spnHistory.Value);
}
