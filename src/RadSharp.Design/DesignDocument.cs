namespace RadSharp.Design;

/// <summary>
/// A form being designed: the live form instance (always at 100 % scale and its design size), the
/// selection, and undo/redo as whole-form JSON snapshots (the GMForms approach). Every change goes
/// through here so the surface, the inspector and the structure view stay in step.
/// </summary>
public sealed class DesignDocument
{
    // Each state remembers its selection, so undoing a delete reselects what came back.
    readonly Stack<(string Json, string[] Selection)> _undo = new(), _redo = new();
    readonly List<Control> _selection = [];
    string _savedText = "";

    public DesignDocument() => New();

    public Form Form { get; private set; } = null!;
    public string? FileName { get; set; }

    /// <summary>True if the form changed since it was loaded or saved.</summary>
    public bool Modified => Save() != _savedText;

    public IReadOnlyList<Control> Selection => _selection;
    /// <summary>The control the inspector shows: the last one selected, or the form.</summary>
    public Control Primary => _selection.Count > 0 ? _selection[^1] : Form;

    public event Action? SelectionChanged;
    /// <summary>Raised after any change to the designed form (property, bounds, tree, undo).</summary>
    public event Action? Changed;
    /// <summary>Raised when <see cref="Form"/> was replaced by a new instance (new, open, undo, redo).</summary>
    public event Action? FormReplaced;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // ---- Documents ----

    public void New(int width = 480, int height = 320)
    {
        var form = new Form { Name = "frmMain", Caption = "Form1" };
        form.SetBounds(0, 0, width, height);
        form.DesignWidth = width;
        form.DesignHeight = height;
        Replace(form);
        FileName = null;
        _undo.Clear();
        _redo.Clear();
        _savedText = Save();
    }

    public IReadOnlyList<string> Load(string json)
    {
        var (form, warnings) = FormReader.Load(json);
        Replace(form);
        _undo.Clear();
        _redo.Clear();
        _savedText = Save();
        return warnings;
    }

    /// <summary>The form file text (deterministic, spec Appendix A).</summary>
    public string Save() => FormWriter.Save(Form);

    /// <summary>Marks the current state as saved (after writing <see cref="Save"/> to disk).</summary>
    public void MarkSaved() => _savedText = Save();

    void Replace(Form form)
    {
        Form = form;
        form.PerformLayout();
        _selection.Clear();
        FormReplaced?.Invoke();
        SelectionChanged?.Invoke();
    }

    // ---- Undo ----

    /// <summary>Records the current state; call before a change (one call per user action).</summary>
    public void Snapshot()
    {
        _undo.Push(Current());
        _redo.Clear();
    }

    (string, string[]) Current() => (Save(), [.. _selection.Select(c => c.Name)]);

    /// <summary>Call after changing the form (or a control on it) directly.</summary>
    public void NotifyChanged()
    {
        // The designer always shows the form at its design size, so a new size is the new design size.
        Form.DesignWidth = Form.Width;
        Form.DesignHeight = Form.Height;
        Form.PerformLayout();
        Form.InvalidateAll();
        Changed?.Invoke();
    }

    public bool Undo() => Restore(_undo, _redo);
    public bool Redo() => Restore(_redo, _undo);

    bool Restore(Stack<(string Json, string[] Selection)> from, Stack<(string Json, string[] Selection)> to)
    {
        if (from.Count == 0) return false;
        to.Push(Current());
        var (json, names) = from.Pop();
        var (form, _) = FormReader.Load(json);
        Replace(form);
        foreach (var n in names)
            if (form.FindControl(n) is { } c && c != form) _selection.Add(c);
        SelectionChanged?.Invoke();
        Changed?.Invoke();
        return true;
    }

    // ---- Selection ----

    public void Select(Control? control, bool add = false)
    {
        if (!add) _selection.Clear();
        if (control != null && control != Form)
        {
            if (add && _selection.Remove(control)) { SelectionChanged?.Invoke(); return; }
            _selection.Add(control);
        }
        SelectionChanged?.Invoke();
    }

    public void SelectMany(IEnumerable<Control> controls)
    {
        _selection.Clear();
        _selection.AddRange(controls.Where(c => c != Form));
        SelectionChanged?.Invoke();
    }

    public bool IsSelected(Control c) => _selection.Contains(c);

    // ---- Creating, deleting, clipboard ----

    /// <summary>A name like "Button1" not used anywhere on the form.</summary>
    public string UniqueName(string className)
    {
        var used = new HashSet<string>(Form.Descendants().Select(c => c.Name).Append(Form.Name), StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
            if (!used.Contains(className + n)) return className + n;
    }

    /// <summary>Whether <paramref name="container"/> can hold a new control of <paramref name="cls"/>.</summary>
    public static bool AcceptsChild(Control container, ComponentClass cls)
    {
        if (typeof(TabSheet).IsAssignableFrom(cls.Type)) return container is PageControl;
        if (typeof(Component).IsAssignableFrom(cls.Type)) return container is RadSharp.Form;
        return container is RadSharp.Form or Panel or GroupBox or TabSheet or ScrollBox or ToolBar;
    }

    /// <summary>Creates a control of <paramref name="cls"/> in <paramref name="parent"/> at a parent-relative rectangle (null size = class default).</summary>
    public Control Create(ComponentClass cls, Control parent, int left, int top, int? width = null, int? height = null)
    {
        Snapshot();
        var control = cls.Create();
        control.Name = UniqueName(cls.Name);
        if (control is TabSheet sheet) sheet.Caption = control.Name;
        control.SetBounds(left, top, width ?? control.Width, height ?? control.Height);
        parent.Add(control);
        if (parent is PageControl pages && control is TabSheet) pages.ActivePageIndex = pages.Pages.Count - 1;
        Select(control);
        NotifyChanged();
        return control;
    }

    public void DeleteSelection()
    {
        if (_selection.Count == 0) return;
        Snapshot();
        foreach (var c in _selection.ToList()) c.Parent?.Remove(c);
        _selection.Clear();
        SelectionChanged?.Invoke();
        NotifyChanged();
    }

    /// <summary>The selected controls as a JSON fragment (children of selected containers come along).</summary>
    public string CopySelection() =>
        FormWriter.SaveFragment(_selection.Where(c => !_selection.Any(o => o != c && c.IsSelfOrDescendantOf(o))));

    /// <summary>Pastes a fragment into <paramref name="parent"/>, renaming clashes and offsetting copies.</summary>
    public List<Control> Paste(string fragment, Control parent, int offset = 8)
    {
        var (controls, _) = FormReader.LoadFragment(fragment);
        if (controls.Count == 0) return controls;
        Snapshot();
        foreach (var c in controls)
        {
            foreach (var d in c.Descendants().Prepend(c))
                if (d.Name.Length == 0 || Form.FindControl(d.Name) != null) d.Name = UniqueName(ComponentRegistry.Of(d).Name);
            if (c.Align == Align.None) c.SetBounds(c.Left + offset, c.Top + offset, c.Width, c.Height);
            parent.Add(c);
        }
        SelectMany(controls);
        NotifyChanged();
        return controls;
    }

    /// <summary>Renames a control (the form generator turns the name into a field).</summary>
    public bool Rename(Control control, string name)
    {
        if (name.Length == 0 || !IsIdentifier(name) || (Form.FindControl(name) is { } other && other != control)) return false;
        Snapshot();
        control.Name = name;
        NotifyChanged();
        return true;
    }

    static bool IsIdentifier(string s) =>
        (char.IsLetter(s[0]) || s[0] == '_') && s.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    /// <summary>The containers from the form down to <paramref name="c"/>'s parent that could be pasted into.</summary>
    public Control PasteTarget()
    {
        var p = Primary;
        if (p != Form && p is Panel or GroupBox or TabSheet or ScrollBox or ToolBar) return p;
        return p.Parent ?? Form;
    }
}
