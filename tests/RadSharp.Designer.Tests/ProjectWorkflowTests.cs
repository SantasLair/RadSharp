using RadSharp.Design.Code;
using RadSharp.Designer;

namespace RadSharp.Tests;

/// <summary>
/// The designer's Delphi-style project workflow, driven on the real designer form with a fake host:
/// welcome page → open/create a project → forms in tabs → handlers → Run (F9) → close project.
/// </summary>
public class ProjectWorkflowTests
{
    sealed class FakeHost : IPlatformServices
    {
        public readonly List<Form> Shown = [];
        public Func<Form, string?>? AnswerModal;   // returns the name of the button to press

        public void ShowForm(Form form) { Shown.Add(form); form.PerformLayout(); }
        public void RunModal(Form form)
        {
            form.PerformLayout();
            if (AnswerModal?.Invoke(form) is { } button && form.FindControl(button) is Button b) form.Simulate.Click(b);
            if (form.IsModal) form.Close();
        }
        public void CloseForm(Form form) => Shown.Remove(form);
        public void ProcessEventsUntil(Func<bool> done) { }
        public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) => done(null);
        public void PostToUiThread(Action action) => action();
        public void ShowModal(Form form, Action onClosed) { RunModal(form); onClosed(); }
    }

    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-designer-").FullName;

    static (DesignerForm Designer, FakeHost Host) Start(string? project = null)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var host = new FakeHost();
        PlatformHost.Current = host;
        var settings = Path.Combine(TempDir(), "designer.json");
        var designer = new DesignerForm(project, settings);
        // A harmless "editor" so tests don't open real windows.
        designer.Settings.Editor = EditorKind.Custom;
        designer.Settings.CustomEditorCommand = "dotnet --version";
        designer.SetBounds(0, 0, 1220, 780);
        designer.PerformLayout();
        return (designer, host);
    }

    [Fact]
    public void Without_a_project_the_welcome_page_shows_and_nothing_can_run()
    {
        var (d, _) = Start();
        Assert.True(d.WelcomeVisible);
        Assert.Null(d.Project);
        Assert.Null(d.Run());
        Assert.Empty(d.OpenForms);
    }

    [Fact]
    public void Opening_a_project_opens_its_main_form_and_remembers_it()
    {
        var project = RadProject.Create(TempDir(), "Demo");
        Assert.True(File.Exists(Path.Combine(project.Directory, "Demo.sln")));
        var (d, _) = Start();
        Assert.True(d.OpenProject(project.ProjectFile));
        Assert.False(d.WelcomeVisible);
        Assert.Equal("MainForm", Assert.Single(d.OpenForms).ClassName);
        Assert.Equal(project.ProjectFile, d.Settings.RecentProjects[0]);

        Assert.True(d.CloseProject());
        Assert.True(d.WelcomeVisible);
        Assert.Empty(d.OpenForms);
    }

    [Fact]
    public void New_form_opens_in_a_tab_and_can_become_the_main_form()
    {
        var project = RadProject.Create(TempDir(), "Tabs");
        var (d, _) = Start(project.ProjectFile);
        d.Simulate.Key("Mod+N");
        Assert.Equal(["MainForm", "Form2"], d.OpenForms.Select(f => f.ClassName));
        Assert.Equal("Form2", d.ActiveForm!.ClassName);
        Assert.True(File.Exists(Path.Combine(project.Directory, "Form2.cs")));

        Assert.True(project.SetMainForm("Form2"));
        Assert.Equal("Form2", project.MainFormClass);
    }

    [Fact]
    public void Double_click_writes_the_handler_and_F9_runs_the_application()
    {
        var project = RadProject.Create(TempDir(), "LiveApp");
        var (d, host) = Start(project.ProjectFile);
        var doc = d.Document!;
        var button = doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 24, 24);
        d.EditHandler(button, CodeIntegration.DefaultEvent(button)!);   // what a double-click does

        var code = Path.Combine(project.Directory, "MainForm.cs");
        var source = File.ReadAllText(code);
        Assert.Contains("void Button1_Click(Control sender)", source);
        File.WriteAllText(code, source.Replace("    void Button1_Click(Control sender)\n    {\n        \n    }",
            "    void Button1_Click(Control sender)\n    {\n        ((Button)sender).Caption = \"Clicked\";\n    }"));

        d.Simulate.Key(Key.F9);
        var live = Assert.Single(host.Shown);
        var liveButton = (Button)live.FindControl("Button1")!;
        live.Simulate.Click(liveButton);
        Assert.Equal("Clicked", liveButton.Caption);
        Assert.Contains(d.OutputLines, l => l.StartsWith("Running MainForm"));
    }

    [Fact]
    public void Closing_a_project_with_changes_asks_first()
    {
        var project = RadProject.Create(TempDir(), "Ask");
        var (d, host) = Start(project.ProjectFile);
        var doc = d.Document!;
        doc.Create(ComponentRegistry.Find("Label")!, doc.Form, 10, 10);

        host.AnswerModal = _ => "btnCancel";
        Assert.False(d.CloseProject());          // Cancel keeps it open
        Assert.False(d.WelcomeVisible);

        host.AnswerModal = _ => "btnYes";
        Assert.True(d.CloseProject());           // Yes saves, then closes
        Assert.Contains("\"class\": \"Label\"", File.ReadAllText(Path.Combine(project.Directory, "MainForm.radform.json")));
    }

    [Fact]
    public void Editor_commands_follow_the_unity_model()
    {
        Assert.Equal("\"code\" \"{folder}\" -g \"{file}:{line}\"", ExternalEditor.CommandTemplate(EditorKind.VSCode, "code", null));
        Assert.Equal("\"rider64\" \"{solution}\" --line {line} \"{file}\"", ExternalEditor.CommandTemplate(EditorKind.Rider, "rider64", null));
        Assert.Equal("\"devenv\" /edit \"{file}\"", ExternalEditor.CommandTemplate(EditorKind.VisualStudio, "devenv", null));
        Assert.Equal("my {file}", ExternalEditor.CommandTemplate(EditorKind.Custom, null, "my {file}"));
    }

    [Fact]
    public void Settings_round_trip_and_keep_ten_recent_projects()
    {
        var path = Path.Combine(TempDir(), "s.json");
        var s = new DesignerSettings { Editor = EditorKind.Rider, CustomEditorCommand = "x {file}" };
        for (int i = 0; i < 12; i++) s.AddRecent(Path.Combine(TempDir(), $"P{i}.csproj"));
        s.Save(path);
        var loaded = DesignerSettings.Load(path);
        Assert.Equal(EditorKind.Rider, loaded.Editor);
        Assert.Equal("x {file}", loaded.CustomEditorCommand);
        Assert.Equal(DesignerSettings.MaxRecent, loaded.RecentProjects.Count);
        Assert.EndsWith("P11.csproj", loaded.RecentProjects[0]);
    }
}
