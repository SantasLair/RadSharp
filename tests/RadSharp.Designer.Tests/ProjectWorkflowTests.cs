using RadSharp.Design;
using RadSharp.Design.Code;
using RadSharp.Designer;

namespace RadSharp.Tests;

/// <summary>
/// The designer's Delphi-style project workflow, driven on the real designer form with a fake host:
/// welcome page → open/create a project → forms in tabs → handlers → Run (F9) → close project.
/// </summary>
[Collection("PlatformHost")]   // each test installs its own PlatformHost.Current (process-wide)
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

    /// <summary>Waits for a task the fake host completes on another thread (it runs what is posted at once).</summary>
    static bool Finishes(Task task, TimeSpan timeout) => task.Wait(timeout);

    static (DesignerForm Designer, FakeHost Host) Start(string? project = null)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var host = new FakeHost();
        PlatformHost.Current = host;
        var settings = Path.Combine(TempDir(), "designer.json");
        var designer = new DesignerForm(project, settings, new DesignerOptions { ProjectControls = false });
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
        var (d, host) = Start(project.ProjectFile);
        host.AnswerModal = form => { Assert.Equal("Form2", ((Edit)form.FindControl("edtFormName")!).Text); return "btnOk"; };
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
        File.WriteAllText(code, System.Text.RegularExpressions.Regex.Replace(source,
            @"(void Button1_Click\(Control sender\)\r?\n\s*\{\r?\n)", "$1        ((Button)sender).Caption = \"Clicked\";\n"));

        d.Simulate.Key(Key.F9);
        // F9 compiles in the background; this host runs what it posts at once, on the thread that posts it.
        Assert.True(Finishes(d.LastRun!, TimeSpan.FromMinutes(3)), "F9 didn't finish");
        var live = Assert.Single(host.Shown);
        var liveButton = (Button)live.FindControl("Button1")!;
        live.Simulate.Click(liveButton);
        Assert.Equal("Clicked", liveButton.Caption);
        Assert.Contains(d.OutputLines, l => l.StartsWith("Running MainForm"));
    }

    [Fact]
    public void Typing_a_handler_name_into_an_event_row_writes_one_quiet_stub()
    {
        var project = RadProject.Create(TempDir(), "TypedHandler");
        var (d, _) = Start(project.ProjectFile);
        var doc = d.Document!;
        var a = doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 10, 10);
        var b = doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 10, 50);
        var code = Path.Combine(project.Directory, "MainForm.cs");
        var before = File.ReadAllText(code);
        var grid = (PropertyGrid)d.FindControl("gridEvents")!;

        doc.Select(a);
        Assert.True(grid.Apply(grid.IndexOf("onClick"), "Shared_Click"));   // what Enter in the row's editor does
        var source = File.ReadAllText(code);
        Assert.Equal("Shared_Click", a.EventHandlers["onClick"]);
        Assert.Contains("void Shared_Click(Control sender)", source.Replace("\r\n", "\n"));

        // A second control pointing at the same method writes nothing more.
        doc.Select(b);
        Assert.True(grid.Apply(grid.IndexOf("onClick"), "Shared_Click"));   // what Enter in the row's editor does
        Assert.Equal(source, File.ReadAllText(code));

        // Clearing the row writes nothing and removes nothing.
        Assert.True(grid.Apply(grid.IndexOf("onClick"), ""));
        Assert.False(b.EventHandlers.ContainsKey("onClick"));
        Assert.Equal(source, File.ReadAllText(code));
        Assert.NotEqual(before, source);

        // Several controls at once: one stub.
        doc.SelectMany([a, b]);
        Assert.True(grid.Apply(grid.IndexOf("onClick"), "Both_Click"));
        var multi = File.ReadAllText(code);
        Assert.Equal(1, multi.Split("void Both_Click(").Length - 1);
        Assert.Equal("Both_Click", b.EventHandlers["onClick"]);
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
    public void The_name_box_refuses_a_name_that_hides_an_inherited_form_member()
    {
        var project = RadProject.Create(TempDir(), "HiddenName");
        var (d, _) = Start(project.ProjectFile);
        var doc = d.Document!;
        var button = doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 10, 10);
        doc.Select(button);

        var nameField = (Edit)d.FindControl("edtName")!;
        nameField.Focus();
        nameField.SelectAll();
        d.Simulate.Type("Monitor");
        d.Simulate.Key(Key.Enter);
        Assert.Equal("Button1", button.Name);
        Assert.Equal("Button1", nameField.Text);
        var status = (StatusBar)d.FindControl("sbMain")!;
        Assert.Contains("\"Monitor\" would hide the inherited member Form.Monitor", status.SimpleText);

        nameField.SelectAll();
        d.Simulate.Type("btnMonitor");
        d.Simulate.Key(Key.Enter);
        Assert.Equal("btnMonitor", button.Name);
    }

    [Fact]
    public void Palette_groups_components_into_categories_and_arms_placement_on_selection()
    {
        var project = RadProject.Create(TempDir(), "Palette");
        var (d, _) = Start(project.ProjectFile);
        var tv = (TreeView)d.FindControl("tvPalette")!;

        var standard = Assert.Single(tv.Nodes, n => n.Text == "Standard");
        var button = Assert.Single(standard.Nodes, n => n.Text == "Button");
        Assert.IsType<ComponentClass>(button.Tag);
        Assert.Equal("Button", ((ComponentClass)button.Tag!).Name);
        Assert.NotNull(button.Icon);   // a toolbox icon, not just a text row
        Assert.Contains(tv.Nodes, n => n.Text == "Dialogs");   // OpenDialog/SaveDialog/FolderDialog
        Assert.Contains(tv.Nodes, n => n.Text == "System");    // Timer/ImageList

        tv.Selected = button;
        Assert.Equal("Button", d.ActiveForm!.Surface.PendingClass?.Name);
    }

    [Fact]
    public void Dragging_a_palette_entry_onto_the_surface_creates_it_there()
    {
        var project = RadProject.Create(TempDir(), "DragPlace");
        var (d, _) = Start(project.ProjectFile);
        var doc = d.Document!;
        var tv = (TreeView)d.FindControl("tvPalette")!;
        var buttonNode = tv.Nodes.Single(n => n.Text == "Standard").Nodes.Single(n => n.Text == "Button");

        // Establish the source's capture with a real press, then pick the entry actually being dragged
        // (a raw pixel press would land on whatever row is there, likely a category header, not Button)
        // before starting the drag with the same general primitive DragMode.Automatic uses internally.
        d.Simulate.Press(tv.AbsoluteLeft + 5, tv.AbsoluteTop + 5);
        tv.Selected = buttonNode;
        d.BeginDrag(tv);

        var surface = d.ActiveForm!.Surface;
        var (sx, sy) = surface.DesignToScreen(64, 88);   // already grid-aligned: no snap rounding to reason about
        d.Simulate.MoveTo(surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy);
        Assert.Equal(Cursor.Default, d.CurrentCursor);   // the surface accepted the drop
        d.Simulate.Release(surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy);

        var created = Assert.IsType<Button>(doc.Primary);
        Assert.Equal((64, 88), (created.Left, created.Top));
    }

    [Fact]
    public void Dragging_a_palette_entry_that_does_not_land_on_the_surface_creates_nothing()
    {
        var project = RadProject.Create(TempDir(), "DragMiss");
        var (d, _) = Start(project.ProjectFile);
        var doc = d.Document!;
        var tv = (TreeView)d.FindControl("tvPalette")!;
        var buttonNode = tv.Nodes.Single(n => n.Text == "Standard").Nodes.Single(n => n.Text == "Button");

        d.Simulate.Press(tv.AbsoluteLeft + 5, tv.AbsoluteTop + 5);
        tv.Selected = buttonNode;
        d.BeginDrag(tv);
        d.Simulate.MoveTo(tv.AbsoluteLeft + 5, tv.AbsoluteTop + 60);   // still over the palette itself
        Assert.Equal(Cursor.NotAllowed, d.CurrentCursor);
        d.Simulate.Release(tv.AbsoluteLeft + 5, tv.AbsoluteTop + 60);

        Assert.Empty(doc.Form.Children);
    }

    [Fact]
    public void Palette_every_leaf_has_a_toolbox_icon()
    {
        var project = RadProject.Create(TempDir(), "PaletteIcons");
        var (d, _) = Start(project.ProjectFile);
        var tv = (TreeView)d.FindControl("tvPalette")!;

        var leaves = tv.Nodes.SelectMany(category => category.Nodes).ToList();
        Assert.True(leaves.Count > 10);   // sanity: the palette actually has entries
        Assert.All(leaves, leaf => Assert.NotNull(leaf.Icon));
        Assert.All(tv.Nodes, category => Assert.Null(category.Icon));   // category headers aren't components
    }

    [Fact]
    public void Palette_filter_narrows_the_tree_and_clears_any_armed_placement()
    {
        var project = RadProject.Create(TempDir(), "PaletteFilter");
        var (d, _) = Start(project.ProjectFile);
        var tv = (TreeView)d.FindControl("tvPalette")!;
        var button = tv.Nodes.Single(n => n.Text == "Standard").Nodes.Single(n => n.Text == "Button");
        tv.Selected = button;
        Assert.NotNull(d.ActiveForm!.Surface.PendingClass);

        var filter = (Edit)d.FindControl("edtPaletteFilter")!;
        filter.Focus();
        d.Simulate.Type("timer");

        var allLeaves = tv.Nodes.SelectMany(n => n.Nodes).Select(n => n.Text).ToList();
        Assert.Equal(["Timer"], allLeaves);
        Assert.Null(d.ActiveForm!.Surface.PendingClass);   // the armed Button no longer matches the filter
    }

    [Fact]
    public void The_inspectors_columns_row_opens_the_columns_editor_and_the_session_is_undoable()
    {
        var project = RadProject.Create(TempDir(), "Cols");
        var (d, host) = Start();
        Assert.True(d.OpenProject(project.ProjectFile));
        var doc = d.Document!;
        var grid = (StringGrid)doc.Create(ComponentRegistry.Find("StringGrid")!, doc.Form, 20, 20);
        doc.Select(grid);
        var props = (PropertyGrid)d.FindControl("gridProps")!;
        int row = props.IndexOf("columns");
        Assert.True(row >= 0);
        Assert.Equal("(0 columns)", props.RowValue(row));
        string before = doc.Save();

        string? caption = null;
        host.AnswerModal = form =>
        {
            caption = form.Caption;
            form.Simulate.Click(form.FindControl("btnAdd")!);
            ((Edit)form.FindControl("edtTitle")!).Text = "Cost";
            return "btnClose";
        };
        props.BeginEdit(row);

        Assert.Equal($"Columns: {grid.Name}", caption);
        Assert.Equal(6, grid.ColCount);
        Assert.Equal("Cost", grid.Columns[5].Title);
        Assert.Equal("(6 columns)", props.RowValue(row));
        Assert.True(doc.Modified);
        Assert.Contains("\"title\": \"Cost\"", doc.Save());

        Assert.True(doc.Undo());
        Assert.Equal(before, doc.Save());
        Assert.Empty(((StringGrid)doc.Form.FindControl(grid.Name)!).Columns);
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

    [Fact]
    public void The_inspectors_columns_and_nodes_rows_open_the_list_view_and_tree_editors()
    {
        var project = RadProject.Create(TempDir(), "Views");
        var (d, host) = Start();
        Assert.True(d.OpenProject(project.ProjectFile));
        var doc = d.Document!;
        var props = (PropertyGrid)d.FindControl("gridProps")!;

        var lv = (ListView)doc.Create(ComponentRegistry.Find("ListView")!, doc.Form, 20, 20);
        doc.Select(lv);
        int row = props.IndexOf("columns");
        Assert.Equal("(0 columns)", props.RowValue(row));
        host.AnswerModal = form =>
        {
            Assert.Equal($"Columns: {lv.Name}", form.Caption);
            form.Simulate.Click(form.FindControl("btnAdd")!);
            ((Edit)form.FindControl("edtTitle")!).Text = "Name";
            return "btnClose";
        };
        props.BeginEdit(row);
        Assert.Equal(["Name"], lv.Columns.Select(c => c.Title));
        Assert.Equal("(1 column)", props.RowValue(row));

        var tree = (TreeView)doc.Create(ComponentRegistry.Find("TreeView")!, doc.Form, 20, 220);
        doc.Select(tree);
        row = props.IndexOf("nodes");
        Assert.Equal("(0 nodes)", props.RowValue(row));
        host.AnswerModal = form =>
        {
            Assert.Equal($"Nodes: {tree.Name}", form.Caption);
            form.Simulate.Click(form.FindControl("btnAdd")!);
            form.Simulate.Click(form.FindControl("btnAddChild")!);
            ((Edit)form.FindControl("edtText")!).Text = "Leaf";
            return "btnClose";
        };
        props.BeginEdit(row);
        Assert.Equal("Leaf", tree.Nodes[0].Nodes[0].Text);
        Assert.Equal("(2 nodes)", props.RowValue(row));
        Assert.Contains("\"text\": \"Leaf\"", doc.Save());

        Assert.True(doc.Undo());                      // the nodes session
        Assert.Empty(((TreeView)doc.Form.FindControl(tree.Name)!).Nodes);
    }

    static string? AskName(Form dialog, string name)
    {
        ((Edit)dialog.FindControl("edtFormName")!).Text = name;
        return "btnOk";
    }

    static string ListText(DesignerForm d) => string.Join("|", ((ListBox)d.FindControl("lstForms")!).Items);

    [Fact]
    public void New_form_asks_for_the_class_name_and_creates_both_files()
    {
        var project = RadProject.Create(TempDir(), "Named");
        var (d, host) = Start(project.ProjectFile);
        host.AnswerModal = form => AskName(form, "SettingsForm");
        d.Simulate.Key("Mod+N");
        Assert.Equal("SettingsForm", d.ActiveForm!.ClassName);
        Assert.True(File.Exists(Path.Combine(project.Directory, "SettingsForm.radform.json")));
        Assert.Contains("partial class SettingsForm : Form", File.ReadAllText(Path.Combine(project.Directory, "SettingsForm.cs")));
        Assert.Contains("SettingsForm", ListText(d));
    }

    [Theory]
    [InlineData("class")]          // keyword
    [InlineData("2Fast")]          // not an identifier
    [InlineData("My Form")]
    [InlineData("MainForm")]       // already a form
    [InlineData("Taken")]          // a code file is in the way
    [InlineData("Helper")]         // a type already declared in the project
    [InlineData("")]
    public void New_form_rejects_invalid_names(string name)
    {
        var project = RadProject.Create(TempDir(), "Invalid");
        File.WriteAllText(Path.Combine(project.Directory, "Taken.cs"), "// x");
        File.WriteAllText(Path.Combine(project.Directory, "Other.cs"), "class Helper { }");
        var (d, host) = Start(project.ProjectFile);
        string? problem = null;
        host.AnswerModal = form =>
        {
            AskName(form, name);
            form.Simulate.Click((Button)form.FindControl("btnOk")!);   // refused: the dialog stays open
            problem = ((Label)form.FindControl("lblProblem")!).Caption;
            return "btnCancel";
        };
        var before = project.FormFiles.Count;
        Assert.Null(d.NewForm());
        Assert.False(string.IsNullOrEmpty(problem));
        Assert.Equal(before, project.FormFiles.Count);
        Assert.Single(d.OpenForms);
    }

    [Fact]
    public void Rename_form_updates_files_references_tab_and_project_list()
    {
        var project = RadProject.Create(TempDir(), "Ren");
        var (d, host) = Start(project.ProjectFile);
        var main = d.ActiveForm!;
        var doc = d.Document!;
        doc.Create(ComponentRegistry.Find("Label")!, doc.Form, 10, 10);   // an unsaved change must survive the rename
        File.WriteAllText(Path.Combine(project.Directory, "Other.cs"), "class Other { object Make() => new MainForm(); }");

        var menu = (MainMenu)d.FindControl("mnuMain")!;
        var item = menu.Items.Single(m => m.Caption == "Project").Items.Single(m => m.Caption == "Rename Form…");
        host.AnswerModal = form => AskName(form, "ShellForm");
        item.PerformClick();

        Assert.Equal("ShellForm", main.ClassName);
        Assert.Equal("ShellForm", main.Sheet.Caption);
        Assert.Equal(project.FormFiles.Single(), main.Path);
        Assert.False(File.Exists(Path.Combine(project.Directory, "MainForm.radform.json")));
        Assert.False(File.Exists(Path.Combine(project.Directory, "MainForm.cs")));
        Assert.Contains("\"class\": \"Label\"", File.ReadAllText(main.Path));
        var code = File.ReadAllText(Path.Combine(project.Directory, "ShellForm.cs"));
        Assert.Contains("partial class ShellForm : Form", code);
        Assert.Contains("public ShellForm()", code);
        Assert.Contains("new ShellForm()", File.ReadAllText(Path.Combine(project.Directory, "Other.cs")));
        Assert.Equal("ShellForm", project.MainFormClass);
        Assert.Contains("ShellForm", ListText(d));
        Assert.DoesNotContain("MainForm", ListText(d));
        Assert.Contains("ShellForm", d.Caption);
    }

    [Fact]
    public void Rename_form_fails_safely_when_a_target_file_exists()
    {
        var project = RadProject.Create(TempDir(), "Clash");
        var (d, _) = Start(project.ProjectFile);
        var codeBefore = File.ReadAllText(Path.Combine(project.Directory, "MainForm.cs"));
        File.WriteAllText(Path.Combine(project.Directory, "Taken.radform.json"), "{}");   // the code file is free, the form file is not
        Assert.False(d.RenameForm("Taken"));
        Assert.Equal("MainForm", d.ActiveForm!.ClassName);
        Assert.Equal(codeBefore, File.ReadAllText(Path.Combine(project.Directory, "MainForm.cs")));
        Assert.True(File.Exists(Path.Combine(project.Directory, "MainForm.radform.json")));
        Assert.False(File.Exists(Path.Combine(project.Directory, "Taken.cs")));

        // RadProject refuses too and leaves everything alone, even when called directly.
        Assert.Throws<IOException>(() => project.RenameForm(d.ActiveForm.Path, "Taken"));
        Assert.True(File.Exists(Path.Combine(project.Directory, "MainForm.cs")));
    }

    [Fact]
    public void New_application_writes_debug_files_for_VS_Code_and_Visual_Studio()
    {
        var project = RadProject.Create(TempDir(), "Dbg");
        string Read(string rel) => File.ReadAllText(Path.Combine(project.Directory, rel));

        var launch = System.Text.Json.JsonDocument.Parse(Read(".vscode/launch.json")).RootElement.GetProperty("configurations")[0];
        Assert.Equal("coreclr", launch.GetProperty("type").GetString());
        Assert.Equal("${workspaceFolder}/bin/Debug/net10.0/Dbg.dll", launch.GetProperty("program").GetString());
        Assert.Equal("build", launch.GetProperty("preLaunchTask").GetString());

        var tasks = System.Text.Json.JsonDocument.Parse(Read(".vscode/tasks.json")).RootElement.GetProperty("tasks");
        Assert.Equal("build", tasks[0].GetProperty("label").GetString());
        Assert.Equal("${workspaceFolder}/Dbg.csproj", tasks[0].GetProperty("args")[1].GetString());

        var profiles = System.Text.Json.JsonDocument.Parse(Read("Properties/launchSettings.json")).RootElement.GetProperty("profiles");
        Assert.Equal("Project", profiles.GetProperty("Dbg").GetProperty("commandName").GetString());
    }

    [Fact]
    public void Form_name_dialog_wraps_long_problems_instead_of_clipping_them()
    {
        var project = RadProject.Create(TempDir(), "Wrap");
        var dialog = new FormNameDialog(project, "New Form", "Form2", null, null);
        Assert.True(((Label)dialog.FindControl("lblProblem")!).WordWrap);
    }

    [Fact]
    public void Menu_editor_onClick_field_writes_the_handler_stub_and_the_app_compiles()
    {
        var project = RadProject.Create(TempDir(), "MenuApp");
        var (d, host) = Start(project.ProjectFile);
        var doc = d.Document!;
        doc.Create(ComponentRegistry.Find("MainMenu")!, doc.Form, 0, 0);
        host.AnswerModal = f =>
        {
            void Click(string b) => f.Simulate.Click((Button)f.FindControl(b)!);
            Click("btnAdd");
            ((Edit)f.FindControl("edtCaption")!).Text = "E&xit";
            ((Edit)f.FindControl("edtHandler")!).Text = "mniExit_Click";
            return "btnClose";
        };
        d.Simulate.Key("Mod+M");

        var source = File.ReadAllText(Path.Combine(project.Directory, "MainForm.cs"));
        Assert.Contains("void mniExit_Click(MenuItem sender)", source);
        Assert.Contains("\"onClick\": \"mniExit_Click\"", File.ReadAllText(Path.Combine(project.Directory, "MainForm.radform.json")));
        Assert.NotNull(d.Run());   // F9: no RAD1001
        Assert.DoesNotContain(d.OutputLines, l => l.Contains("RAD1001"));
    }

    [Fact]
    public void Menu_editor_double_click_proposes_a_default_handler_name_and_writes_the_stub()
    {
        var project = RadProject.Create(TempDir(), "MenuDbl");
        var (d, _) = Start(project.ProjectFile);
        var doc = d.Document!;
        var menu = (MainMenu)doc.Create(ComponentRegistry.Find("MainMenu")!, doc.Form, 0, 0);
        var editor = new MenuEditor(doc, menu) { HandlerRequested = d.EditMenuHandlers };
        var item = editor.AddItem("Open");
        item.Name = "mniOpen";
        editor.EditHandler();   // what a double-click on the item or the onClick field does
        Assert.Equal("mniOpen_Click", item.ClickHandler);
        Assert.Contains("void mniOpen_Click(MenuItem sender)", File.ReadAllText(Path.Combine(project.Directory, "MainForm.cs")));
    }
}
