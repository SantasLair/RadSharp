using RadSharp.CodeEditing;
using RadSharp.Design.Code;
using RadSharp.Ide.Language;

namespace RadSharp.Designer;

/// <summary>
/// The language service in the IDE (milestones I2/I3): one <see cref="LanguageHost"/> per open project, the code
/// commands (go to definition, find references, rename, format, code fixes, completion, signature help), the semantic
/// rename of a control renamed in the inspector, and the live diagnostics in Problems (source <c>"live"</c>).
/// </summary>
/// <remarks>
/// <b>F12.</b> In a C# code document with the language service ready, F12 goes to the definition of the symbol at the
/// caret. When the active document is a form, when the workspace isn't ready, or when the caret isn't on a symbol with a
/// definition, F12 keeps Delphi's gesture and toggles between the form and its code-behind.
/// </remarks>
public partial class DesignerForm
{
    /// <summary>The Problems source id of the language service's live diagnostics.</summary>
    public const string LiveProblemSource = "live";

    readonly Dictionary<string, LanguageHost> _languageHosts = new(StringComparer.OrdinalIgnoreCase);
    IReadOnlyList<ReferenceInfo> _findResults = [];

    /// <summary>The open projects' language hosts.</summary>
    public IReadOnlyCollection<LanguageHost> LanguageHosts => _languageHosts.Values;

    /// <summary>The language host of the project that owns <paramref name="path"/>.</summary>
    public LanguageHost? LanguageHostFor(string path)
    {
        var project = ProjectOf(path) ?? _project;
        return project != null && _languageHosts.TryGetValue(project.ProjectFile, out var host) ? host : null;
    }

    /// <summary>The language features of the active document, if it's a C# file of a project whose workspace is ready.</summary>
    public DocumentLanguage? ActiveLanguage =>
        _docs.Active is CodeDocument code && LanguageHostFor(code.Path) is { IsReady: true } host ? host.Find(code) : null;

    /// <summary>The last Find All References results (the Find Results window lists them).</summary>
    public IReadOnlyList<ReferenceInfo> FindResults => _findResults;

    /// <summary>The semantic control rename in flight (tests wait on it).</summary>
    public Task? PendingControlRename { get; private set; }

    // ---- Hosts ----

    /// <summary>After a project (or solution) opened: a host per project, opening in the background.</summary>
    void StartLanguage()
    {
        if (!LanguageHost.Enabled) return;
        foreach (var project in Projects)
        {
            if (_languageHosts.ContainsKey(project.ProjectFile)) continue;
            var host = new LanguageHost(project);
            _languageHosts[project.ProjectFile] = host;
            host.LiveDiagnosticsChanged += _ => UpdateLiveProblems();
            host.StateChanged += () =>
            {
                if (host.Error != null) Output($"Language service unavailable for {project.Name}: {host.Error} Editing still works; fix the project (Build shows what is wrong), then run Code > Reload Language Service.");
                RefreshCommandState();
            };
        }
        foreach (var doc in _docs.Documents) AttachLanguage(doc);
    }

    /// <summary>The project closed: its workspaces go, and so do their live problems.</summary>
    void StopLanguage()
    {
        foreach (var host in _languageHosts.Values) host.Dispose();
        _languageHosts.Clear();
        _problemStore.Clear(LiveProblemSource);
        _findResults = [];
        lstFindResults.Items = [];
    }

    void AttachLanguage(IDocumentView doc)
    {
        if (doc.Path is not { } path || LanguageHostFor(path) is not { } host) return;
        if (doc is CodeDocument code) host.Attach(code);
        else if (doc is FormDocument form) host.AttachForm(form);
    }

    void DetachLanguage(IDocumentView doc)
    {
        foreach (var host in _languageHosts.Values)
        {
            if (doc is CodeDocument code) host.Detach(code);
            else if (doc is FormDocument form) host.DetachForm(form);
        }
    }

    void UpdateLiveProblems()
    {
        var messages = _languageHosts.Values.SelectMany(h => h.LiveBuildMessages).ToList();
        if (messages.Count == 0 && _problemStore.Find(LiveProblemSource) == null) return;
        _problemStore.Set(LiveProblemSource, messages);
    }

    // ---- Commands ----

    bool HasCodeLanguage => ActiveLanguage != null;

    void RegisterLanguageCommands()
    {
        var c = _commands;
        void Reg(string id, string title, Action run, string shortcut = "") =>
            c.Register(new Command(id, title, "Code", run, shortcut, () => HasCodeLanguage));
        // F12 itself stays on View.ToggleFormCode (see the class remarks): one key, one command, rebindable as one.
        Reg("Code.GoToDefinition", "Go to Definition", () => _ = GoToDefinition());
        Reg("Code.FindReferences", "Find All References", () => _ = FindReferences(), "Shift+F12");
        Reg("Code.Rename", "Rename…", () => _ = RenameSymbol(), "F2");
        Reg("Code.FormatDocument", "Format Document", () => _ = Format(selection: false), "Shift+Alt+F");
        Reg("Code.FormatSelection", "Format Selection", () => _ = Format(selection: true));
        Reg("Code.QuickFix", "Quick Fix…", () => _ = ShowCodeFixes(), "Ctrl+.");
        Reg("Code.Complete", "Complete Word", () => ActiveLanguage?.Editor.ShowCompletion(), "Ctrl+Space");
        Reg("Code.SignatureHelp", "Parameter Info", () => _ = ActiveLanguage?.ShowSignatureHelp(), "Ctrl+Shift+Space");
        lstFindResults.DoubleClick += _ => OpenFindResult(lstFindResults.ItemIndex);
    }

    void AddCodeMenuItems(MenuItem code)
    {
        foreach (var id in new[] { "Code.GoToDefinition", "Code.FindReferences", "Code.Rename" })
            code.Items.Add(_commands.CreateMenuItem(id));
        code.AddSeparator();
        foreach (var id in new[] { "Code.Complete", "Code.SignatureHelp", "Code.QuickFix" })
            code.Items.Add(_commands.CreateMenuItem(id));
        code.AddSeparator();
        foreach (var id in new[] { "Code.FormatDocument", "Code.FormatSelection", "Code.ReloadLanguage" })
            code.Items.Add(_commands.CreateMenuItem(id));
    }

    // ---- F12, go to definition ----

    /// <summary>F12: go to definition in code (when there is one at the caret), otherwise toggle form and code.</summary>
    public Task GoToDefinitionOrToggle()
    {
        if (ActiveLanguage == null) { ToggleFormCode(); return Task.CompletedTask; }
        return GoToDefinition(toggleIfNone: true);
    }

    /// <summary>Opens the definition of the symbol at the caret: a source file at its line, the designer with a control selected, or a message for metadata.</summary>
    public Task GoToDefinition(bool toggleIfNone = false)
    {
        if (ActiveLanguage is not { } lang || LanguageHostFor(lang.Path) is not { } host) return Task.CompletedTask;
        host.Flush();
        int caret = lang.Editor.CaretOffset;
        var path = lang.Path;
        return host.Request((s, t) => s.GoToDefinitionAsync(path, caret, t), result =>
        {
            var targets = result.Value;
            if (targets.Count == 0)
            {
                if (toggleIfNone) ToggleFormCode();
                else sbMain.SimpleText = "No definition here";
                return;
            }
            var target = targets.FirstOrDefault(t => t.Kind == "Source") ?? targets.FirstOrDefault(t => t.Kind == "Form") ?? targets[0];
            NavigateToDefinition(target);
        });
    }

    void NavigateToDefinition(DefinitionLocation target)
    {
        using var _ = JumpScope();
        switch (target.Kind)
        {
            case "Source" when target.File != null:
                NavigateToCode(target.File, target.Line, target.Column);
                break;
            case "Form" when target.File != null:
                if (OpenFormFile(target.File) is { } form && form.Document.Form.FindControl(target.Symbol) is { } control)
                {
                    form.Document.Select(control);
                    sbMain.SimpleText = $"{target.Symbol}: {ComponentRegistry.Of(control).Name} on {form.ClassName}";
                }
                break;
            case "Metadata":
                sbMain.SimpleText = $"{target.Symbol} is defined in {target.AssemblyName ?? "a referenced assembly"} (no source)";
                Output(sbMain.SimpleText);
                break;
            default:
                sbMain.SimpleText = $"{target.Symbol} is in generated code";
                break;
        }
    }

    // ---- Find all references ----

    /// <summary>Shift+F12: every reference to the symbol at the caret, in the Find Results window.</summary>
    public Task FindReferences()
    {
        if (ActiveLanguage is not { } lang || LanguageHostFor(lang.Path) is not { } host) return Task.CompletedTask;
        host.Flush();
        int caret = lang.Editor.CaretOffset;
        var path = lang.Path;
        sbMain.SimpleText = "Finding references…";
        return host.Request((s, t) => s.FindReferencesAsync(path, caret, t), result =>
        {
            _findRowActions = null;   // the list shows references now, not Find in Files rows
            _findResults = [.. result.Value.Where(r => !r.IsInGeneratedCode)];
            var dir = host.Project.Directory;
            lstFindResults.Items = [.. _findResults.Select(r =>
                $"{(r.File.StartsWith(dir, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(dir, r.File) : r.File)}({r.Line},{r.Column}):  {r.LineText}{(r.IsDefinition ? "   (definition)" : "")}")];
            lstFindResults.Invalidate();
            _dock.Show(FindToolWindow(ToolWindows.FindResults));
            sbMain.SimpleText = $"{_findResults.Count} reference(s)";
        });
    }

    /// <summary>Double-click in Find Results: shows that reference.</summary>
    public void OpenFindResult(int index)
    {
        if (_findRowActions != null) { if (index >= 0 && index < _findRowActions.Length) _findRowActions[index]?.Invoke(); return; }
        if (index >= 0 && index < _findResults.Count && _findResults[index] is var r) NavigateToCode(r.File, r.Line, r.Column);
    }

    // ---- Rename ----

    /// <summary>F2: asks for the new name, previews the changed lines, then renames across the project (open buffers included, one undo step each).</summary>
    public Task RenameSymbol()
    {
        if (ActiveLanguage is not { } lang || LanguageHostFor(lang.Path) is not { } host) return Task.CompletedTask;
        var editor = lang.Editor;
        var dialog = new RenameSymbolDialog(WordAt(editor), this);
        if (dialog.ShowModal() != ModalResult.Ok || dialog.NewName.Length == 0) return Task.CompletedTask;
        var newName = dialog.NewName;
        host.Flush();
        int caret = editor.CaretOffset;
        var path = lang.Path;
        return host.Request((s, t) => s.RenameAsync(path, caret, newName, t), result =>
        {
            if (!result.Success) { Dialogs.MessageDlg(result.Error ?? "This can't be renamed.", MessageType.Warning, owner: this); return; }
            if (result.Documents.Count == 0) { sbMain.SimpleText = "Nothing to rename"; return; }
            var preview = new RenamePreviewDialog($"Rename to {newName}", result.Preview, host.Project.Directory, this);
            if (preview.ShowModal() != ModalResult.Ok) return;
            ApplyDocumentEdits(host, result.Version, result.Documents, saveClean: false);
            sbMain.SimpleText = $"Renamed to {newName} in {result.Documents.Count} file(s)";
        });
    }

    /// <summary>The identifier at (or just before) the caret.</summary>
    static string WordAt(CodeEditor editor)
    {
        var pos = editor.CaretPosition;
        var line = editor.Snapshot.GetLineText(pos.Line);
        int s = Math.Min(pos.Column, line.Length), e = s;
        while (s > 0 && (char.IsLetterOrDigit(line[s - 1]) || line[s - 1] == '_')) s--;
        while (e < line.Length && (char.IsLetterOrDigit(line[e]) || line[e] == '_')) e++;
        return line[s..e];
    }

    /// <summary>
    /// Applies a service's per-document edits (computed at workspace <paramref name="version"/>): an open buffer gets one
    /// undo step (saved too if it was clean and <paramref name="saveClean"/>), an open form reloads, any other file is
    /// written to disk.
    /// </summary>
    void ApplyDocumentEdits(LanguageHost host, long version, IReadOnlyList<DocumentEdits> documents, bool saveClean)
    {
        foreach (var d in documents)
        {
            switch (FindDocument(d.File))
            {
                case CodeDocument code:
                    bool clean = !code.IsDirty;
                    if (host.Find(code) is not { } lang || !lang.ApplyServiceEdits(version, d.Edits)) code.ApplyText(d.NewText);
                    if (clean && saveClean) code.Save();
                    break;
                case FormDocument form:
                    form.Document.Snapshot();
                    form.Document.Load(d.NewText);
                    form.Document.FileName = form.Path;
                    form.Document.NotifyChanged();
                    break;
                default:
                    try { File.WriteAllText(d.File, d.NewText); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Output($"Could not write {d.File}: {e.Message}"); continue; }
                    host.Workspace?.ReloadFromDisk(d.File);
                    break;
            }
        }
        host.Flush();
        UpdateTitle();
    }

    // ---- Format, code fixes ----

    /// <summary>Shift+Alt+F (or Format Selection): Roslyn's formatter over the document or the selection, as one undo step.</summary>
    public Task Format(bool selection)
    {
        if (ActiveLanguage is not { } lang || LanguageHostFor(lang.Path) is not { } host) return Task.CompletedTask;
        host.Flush();
        var sel = lang.Editor.Selection;
        TextRange? range = selection && !sel.IsEmpty ? new TextRange(sel.Start, sel.Length) : null;
        var path = lang.Path;
        return host.Request((s, t) => s.FormatAsync(path, range, t), result =>
        {
            if (result.Value.Count == 0) { sbMain.SimpleText = "Already formatted"; return; }
            lang.ApplyServiceEdits(result.Version, result.Value);
        });
    }

    /// <summary>Ctrl+.: the code fixes for the diagnostics at the caret (or selection), in a popup menu there.</summary>
    public Task ShowCodeFixes()
    {
        if (ActiveLanguage is not { } lang || LanguageHostFor(lang.Path) is not { } host) return Task.CompletedTask;
        host.Flush();
        var editor = lang.Editor;
        var sel = editor.Selection;
        var snapshot = editor.Snapshot;
        TextRange range = sel.IsEmpty
            ? new TextRange(snapshot.GetLineStart(editor.CaretPosition.Line), snapshot.GetLineLength(editor.CaretPosition.Line))
            : new TextRange(sel.Start, sel.Length);
        var path = lang.Path;
        return host.Request((s, t) => s.GetCodeFixesAsync(path, range, t), result =>
        {
            if (result.Value.Count == 0) { sbMain.SimpleText = "No code fixes here"; return; }
            var menu = new PopupMenu();
            foreach (var fix in result.Value)
            {
                var f = fix;
                menu.Add(f.Title, _ => ApplyDocumentEdits(host, result.Version, f.Edits, saveClean: false));
            }
            var caret = editor.CaretRect;
            menu.Popup(this, editor.AbsoluteLeft + caret.X, editor.AbsoluteTop + caret.Y + caret.Height);
        });
    }

    // ---- The designer's control rename ----

    /// <summary>
    /// A control renamed in the inspector: its uses and its handlers follow semantically through
    /// <see cref="LanguageService.RenameControlAsync"/> (only real references change, not a same-named identifier
    /// elsewhere). The workspace must still see the old name, so form updates wait until the rename is applied. False
    /// when the workspace isn't ready; the caller then renames textually.
    /// </summary>
    bool TryRenameControlSemantically(FormDocument form, string oldName, string newName)
    {
        if (LanguageHostFor(form.Path) is not { IsReady: true } host) return false;
        var suspended = host.SuspendFormUpdates();
        host.Flush();   // the buffers; forms stay as they were
        bool applied = false;
        var formPath = form.Path;
        var request = host.Request((s, t) => s.RenameControlAsync(formPath, oldName, newName, cancel: t), result =>
        {
            applied = true;
            if (!result.Success)
            {
                suspended.Dispose();
                Output($"Semantic rename failed ({result.Error}); renaming textually.");
                RenameInCode(oldName, newName, textual: true);
                return;
            }
            ApplyDocumentEdits(host, result.Version, result.Documents, saveClean: true);
            if (form.Document.Form.FindControl(newName) is { } target)
                foreach (var (ev, handler) in target.EventHandlers.ToList())
                    if (result.HandlerRenames.FirstOrDefault(h => h.OldName == handler) is { NewName: { } renamed }) target.EventHandlers[ev] = renamed;
            form.Document.NotifyChanged();
            suspended.Dispose();
            Save(form);
            sbMain.SimpleText = $"Renamed {oldName} to {newName} in code";
        });
        PendingControlRename = request;
        // A request that failed outright (no result at all) falls back too.
        _ = request.ContinueWith(_ => host.Ui(() =>
        {
            if (applied) return;
            suspended.Dispose();
            RenameInCode(oldName, newName, textual: true);
        }), TaskScheduler.Default);
        return true;
    }
}
