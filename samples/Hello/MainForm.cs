using RadSharp;

namespace Hello;

/// <summary>The hand-written half of the form; MainForm.radform.json generates the rest.</summary>
public partial class MainForm : Form
{
    int _clicks;

    public MainForm() => InitializeComponent();

    void btnNew_Click(Control sender) => lblStatus.Caption = $"New clicked {++_clicks} time(s)";

    void btnCancel_Click(Control sender) => lblStatus.Caption = "Cancel clicked";
}
