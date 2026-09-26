using RadSharp;
using RadSharp.Design;

namespace RadSharp.Designer;

/// <summary>
/// The RADSharp form designer — itself a RADSharp form (DesignerForm.radform.json). Palette on the left,
/// design surface in the middle, Object Inspector (properties, events, structure) on the right.
/// </summary>
public partial class DesignerForm : Form
{
    readonly DesignDocument _doc = new();
    readonly List<ComponentClass> _palette;
    bool _syncing;

    public DesignerForm(string? path = null)
    {
        InitializeComponent();
        surface.Document = _doc;
        surface.Created += _ => lstPalette.ItemIndex = -1;
        surface.DoubleClicked += OnSurfaceDoubleClicked;

        _palette = [.. ComponentRegistry.All
            .Where(c => c.CanCreate && c.Type != typeof(Control) && !typeof(Form).IsAssignableFrom(c.Type) && c.Type != typeof(DesignSurface))
            .OrderBy(c => c.Name, StringComparer.Ordinal)];
        lstPalette.Items = [.. _palette.Select(c => c.Name)];

        foreach (var grid in new[] { gridProps, gridEvents })
        {
            grid.PropertyChanging += (_, _) => _doc.Snapshot();
            grid.PropertyChanged += (_, _) => _doc.NotifyChanged();
        }
        gridEvents.EventDoubleClick += (_, ev) => EditHandler(_doc.Primary, ev);

        _doc.SelectionChanged += UpdateInspector;
        _doc.Changed += () => { gridProps.RefreshValues(); gridEvents.RefreshValues(); RebuildStructure(); UpdateTitle(); };
        _doc.FormReplaced += () => { UpdateInspector(); RebuildStructure(); };

        if (path != null && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(path)) OpenProject(path);
        else if (path != null && File.Exists(path)) OpenFile(path);
        UpdateInspector();
        RebuildStructure();
        UpdateTitle();
        Shown += _ => CheckSdk();
    }

    public DesignDocument Document => _doc;

    // ---- Files ----

    void OpenFile(string path)
    {
        try
        {
            var warnings = _doc.Load(File.ReadAllText(path));
            _doc.FileName = path;
            if (warnings.Count > 0) Dialogs.ShowMessage("The form loaded with warnings:\n" + string.Join("\n", warnings.Take(12)), this);
        }
        catch (Exception e) when (e is IOException or FormFileException or UnauthorizedAccessException)
        {
            Dialogs.MessageDlg($"Could not open {Path.GetFileName(path)}:\n{e.Message}", MessageType.Error, owner: this);
        }
        UpdateTitle();
    }

    bool SaveTo(string? path)
    {
        if (path == null)
        {
            dlgSave.FileName = _doc.Form.Name.Length > 0 ? _doc.Form.Name + ".radform.json" : "Form1.radform.json";
            if (!dlgSave.Execute()) return false;
            path = dlgSave.FileName;
            if (!path.EndsWith(".radform.json", StringComparison.OrdinalIgnoreCase)) path += ".radform.json";
        }
        File.WriteAllText(path, _doc.Save());
        _doc.FileName = path;
        _doc.MarkSaved();
        UpdateTitle();
        RefreshProject();
        sbMain.SimpleText = "Saved " + path;
        return true;
    }

    /// <summary>Asks to save unsaved changes; returns false if the user cancelled.</summary>
    bool ConfirmDiscard()
    {
        if (!_doc.Modified) return true;
        return Dialogs.MessageDlg($"Save changes to {DocumentTitle}?", MessageType.Confirmation, MessageButtons.YesNoCancel, this) switch
        {
            ModalResult.Yes => SaveTo(_doc.FileName),
            ModalResult.No => true,
            _ => false,
        };
    }

    string DocumentTitle => _doc.FileName != null ? Path.GetFileName(_doc.FileName) : "Untitled";

    void UpdateTitle() => Caption = $"RADSharp Designer — {DocumentTitle}{(_doc.Modified ? " *" : "")}";

    // ---- Inspector, name, structure ----

    void UpdateInspector()
    {
        _syncing = true;
        var target = _doc.Primary;
        gridProps.Target = target;
        gridEvents.Target = target;
        edtName.Text = target.Name;
        tvStructure.Selected = FindNode(tvStructure.Nodes, target);
        sbMain.SimpleText = _doc.Selection.Count > 1
            ? $"{_doc.Selection.Count} controls selected"
            : $"{target.Name}: {ComponentRegistry.Of(target).Name}  —  {target.Bounds}";
        _syncing = false;
        surface.Invalidate();
    }

    void RebuildStructure()
    {
        _syncing = true;
        tvStructure.Nodes.Clear();
        var root = new TreeNode(Label(_doc.Form)) { Tag = _doc.Form, Expanded = true };
        void Add(TreeNode node, Control c)
        {
            foreach (var child in c.Children)
            {
                var n = node.Add(Label(child));
                n.Tag = child;
                n.Expanded = true;
                Add(n, child);
            }
        }
        Add(root, _doc.Form);
        tvStructure.Nodes.Add(root);
        tvStructure.Selected = FindNode(tvStructure.Nodes, _doc.Primary);
        tvStructure.Invalidate();
        _syncing = false;
    }

    static string Label(Control c) => $"{c.Name}: {ComponentRegistry.Of(c).Name}";

    static TreeNode? FindNode(IEnumerable<TreeNode> nodes, Control c)
    {
        foreach (var n in nodes)
        {
            if (n.Tag == c) return n;
            if (FindNode(n.Nodes, c) is { } found) return found;
        }
        return null;
    }

    void tvStructure_Change(Control sender)
    {
        if (_syncing || tvStructure.Selected?.Tag is not Control c) return;
        _doc.Select(c == _doc.Form ? null : c);
    }

    void edtName_KeyDown(Control sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Modifiers != KeyModifiers.None) return;
        ApplyName();
        e.Handled = true;
    }

    void edtName_Blur(Control sender) => ApplyName();

    void ApplyName()
    {
        var target = _doc.Primary;
        var name = edtName.Text.Trim();
        if (name == target.Name) return;
        var old = target.Name;
        if (!_doc.Rename(target, name))
        {
            sbMain.SimpleText = $"\"{name}\" is not a valid, unused name";
            edtName.Text = target.Name;
            return;
        }
        if (old.Length > 0) RenameInCode(old, name);
    }

    void pgInspector_Change(Control sender) => UpdateInspector();

    // ---- Palette ----

    void lstPalette_Change(Control sender)
    {
        surface.PendingClass = lstPalette.ItemIndex >= 0 ? _palette[lstPalette.ItemIndex] : null;
        if (surface.PendingClass is { } cls) sbMain.SimpleText = $"Click or drag on the form to place a {cls.Name} (Shift keeps it selected)";
    }

    // ---- Commands (menu items and tool buttons share these handlers) ----

    void mnuNew_Click(object sender)
    {
        if (!ConfirmDiscard()) return;
        _doc.New();
        UpdateTitle();
    }

    void mnuOpen_Click(object sender)
    {
        if (!ConfirmDiscard() || !dlgOpen.Execute()) return;
        OpenFile(dlgOpen.FileName);
    }

    void mnuSave_Click(object sender) => SaveTo(_doc.FileName);
    void mnuSaveAs_Click(object sender) => SaveTo(null);
    void mnuExit_Click(object sender) => Close();
    void mnuUndo_Click(object sender) => _doc.Undo();
    void mnuRedo_Click(object sender) => _doc.Redo();
    void mnuCut_Click(object sender) { surface.CopySelection(); _doc.DeleteSelection(); }
    void mnuCopy_Click(object sender) => surface.CopySelection();
    void mnuPaste_Click(object sender) => surface.PasteClipboard();
    void mnuDelete_Click(object sender) => _doc.DeleteSelection();
    void mnuSelectAll_Click(object sender) => _doc.SelectMany(_doc.PasteTarget().Children.Where(c => c.IsShown || c is Component));

    void mnuMenuEditor_Click(object sender)
    {
        var menu = _doc.Primary as IMenuOwner ?? _doc.Form.Descendants().OfType<IMenuOwner>().FirstOrDefault();
        if (menu == null)
        {
            Dialogs.ShowMessage("Add a MainMenu or PopupMenu to the form first.", this);
            return;
        }
        MenuEditor.Edit(_doc, menu, this);
    }

    void mnuTabOrder_Click(object sender) => TabOrderEditor.Edit(_doc, _doc.PasteTarget(), this);

    void mnuBringToFront_Click(object sender) => Reorder(toFront: true);
    void mnuSendToBack_Click(object sender) => Reorder(toFront: false);

    void Reorder(bool toFront)
    {
        var targets = _doc.Selection.Where(c => c.Parent != null).ToList();
        if (targets.Count == 0) return;
        _doc.Snapshot();
        foreach (var c in targets)
        {
            var parent = c.Parent!;
            parent.Remove(c);
            parent.Insert(toFront ? parent.Children.Count : 0, c);
        }
        _doc.SelectMany(targets);
        _doc.NotifyChanged();
    }

    void mnuShowGrid_Click(object sender)
    {
        surface.ShowGrid = !surface.ShowGrid;
        mniShowGrid.Checked = surface.ShowGrid;
        surface.Invalidate();
    }

    void frmDesigner_CloseQuery(Control sender, CloseQueryEventArgs e)
    {
        e.CanClose = ConfirmDiscard();
        if (e.CanClose) CloseLiveForm();
    }
}
