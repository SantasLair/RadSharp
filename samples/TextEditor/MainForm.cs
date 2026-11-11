using RadSharp;
using RadSharp.CodeEditing;

namespace TextEditor;

/// <summary>
/// A small code-aware Notepad: a CodeEditor (C# colours, folding, multiple carets, find/replace), menus, native
/// open/save dialogs and an unsaved-changes prompt. Line breaks are kept as loaded (CRLF or LF).
/// </summary>
public partial class MainForm : Form
{
    string? _path;

    public MainForm()
    {
        InitializeComponent();
        UpdateTitle();
        UpdateStatus();
    }

    bool Dirty => edCode.IsModified;

    void UpdateTitle() => Caption = $"{(_path != null ? Path.GetFileName(_path) : "Untitled")}{(Dirty ? " *" : "")} — Text Editor";

    void UpdateStatus()
    {
        var p = edCode.CaretPosition;
        int carets = edCode.Selections.Count;
        var eol = edCode.Buffer.NewLine == "\r\n" ? "CRLF" : "LF";
        sbMain.SimpleText = $"Ln {p.Line + 1}, Col {p.Column + 1}{(carets > 1 ? $"    {carets} carets" : "")}    {edCode.LineCount} lines    {eol}    {(edCode.TokenSource != null ? "C#" : "Plain text")}";
    }

    void edCode_TextChanged(Control sender, TextChangedEventArgs e)
    {
        UpdateTitle();
        UpdateStatus();
    }

    void edCode_CaretMoved(Control sender) => UpdateStatus();

    bool ConfirmDiscard()
    {
        if (!Dirty) return true;
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
        File.WriteAllText(path, edCode.Text);
        edCode.Buffer.MarkClean();
        _path = path;
        UpdateTitle();
        return true;
    }

    void Load(string path)
    {
        edCode.Language = Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase) ? "csharp" : "plain";
        edCode.Text = File.ReadAllText(path);
        mniCSharp.Checked = edCode.Language == "csharp";
        _path = path;
        UpdateTitle();
        UpdateStatus();
    }

    void mnuNew_Click(MenuItem sender)
    {
        if (!ConfirmDiscard()) return;
        edCode.Text = "";
        _path = null;
        UpdateTitle();
        UpdateStatus();
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
    void mnuUndo_Click(MenuItem sender) => edCode.Undo();
    void mnuRedo_Click(MenuItem sender) => edCode.Redo();
    void mnuCut_Click(MenuItem sender) => edCode.CutToClipboard();
    void mnuCopy_Click(MenuItem sender) => edCode.CopyToClipboard();
    void mnuPaste_Click(MenuItem sender) => edCode.PasteFromClipboard();
    void mnuSelectAll_Click(MenuItem sender) => edCode.SelectAll();
    void mnuFind_Click(MenuItem sender) => edCode.ShowFind();
    void mnuReplace_Click(MenuItem sender) => edCode.ShowFind(replace: true);
    void mnuFindNext_Click(MenuItem sender) => edCode.FindNext();

    void mnuLineNumbers_Click(MenuItem sender)
    {
        edCode.ShowLineNumbers = !edCode.ShowLineNumbers;
        mniLineNumbers.Checked = edCode.ShowLineNumbers;
    }

    void mnuCSharp_Click(MenuItem sender)
    {
        edCode.Language = edCode.Language == "csharp" ? "plain" : "csharp";
        mniCSharp.Checked = edCode.Language == "csharp";
        UpdateStatus();
    }

    void frmEditor_CloseQuery(Control sender, CloseQueryEventArgs e) => e.CanClose = ConfirmDiscard();

    /// <summary>Some text for screenshots.</summary>
    public void LoadDemoText()
    {
        edCode.Text = """
            using RadSharp;

            namespace Demo;

            /// <summary>The RADSharp code editor: colours, folding, multiple carets, find/replace.</summary>
            public sealed class Greeter
            {
                readonly string _name = "World";

                public void Greet(int times)
                {
                    for (int i = 0; i < times; i++)
                        Console.WriteLine($"Hello, {_name}! 日本語もOK 👍");
                }
            }
            """;
        edCode.GoTo(12, 12);
        edCode.Focus();
        UpdateTitle();
        UpdateStatus();
    }
}
