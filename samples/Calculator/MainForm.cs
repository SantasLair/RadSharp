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

    string Display
    {
        get => lblDisplay.Caption;
        set => lblDisplay.Caption = value;
    }

    decimal Value => decimal.Parse(Display, CultureInfo.InvariantCulture);

    void frmCalculator_Show(Control sender) => btnEquals.Focus();

    void digit_Click(Control sender)
    {
        var key = ((Button)sender).Caption;
        if (_startNew) { Display = key == "." ? "0." : key; _startNew = false; return; }
        if (key == "." && Display.Contains('.')) return;
        if (Display.Length >= 16) return;
        Display = Display == "0" && key != "." ? key : Display + key;
    }

    void op_Click(Control sender)
    {
        Calculate();
        _pending = ((Button)sender).Caption;
        lblHistory.Caption = $"{Format(_accumulator)} {_pending}";
    }

    void btnEquals_Click(Control sender)
    {
        var expression = _pending != null ? $"{Format(_accumulator)} {_pending} {Display} =" : "";
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
        Display = Format(_accumulator);
        _startNew = true;
    }

    static string Format(decimal d) => d.ToString("0.############", CultureInfo.InvariantCulture);

    void btnClear_Click(Control sender)
    {
        _accumulator = 0;
        _pending = null;
        _startNew = true;
        Display = "0";
        lblHistory.Caption = "";
    }

    void btnSign_Click(Control sender) => Display = Format(-Value);
    void btnPercent_Click(Control sender) { Display = Format(Value / 100); _startNew = true; }

    void btnBack_Click(Control sender)
    {
        if (_startNew) return;
        Display = Display.Length > 1 ? Display[..^1] : "0";
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
