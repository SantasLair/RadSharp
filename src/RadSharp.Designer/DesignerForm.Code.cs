using RadSharp.Design;
using RadSharp.Design.Code;
using RadSharp.Platform.Sdl;

namespace RadSharp.Designer;

/// <summary>The designer's code side: projects, handlers in the code-behind, live run (F9) and Build.</summary>
public partial class DesignerForm
{
    RadProject? _project;
    LiveSession? _live;
    Form? _liveForm;
    bool _building;
    readonly List<BuildMessage?> _outputMessages = [];

    public RadProject? Project => _project;

    // ---- Projects ----

    public void OpenProject(string projectFile)
    {
        _project = new RadProject(projectFile);
        RefreshProject();
        var main = _project.FormFiles.FirstOrDefault(f => RadProject.FormClassName(f) == "MainForm") ?? _project.FormFiles.FirstOrDefault();
        if (main != null) OpenFile(main);
        Output($"Opened project {_project.ProjectFile}");
    }

    void RefreshProject()
    {
        lblProject.Caption = _project == null ? "No project open" : _project.Name;
        lstForms.Items = _project == null ? [] : [.. _project.FormFiles.Select(Path.GetFileName)!];
    }

    void mnuNewProject_Click(object sender)
    {
        if (!ConfirmDiscard()) return;
        var folder = new FolderDialog { Title = "Folder for the new project" };
        Add(folder);
        try
        {
            if (!folder.Execute()) return;
            var name = Path.GetFileName(folder.FileName.TrimEnd(Path.DirectorySeparatorChar, '/'));
            if (Directory.EnumerateFileSystemEntries(folder.FileName).Any())
            {
                Dialogs.ShowMessage("Choose an empty folder for the new project.", this);
                return;
            }
            var project = RadProject.Create(folder.FileName, name, packageSource: Environment.GetEnvironmentVariable("RADSHARP_PACKAGES"));
            OpenProject(project.ProjectFile);
        }
        finally { Remove(folder); }
    }

    void mnuOpenProject_Click(object sender)
    {
        if (!ConfirmDiscard()) return;
        var open = new OpenDialog { Title = "Open project", Filter = "C# projects|*.csproj" };
        Add(open);
        try { if (open.Execute()) OpenProject(open.FileName); }
        finally { Remove(open); }
    }

    void mnuNewForm_Click(object sender)
    {
        if (_project == null) { Dialogs.ShowMessage("Open or create a project first.", this); return; }
        if (!ConfirmDiscard()) return;
        int n = 2;
        while (File.Exists(Path.Combine(_project.Directory, $"Form{n}.radform.json"))) n++;
        var formFile = _project.AddForm($"Form{n}");
        RefreshProject();
        OpenFile(formFile);
    }

    void lstForms_DblClick(Control sender)
    {
        if (_project == null || lstForms.ItemIndex < 0 || !ConfirmDiscard()) return;
        OpenFile(_project.FormFiles[lstForms.ItemIndex]);
    }

    // ---- Handlers in the code-behind ----

    string? CodeFile => _doc.FileName is { } f && File.Exists(RadProject.CodeFileFor(f)) ? RadProject.CodeFileFor(f) : null;
    string? FormClass => _doc.FileName is { } f ? RadProject.FormClassName(f) : null;

    /// <summary>
    /// Double-click on a control or an event: gives the event a handler (the conventional name if it has
    /// none), makes sure the method exists in the code-behind, saves, and opens the editor there.
    /// </summary>
    public void EditHandler(Control target, EventMeta ev)
    {
        if (!target.EventHandlers.TryGetValue(ev.Name, out var handler))
        {
            handler = CodeIntegration.HandlerName(target, _doc.Form, ev);
            _doc.Snapshot();
            target.EventHandlers[ev.Name] = handler;
            _doc.NotifyChanged();
        }
        if (CodeFile is not { } code || FormClass is not { } cls)
        {
            sbMain.SimpleText = $"Handler name set to {handler}; save the form in a project to write its code.";
            return;
        }
        var (source, line) = CodeIntegration.EnsureHandler(File.ReadAllText(code), cls, handler, ev.Parameters);
        File.WriteAllText(code, source);
        SaveTo(_doc.FileName);
        if (!ExternalEditor.Open(code, line)) sbMain.SimpleText = $"Wrote {handler} in {Path.GetFileName(code)} (line {line})";
        else sbMain.SimpleText = $"Opened {Path.GetFileName(code)}:{line}";
    }

    void OnSurfaceDoubleClicked(Control control)
    {
        if (CodeIntegration.DefaultEvent(control) is { } ev) EditHandler(control, ev);
    }

    /// <summary>After a rename: the field and the conventionally named handlers follow in the code.</summary>
    void RenameInCode(string oldName, string newName)
    {
        var target = _doc.Form.FindControl(newName);
        var renames = new List<(string Old, string New)> { (oldName, newName) };
        if (target != null)
            foreach (var (ev, handler) in target.EventHandlers.ToList())
                if (handler.StartsWith(oldName + "_", StringComparison.Ordinal))
                {
                    var renamed = newName + handler[oldName.Length..];
                    target.EventHandlers[ev] = renamed;
                    renames.Add((handler, renamed));
                }
        if (CodeFile is { } code)
        {
            var source = File.ReadAllText(code);
            foreach (var (o, n) in renames) source = CodeIntegration.RenameIdentifier(source, o, n);
            File.WriteAllText(code, source);
        }
        _doc.NotifyChanged();
        if (_doc.FileName != null) SaveTo(_doc.FileName);
    }

    // ---- Run (F9) ----

    void mnuRun_Click(object sender)
    {
        if (_project == null || FormClass is not { } cls) { Dialogs.ShowMessage("Open a project form to run it.", this); return; }
        CloseLiveForm();
        ClearOutput();
        Output($"Compiling {_project.Name}…");
        var overrides = new Dictionary<string, string> { [Path.GetFullPath(_doc.FileName!)] = _doc.Save() };
        var session = LiveSession.Compile(_project, overrides);
        foreach (var m in session.Messages) Output(m.ToString(), m);
        if (!session.Success)
        {
            Output($"{session.Messages.Count} error(s).");
            session.Dispose();
            return;
        }
        try
        {
            var form = session.CreateForm(cls);
            _live = session;
            _liveForm = form;
            form.Closed += _ => CloseLiveForm();
            form.Show();
            Output($"Running {cls} (close its window to return).");
        }
        catch (Exception e)
        {
            session.Dispose();
            Output("The form failed to start: " + (e.InnerException ?? e).Message);
        }
    }

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
        if (_project == null) { Dialogs.ShowMessage("Open a project to build it.", this); return; }
        if (_building) return;
        if (_doc.Modified && _doc.FileName != null) SaveTo(_doc.FileName);
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
            ExternalEditor.Open(m.File, m.Line);
    }
}
