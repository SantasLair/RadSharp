using System.Globalization;
using RadSharp;

namespace Calculator;

/// <summary>A pocket calculator: one shared handler for the digit keys, one for the operators (Delphi style).</summary>
public partial class MainForm : Form
{
    decimal _accumulator;
    string? _pending;          // the operator waiting for its second operand
    bool _startNew = true;     // the next digit starts a new number

    public MainForm() => InitializeComponent();

    string Readout
    {
        get => lblDisplay.Caption;
        set => lblDisplay.Caption = value;
    }

    decimal Value => decimal.Parse(Readout, CultureInfo.InvariantCulture);

    void frmCalculator_Show(Control sender) => btnEquals.Focus();

    void digit_Click(Control sender)
    {
        var key = ((Button)sender).Caption;
        if (_startNew) { Readout = key == "." ? "0." : key; _startNew = false; return; }
        if (key == "." && Readout.Contains('.')) return;
        if (Readout.Length >= 16) return;
        Readout = Readout == "0" && key != "." ? key : Readout + key;
    }

    void op_Click(Control sender)
    {
        Calculate();
        _pending = ((Button)sender).Caption;
        lblHistory.Caption = $"{Format(_accumulator)} {_pending}";
    }

    void btnEquals_Click(Control sender)
    {
        var expression = _pending != null ? $"{Format(_accumulator)} {_pending} {Readout} =" : "";
        Calculate();
        _pending = null;
        lblHistory.Caption = expression;
    }

    void Calculate()
    {
        if (_startNew && _pending != null) return;   // two operators in a row: just switch
        var v = Value;
        _accumulator = _pending switch
        {
            "+" => _accumulator + v,
            "−" => _accumulator - v,
            "×" => _accumulator * v,
            "÷" when v == 0 => throw new DivideByZeroException(),
            "÷" => _accumulator / v,
            _ => v,
        };
        Readout = Format(_accumulator);
        _startNew = true;
    }

    static string Format(decimal d) => d.ToString("0.############", CultureInfo.InvariantCulture);

    void btnClear_Click(Control sender)
    {
        _accumulator = 0;
        _pending = null;
        _startNew = true;
        Readout = "0";
        lblHistory.Caption = "";
    }

    void btnSign_Click(Control sender) => Readout = Format(-Value);
    void btnPercent_Click(Control sender) { Readout = Format(Value / 100); _startNew = true; }

    void btnBack_Click(Control sender)
    {
        if (_startNew) return;
        Readout = Readout.Length > 1 ? Readout[..^1] : "0";
    }

    /// <summary>Typing digits and operators works too.</summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        foreach (var ch in e.Text)
        {
            var key = ch switch { '*' => "×", '/' => "÷", '-' => "−", ',' => ".", _ => ch.ToString() };
            var button = Descendants().OfType<Button>().FirstOrDefault(b => b.Caption == key);
            button?.PerformClick();
        }
    }
}
