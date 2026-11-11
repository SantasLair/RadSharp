using RadSharp.Drawing;
using RadSharp.CodeEditing;
using RadSharp.Design.Code;

namespace RadSharp.Designer;

/// <summary>
/// Polish and feel (I9): editor settings and themes applied live, "follow the OS theme", autosave, crash recovery of
/// unsaved buffers, the editor context menu, and re-opening the language workspace when a project file changed.
/// </summary>
public partial class DesignerForm
{
    readonly Timer _polishTimer = new() { Name = "tmrPolish", Interval = 1000 };
    RecoveryStore _recovery = null!;
    IReadOnlyList<RecoveryItem> _pendingRecovery = [];
    DateTime _lastEdit = DateTime.UtcNow, _lastRecoveryWrite = DateTime.UtcNow;
    bool _editedSinceAutosave;
    readonly Dictionary<string, DateTime> _projectStamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How often unsaved buffers are written to the recovery folder.</summary>
    public static TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The clock autosave and recovery use (tests move it).</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public RecoveryStore Recovery => _recovery;

    /// <summary>The unsaved buffers an abnormal exit left, waiting for <see cref="OfferRecovery"/>.</summary>
    public IReadOnlyList<RecoveryItem> PendingRecovery => _pendingRecovery;

    void InitPolish()
    {
        _recovery = new RecoveryStore(Path.Combine(_paths.Directory, "recovery"));
        if (_recovery.BeginSession()) _pendingRecovery = _recovery.List();
        _polishTimer.Elapsed += _ => PolishTick();
        Add(_polishTimer);
        Shown += _ => OfferRecovery();
        ApplyOsTheme();
    }

    /// <summary>Called by the timer: idle autosave, the periodic recovery write, the OS theme.</summary>
    public void PolishTick()
    {
        RunAutosaveTick();
        if (Clock() - _lastRecoveryWrite >= RecoveryInterval) WriteRecovery();
        ApplyOsTheme();
    }

    // ---- Editor settings and themes ----

    /// <summary>Applies the Editor options (font, tabs, line numbers, colours) to every open code document.</summary>
    public void ApplyEditorSettings()
    {
        foreach (var doc in _docs.Documents.OfType<CodeDocument>()) ApplyEditorSettings(doc);
    }

    void ApplyEditorSettings(CodeDocument doc)
    {
        if (doc.Editor is not CodeEditorAdapter adapter) return;
        var e = adapter.Editor;
        e.FontFamily = _ide.EditorFontFamily;
        e.FontSize = _ide.EditorFontSize;
        e.Ligatures = _ide.Ligatures;
        e.TabSize = _ide.TabSize;
        e.InsertSpaces = _ide.InsertSpaces;
        e.ShowLineNumbers = _ide.ShowLineNumbers;
        e.HighlightCurrentLine = _ide.HighlightCurrentLine;
        e.Colors = EditorThemes.Colors(_ide.EditorTheme);
        e.Invalidate();
    }

    /// <summary>Switches the IDE between its light and dark themes (the editors follow unless they have their own colour theme).</summary>
    public void SetDarkTheme(bool dark)
    {
        _settings.Theme = dark ? "dark" : "light";
        Theme.Current = dark ? Theme.Dark : Theme.Default;
        this.Theme = Theme.Current;
        SaveSettings();
        ApplyEditorSettings();
        InvalidateAll();
    }

    /// <summary>With "Follow OS theme" on, takes the light/dark choice from the operating system. True if the theme changed.</summary>
    public bool ApplyOsTheme()
    {
        if (!_ide.FollowOsTheme || SystemTheme.IsDark is not { } dark || dark == (_settings.Theme == "dark")) return false;
        SetDarkTheme(dark);
        return true;
    }

    // ---- Per-document hooks (called when a document is added) ----

    void HookDocument(IDocumentView doc)
    {
        if (doc is CodeDocument code)
        {
            ApplyEditorSettings(code);
            code.Editor.Control.ContextMenu = BuildCodeContextMenu(code);
            code.Editor.TextChanged += NoteEdit;
        }
        else if (doc is FormDocument form) form.Document.Changed += NoteEdit;
        doc.View.LostFocus += _ => { if (_ide.AutosaveOnFocusLoss && doc.IsDirty && doc.Path != null && _docs.Documents.Contains(doc)) Save(doc); };
    }

    void NoteEdit()
    {
        _lastEdit = Clock();
        _editedSinceAutosave = true;
    }

    // ---- Autosave ----

    /// <summary>Saves the modified documents once <see cref="IdeSettings.AutosaveIdleSeconds"/> passed without an edit. Returns how many were saved.</summary>
    public int RunAutosaveTick()
    {
        if (_ide.AutosaveIdleSeconds <= 0 || !_editedSinceAutosave || Clock() - _lastEdit < TimeSpan.FromSeconds(_ide.AutosaveIdleSeconds)) return 0;
        _editedSinceAutosave = false;
        int saved = 0;
        foreach (var d in _docs.Documents.Where(d => d.IsDirty && d.Path != null).ToList())
            if (Save(d)) saved++;
        return saved;
    }

    // ---- Crash recovery ----

    /// <summary>Writes every unsaved buffer (code text, form JSON) to the recovery folder; with none, the folder is emptied.</summary>
    public void WriteRecovery()
    {
        _lastRecoveryWrite = Clock();
        var now = DateTime.UtcNow;
        var items = new List<RecoveryItem>();
        foreach (var d in _docs.Documents.Where(d => d.IsDirty && d.Path != null))
        {
            switch (d)
            {
                case CodeDocument c: items.Add(new RecoveryItem("code", c.Path, c.Text, now)); break;
                case FormDocument f: items.Add(new RecoveryItem("form", f.Path, f.Document.Save(), now)); break;
            }
        }
        _recovery.Write(items);
    }

    /// <summary>After an abnormal exit: asks whether to bring the unsaved buffers back.</summary>
    public void OfferRecovery()
    {
        if (_pendingRecovery.Count == 0) return;
        var names = string.Join(", ", _pendingRecovery.Select(i => System.IO.Path.GetFileName(i.Path)).Take(6));
        var answer = Dialogs.MessageDlg($"RADSharp did not close properly last time.\nRestore the unsaved changes in {_pendingRecovery.Count} file(s)?\n{names}",
            MessageType.Warning, MessageButtons.YesNo, this);
        if (answer == ModalResult.Yes) RestoreRecovery();
        else { _recovery.Clear(); _pendingRecovery = []; }
    }

    /// <summary>Opens the files of <see cref="PendingRecovery"/> and puts the saved buffers back as unsaved changes. Returns how many were restored.</summary>
    public int RestoreRecovery()
    {
        if (_project == null) { Output("Open the project first to restore the unsaved changes from the last session."); return 0; }
        int restored = 0;
        foreach (var item in _pendingRecovery)
        {
            if (!File.Exists(item.Path)) continue;
            switch (item.Kind)
            {
                case "code" when OpenCodeFile(item.Path, activate: false) is { } code:
                    if (code.Text == item.Text) break;
                    code.Text = item.Text;
                    restored++;
                    break;
                case "form" when OpenFormFile(item.Path, activate: false) is { } form:
                    if (form.Document.Save() == item.Text) break;
                    try
                    {
                        form.Document.Snapshot();
                        form.Document.Load(item.Text);
                        form.Document.FileName = form.Path;
                        form.Document.NotifyChanged();
                        restored++;
                    }
                    catch (FormFileException) { Output($"The recovered copy of {Path.GetFileName(item.Path)} could not be read; the file on disk is unchanged."); }
                    break;
            }
        }
        _pendingRecovery = [];
        if (restored > 0) Output($"Restored unsaved changes in {restored} file(s) from the last session.");
        WriteRecovery();
        return restored;
    }

    /// <summary>A clean exit: the recovery files and the running marker go.</summary>
    void EndRecoverySession() => _recovery.EndSession();

    // ---- The editor context menu ----

    /// <summary>The right-click menu of a code document: language commands (C# only), then Cut, Copy, Paste.</summary>
    public PopupMenu BuildCodeContextMenu(CodeDocument doc)
    {
        var menu = new PopupMenu();
        if (doc.IsCSharp)
        {
            Item(menu, "Code.GoToDefinition", "Go to Definition");
            Item(menu, "Code.FindReferences", "Find All References");
            Item(menu, "Code.Rename", "Rename…");
            Item(menu, "Code.FormatDocument", "Format Document");
            Item(menu, "Code.QuickFix", "Quick Fixes…");
            Separator(menu);
        }
        Item(menu, "Edit.Cut");
        Item(menu, "Edit.Copy");
        Item(menu, "Edit.Paste");
        Separator(menu);
        Item(menu, "Edit.Undo");
        Item(menu, "Edit.Redo");
        Item(menu, "Edit.SelectAll");
        // The commands act on the active document: a right-click in the other half of a split view makes it active first.
        menu.Opening += _ =>
        {
            if (_docs.Active != doc) _docs.Activate(doc);
            _commands.Refresh(menu.Items);
        };
        return menu;
    }

    // ---- Re-opening the language workspace when a project file changed ----

    /// <summary>Notes the project files' timestamps and the project's open time (the welcome page shows it).</summary>
    void NoteProjectOpened(string path)
    {
        _ide.LastOpened[Path.GetFullPath(path)] = DateTime.UtcNow;
        SaveIdeSettings();
        foreach (var p in Projects) _projectStamps[p.ProjectFile] = StampOf(p.ProjectFile);
    }

    static DateTime StampOf(string file)
    {
        try { return File.GetLastWriteTimeUtc(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; }
    }

    /// <summary>Re-opens the language workspace of every project whose <c>.csproj</c> changed since it was last looked at. Returns how many.</summary>
    public int CheckProjectFilesChanged()
    {
        int reloaded = 0;
        foreach (var p in Projects)
        {
            var stamp = StampOf(p.ProjectFile);
            if (_projectStamps.TryGetValue(p.ProjectFile, out var old) && old != stamp)
            {
                _projectStamps[p.ProjectFile] = stamp;
                if (ReloadLanguage(p)) reloaded++;
            }
            else _projectStamps[p.ProjectFile] = stamp;
        }
        return reloaded;
    }

    /// <summary>Disposes and recreates a project's language host, re-attaching its open documents and forms.</summary>
    public bool ReloadLanguage(RadProject project)
    {
        if (!LanguageHost.Enabled || !_languageHosts.Remove(project.ProjectFile, out var old)) return false;
        old.Dispose();
        StartLanguage();
        UpdateLiveProblems();
        Output($"{Path.GetFileName(project.ProjectFile)} changed: reloading the language service.");
        return true;
    }

    void ReloadAllLanguage()
    {
        foreach (var p in Projects) ReloadLanguage(p);
    }
}
