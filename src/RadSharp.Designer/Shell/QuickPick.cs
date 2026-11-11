using RadSharp.Drawing;

namespace RadSharp.Designer;

/// <summary>One row of a <see cref="QuickPick"/>: a label, a dimmer detail after it (a folder, a container type), text on the right
/// (a shortcut, a category) and the object it stands for.</summary>
public sealed record QuickPickItem(string Label, string Detail = "", string Right = "", bool Enabled = true, object? Tag = null)
{
    /// <summary>Which characters of <see cref="Label"/> the filter matched (set by the picker).</summary>
    public int[] LabelMatches { get; init; } = [];
}

/// <summary>
/// A popup with a filter box over a list (the command palette, go to file, go to symbol): type to filter, Up/Down/PageUp/PageDown
/// to move, Enter to choose, Esc to close; a click chooses a row. With <see cref="InputOnly"/> it is just the box and a hint
/// (go to line) and Enter submits the text.
/// </summary>
/// <remarks>
/// By default the picker filters <see cref="Items"/> itself, fuzzily, on label then detail (<see cref="FuzzyMatcher"/>). A picker whose
/// list comes from elsewhere (workspace symbols, computed in the background) sets <see cref="FilterLocally"/> to false, listens to
/// <see cref="QueryChanged"/> and calls <see cref="SetItems"/> when it has an answer.
/// </remarks>
public sealed class QuickPick : Control
{
    const int MaxRows = 10;

    readonly Edit _input = new() { Name = "edtQuickPick", AutoSelect = false };
    readonly ListBox _list = new() { Name = "lstQuickPick", ItemHeight = 24, BorderStyle = BorderStyle.None };
    readonly List<QuickPickItem> _all = [];
    List<QuickPickItem> _visible = [];
    Control? _previousFocus;
    Form? _form;
    bool _closing;

    public QuickPick()
    {
        Name = "quickPick";
        SetDefaultSize(580, 60);
        _input.KeyDown += InputKeyDown;
        _input.Change += _ => OnQueryChanged();
        _list.DrawItem += DrawRow;
        _list.Click += _ => { if (_list.ItemIndex >= 0) Accept(); };
        Add(_input);
        Add(_list);
    }

    /// <summary>What the box says when empty.</summary>
    public string Placeholder { get => _input.Placeholder; set => _input.Placeholder = value; }

    /// <summary>Only the box and <see cref="Hint"/>: Enter raises <see cref="Submitted"/>.</summary>
    public bool InputOnly { get; set; }

    /// <summary>In input-only mode: the line under the box, from the text typed so far (null: <see cref="HintText"/>).</summary>
    public Func<string, string>? HintProvider { get; set; }
    public string HintText { get; set; } = "";

    /// <summary>The picker filters <see cref="Items"/> by the text itself. Off: the owner answers <see cref="QueryChanged"/> with <see cref="SetItems"/>.</summary>
    public bool FilterLocally { get; set; } = true;

    /// <summary>Shown instead of an empty list.</summary>
    public string EmptyText { get; set; } = "No matching results";

    /// <summary>The text of the filter box.</summary>
    public string Text { get => _input.Text; set => _input.Text = value ?? ""; }

    public IReadOnlyList<QuickPickItem> Items => _all;

    /// <summary>The rows shown now, best first.</summary>
    public IReadOnlyList<QuickPickItem> Rows => _visible;

    public int SelectedIndex { get => _list.ItemIndex; set => _list.ItemIndex = value; }
    public QuickPickItem? Selected => SelectedIndex >= 0 && SelectedIndex < _visible.Count ? _visible[SelectedIndex] : null;

    /// <summary>The filter box, for tests and callers that preset a selection.</summary>
    public Edit Input => _input;

    /// <summary>Raised when the text changed (also once at open).</summary>
    public event Action<string>? QueryChanged;

    /// <summary>Raised when an enabled row was chosen (Enter or a click); the picker has closed.</summary>
    public event Action<QuickPickItem>? Accepted;

    /// <summary>Raised in input-only mode when Enter was pressed; the picker has closed. Return false from <see cref="Validate"/> to keep it open.</summary>
    public event Action<string>? Submitted;

    /// <summary>Input-only: whether the text can be submitted.</summary>
    public Func<string, bool>? Validate { get; set; }

    /// <summary>Raised after the picker closed without choosing.</summary>
    public event Action? Dismissed;

    /// <summary>Replaces the list (in <see cref="FilterLocally"/> mode this is the whole candidate set; otherwise the rows to show).</summary>
    public void SetItems(IEnumerable<QuickPickItem> items)
    {
        _all.Clear();
        _all.AddRange(items);
        Refilter();
    }

    void OnQueryChanged()
    {
        if (_closing) return;
        if (FilterLocally && !InputOnly) Refilter();
        else if (InputOnly) Relayout();
        QueryChanged?.Invoke(_input.Text);
        Invalidate();
    }

    void Refilter()
    {
        var q = _input.Text.Trim();
        if (!FilterLocally || q.Length == 0)
            _visible = [.. _all.Select(i => i.LabelMatches.Length == 0 ? i : i with { LabelMatches = [] })];
        else
        {
            var ranked = new List<(QuickPickItem Item, int Score)>();
            foreach (var item in _all)
            {
                var onLabel = FuzzyMatcher.Find(q, item.Label);
                if (onLabel is { } l) { ranked.Add((item with { LabelMatches = l.Indices }, l.Score + 20)); continue; }
                var onDetail = item.Detail.Length > 0 ? FuzzyMatcher.Find(q, item.Detail + "/" + item.Label) : null;
                if (onDetail is { } d) ranked.Add((item with { LabelMatches = [] }, d.Score));
            }
            _visible = [.. ranked.OrderByDescending(r => r.Score).Select(r => r.Item)];
        }
        _list.Items = [.. _visible.Select(i => i.Label)];
        _list.ItemIndex = _visible.Count > 0 ? FirstEnabled() : -1;
        Relayout();
        Invalidate();
    }

    int FirstEnabled()
    {
        for (int i = 0; i < _visible.Count; i++) if (_visible[i].Enabled) return i;
        return 0;
    }

    // ---- Opening and closing ----

    /// <summary>Shows the picker near the top of <paramref name="form"/> and focuses its box.</summary>
    public void Open(Form form)
    {
        _form = form;
        _previousFocus = form.FocusedControl;
        _closing = false;
        Refilter();
        Relayout();
        int x = (form.Width - Width) / 2, y = Px(48);
        form.OpenPopup(this, x, y);
        _input.Focus();
        QueryChanged?.Invoke(_input.Text);
    }

    /// <summary>Closes without choosing.</summary>
    public void Dismiss()
    {
        if (!Close()) return;
        Dismissed?.Invoke();
    }

    bool Close()
    {
        if (_closing || _form == null) return false;
        _closing = true;
        var form = _form;
        form.ClosePopup(this);
        if (_previousFocus != null && _previousFocus.OwningForm == form) form.SetFocus(_previousFocus);
        return true;
    }

    protected override void OnPopupClosed()
    {
        base.OnPopupClosed();
        // Closed from outside (a click elsewhere): the same as Esc.
        if (!_closing) { _closing = true; Dismissed?.Invoke(); }
    }

    /// <summary>Chooses the selected row (or, input-only, submits the text).</summary>
    public void Accept()
    {
        if (InputOnly)
        {
            var text = _input.Text;
            if (Validate != null && !Validate(text)) return;
            if (Close()) Submitted?.Invoke(text);
            return;
        }
        if (Selected is not { Enabled: true } item) return;
        if (Close()) Accepted?.Invoke(item);
    }

    void InputKeyDown(Control sender, KeyEventArgs e)
    {
        if (e.Modifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Enter: Accept(); e.Handled = true; break;
            case Key.Escape: Dismiss(); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.PageUp: Move(-(MaxRows - 1)); e.Handled = true; break;
            case Key.PageDown: Move(MaxRows - 1); e.Handled = true; break;
        }
    }

    void Move(int delta)
    {
        if (InputOnly || _visible.Count == 0) return;
        int i = Math.Clamp(_list.ItemIndex + delta, 0, _visible.Count - 1);
        // Wrap on single steps, like most palettes.
        if (Math.Abs(delta) == 1 && _list.ItemIndex + delta is var raw && (raw < 0 || raw >= _visible.Count)) i = raw < 0 ? _visible.Count - 1 : 0;
        _list.ItemIndex = i;
    }

    // ---- Layout and painting ----

    int RowCount => InputOnly ? 0 : Math.Max(1, Math.Min(_visible.Count, MaxRows));
    int InputHeight => Px(30);
    int HintHeight => InputOnly ? Px(22) : 0;

    void Relayout()
    {
        int pad = Px(4);
        int listHeight = RowCount * Px(24);
        int height = InputHeight + HintHeight + listHeight + (InputOnly ? pad : 0) + 2 * Px(1);
        int width = Px(580);
        _input.SetBounds(pad + Px(1), pad, width - 2 * pad - 2 * Px(1), InputHeight - pad - Px(1));
        _list.SetBounds(Px(1), InputHeight + HintHeight, width - 2 * Px(1), listHeight);
        _list.Visible = !InputOnly;
        if (Width != width || Height != height) SetBounds(Left, Top, width, height);
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        canvas.FillRect(new Rect(0, 0, Width, Height), t.PanelBackground);
        if (InputOnly)
        {
            var hint = HintProvider?.Invoke(_input.Text) ?? HintText;
            canvas.DrawText(hint, canvas.DefaultFont, t.TextDisabled, new Rect(Px(10), InputHeight, Width - Px(20), HintHeight), TextAlign.Left, VerticalAlign.Center);
        }
        else if (_visible.Count == 0)
            canvas.DrawText(EmptyText, canvas.DefaultFont, t.TextDisabled, new Rect(Px(10), InputHeight, Width - Px(20), Px(24)), TextAlign.Left, VerticalAlign.Center);
        canvas.DrawBorder(new Rect(0, 0, Width, Height), t.Accent, Math.Max(1, Px(1)));
    }

    void DrawRow(Control sender, Canvas canvas, int index, Rect rect, bool selected)
    {
        if (index >= _visible.Count) return;
        var t = canvas.Theme;
        var item = _visible[index];
        if (selected) canvas.FillRect(rect, t.Selection);
        var normal = item.Enabled ? t.Text : t.TextDisabled;
        var dim = t.TextDisabled;
        var font = canvas.DefaultFont;
        int x = rect.X + canvas.Px(8), right = rect.X + rect.Width - canvas.Px(8);

        if (item.Right.Length > 0)
        {
            int w = (int)Math.Ceiling(font.MeasureWidth(item.Right));
            canvas.DrawText(item.Right, font, dim, new Rect(right - w, rect.Y, w, rect.Height), TextAlign.Right, VerticalAlign.Center);
            right -= w + canvas.Px(12);
        }

        // The label, with the matched characters in the accent colour and bold.
        var bold = canvas.DefaultBoldFont;
        var matches = new HashSet<int>(item.LabelMatches);
        int i = 0;
        while (i < item.Label.Length && x < right)
        {
            bool hit = matches.Contains(i);
            int j = i;
            while (j < item.Label.Length && matches.Contains(j) == hit) j++;
            var run = item.Label[i..j];
            var f = hit ? bold : font;
            int w = (int)Math.Ceiling(f.MeasureWidth(run));
            canvas.DrawText(run, f, hit ? t.Accent : normal, new Rect(x, rect.Y, Math.Min(w, right - x), rect.Height), TextAlign.Left, VerticalAlign.Center, ellipsis: false);
            x += w;
            i = j;
        }
        if (item.Detail.Length > 0 && x + canvas.Px(12) < right)
        {
            x += canvas.Px(10);
            canvas.DrawText(item.Detail, font, dim, new Rect(x, rect.Y, right - x, rect.Height), TextAlign.Left, VerticalAlign.Center, ellipsis: true);
        }
    }
}
