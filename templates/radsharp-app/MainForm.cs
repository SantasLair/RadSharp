using RadSharp;

namespace RadSharpApp;

/// <summary>
/// Your half of the main form. The controls (btnHello, lblMessage...) and InitializeComponent()
/// are generated from MainForm.radform.json.
/// </summary>
public partial class MainForm : Form
{
    int _clicks;

    public MainForm() => InitializeComponent();

    void btnHello_Click(Control sender)
    {
        _clicks++;
        lblMessage.Caption = $"Clicked {_clicks} time{(_clicks == 1 ? "" : "s")}.";
    }
}
