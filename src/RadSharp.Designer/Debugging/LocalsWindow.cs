using RadSharp.Ide.Debug.Protocol;

namespace RadSharp.Designer;

/// <summary>One row of a <see cref="VariablesTree"/>: a variable (or watch expression) and its value.</summary>
public sealed class VariableItem(string name, string value, string? type, int variablesReference)
{
    public string Name { get; } = name;
    public string Value { get; } = value;
    public string? Type { get; } = type;
    /// <summary>Non-zero for a structured value whose children load on demand.</summary>
    public int VariablesReference { get; } = variablesReference;
    /// <summary>The children were requested (or there are none).</summary>
    public bool Loaded { get; internal set; }
    /// <summary>For a watch row: its index in the watch list.</summary>
    public int WatchIndex { get; init; } = -1;

    public static string Format(string name, string value, string? type) =>
        type is { Length: > 0 } t ? $"{name} = {value}  ({t})" : $"{name} = {value}";
}

/// <summary>
/// A tree of debugger values whose structured entries expand lazily: a node with a variables reference gets a
/// placeholder child, and expanding it (click on the arrow, Right, double-click, or <see cref="ExpandAsync"/>) asks
/// <see cref="LoadChildren"/> for its members. Expanded paths are remembered by name, so the tree reopens them after
/// a step.
/// </summary>
public class VariablesTree : TreeView
{
    const string Placeholder = "…";
    readonly HashSet<string> _expandedPaths = new(StringComparer.Ordinal);
    int _generation;

    public VariablesTree() { Name = "tvVariables"; }

    /// <summary>Loads the children of a structured value (the controller's <c>variables</c> request).</summary>
    public Func<int, Task<IReadOnlyList<Variable>>>? LoadChildren { get; set; }

    /// <summary>Runs work on the UI thread (load results arrive on the pool).</summary>
    public Action<Action> Post { get; set; } = Application.Post;

    /// <summary>Raised after a node's children arrived (tests wait for it).</summary>
    public event Action<TreeNode>? ChildrenLoaded;

    /// <summary>Replaces the rows; remembered expanded paths are expanded again.</summary>
    public void SetItems(IEnumerable<VariableItem> items)
    {
        _generation++;
        Nodes.Clear();
        foreach (var item in items) Nodes.Add(CreateNode(item));
        Selected = null;
        foreach (var node in Nodes.ToList()) ReExpand(node);
        Invalidate();
    }

    public static TreeNode CreateNode(VariableItem item)
    {
        var node = new TreeNode(VariableItem.Format(item.Name, item.Value, item.Type)) { Tag = item };
        if (item.VariablesReference > 0) node.Add(Placeholder);
        else item.Loaded = true;
        return node;
    }

    static string PathOf(TreeNode node)
    {
        var parts = new List<string>();
        for (var n = node; n != null; n = n.Parent) parts.Add((n.Tag as VariableItem)?.Name ?? n.Text);
        parts.Reverse();
        return string.Join("\u0001", parts);
    }

    void ReExpand(TreeNode node)
    {
        if (_expandedPaths.Contains(PathOf(node))) _ = ExpandAsync(node);
    }

    /// <summary>Expands a node, loading its children first if they haven't been.</summary>
    public Task ExpandAsync(TreeNode node)
    {
        if (node.Tag is not VariableItem item) return Task.CompletedTask;
        _expandedPaths.Add(PathOf(node));
        node.Expanded = true;
        Invalidate();
        if (item.Loaded || LoadChildren is not { } load) return Task.CompletedTask;
        item.Loaded = true;
        int generation = _generation;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        load(item.VariablesReference).ContinueWith(t => Post(() =>
        {
            try
            {
                if (generation != _generation) return;
                node.Nodes.Clear();
                var children = t.IsCompletedSuccessfully ? t.Result : [];
                foreach (var v in children) Attach(node, CreateNode(new VariableItem(v.Name, v.Value, v.Type, v.VariablesReference)));
                if (children.Count == 0) Attach(node, new TreeNode("(no members)") { Tag = new VariableItem("", "", null, 0) { Loaded = true } });
                Invalidate();
                ChildrenLoaded?.Invoke(node);
                foreach (var child in node.Nodes.ToList()) ReExpand(child);
            }
            finally { done.TrySetResult(); }
        }), TaskScheduler.Default);
        return done.Task;
    }

    static void Attach(TreeNode parent, TreeNode child)
    {
        // TreeNode.Add sets the parent; re-create through it so Level and Parent are right.
        var added = parent.Add(child.Text);
        added.Tag = child.Tag;
        foreach (var grandChild in child.Nodes) added.Add(grandChild.Text);
    }

    /// <summary>Finds a row by its path of names ("this", "_total").</summary>
    public TreeNode? Find(params string[] path)
    {
        IEnumerable<TreeNode> level = Nodes;
        TreeNode? found = null;
        foreach (var name in path)
        {
            found = level.FirstOrDefault(n => (n.Tag as VariableItem)?.Name == name);
            if (found == null) return null;
            level = found.Nodes;
        }
        return found;
    }

    void LoadExpanded()
    {
        foreach (var node in VisibleNodes())
        {
            if (node.Tag is not VariableItem item) continue;
            if (node.Expanded) { if (!item.Loaded) _ = ExpandAsync(node); else _expandedPaths.Add(PathOf(node)); }
            else _expandedPaths.Remove(PathOf(node));
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        LoadExpanded();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        LoadExpanded();
    }
}

/// <summary>The Locals tool window: the selected frame's variables (every scope's; more than one scope shows them grouped).</summary>
public sealed class LocalsWindow : Control
{
    readonly VariablesTree _tree = new() { Name = "tvLocals", Align = Align.Client };

    public LocalsWindow()
    {
        Name = "pnlLocalsWindow";
        TabStop = false;
        Add(_tree);
    }

    public VariablesTree Tree => _tree;

    /// <summary>Shows a frame's scopes; an empty list clears the window (running, or design time).</summary>
    public void SetScopes(IReadOnlyList<(Scope Scope, IReadOnlyList<Variable> Variables)> scopes)
    {
        static VariableItem Item(Variable v) => new(v.Name, v.Value, v.Type, v.VariablesReference);
        if (scopes.Count == 1) { _tree.SetItems(scopes[0].Variables.Select(Item)); return; }
        _tree.SetItems(scopes.Select(s => new VariableItem(s.Scope.Name, "", null, s.Scope.VariablesReference)));
        foreach (var (node, scope) in _tree.Nodes.Zip(scopes))
        {
            node.Text = scope.Scope.Name;
            if (scope.Variables.Count == 0) continue;
            ((VariableItem)node.Tag!).Loaded = true;
            node.Nodes.Clear();
            foreach (var v in scope.Variables)
            {
                var child = node.Add(VariableItem.Format(v.Name, v.Value, v.Type));
                child.Tag = Item(v);
                if (v.VariablesReference > 0) child.Add("…");
                else ((VariableItem)child.Tag).Loaded = true;
            }
            node.Expanded = true;
        }
        _tree.Invalidate();
    }

    /// <summary>The value text of a top-level local ("20"), or null.</summary>
    public string? ValueOf(string name) =>
        _tree.Nodes.Concat(_tree.Nodes.SelectMany(n => n.Nodes)).Select(n => n.Tag as VariableItem).FirstOrDefault(v => v?.Name == name)?.Value;
}
