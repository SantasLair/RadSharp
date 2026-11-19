using System.Reflection;
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
        using var designing = Control.EnterDesignMode();
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

    /// <summary>
    /// How the designer reads form text: a node whose class it doesn't know (one the project defines, say) is kept as
    /// an <see cref="UnknownControl"/> placeholder, so saving writes it back instead of deleting it. So is one whose
    /// class only a live run (F9) registered: that class goes away with the run, while the designed form stays.
    /// </summary>
    public static readonly FormLoadOptions LoadOptions = new()
    {
        KeepUnknownClasses = true,
        // RadSharp's own class of a name before a live run's that shadows it.
        FindClass = name => ComponentRegistry.Find(name, IsDesignerClass),
        AcceptClass = IsDesignerClass,
    };

    static bool IsDesignerClass(ComponentClass c) => c.CanCreate && !c.Type.Assembly.IsCollectible;

    /// <summary>
    /// The project's controls this document may create besides RadSharp's own, by form-file class name (the designer's
    /// design session: see <c>DesignSession</c>); null: none. Every other class from a collectible assembly is read as
    /// a placeholder. Takes effect on the next load: after changing it, call <see cref="Reload"/>.
    /// </summary>
    public Func<string, ComponentClass?>? ProjectClasses { get; set; }

    /// <summary>How this document reads form text: <see cref="LoadOptions"/>, plus <see cref="ProjectClasses"/>.</summary>
    public FormLoadOptions Options => ProjectClasses is not { } project ? LoadOptions : LoadOptions with
    {
        // A project's class wins its name, as in the running application (registered after RadSharp's).
        FindClass = name => project(name) ?? ComponentRegistry.Find(name, IsDesignerClass),
        AcceptClass = c => IsDesignerClass(c) || project(c.Name) == c,
    };

    public IReadOnlyList<string> Load(string json)
    {
        var result = Read(json);
        Replace(result.Form);
        _undo.Clear();
        _redo.Clear();
        _savedText = Save();
        return result.Warnings;
    }

    /// <summary>
    /// Reads the form again from <paramref name="json"/> (null: its current text) with the current
    /// <see cref="ProjectClasses"/>: placeholders of classes that became available turn into real controls, and real
    /// controls of classes that went away into placeholders, which keep their text. Undo and redo, the selection (by
    /// name) and whether the document is modified stay as they were. Pass the text saved before the classes changed
    /// (while the old ones were still registered).
    /// </summary>
    public IReadOnlyList<string> Reload(string? json = null)
    {
        json ??= Save();
        bool modified = json != _savedText;
        var names = _selection.Select(c => c.Name).ToList();
        var result = Read(json);
        Replace(result.Form);
        foreach (var n in names)
            if (result.Form.FindControl(n) is { } c && c != result.Form) _selection.Add(c);
        // A real control may save more than its placeholder did (a setter that sets another published property):
        // that isn't a change the user made.
        if (!modified) _savedText = Save();
        SelectionChanged?.Invoke();
        Changed?.Invoke();
        return result.Warnings;
    }

    /// <summary>
    /// Reads form text in design mode with <see cref="Options"/>. A project's control that throws while it is created
    /// or read is a placeholder on its own (the reader sees to that); one that throws once the form is laid out (it
    /// looks for the running application around it, say) can't be singled out, so the form is read again without the
    /// project's classes: all of them placeholders on it, which keep their text, with a warning that says why.
    /// </summary>
    FormLoadResult Read(string json)
    {
        using var designing = Control.EnterDesignMode();
        if (ProjectClasses == null) return FormReader.Load(json, Options);
        try { return FormReader.Load(json, Options); }
        catch (Exception e) when (e is not (OutOfMemoryException or FormFileException))
        {
            var fallback = FormReader.Load(json, LoadOptions);
            var inner = e is System.Reflection.TargetInvocationException { InnerException: { } i } ? i : e;
            return fallback with
            {
                Warnings = [$"The project's controls threw while the form was laid out ({inner.GetType().Name}: {inner.Message}); " +
                    "they are placeholders on this form until that is fixed and the project rebuilt.", .. fallback.Warnings],
            };
        }
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
        var form = Read(json).Form;
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
    public string UniqueName(string className) => UniqueName(className, []);

    string UniqueName(string className, IEnumerable<string> alsoTaken)
    {
        // Names inside a placeholder's (unloaded) children are still in the file, so they count as used.
        var used = new HashSet<string>(Form.Descendants().Select(c => c.Name).Append(Form.Name)
            .Concat(Form.Descendants().OfType<UnknownControl>().SelectMany(u => u.ContainedNames)).Concat(alsoTaken), StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
            if (!used.Contains(className + n) && HiddenMember(className + n) == null) return className + n;
    }

    /// <summary>
    /// Finds the member of the edited form's base class that a control named <c>name</c> would hide (the generator
    /// reports that as error RAD1006): returns "Type.Member", or null if there is none. Defaults to the members of
    /// <see cref="RadSharp.Form"/>; set it when the form derives from a base class the designer knows about.
    /// </summary>
    public Func<string, string?> InheritedMemberLookup { get; set; } = FormMemberLookup;

    /// <summary>The member of the form's base class a control of this name would hide, or null (see <see cref="InheritedMemberLookup"/>).</summary>
    public string? HiddenMember(string name) => InheritedMemberLookup(name);

    /// <summary>The inherited members of <see cref="RadSharp.Form"/> that code in another assembly can see (public, protected).</summary>
    static string? FormMemberLookup(string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        {
            foreach (var m in typeof(RadSharp.Form).GetMember(name, flags))
            {
                bool visible = m switch
                {
                    MethodBase { IsSpecialName: true } => false,
                    MethodBase b => b.IsPublic || b.IsFamily || b.IsFamilyOrAssembly,
                    FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
                    PropertyInfo p => p.GetMethod is { } g && (g.IsPublic || g.IsFamily || g.IsFamilyOrAssembly)
                        || p.SetMethod is { } st && (st.IsPublic || st.IsFamily || st.IsFamilyOrAssembly),
                    EventInfo e => e.AddMethod is { } a && (a.IsPublic || a.IsFamily || a.IsFamilyOrAssembly),
                    Type n => n.IsNestedPublic || n.IsNestedFamily || n.IsNestedFamORAssem,
                    _ => false,
                };
                if (visible) return $"{(m.DeclaringType ?? typeof(RadSharp.Form)).Name}.{m.Name}";
            }
        }
        return null;
    }

    /// <summary>
    /// Why <paramref name="name"/> can't be <paramref name="control"/>'s name, or null if it can: not a valid
    /// identifier, already used, or hiding an inherited member of the form (RAD1006). The form's own name is exempt from
    /// the last rule: it names the class, not a field.
    /// </summary>
    public string? NameProblem(Control control, string name)
    {
        if (name.Length == 0 || !IsIdentifier(name) || (Form.FindControl(name) is { } other && other != control))
            return $"\"{name}\" is not a valid, unused name";
        if (control != Form && HiddenMember(name) is { } hidden)
            return $"\"{name}\" would hide the inherited member {hidden}; choose another name";
        return null;
    }

    /// <summary>Whether a placeholder's raw (unloaded) children hold a control named <paramref name="name"/>.</summary>
    bool IsNameInPlaceholder(string name) =>
        Form.Descendants().OfType<UnknownControl>().Any(u => u.ContainedNames.Contains(name, StringComparer.Ordinal));

    /// <summary>Whether a control on the form, or one inside a placeholder's raw children, is named <paramref name="name"/>.</summary>
    bool IsNameUsed(string name) =>
        Form.FindControl(name) != null || Form.Descendants().OfType<UnknownControl>().Any(u => u.ContainedNames.Contains(name, StringComparer.Ordinal));

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
        Control control;
        using (Control.EnterDesignMode()) control = cls.Create();   // first: a constructor that throws changes nothing
        Snapshot();
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
        List<Control> controls;
        using (Control.EnterDesignMode()) controls = FormReader.LoadFragment(fragment, Options).Controls;
        if (controls.Count == 0) return controls;
        Snapshot();
        // Names this paste has given out (its controls join the form one top-level control at a time).
        var taken = new HashSet<string>(StringComparer.Ordinal);
        bool Clashes(string name) => name.Length == 0 || taken.Contains(name) || IsNameUsed(name);
        foreach (var c in controls)
        {
            foreach (var d in c.Descendants().Prepend(c))
            {
                if (Clashes(d.Name)) d.Name = UniqueName(d is UnknownControl u ? u.ClassName : ComponentRegistry.Of(d).Name, taken);
                taken.Add(d.Name);
                // The controls inside a placeholder's raw children are in the file too: a copy's need new names as well.
                if (d is not UnknownControl placeholder) continue;
                foreach (var (name, cls) in placeholder.ContainedNames.Zip(placeholder.ContainedClassNames).ToList())
                {
                    if (Clashes(name))
                    {
                        var renamed = UniqueName(cls.Length > 0 ? cls : "Control", taken);
                        placeholder.RenameContained(name, renamed);
                        taken.Add(renamed);
                    }
                    else taken.Add(name);
                }
            }
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
        if (NameProblem(control, name) != null || IsNameInPlaceholder(name)) return false;
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
