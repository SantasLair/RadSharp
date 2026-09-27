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
    public void Lock_controls_menu_item_locks_every_open_surface_and_new_ones_too()
    {
        var project = RadProject.Create(TempDir(), "Lock");
        var (d, _) = Start(project.ProjectFile);
        var doc = d.Document!;
        var button = doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 24, 24);
        var original = button.Bounds;
        var surface = d.ActiveForm!.Surface;

        var menuItem = (MenuItem)((MainMenu)d.FindControl("mnuMain")!).Items
            .Single(m => m.Caption == "Edit").Items.Single(m => m.Caption == "Lock Controls");
        menuItem.PerformClick();
        Assert.True(surface.Locked);

        var (sx, sy) = surface.DesignToScreen(30, 30);
        d.Simulate.Press(surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy);
        d.Simulate.MoveTo(surface.AbsoluteLeft + sx + 20, surface.AbsoluteTop + sy + 20);
        d.Simulate.Release(surface.AbsoluteLeft + sx + 20, surface.AbsoluteTop + sy + 20);
        Assert.Equal(original, button.Bounds);   // dragging did nothing while locked

        d.Simulate.Key("Mod+N");
        Assert.True(d.ActiveForm!.Surface.Locked);   // a form opened after locking starts locked too
    }

    [Fact]
    public void Right_click_context_menu_deletes_the_selected_control()
    {
        var project = RadProject.Create(TempDir(), "Ctx");
        var (d, _) = Start(project.ProjectFile);
        var doc = d.Document!;
        var surface = d.ActiveForm!.Surface;
        var button = doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 24, 24);
        doc.Select(null);

        var (sx, sy) = surface.DesignToScreen(30, 30);   // inside the button
        d.Simulate.Click(surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy, MouseButton.Right);
        Assert.Same(button, doc.Primary);

        var popup = Assert.IsType<MenuPopup>(d.Popups[^1]);
        int i = popup.Items.ToList().FindIndex(m => m.Caption == "Delete");
        Assert.True(i >= 0);
        var r = popup.ItemRect(i);
        d.Simulate.Click(popup.Left + r.X + 10, popup.Top + r.Y + r.Height / 2);

        Assert.DoesNotContain(button, doc.Form.Children);
        Assert.Empty(d.Popups);
    }

    [Fact]
    public void Right_click_on_empty_canvas_disables_selection_only_commands()
    {
        var project = RadProject.Create(TempDir(), "CtxEmpty");
        var (d, _) = Start(project.ProjectFile);
        var doc = d.Document!;
        doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 24, 24);
        var surface = d.ActiveForm!.Surface;

        var (sx, sy) = surface.DesignToScreen(400, 250);   // empty canvas, well inside the form
        d.Simulate.Click(surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy, MouseButton.Right);
        Assert.Empty(doc.Selection);

        var popup = Assert.IsType<MenuPopup>(d.Popups[^1]);
        Assert.False(popup.Items.First(m => m.Caption == "Delete").Enabled);
        Assert.True(popup.Items.First(m => m.Caption == "Select All").Enabled);
    }

    [Fact]
    public void Multi_selecting_controls_lets_the_inspector_edit_all_of_them_at_once()
    {
        var project = RadProject.Create(TempDir(), "Multi");
        var (d, _) = Start(project.ProjectFile);
        var doc = d.Document!;
        var a = (Button)doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 10, 10);
        var b = (Button)doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 10, 60);
        doc.SelectMany([a, b]);

        var grid = (PropertyGrid)d.FindControl("gridProps")!;
        Assert.Equal(2, grid.Targets.Count);
        var nameField = (Edit)d.FindControl("edtName")!;
        Assert.False(nameField.Enabled);   // can't rename two controls to the same name

        int caption = grid.IndexOf("caption");
        grid.BeginEdit(caption);
        d.Simulate.Type("Both");
        d.Simulate.Key(Key.Enter);
        Assert.Equal("Both", a.Caption);
        Assert.Equal("Both", b.Caption);
    }

    [Fact]
    public void Properties_toolbar_toggles_categorized_view_and_filters_rows()
    {
        var project = RadProject.Create(TempDir(), "Inspector");
        var (d, _) = Start(project.ProjectFile);
        var doc = d.Document!;
        doc.Select(doc.Form);   // put a real control (the form) in the inspector

        var grid = (PropertyGrid)d.FindControl("gridProps")!;
        Assert.Equal(PropertyGridArrangement.Categorized, grid.Arrangement);   // the designer's own default
        int categorizedCount = grid.RowCount;

        var combo = (ComboBox)d.FindControl("cboPropsArrange")!;
        combo.ItemIndex = 0;   // "Alphabetical" — the setter itself raises Change, same as picking it from the drop-down
        Assert.Equal(PropertyGridArrangement.Alphabetical, grid.Arrangement);
        bool anyHeader = Enumerable.Range(0, grid.RowCount).Any(grid.IsHeader);
        Assert.False(anyHeader);

        var filter = (Edit)d.FindControl("edtPropsFilter")!;
        filter.Focus();
        d.Simulate.Type("caption");
        Assert.True(grid.RowCount < categorizedCount);
        Assert.True(grid.IndexOf("caption") >= 0);
    }

    [Fact]
    public void Project_tab_lists_every_source_file_not_only_forms()
    {
        var project = RadProject.Create(TempDir(), "Files");
        File.WriteAllText(Path.Combine(project.Directory, "Helper.cs"), "namespace Files;\npublic static class Helper { }\n");
        var (d, _) = Start(project.ProjectFile);   // Start() already points the editor at a harmless command
        var pgInspector = (PageControl)d.FindControl("pgInspector")!;
        var tsProject = (TabSheet)d.FindControl("tsProject")!;
        pgInspector.ActivePageIndex = pgInspector.Pages.ToList().IndexOf(tsProject);

        var lstForms = (ListBox)d.FindControl("lstForms")!;
        Assert.Contains(lstForms.Items, i => i.StartsWith("MainForm"));
        Assert.Contains("Helper.cs", lstForms.Items);
        Assert.Contains("Program.cs", lstForms.Items);

        int i = Array.IndexOf(lstForms.Items, "Helper.cs");
        var r = lstForms.ItemRect(i);
        d.Simulate.DoubleClick(lstForms.AbsoluteLeft + r.X + 10, lstForms.AbsoluteTop + r.Y + r.Height / 2);

        var status = (StatusBar)d.FindControl("sbMain")!;
        Assert.Equal("Opened Helper.cs:1 in Custom", status.SimpleText);
    }

    [Fact]
    public void Renaming_a_control_updates_every_project_source_file_not_only_its_own_code_behind()
    {
        var project = RadProject.Create(TempDir(), "RenameWide");
        var helperFile = Path.Combine(project.Directory, "Helper.cs");
        File.WriteAllText(helperFile, """
            namespace RenameWide;
            public static class Helper
            {
                public static void Touch(MainForm f) => f.Button1.Caption = "x";
            }

            """);
        var (d, _) = Start(project.ProjectFile);
        var doc = d.Document!;
        var button = doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 10, 10);
        Assert.Equal("Button1", button.Name);
        doc.Select(button);

        var nameField = (Edit)d.FindControl("edtName")!;
        nameField.Focus();
        nameField.SelectAll();
        d.Simulate.Type("btnGo");
        d.Simulate.Key(Key.Enter);
        Assert.Equal("btnGo", button.Name);

        Assert.Contains("f.btnGo.Caption", File.ReadAllText(helperFile));
        Assert.DoesNotContain("Button1", File.ReadAllText(helperFile));
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
