using RadSharp.Design;
using RadSharp.Design.Code;
using RadSharp.Drawing;
using RadSharp.Platform.Sdl;

namespace RadSharp.Designer;

/// <summary>The designer's project and code commands: new/open/close, the external IDE, Run (F9) and Build.</summary>
public partial class DesignerForm
{
    LiveSession? _live;
    Form? _liveForm;
    bool _building;
    readonly List<BuildMessage?> _outputMessages = [];

    // ---- File menu ----

    void mnuNewApplication_Click(object sender)
    {
        var dialog = new NewApplicationDialog(_settings, this);
        if (dialog.ShowModal() != ModalResult.Ok) return;
        if (_project != null && !CloseProject()) return;
        try
        {
            var project = RadProject.Create(dialog.ProjectFolder, dialog.ProjectName, packageSource: dialog.PackageSource);
            _settings.ProjectsFolder = dialog.Location;
            OpenProject(project.ProjectFile);
            Output($"Created {project.ProjectFile}");
        }
        catch (IOException e) { Dialogs.MessageDlg($"Could not create the project:\n{e.Message}", MessageType.Error, owner: this); }
    }

    void mnuOpenProject_Click(object sender)
    {
        if (!dlgOpenProject.Execute()) return;
        OpenProject(dlgOpenProject.FileName);
    }

    /// <summary>
    /// File › New Form: asks for the class name (default the next free <c>FormN</c>), then creates
    /// <c>Name.radform.json</c> + <c>Name.cs</c> and opens it in a new tab. With <paramref name="name"/> it skips the
    /// question (the name must still be valid, else null). Null too if cancelled.
    /// </summary>
    public OpenForm? NewForm(string? name = null)
    {
        if (_project == null) return null;
        if (name == null)
        {
            var dialog = new FormNameDialog(_project, "New Form", _project.NextFreeFormName(), null, this);
            if (dialog.ShowModal() != ModalResult.Ok) return null;
            name = dialog.FormName;
        }
        if (_project.ValidateFormName(name) is { } problem) { Dialogs.ShowMessage(problem, this); return null; }
        string file;
        try { file = _project.AddForm(name); }
        catch (IOException e) { Dialogs.MessageDlg($"Could not create the form:\n{e.Message}", MessageType.Error, owner: this); return null; }
        var form = OpenFormFile(file);
        UpdateUi();
        return form;
    }

    /// <summary>
    /// Project › Rename Form: renames the active form's class (files, code-behind, every reference in the project's
    /// sources, its tab and the project list). The form is saved first. Returns false if cancelled, invalid or
    /// failed (nothing is changed then).
    /// </summary>
    public bool RenameForm(string? newName = null)
    {
        if (_project == null || _active is not { } form) return false;
        var oldName = form.ClassName;
        if (newName == null)
        {
            var dialog = new FormNameDialog(_project, "Rename Form", oldName, oldName, this);
            if (dialog.ShowModal() != ModalResult.Ok) return false;
            newName = dialog.FormName;
        }
        if (newName == oldName) return false;
        if (_project.ValidateFormName(newName, form.Path) is { } problem) { Dialogs.ShowMessage(problem, this); return false; }
        // Other open forms' tabs only show a class name, but unsaved edits to this form must reach the renamed file.
        if (form.Document.Modified) Save(form);
        string newFile;
        try { newFile = _project.RenameForm(form.Path, newName); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dialogs.MessageDlg($"Could not rename {oldName}; nothing was changed:\n{e.Message}", MessageType.Error, owner: this);
            return false;
        }
        form.Path = newFile;
        form.Document.FileName = newFile;
        form.Sheet.Caption = form.ClassName + (form.Document.Modified ? " *" : "");
        Output($"Renamed {oldName} to {newName} (files and references in the project's source files).");
        UpdateUi();
        return true;
    }

    void mnuRenameForm_Click(object sender) => RenameForm();

    void mnuNewForm_Click(object sender) => NewForm();

    void mnuSave_Click(object sender)
    {
        if (_active != null) Save(_active);
    }

    void mnuSaveAll_Click(object sender) => SaveAll();

    void mnuCloseForm_Click(object sender)
    {
        if (_active != null) CloseForm(_active);
    }

    void mnuCloseProject_Click(object sender) => CloseProject();
    void mnuExit_Click(object sender) => Close();

    // ---- Project menu ----

    void mnuRemoveForm_Click(object sender)
    {
        if (_project == null || _active is not { } form) return;
        var cls = form.ClassName;
        if (Dialogs.MessageDlg($"Remove {cls} from the project?\nThis deletes {cls}.radform.json and {cls}.cs.",
                MessageType.Warning, MessageButtons.YesNo, this) != ModalResult.Yes) return;
        RemoveTab(form);
        _project.RemoveForm(form.Path);
        if (_project.MainFormClass == cls) Output($"{cls} was the main form: choose another with Project › Set as Main Form.");
        UpdateUi();
    }

    void mnuSetMainForm_Click(object sender)
    {
        if (_project == null || _active == null) return;
        if (_project.SetMainForm(_active.ClassName)) Output($"{_active.ClassName} is now the main form (Program.cs).");
        else Output("Program.cs has no SdlHost.Run(new …()) line to change.");
        UpdateUi();
    }

    void mnuOpenInIde_Click(object sender)
    {
        if (_project == null) return;
        var (exe, name) = EditorExecutable();
        if (!ExternalEditor.OpenProject(_settings.Editor, exe, _settings.CustomEditorCommand, _project))
            Output($"Could not start {name}; choose another editor in Tools › Options.");
        else sbMain.SimpleText = $"Opened {_project.Name} in {name}";
    }

    /// <summary>F12: Delphi's form/code toggle; here it opens the form's code-behind in the IDE.</summary>
    void mnuToggleCode_Click(object sender)
    {
        if (_active == null) return;
        var code = RadProject.CodeFileFor(_active.Path);
        if (File.Exists(code)) OpenInEditor(code, 1);
    }

    (string? Exe, string Name) EditorExecutable()
    {
        var found = ExternalEditor.Detect().FirstOrDefault(e => e.Kind == _settings.Editor);
        return (found?.Executable, found?.Name ?? _settings.Editor.ToString());
    }

    void OpenInEditor(string file, int line)
    {
        var (exe, name) = EditorExecutable();
        if (ExternalEditor.Open(_settings.Editor, exe, _settings.CustomEditorCommand, _project, file, line))
            sbMain.SimpleText = $"Opened {Path.GetFileName(file)}:{line} in {name}";
        else
            Output($"Could not start {name} for {Path.GetFileName(file)}:{line}; choose another editor in Tools › Options.");
    }

    void mnuOptions_Click(object sender)
    {
        var dialog = new OptionsDialog(_settings, this);
        if (dialog.ShowModal() != ModalResult.Ok) return;
        dialog.Apply();
        SaveSettings();
        UpdateUi();
    }

    // ---- Handlers in the code-behind ----

    /// <summary>
    /// Double-click on a control or an event: gives the event a handler (the conventional name if it has
    /// none), makes sure the method exists in the code-behind, saves the form, and opens the IDE there.
    /// </summary>
    public void EditHandler(Control target, EventMeta ev)
    {
        if (_active is not { } form) return;
        var doc = form.Document;
        if (!target.EventHandlers.TryGetValue(ev.Name, out var handler))
        {
            handler = CodeIntegration.HandlerName(target, doc.Form, ev);
            doc.Snapshot();
            target.EventHandlers[ev.Name] = handler;
            doc.NotifyChanged();
        }
        int line = EnsureStub(form, handler, ev.Parameters);
        if (line == 0) return;
        Save(form);
        OpenInEditor(RadProject.CodeFileFor(form.Path), line);
    }

    /// <summary>
    /// The menu editor's code side: makes sure the handler of the <paramref name="items"/> (a <see cref="MenuItem"/> click,
    /// <c>(MenuItem sender)</c>) exists in the code-behind, saves the form, and opens the IDE there when
    /// <paramref name="open"/>. Wired to <see cref="MenuEditor.HandlerRequested"/>.
    /// </summary>
    public void EditMenuHandlers(IReadOnlyList<MenuItem> items, bool open)
    {
        if (_active is not { } form) return;
        int line = 1;
        foreach (var item in items.Where(i => i.ClickHandler.Length > 0))
            line = EnsureStub(form, item.ClickHandler, "MenuItem sender");
        if (items.Count > 0 && line > 0) Save(form);
        if (open && line > 0) OpenInEditor(RadProject.CodeFileFor(form.Path), line);
    }

    /// <summary>Writes the stub if missing; returns the caret line, or 0 when the code file is missing.</summary>
    int EnsureStub(OpenForm form, string handler, string parameters)
    {
        var code = RadProject.CodeFileFor(form.Path);
        if (!File.Exists(code)) { sbMain.SimpleText = $"{Path.GetFileName(code)} is missing"; return 0; }
        var original = File.ReadAllText(code);
        var (source, line) = CodeIntegration.EnsureHandler(original, form.ClassName, handler, parameters);
        if (source != original) File.WriteAllText(code, source);
        return line;
    }

    /// <summary>
    /// After a rename: the field and the conventionally named handlers follow in the code, across every
    /// source file in the project (not only this form's own code-behind), so a reference from a partial
    /// class split across files, or a helper method reaching in, follows too. This is textual, not semantic
    /// (there is no Roslyn <c>Workspace</c> here): it can't distinguish an unrelated identifier that happens
    /// to match, the way a full solution-wide rename would.
    /// </summary>
    void RenameInCode(string oldName, string newName)
    {
        if (_active is not { } form || _project is not { } project) return;
        var target = form.Document.Form.FindControl(newName);
        var renames = new List<(string Old, string New)> { (oldName, newName) };
        if (target != null)
            foreach (var (ev, handler) in target.EventHandlers.ToList())
                if (handler.StartsWith(oldName + "_", StringComparison.Ordinal))
                {
                    var renamed = newName + handler[oldName.Length..];
                    target.EventHandlers[ev] = renamed;
                    renames.Add((handler, renamed));
                }
        foreach (var file in project.SourceFiles)
        {
            var source = File.ReadAllText(file);
            var updated = source;
            foreach (var (o, n) in renames) updated = CodeIntegration.RenameIdentifier(updated, o, n);
            if (updated != source) File.WriteAllText(file, updated);
        }
        form.Document.NotifyChanged();
        Save(form);
    }

    // ---- Run (F9) ----

    /// <summary>
    /// Run (F9), as in Delphi: compiles the application — with every open form's unsaved state — and
    /// starts it from its main form. Returns the running form, or null.
    /// </summary>
    public Form? Run()
    {
        if (_project == null) return null;
        CloseLiveForm();
        ClearOutput();
        Output($"Compiling {_project.Name}…");
        var overrides = _forms.ToDictionary(f => Path.GetFullPath(f.Path), f => f.Document.Save());
        var session = LiveSession.Compile(_project, overrides);
        foreach (var m in session.Messages) Output(m.ToString(), m);
        if (!session.Success)
        {
            Output($"{session.Messages.Count(m => m.IsError)} error(s).");
            session.Dispose();
            return null;
        }
        var main = _project.MainFormClass ?? _active?.ClassName;
        if (main == null) { Output("The project has no main form."); session.Dispose(); return null; }
        try
        {
            var form = session.CreateForm(main);
            _live = session;
            _liveForm = form;
            form.Closed += _ => CloseLiveForm();
            form.Show();
            Output($"Running {main} (close its window to return to the designer).");
            return form;
        }
        catch (Exception e)
        {
            session.Dispose();
            Output("The application failed to start: " + (e.InnerException ?? e).Message);
            return null;
        }
    }

    void mnuRun_Click(object sender) => Run();

    void CloseLiveForm()
    {
        var form = _liveForm;
        _liveForm = null;
        if (form != null && PlatformHost.Current != null) PlatformHost.Current.CloseForm(form);
        _live?.Dispose();
        _live = null;
    }

    // ---- Build ----

    async void mnuBuild_Click(object sender)
    {
        if (_project == null || _building) return;
        SaveAll();
        _building = true;
        ClearOutput();
        Output($"Building {_project.Name} (NativeAOT, {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier})…");
        try
        {
            var (exit, dir) = await ProjectBuilder.PublishAsync(_project, line => Application.Post(() => Output(line)));
            Application.Post(() =>
            {
                Output(exit == 0 ? $"Build succeeded: {dir}" : $"Build failed (exit code {exit}).");
                sbMain.SimpleText = exit == 0 ? "Build succeeded" : "Build failed";
            });
        }
        finally { _building = false; }
    }

    // ---- Output ----

    void ClearOutput()
    {
        _outputMessages.Clear();
        lstOutput.Items = [];
    }

    public IReadOnlyList<string> OutputLines => lstOutput.Items;

    void Output(string line, BuildMessage? message = null)
    {
        _outputMessages.Add(message);
        var items = lstOutput.Items.Append(line).ToArray();
        if (items.Length > 2000) { items = items[^2000..]; _outputMessages.RemoveRange(0, _outputMessages.Count - 2000); }
        lstOutput.Items = items;
        lstOutput.ItemIndex = items.Length - 1;
    }

    void lstOutput_DblClick(Control sender)
    {
        int i = lstOutput.ItemIndex;
        if (i >= 0 && i < _outputMessages.Count && _outputMessages[i] is { } m && m.File.Length > 0)
            OpenInEditor(m.File, m.Line);
    }

    // ---- SDK check ----

    /// <summary>Run and Build need a .NET 10 SDK; say so once, at startup, if there isn't one.</summary>
    async void CheckSdk()
    {
        string sdks;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet", "--list-sdks")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            sdks = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { sdks = ""; }
        bool ok = sdks.Split('\n').Any(l => l.StartsWith("10.", StringComparison.Ordinal));
        if (ok || PlatformHost.Current == null) return;
        Application.Post(() =>
        {
            Output("No .NET 10 SDK found: designing works, but Run (F9) and Build need it. Get it from https://dot.net.");
            sbMain.SimpleText = "Install the .NET 10 SDK to run and build apps";
        });
    }

    // ---- Theme ----

    void mnuToggleDarkMode_Click(object sender)
    {
        _settings.Theme = _settings.Theme == "dark" ? "light" : "dark";
        Theme.Current = _settings.Theme == "dark" ? Theme.Dark : Theme.Default;
        this.Theme = Theme.Current;
        SaveSettings();
        InvalidateAll();
    }
}
