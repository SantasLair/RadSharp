using RadSharp;

namespace WindowsDemo;

/// <summary>A small modal dialog: its OK button sets ModalResult, which closes it.</summary>
public partial class InfoDialog : Form
{
    public InfoDialog() => InitializeComponent();
}
