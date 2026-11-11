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

    void NewApplication() => NewApplication(ProjectTemplates.Empty);

    void NewApplication(ProjectTemplate template)
    {
        var dialog = new NewApplicationDialog(_settings, this);
        if (dialog.ShowModal() != ModalResult.Ok) return;
        if (CreateProject(template, dialog.ProjectFolder, dialog.ProjectName, dialog.PackageSource) != null) _settings.ProjectsFolder = dialog.Location;
    }

    void OpenProjectDialog()
    {
        if (!dlgOpenProject.Execute()) return;
        OpenProject(dlgOpenProject.FileName);
    }

    /// <summary>Project › Add New Form: <c>FormN.radform.json</c> + <c>FormN.cs</c>, opened in a new tab.</summary>
    public FormDocument? NewForm()
    {
        if (_project == null) return null;
        int n = 2;
        while (File.Exists(Path.Combine(_project.Directory, $"Form{n}.radform.json"))) n++;
        var file = _project.AddForm($"Form{n}");
        var form = OpenFormFile(file);
        UpdateUi();
        return form;
    }

    // ---- Project menu ----

    void RemoveForm()
    {
        if (_project == null || _current is not { } form) return;
        var cls = form.ClassName;
        if (Dialogs.MessageDlg($"Remove {cls} from the project?\nThis deletes {cls}.radform.json and {cls}.cs.",
                MessageType.Warning, MessageButtons.YesNo, this) != ModalResult.Yes) return;
        if (FindDocument(form.CodeFile) is { } code) RemoveDocument(code);
        RemoveDocument(form);
        _project.RemoveForm(form.Path);
        if (_project.MainFormClass == cls) Output($"{cls} was the main form: choose another with Project › Set as Main Form.");
        UpdateUi();
    }

    void SetMainForm()
    {
        if (_project == null || _current == null) return;
        if (_project.SetMainForm(_current.ClassName)) Output($"{_current.ClassName} is now the main form (Program.cs).");
        else Output("Program.cs has no SdlHost.Run(new …()) line to change.");
        UpdateUi();
    }

    /// <summary>Project › Open in IDE: the whole project in the external editor (VS Code, Visual Studio, Rider).</summary>
    void OpenInIde()
    {
        if (_project == null) return;
        var (exe, name) = _external.Editor();
        if (!ExternalEditor.OpenProject(_settings.Editor, exe, _settings.CustomEditorCommand, _project))
        {
            Output($"Could not start {name}; choose another editor in Tools › Options.");
            NextStep("Open in IDE", $"Could not start {name}.", ("Choose another editor…", ShowOptions));
        }
        else sbMain.SimpleText = $"Opened {_project.Name} in {name}";
    }

    // ---- Handlers in the code-behind ----

    /// <summary>
    /// Double-click on a control or an event: gives the event a handler (the conventional name if it has
    /// none), makes sure the method exists in the code-behind, saves the form, and shows the handler through
    /// <see cref="CodeNavigator"/> (the built-in code editor by default; the external IDE if so configured).
    /// </summary>
    public void EditHandler(Control target, EventMeta ev)
    {
        if (_current is not { } form) return;
        var doc = form.Document;
        if (!target.EventHandlers.TryGetValue(ev.Name, out var handler))
        {
            handler = CodeIntegration.HandlerName(target, doc.Form, ev);
            doc.Snapshot();
            target.EventHandlers[ev.Name] = handler;
            doc.NotifyChanged();
        }
        var code = form.CodeFile;
        if (!File.Exists(code)) { sbMain.SimpleText = $"{Path.GetFileName(code)} is missing"; return; }
        var (source, line) = CodeIntegration.EnsureHandler(ReadSource(code), form.ClassName, handler, ev.Parameters);
        WriteSource(code, source);
        Save(form);
        NavigateToCode(code, line, IndentColumn(source, line));
    }

    /// <summary>
    /// After a rename: the field and the conventionally named handlers follow in the code, across every
    /// source file in the project (not only this form's own code-behind), so a reference from a partial
    /// class split across files, or a helper method reaching in, follows too. When the project's language
    /// workspace is ready the rename is semantic (<see cref="TryRenameControlSemantically"/>); otherwise, or with
    /// <paramref name="textual"/>, it is textual: it can't distinguish an unrelated identifier that happens to match.
    /// </summary>
    void RenameInCode(string oldName, string newName, bool textual = false)
    {
        if (_current is not { } form || _project is not { } project) return;
        if (!textual && TryRenameControlSemantically(form, oldName, newName)) return;   // semantic when the workspace is ready
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
            var source = ReadSource(file);
            var updated = source;
            foreach (var (o, n) in renames) updated = CodeIntegration.RenameIdentifier(updated, o, n);
            if (updated != source) WriteSource(file, updated);
        }
        form.Document.NotifyChanged();
        Save(form);
    }

    /// <summary>The 1-based column just after a line's indentation (where the caret goes in a handler body).</summary>
    static int IndentColumn(string source, int line)
    {
        var lines = source.Split('\n');
        if (line < 1 || line > lines.Length) return 1;
        var text = lines[line - 1].TrimEnd('\r');
        return text.Length - text.TrimStart(' ', '\t').Length + 1;
    }

    // ---- Run (F9) ----

    /// <summary>
    /// Run (F9), as in Delphi: compiles the application — with every open form's unsaved state — and
    /// starts it from its main form. Everything is saved first, as Build does. Returns the running form, or null.
    /// </summary>
    public Form? Run()
    {
        if (_project == null) return null;
        SaveAll();
        CloseLiveForm();
        ClearOutput();
        Output($"Compiling {_project.Name}…");
        var overrides = _forms.ToDictionary(f => Path.GetFullPath(f.Path), f => f.Document.Save());
        var session = LiveSession.Compile(_project, overrides, debugInformation: true);
        foreach (var m in session.Messages) Output(m.ToString(), m);
        _problemStore.Set(ProblemSources.LiveCompile, session.Messages);
        if (!session.Success)
        {
            Output($"{session.Messages.Count} error(s): see Problems (double-click one to go to it).");
            _dock.Show(FindToolWindow(ToolWindows.Problems));
            session.Dispose();
            return null;
        }
        var main = _project.MainFormClass ?? _current?.ClassName;
        if (main == null) { Output("The project has no main form: open a form and choose Project › Set as Main Form."); session.Dispose(); return null; }
        try
        {
            var form = session.CreateForm(main);
            _live = session;
            _liveForm = form;
            AttachLiveHooks();
            form.Closed += _ => CloseLiveForm();
            form.Show();
            Output($"Running {main} (close its window to return to the designer).");
            return form;
        }
        catch (Exception e)
        {
            session.Dispose();
            Output("The application failed to start: " + (e.InnerException ?? e).Message);
            foreach (var line in ExceptionReport.Describe(e.InnerException ?? e)) Output(line.Text, line.Location);
            return null;
        }
    }

    void CloseLiveForm()
    {
        var form = _liveForm;
        _liveForm = null;
        if (form != null && PlatformHost.Current != null) PlatformHost.Current.CloseForm(form);
        DetachLiveHooks();
        _live?.Dispose();
        _live = null;
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
            NavigateToCode(m.File, m.Line, Math.Max(1, m.Column));
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

    void ToggleDarkMode() => SetDarkTheme(_settings.Theme != "dark");
}
