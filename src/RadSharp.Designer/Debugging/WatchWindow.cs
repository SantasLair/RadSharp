using RadSharp.Ide.Debug.Protocol;

namespace RadSharp.Designer;

/// <summary>
/// The Watch tool window: expressions evaluated in the selected frame whenever the debuggee stops (or another frame is
/// selected). Type an expression in the box and press Enter to add it; select a row and press F2 (or double-click it)
/// to edit it in the box, Enter to replace it; Delete removes it. Structured results expand like Locals.
/// </summary>
public sealed class WatchWindow : Control
{
    readonly Edit _input = new() { Name = "edtWatch", Hint = "Add a watch expression (Enter)" };
    readonly VariablesTree _tree = new() { Name = "tvWatch" };
    readonly List<string> _expressions = [];
    int _editing = -1;
    int _generation;

    public WatchWindow()
    {
        Name = "pnlWatchWindow";
        TabStop = false;
        Add(_input);
        Add(_tree);
        _input.KeyDown += (_, e) =>
        {
            if (e.Modifiers != KeyModifiers.None) return;
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _editing = -1; _input.Text = ""; e.Handled = true; }
        };
        _tree.KeyDown += (_, e) =>
        {
            if (e.Modifiers != KeyModifiers.None || SelectedIndex is not { } i) return;
            if (e.Key == Key.Delete) { Remove(i); e.Handled = true; }
            else if (e.Key == Key.F2) { BeginEdit(i); e.Handled = true; }
        };
        _tree.DoubleClick += _ => { if (SelectedIndex is { } i && _tree.Selected?.Parent == null) BeginEdit(i); };
        Refresh();
    }

    /// <summary>Evaluates an expression in the current frame; null when there is no paused debuggee.</summary>
    public Func<string, Task<EvaluateResponse?>>? Evaluate { get; set; }

    /// <summary>Runs work on the UI thread.</summary>
    public Action<Action> Post { get => _tree.Post; set => _tree.Post = value; }

    public VariablesTree Tree => _tree;
    public Edit Input => _input;
    public IReadOnlyList<string> Expressions => _expressions;

    /// <summary>Raised after every row has its value (tests wait for it).</summary>
    public event Action? Evaluated;

    /// <summary>The value shown for watch <paramref name="index"/>.</summary>
    public string? ValueOf(int index) => _tree.Nodes.FirstOrDefault(n => (n.Tag as VariableItem)?.WatchIndex == index)?.Tag is VariableItem v ? v.Value : null;

    int? SelectedIndex
    {
        get
        {
            var node = _tree.Selected;
            while (node?.Parent != null) node = node.Parent;
            return (node?.Tag as VariableItem)?.WatchIndex is { } i and >= 0 ? i : null;
        }
    }

    public void Add(string expression)
    {
        expression = expression.Trim();
        if (expression.Length == 0) return;
        _expressions.Add(expression);
        Refresh();
    }

    public void Replace(int index, string expression)
    {
        expression = expression.Trim();
        if (index < 0 || index >= _expressions.Count) return;
        if (expression.Length == 0) { Remove(index); return; }
        _expressions[index] = expression;
        Refresh();
    }

    public void Remove(int index)
    {
        if (index < 0 || index >= _expressions.Count) return;
        _expressions.RemoveAt(index);
        Refresh();
    }

    public void BeginEdit(int index)
    {
        if (index < 0 || index >= _expressions.Count) return;
        _editing = index;
        _input.Text = _expressions[index];
        _input.Focus();
        _input.SelectAll();
    }

    void Commit()
    {
        if (_editing >= 0) Replace(_editing, _input.Text);
        else Add(_input.Text);
        _editing = -1;
        _input.Text = "";
    }

    /// <summary>Re-evaluates every expression (the debuggee stopped, or the frame changed).</summary>
    public void Refresh()
    {
        int generation = ++_generation;
        var expressions = _expressions.ToList();
        _tree.SetItems(expressions.Select((e, i) => new VariableItem(e, Evaluate == null ? "" : "…", null, 0) { WatchIndex = i }));
        if (Evaluate is not { } evaluate || expressions.Count == 0) { Evaluated?.Invoke(); return; }
        var tasks = expressions.Select(async e =>
        {
            try { return (await evaluate(e).ConfigureAwait(false)) is { } r ? (r.Result, r.Type, r.VariablesReference) : ("", (string?)null, 0); }
            catch (Exception ex) { return ("<" + ShortError(ex.Message) + ">", (string?)null, 0); }
        }).ToList();
        Task.WhenAll(tasks).ContinueWith(t => Post(() =>
        {
            if (generation != _generation) return;
            var results = t.Result;
            _tree.SetItems(expressions.Select((e, i) => new VariableItem(e, results[i].Item1, results[i].Item2, results[i].Item3) { WatchIndex = i }));
            Evaluated?.Invoke();
        }), TaskScheduler.Default);
    }

    static string ShortError(string message)
    {
        int colon = message.IndexOf(": ", StringComparison.Ordinal);
        return colon >= 0 && message.StartsWith("Debug adapter rejected", StringComparison.Ordinal) ? message[(colon + 2)..] : message;
    }

    protected override void OnResize()
    {
        base.OnResize();
        int h = Px(26);
        _input.SetBounds(Px(4), Px(3), Math.Max(0, Width - Px(8)), h);
        _tree.SetBounds(0, h + Px(6), Width, Math.Max(0, Height - h - Px(6)));
    }
}
