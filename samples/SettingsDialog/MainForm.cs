using RadSharp;

namespace SettingsDialog;

public sealed record Settings(string User, string Theme, bool Autosave, int Volume, int History);

/// <summary>Opens <see cref="SettingsForm"/> modally and shows what came back (Delphi's ShowModal = mrOk pattern).</summary>
public partial class MainForm : Form
{
    Settings _settings = new("", "Light", false, 7, 50);

    public MainForm()
    {
        InitializeComponent();
        ShowSettings();
    }

    void btnSettings_Click(Control sender)
    {
        var dialog = new SettingsForm { Owner = this };
        dialog.Load(_settings);
        if (dialog.ShowModal() == ModalResult.Ok)
        {
            _settings = dialog.Result;
            lblSummary.Caption = "Settings saved.";
        }
        else lblSummary.Caption = "Cancelled — nothing changed.";
        ShowSettings();
    }

    void ShowSettings() =>
        lblDetails.Caption = $"{(_settings.User.Length > 0 ? _settings.User : "(no name)")}, {_settings.Theme} theme, " +
                             $"autosave {(_settings.Autosave ? "on" : "off")}, volume {_settings.Volume}, history {_settings.History}";
}
