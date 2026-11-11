namespace RadSharp.Designer;

/// <summary>
/// The Find in Files / Replace in Files controls at the top of the Find Results window: the text, match case / whole word / regex,
/// a file filter and a replacement. It only collects the input and raises <see cref="FindRequested"/>, <see cref="ReplaceRequested"/> and
/// <see cref="StopRequested"/>; the shell does the searching and fills the list below it.
/// </summary>
public sealed class FindPanel : Panel
{
    readonly Edit _find = new() { Name = "edtFindText", Placeholder = "Find in files (Enter)" };
    readonly Edit _replace = new() { Name = "edtReplaceText", Placeholder = "Replace with" };
    readonly Edit _filter = new() { Name = "edtFindFilter", Placeholder = "Files: *.cs;*.radform.json;!*Tests*  (empty: all text files)" };
    readonly CheckBox _case = new() { Name = "chkFindCase", Caption = "Match case" };
    readonly CheckBox _word = new() { Name = "chkFindWord", Caption = "Whole word" };
    readonly CheckBox _regex = new() { Name = "chkFindRegex", Caption = "Regex" };
    readonly Button _findButton = new() { Name = "btnFindAll", Caption = "Find" };
    readonly Button _replaceButton = new() { Name = "btnReplaceAll", Caption = "Replace All" };
    readonly Button _stopButton = new() { Name = "btnFindStop", Caption = "Stop", Enabled = false };
    readonly Label _status = new() { Name = "lblFindStatus" };

    public FindPanel()
    {
        Name = "pnlFindInFiles";
        BorderStyle = BorderStyle.None;
        Align = Align.Top;
        SetBounds(0, 0, 400, 92);
        foreach (Control c in new Control[] { _find, _case, _word, _regex, _findButton, _stopButton, _replace, _replaceButton, _filter, _status }) Add(c);
        _findButton.Click += _ => FindRequested?.Invoke(Options);
        _replaceButton.Click += _ => ReplaceRequested?.Invoke(Options, Replacement);
        _stopButton.Click += _ => StopRequested?.Invoke();
        _find.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.Modifiers == KeyModifiers.None) { FindRequested?.Invoke(Options); e.Handled = true; } };
        _replace.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.Modifiers == KeyModifiers.None) { ReplaceRequested?.Invoke(Options, Replacement); e.Handled = true; } };
        _filter.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.Modifiers == KeyModifiers.None) { FindRequested?.Invoke(Options); e.Handled = true; } };
    }

    public Edit FindBox => _find;
    public Edit ReplaceBox => _replace;
    public Edit FilterBox => _filter;
    public CheckBox MatchCaseBox => _case;
    public CheckBox WholeWordBox => _word;
    public CheckBox RegexBox => _regex;
    public Button FindButton => _findButton;
    public Button ReplaceButton => _replaceButton;
    public Button StopButton => _stopButton;

    public FindOptions Options => new(_find.Text, _case.Checked, _word.Checked, _regex.Checked, _filter.Text.Trim());
    public string Replacement => _replace.Text;

    /// <summary>The line of status text (match counts, errors).</summary>
    public string Status { get => _status.Caption; set => _status.Caption = value; }

    /// <summary>A search is running: Stop is available, Find and Replace wait.</summary>
    public bool Busy
    {
        get => _stopButton.Enabled;
        set
        {
            _stopButton.Enabled = value;
            _findButton.Enabled = !value;
            _replaceButton.Enabled = !value;
        }
    }

    public event Action<FindOptions>? FindRequested;
    public event Action<FindOptions, string>? ReplaceRequested;
    public event Action? StopRequested;

    protected override void OnResize()
    {
        base.OnResize();
        int pad = Px(6), h = Px(24), gap = Px(6);
        int y1 = Px(4), y2 = y1 + h + Px(4), y3 = y2 + h + Px(4);
        int w = Width;
        int btn = Px(92);

        // Row 1: the text, the three switches, Find and Stop.
        int right = w - pad;
        _stopButton.SetBounds(right - Px(60), y1, Px(60), h); right -= Px(60) + gap;
        _findButton.SetBounds(right - btn, y1, btn, h); right -= btn + gap;
        _regex.SetBounds(right - Px(84), y1, Px(84), h); right -= Px(84);
        _word.SetBounds(right - Px(112), y1, Px(112), h); right -= Px(112);
        _case.SetBounds(right - Px(110), y1, Px(110), h); right -= Px(110) + gap;
        _find.SetBounds(pad, y1, Math.Max(Px(80), right - pad), h);

        // Row 2: the replacement and Replace All; row 3: the filter and the status.
        _replaceButton.SetBounds(w - pad - btn, y2, btn, h);
        _replace.SetBounds(pad, y2, Math.Max(Px(80), w - 2 * pad - btn - gap), h);
        int statusWidth = Math.Min(Px(260), w / 3);
        _status.SetBounds(w - pad - statusWidth, y3, statusWidth, h);
        _filter.SetBounds(pad, y3, Math.Max(Px(80), w - 2 * pad - statusWidth - gap), h);
        int height = y3 + h + Px(6);
        if (Height != height) Height = height;
    }
}
