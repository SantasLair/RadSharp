using RadSharp.CodeEditing;
using RadSharp.Drawing;

namespace RadSharp.Designer;

/// <summary>Tools › Options: the Editor page (font, tabs, line numbers, colour theme, autosave) beside the General one.</summary>
public sealed partial class OptionsDialog
{
    ComboBox? _theme, _family;
    SpinEdit? _fontSize, _tabSize, _autosaveSeconds;
    CheckBox? _ligatures, _spaces, _lineNumbers, _currentLine, _followOs, _autosaveFocus;
    readonly List<string> _families = [];

    /// <summary>Moves the existing controls onto a General page and adds the Editor page; the buttons stay below.</summary>
    void BuildPages()
    {
        if (_ide == null) return;
        var general = Children.Where(c => c.Name is not ("btnOk" or "btnCancel")).ToList();
        var buttons = Children.Where(c => c.Name is "btnOk" or "btnCancel").ToList();
        SetBounds(0, 0, DialogLayout.S(640), DialogLayout.S(620));
        DesignWidth = Width;
        DesignHeight = Height;

        var pages = DialogLayout.Put(this, new PageControl { Name = "pgOptions" }, 8, 8, 624, 556);
        var tabGeneral = new TabSheet { Name = "tabGeneral", Caption = "General" };
        var tabEditor = new TabSheet { Name = "tabEditor", Caption = "Editor" };
        pages.Add(tabGeneral);
        pages.Add(tabEditor);
        foreach (var c in general) { Remove(c); tabGeneral.Add(c); }
        foreach (var b in buttons) b.SetBounds(b.Left, DialogLayout.S(572), b.Width, b.Height);
        foreach (var b in buttons) { Remove(b); Add(b); }   // after the page control, so the buttons are on top

        int y = 16;
        T Put<T>(T c, int x, int w, int h = 26) where T : Control => DialogLayout.Put(tabEditor, c, x, y, w, h);
        void Row(string caption) => DialogLayout.Put(tabEditor, new Label { Caption = caption }, 16, y + 2, 170, 24);

        Row("Colour theme");
        _theme = Put(new ComboBox { Name = "cboEditorTheme", Items = [.. EditorThemes.All.Select(t => t.Name)] }, 190, 260);
        _theme.ItemIndex = Math.Max(0, EditorThemes.All.ToList().FindIndex(t => t.Id == _ide.EditorTheme));
        y += 36;

        Row("Font");
        _families.AddRange([CodeEditorFonts.Family, "Inter"]);
        var names = new List<string> { "JetBrains Mono (bundled)", "Inter (the UI font)" };
        if (!_families.Contains(_ide.EditorFontFamily, StringComparer.OrdinalIgnoreCase)) { _families.Add(_ide.EditorFontFamily); names.Add(_ide.EditorFontFamily); }
        _family = Put(new ComboBox { Name = "cboEditorFont", Items = [.. names] }, 190, 260);
        _family.ItemIndex = _families.FindIndex(f => string.Equals(f, _ide.EditorFontFamily, StringComparison.OrdinalIgnoreCase));
        y += 36;

        Row("Font size (px)");
        _fontSize = Put(new SpinEdit { Name = "edtEditorFontSize", MinValue = 6, MaxValue = 48, Value = (int)Math.Round(_ide.EditorFontSize) }, 190, 90);
        y += 36;

        _ligatures = Put(new CheckBox { Name = "chkLigatures", Caption = "Programming ligatures (=> != ...), JetBrains Mono only", Checked = _ide.Ligatures }, 16, 580);
        y += 36;

        Row("Tab size");
        _tabSize = Put(new SpinEdit { Name = "edtTabSize", MinValue = 1, MaxValue = 16, Value = _ide.TabSize }, 190, 90);
        y += 36;
        _spaces = Put(new CheckBox { Name = "chkInsertSpaces", Caption = "Insert spaces when Tab is pressed", Checked = _ide.InsertSpaces }, 16, 580);
        y += 32;
        _lineNumbers = Put(new CheckBox { Name = "chkLineNumbers", Caption = "Show line numbers", Checked = _ide.ShowLineNumbers }, 16, 580);
        y += 32;
        _currentLine = Put(new CheckBox { Name = "chkCurrentLine", Caption = "Highlight the current line", Checked = _ide.HighlightCurrentLine }, 16, 580);
        y += 40;

        _followOs = Put(new CheckBox
        {
            Name = "chkFollowOs",
            Caption = SystemTheme.Available ? "Follow the operating system's light/dark setting" : "Follow the operating system's light/dark setting (not available on this system)",
            Checked = _ide.FollowOsTheme,
            Enabled = SystemTheme.Available,
        }, 16, 580);
        y += 40;

        _autosaveFocus = Put(new CheckBox { Name = "chkAutosaveFocus", Caption = "Save a file when its editor loses focus", Checked = _ide.AutosaveOnFocusLoss }, 16, 580);
        y += 32;
        Row("Save after idle (seconds)");
        _autosaveSeconds = Put(new SpinEdit { Name = "edtAutosaveSeconds", MinValue = 0, MaxValue = 3600, Value = _ide.AutosaveIdleSeconds }, 190, 90);
        DialogLayout.Put(tabEditor, new Label { Caption = "0 = off. Unsaved changes are also kept for crash recovery every 30 seconds.", FontColor = Drawing.Color.FromHex("#6B7280") }, 290, y + 2, 320, 24);
    }

    /// <summary>Takes the Editor page into the IDE settings.</summary>
    void ApplyEditorPage()
    {
        if (_ide == null || _theme == null) return;
        _ide.EditorTheme = EditorThemes.All[Math.Clamp(_theme.ItemIndex, 0, EditorThemes.All.Count - 1)].Id;
        if (_family!.ItemIndex >= 0 && _family.ItemIndex < _families.Count) _ide.EditorFontFamily = _families[_family.ItemIndex];
        _ide.EditorFontSize = _fontSize!.Value;
        _ide.Ligatures = _ligatures!.Checked;
        _ide.TabSize = _tabSize!.Value;
        _ide.InsertSpaces = _spaces!.Checked;
        _ide.ShowLineNumbers = _lineNumbers!.Checked;
        _ide.HighlightCurrentLine = _currentLine!.Checked;
        _ide.FollowOsTheme = _followOs!.Checked && SystemTheme.Available;
        _ide.AutosaveOnFocusLoss = _autosaveFocus!.Checked;
        _ide.AutosaveIdleSeconds = _autosaveSeconds!.Value;
    }
}
