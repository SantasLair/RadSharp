using RadSharp.Design;
using RadSharp.Ide.Language;

namespace RadSharp.Designer;

/// <summary>
/// Navigation (milestone I5): the command palette, quick open (files, symbols, lines), the back/forward history and the Go menu.
/// Every entry is a <see cref="Command"/>; the pickers are one <see cref="QuickPick"/> popup. Find in Files is in
/// <c>DesignerForm.Search.cs</c>, the structure/code highlights in <c>DesignerForm.CodeSync.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shortcuts.</b> Ctrl+Shift+P palette, Ctrl+P go to file, Ctrl+T go to symbol in workspace, Ctrl+Shift+O go to symbol in file,
/// Ctrl+G go to line, Alt+Left / Alt+Right back and forward (on macOS Ctrl+- and Ctrl+Shift+-, where Alt+arrows move by word),
/// Ctrl+Shift+F / Ctrl+Shift+H find / replace in files (Mod is Ctrl, or Cmd on macOS). The code editor handles none of these.
/// </para>
/// <para>
/// <b>History.</b> The shell tells <see cref="NavigationHistory"/> where the user is whenever the active document changes, the code
/// caret moves or a control is selected; a jump that opens, activates and positions (<c>NavigateToCode</c>, a quick-open pick) is one
/// entry (<see cref="JumpScope"/>). Back and forward reopen a closed document if they must.
/// </para>
/// </remarks>
public partial class DesignerForm
{
    readonly NavigationHistory _history = new();
    RecentCommands _recentCommands = new();
    QuickPick? _quickPick;

    /// <summary>The back/forward list (tests and the toolbar read it).</summary>
    public NavigationHistory History => _history;

    /// <summary>The command palette or quick-open popup, while one is open.</summary>
    public QuickPick? ActiveQuickPick => _quickPick is { } q && IsPopupOpen(q) ? q : null;

    /// <summary>The commands the palette ran, newest first (persisted in <c>recent-commands.json</c> in the IDE settings folder).</summary>
    public RecentCommands RecentCommands => _recentCommands;

    string RecentCommandsPath => Path.Combine(_paths.Directory, "recent-commands.json");

    // ---- Commands, menus, toolbar ----

    bool HasCodeFile => _project != null && _docs.Active is CodeDocument;
    bool HasSymbolsInFile => _project != null && (_docs.Active is CodeDocument { IsCSharp: true } || _docs.Active is FormDocument);

    void RegisterNavigationCommands()
    {
        var c = _commands;
        _recentCommands = RecentCommands.Load(RecentCommandsPath);
        void Reg(string id, string title, string category, Action run, string shortcut = "", Func<bool>? enabled = null) =>
            c.Register(new Command(id, title, category, run, shortcut, enabled));

        bool mac = OperatingSystem.IsMacOS();   // Alt+arrows move by word there
        Reg("Go.CommandPalette", "Command Palette…", "Go", () => ShowCommandPalette(), "Mod+Shift+P");
        Reg("Go.File", "Go to File…", "Go", () => ShowGoToFile(), "Mod+P", () => HasProject);
        Reg("Go.SymbolInWorkspace", "Go to Symbol in Workspace…", "Go", () => ShowGoToSymbolInWorkspace(), "Mod+T", () => HasProject);
        Reg("Go.SymbolInFile", "Go to Symbol in File…", "Go", () => ShowGoToSymbolInFile(), "Mod+Shift+O", () => HasSymbolsInFile);
        Reg("Go.Line", "Go to Line…", "Go", () => ShowGoToLine(), "Mod+G", () => HasCodeFile);
        Reg("Go.Back", "Navigate Back", "Go", () => GoBack(), mac ? "Ctrl+-" : "Alt+Left", () => _history.CanGoBack);
        Reg("Go.Forward", "Navigate Forward", "Go", () => GoForward(), mac ? "Ctrl+Shift+-" : "Alt+Right", () => _history.CanGoForward);
        Reg("Search.FindInFiles", "Find in Files…", "Search", () => ShowFindInFiles(replace: false), "Mod+Shift+F", () => HasProject);
        Reg("Search.ReplaceInFiles", "Replace in Files…", "Search", () => ShowFindInFiles(replace: true), "Mod+Shift+H", () => HasProject);
        c.Find("Go.CommandPalette")!.HiddenFromPalette = true;   // opening the palette from the palette is a no-op

        // Commands that take a parameter from a menu (a tab, a tool window) are no use without one.
        foreach (var id in new[] { "File.Reopen", "Window.DockLeft", "Window.DockRight", "Window.DockBottom", "Window.Hide", "Window.Float", "Window.KeepOpen" })
            if (c.Find(id) is { } hidden) hidden.HiddenFromPalette = true;

        // Back and forward buttons beside Undo/Redo; the buttons' enabled state follows the history.
        var bar = tbMain;
        int at = bar.Children.ToList().FindIndex(x => x.Name == "sep2") + 1;
        if (at <= 0) at = bar.Children.Count;
        var back = new ToolButton { Name = "btnBack", Caption = "◀ Back", Width = 64 };
        var forward = new ToolButton { Name = "btnForward", Caption = "Forward ▶", Width = 80 };
        bar.Insert(at, back);
        bar.Insert(at + 1, forward);
        var navSeparator = new ToolSeparator { Name = "sepNavigation" };
        bar.Insert(at + 2, navSeparator);
        navSeparator.SetBounds(0, 0, navSeparator.Px(9), navSeparator.Px(24));
        back.Width = back.Px(64);   // widths are device pixels: scale them like the buttons the radform defines
        forward.Width = forward.Px(80);
        c.Bind(back, "Go.Back");
        c.Bind(forward, "Go.Forward");

        bool couldBack = false, couldForward = false;
        _history.Changed += () =>
        {
            if (couldBack == _history.CanGoBack && couldForward == _history.CanGoForward) return;
            couldBack = _history.CanGoBack;
            couldForward = _history.CanGoForward;
            RefreshCommandState();
        };

        InitSearch();
    }

    /// <summary>The Go menu (between View and Code).</summary>
    void AddGoMenuItems(MenuItem go)
    {
        foreach (var id in new[] { "Go.Back", "Go.Forward" }) go.Items.Add(_commands.CreateMenuItem(id));
        go.AddSeparator();
        foreach (var id in new[] { "Go.File", "Go.SymbolInWorkspace", "Go.SymbolInFile", "Go.Line" }) go.Items.Add(_commands.CreateMenuItem(id));
        go.AddSeparator();
        go.Items.Add(_commands.CreateMenuItem("Go.CommandPalette"));
    }

    /// <summary>Find in Files and Replace in Files at the end of the Edit menu.</summary>
    void AddSearchMenuItems(MenuItem edit)
    {
        edit.AddSeparator();
        foreach (var id in new[] { "Search.FindInFiles", "Search.ReplaceInFiles" }) edit.Items.Add(_commands.CreateMenuItem(id));
    }

    // ---- The popup ----

    QuickPick NewPicker(string placeholder)
    {
        if (_quickPick != null && IsPopupOpen(_quickPick)) ClosePopup(_quickPick);
        return _quickPick = new QuickPick { Placeholder = placeholder };
    }

    // ---- Command palette ----

    /// <summary>Ctrl+Shift+P: every command, fuzzy filtered; Enter runs the chosen one (a disabled one is shown greyed and can't be chosen).</summary>
    public QuickPick ShowCommandPalette()
    {
        var pick = NewPicker("Type a command…");
        pick.EmptyText = "No matching commands";
        pick.SetItems(CommandPalette.Build(_commands, _recentCommands));
        pick.Accepted += item =>
        {
            if (item.Tag is not Command command) return;
            _recentCommands.Touch(command.Id);
            _recentCommands.Save(RecentCommandsPath);
            command.Execute();
        };
        pick.Open(this);
        return pick;
    }

    // ---- Go to file ----

    /// <summary>Ctrl+P: every file of the open projects (not bin/obj), fuzzy matched on the file name and then the path; open documents first.</summary>
    public QuickPick ShowGoToFile()
    {
        var pick = NewPicker("Go to file…");
        pick.EmptyText = "No matching files";
        pick.SetItems(ProjectFileItems());
        pick.Accepted += item => { if (item.Tag is string path) OpenFileFromPicker(path); };
        pick.Open(this);
        return pick;
    }

    List<QuickPickItem> ProjectFileItems()
    {
        var items = new List<QuickPickItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        QuickPickItem Item(string file)
        {
            var project = ProjectOf(file);
            var dir = Path.GetDirectoryName(file) ?? "";
            var relative = project != null ? Path.GetRelativePath(project.Directory, dir) : dir;
            if (relative == ".") relative = "";
            var detail = Projects.Count > 1 && project != null ? Path.Combine(project.Name, relative).TrimEnd('\\', '/') : relative;
            return new QuickPickItem(Path.GetFileName(file), detail.Replace('\\', '/'), "", true, file);
        }
        foreach (var doc in _docs.Documents)
            if (doc.Path is { } p && seen.Add(p)) items.Add(Item(p));
        foreach (var file in FindInFiles.EnumerateFiles(Projects.Select(p => p.Directory), ""))
            if (seen.Add(file)) items.Add(Item(file));
        return items;
    }

    /// <summary>Opens a project file as the Project window would: a form in the designer, text in the code editor; an open file just comes to the front.</summary>
    void OpenFileFromPicker(string path)
    {
        using var _ = JumpScope();
        if (FindDocument(path) is { } open) { _docs.Activate(open); return; }
        if (path.EndsWith(".radform.json", StringComparison.OrdinalIgnoreCase)) OpenFormFile(path);
        else if (File.Exists(path)) NavigateToCode(path, 1, 1);
    }

    // ---- Go to symbol ----

    /// <summary>Ctrl+T: declarations of the projects by name (Roslyn); a control field leads to the control in its form.</summary>
    public QuickPick ShowGoToSymbolInWorkspace()
    {
        var pick = NewPicker("Go to symbol in workspace…");
        pick.FilterLocally = false;
        pick.EmptyText = "Type a symbol name";
        CancellationTokenSource? running = null;
        pick.QueryChanged += query =>
        {
            running?.Cancel();
            query = query.Trim();
            if (query.Length == 0) { pick.EmptyText = "Type a symbol name"; pick.SetItems([]); return; }
            var hosts = _languageHosts.Values.Where(h => h.IsReady).ToList();
            if (hosts.Count == 0) { pick.EmptyText = LanguageHosts.Count == 0 ? "No language service for this project" : "The language service is still starting…"; pick.SetItems([]); return; }
            pick.EmptyText = "No matching symbols";
            var cts = running = new CancellationTokenSource();
            var perHost = new Dictionary<LanguageHost, IReadOnlyList<WorkspaceSymbol>>();
            foreach (var host in hosts)
            {
                var h = host;
                host.Flush();
                _ = host.Request((s, t) => s.SearchSymbolsAsync(query, 50, t), result =>
                {
                    if (cts.IsCancellationRequested) return;
                    perHost[h] = result.Value;
                    pick.SetItems(perHost.Values.SelectMany(v => v).Take(100).Select(SymbolItem));
                }, cts.Token);
            }
        };
        pick.Accepted += item =>
        {
            if (item.Tag is not WorkspaceSymbol symbol) return;
            using var _ = JumpScope();
            NavigateToDefinition(symbol.Location);
        };
        pick.Open(this);
        return pick;
    }

    static QuickPickItem SymbolItem(WorkspaceSymbol s)
    {
        var where = s.Location.File is { } file ? $"{Path.GetFileName(file)}:{s.Location.Line}" : "";
        return new QuickPickItem(s.Name, s.Container.Length > 0 ? $"{s.Kind} · {s.Container}" : s.Kind, where, true, s);
    }

    /// <summary>Ctrl+Shift+O: the declarations of the active C# file (from its syntax tree), or the controls of the active form.</summary>
    public QuickPick? ShowGoToSymbolInFile()
    {
        switch (_docs.Active)
        {
            case CodeDocument { IsCSharp: true } code:
            {
                var pick = NewPicker("Go to symbol in file…");
                pick.EmptyText = "No matching symbols";
                pick.SetItems(LanguageService.GetDocumentSymbols(code.Text).Select(s =>
                    new QuickPickItem(s.Name, s.Container.Length > 0 ? $"{s.Kind} · {s.Container}" : s.Kind, $"line {s.Line}", true, s)));
                pick.Accepted += item =>
                {
                    if (item.Tag is not DocumentSymbol s) return;
                    using var _ = JumpScope();
                    code.GoTo(s.Line, s.Column);
                    code.Focus();
                };
                pick.Open(this);
                return pick;
            }
            case FormDocument form:
            {
                var pick = NewPicker("Go to control…");
                pick.EmptyText = "No matching controls";
                var root = form.Document.Form;
                pick.SetItems(root.Descendants().Where(c => c.Name.Length > 0).Select(c =>
                    new QuickPickItem(c.Name, ComponentRegistry.Of(c).Name, "", true, c)));
                pick.Accepted += item =>
                {
                    if (item.Tag is not Control control) return;
                    using var _ = JumpScope();
                    form.Document.Select(control);
                    form.Focus();
                };
                pick.Open(this);
                return pick;
            }
        }
        return null;
    }

    // ---- Go to line ----

    /// <summary>Ctrl+G: a small input for a line number (or <c>line:column</c>) in the active code file.</summary>
    public QuickPick? ShowGoToLine()
    {
        if (_docs.Active is not CodeDocument code) return null;
        int lines = LineCountOf(code);
        var pick = NewPicker("Line number, or line:column");
        pick.InputOnly = true;
        pick.HintProvider = text => TryParseLine(text, lines, out int line, out _)
            ? $"Go to line {line} of {lines}"
            : $"Type a line number between 1 and {lines} (line:column also works)";
        pick.Validate = text => TryParseLine(text, lines, out _, out _);
        pick.Submitted += text =>
        {
            if (!TryParseLine(text, lines, out int line, out int column)) return;
            using var _ = JumpScope();
            code.GoTo(line, column);
            code.Focus();
        };
        pick.Open(this);
        return pick;
    }

    static int LineCountOf(CodeDocument code) =>
        code.Editor is CodeEditorAdapter a ? a.Editor.LineCount : code.Text.Count(ch => ch == '\n') + 1;

    /// <summary>Parses <c>12</c> or <c>12:5</c> (1-based); the line is clamped to the file, a line outside it is an error only if it isn't a positive number.</summary>
    public static bool TryParseLine(string text, int lineCount, out int line, out int column)
    {
        line = 1; column = 1;
        var parts = text.Trim().Split(':', 2, StringSplitOptions.TrimEntries);
        if (!int.TryParse(parts[0], out line) || line < 1) return false;
        if (parts.Length > 1 && (!int.TryParse(parts[1], out column) || column < 1)) return false;
        line = Math.Min(line, Math.Max(1, lineCount));
        return true;
    }

    // ---- Back and forward ----

    /// <summary>Alt+Left: the previous place (a document and caret, or a form and its selected control).</summary>
    public bool GoBack() => Step(back: true);

    /// <summary>Alt+Right: the next place, after going back.</summary>
    public bool GoForward() => Step(back: false);

    bool Step(bool back)
    {
        CaptureLocation();   // the place we are at now is the one to come back to
        // An entry that can't be shown (its file is gone) is dropped by the history, and the next one is tried.
        bool Show(NavLocation location) => File.Exists(location.Path) && ShowLocation(location);
        return (back ? _history.Back(Show) : _history.Forward(Show)) != null;
    }

    /// <summary>Shows a history entry, reopening the document if it was closed. The arrival isn't recorded again.</summary>
    bool ShowLocation(NavLocation location)
    {
        _history.Navigating = true;
        try
        {
            if (location.Kind == NavLocation.Form)
            {
                if (OpenFormFile(location.Path) is not { } form) return false;
                var root = form.Document.Form;
                var control = location.Control.Length > 0 ? root.FindControl(location.Control) : null;
                form.Document.Select(control == root ? null : control);
                form.Focus();
                return true;
            }
            if (OpenCodeFile(location.Path) is not { } code) return false;
            code.GoTo(location.Line, location.Column);
            code.Focus();
            return true;
        }
        finally { _history.Navigating = false; }
    }

    /// <summary>Everything done until the scope is disposed (opening, activating and positioning) is one history entry.</summary>
    IDisposable JumpScope()
    {
        _history.BeginJump();
        return new JumpEnd(_history);
    }

    sealed class JumpEnd(NavigationHistory history) : IDisposable
    {
        bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            history.EndJump();
        }
    }

    /// <summary>Tells the history where the user is (the active document's caret, or its selected control).</summary>
    void CaptureLocation()
    {
        if (_restoring || _history.Navigating || LocationOf(_docs?.Active) is not { } location) return;
        _history.Capture(location);
    }

    static NavLocation? LocationOf(IDocumentView? doc) => doc switch
    {
        CodeDocument code => NavLocation.InCode(code.Path, code.Editor.Caret.Line, code.Editor.Caret.Column),
        FormDocument form => NavLocation.InForm(form.Path, form.Document.Selection.Count > 0 ? form.Document.Primary.Name : ""),
        _ => null,
    };

    /// <summary>Starts following a new document: caret moves and selections become history entries and drive the structure/code highlights.</summary>
    void HookNavigation(IDocumentView doc)
    {
        switch (doc)
        {
            case CodeDocument code:
                if (code.Editor is CodeEditorAdapter adapter)
                    adapter.Editor.CaretMoved += _ => OnCaretMoved(code);
                code.Closed += () => OnCodeDocumentClosed(code);
                break;
            case FormDocument form:
                form.Document.SelectionChanged += () => OnFormSelectionChanged(form);
                form.Document.Changed += () => OnFormChangedForSync(form);
                break;
        }
        HookCodeSync(doc);
    }

    void OnCaretMoved(CodeDocument code)
    {
        if (_docs != null && _docs.Active == code) CaptureLocation();
        UpdateSurfaceHighlights(code);
    }

    void OnFormSelectionChanged(FormDocument form)
    {
        if (_docs.Active == form) CaptureLocation();
        UpdateCodeHighlights(form);
    }

    /// <summary>The project closed: nothing to go back to, nothing found.</summary>
    void ClearNavigationHistory()
    {
        _history.Clear();
        _findInFilesResults = [];
        _findRowActions = null;
        _highlightSources.Clear();
        _symbolCache.Clear();
        _syncSignatures.Clear();
    }
}
