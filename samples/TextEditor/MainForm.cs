using RadSharp;

namespace TextEditor;

/// <summary>A small Notepad: a Memo, menus, native open/save dialogs and an unsaved-changes prompt.</summary>
public partial class MainForm : Form
{
    string? _path;
    bool _dirty;

    public MainForm()
    {
        InitializeComponent();
        UpdateStatus();
    }

    void UpdateTitle() => Caption = $"{(_path != null ? Path.GetFileName(_path) : "Untitled")}{(_dirty ? " *" : "")} — Text Editor";

    void UpdateStatus()
    {
        var text = memText.Text;
        int caret = Math.Min(memText.CaretPosition, text.Length);
        int line = text.AsSpan(0, caret).Count('\n') + 1;
        int col = caret - (text.LastIndexOf('\n', Math.Max(0, caret - 1)) + 1) + 1;
        if (caret == 0) col = 1;
        sbMain.SimpleText = $"Ln {line}, Col {col}    {text.Length} characters    {(memText.WordWrap ? "Wrap" : "No wrap")}";
    }

    void memText_Change(Control sender)
    {
        if (!_dirty) { _dirty = true; UpdateTitle(); }
        UpdateStatus();
    }

    void memText_KeyUp(Control sender, KeyEventArgs e) => UpdateStatus();
    void memText_MouseUp(Control sender, MouseEventArgs e) => UpdateStatus();

    bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        return Dialogs.MessageDlg("Save the changes?", MessageType.Confirmation, MessageButtons.YesNoCancel, this) switch
        {
            ModalResult.Yes => Save(_path),
            ModalResult.No => true,
            _ => false,
        };
    }

    bool Save(string? path)
    {
        if (path == null)
        {
            if (!dlgSave.Execute()) return false;
            path = dlgSave.FileName;
        }
        File.WriteAllText(path, memText.Text);
        _path = path;
        _dirty = false;
        UpdateTitle();
        return true;
    }

    void Load(string path)
    {
        memText.Text = File.ReadAllText(path);
        _path = path;
        _dirty = false;
        UpdateTitle();
        UpdateStatus();
    }

    void mnuNew_Click(MenuItem sender)
    {
        if (!ConfirmDiscard()) return;
        memText.Text = "";
        _path = null;
        _dirty = false;
        UpdateTitle();
    }

    void mnuOpen_Click(MenuItem sender)
    {
        if (!ConfirmDiscard() || !dlgOpen.Execute()) return;
        try { Load(dlgOpen.FileName); }
        catch (IOException e) { Dialogs.MessageDlg(e.Message, MessageType.Error, owner: this); }
    }

    void mnuSave_Click(MenuItem sender) => Save(_path);
    void mnuSaveAs_Click(MenuItem sender) => Save(null);
    void mnuExit_Click(MenuItem sender) => Close();
    void mnuUndo_Click(MenuItem sender) => memText.Undo();
    void mnuCut_Click(MenuItem sender) => memText.CutToClipboard();
    void mnuCopy_Click(MenuItem sender) => memText.CopyToClipboard();
    void mnuPaste_Click(MenuItem sender) => memText.PasteFromClipboard();
    void mnuSelectAll_Click(MenuItem sender) => memText.SelectAll();

    void mnuWordWrap_Click(MenuItem sender)
    {
        memText.WordWrap = !memText.WordWrap;
        mniWordWrap.Checked = memText.WordWrap;
        UpdateStatus();
    }

    void frmEditor_CloseQuery(Control sender, CloseQueryEventArgs e) => e.CanClose = ConfirmDiscard();

    /// <summary>Some text for screenshots.</summary>
    public void LoadDemoText()
    {
        memText.Text = "RadSharp text editor\n\nA Memo with word wrap, undo, the clipboard and IME input; menus with shortcuts; " +
                       "native open and save dialogs; and a prompt for unsaved changes when closing.\n\n日本語もOK 👍";
        _dirty = true;
        UpdateTitle();
        memText.Focus();
    }
}
