namespace RadSharp.Tests.Forms;

/// <summary>A form whose other half is generated from OrderForm.radform.json by the form generator.</summary>
public partial class OrderForm : Form
{
    public readonly List<string> Log = [];

    public OrderForm() => InitializeComponent();

    void frmOrder_Resize(Control sender) => Log.Add($"resize {Width}x{Height}");
    void chkGift_Change(Control sender) => Log.Add($"gift {chkGift.Checked}");
    void btnOrder_Click(Control sender) => Log.Add("order");
}
