using RadSharp;

namespace SettingsDialog;

public sealed class Settings
{
    public string User { get; set; } = "";
    public string Theme { get; set; } = "Light";
    public bool Autosave { get; set; }
    public int Volume { get; set; } = 7;
    public int History { get; set; } = 50;
}

/// <summary>Opens <see cref="SettingsForm"/> modally and shows what came back (Delphi's ShowModal = mrOk pattern).</summary>
public partial class MainForm : Form
{
    readonly Settings _settings = new();

    public MainForm()
    {
        InitializeComponent();
        ShowSettings();
    }

    void btnSettings_Click(Control sender)
    {
        var dialog = new SettingsForm { Owner = this };
        lblSummary.Caption = dialog.Edit(_settings) ? "Settings saved." : "Cancelled — nothing changed.";
        ShowSettings();
    }

    void ShowSettings() =>
        lblDetails.Caption = $"{(_settings.User.Length > 0 ? _settings.User : "(no name)")}, {_settings.Theme} theme, " +
                             $"autosave {(_settings.Autosave ? "on" : "off")}, volume {_settings.Volume}, history {_settings.History}";
}
