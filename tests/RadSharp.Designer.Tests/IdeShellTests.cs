using RadSharp.Design.Code;
using RadSharp.Designer;

namespace RadSharp.Tests;

/// <summary>
/// The IDE shell (milestone I3, phase A): commands and the keymap, the document area (forms and code in one tab
/// strip), the dock host, session restore and dirty-state safety — driven on the real designer form.
/// </summary>
[Collection("Designer")]   // sets PlatformHost.Current, like ProjectWorkflowTests
public class IdeShellTests
{
    sealed class FakeHost : IPlatformServices
    {
        public readonly List<Form> Shown = [];
        public readonly List<string> Prompts = [];
        public Func<Form, string?>? AnswerModal;   // returns the name of the button to press

        public void ShowForm(Form form) { Shown.Add(form); form.PerformLayout(); }
        public void RunModal(Form form)
        {
            form.PerformLayout();
            Prompts.Add(form.Caption);
            if (AnswerModal?.Invoke(form) is { } button && form.FindControl(button) is Button b) form.Simulate.Click(b);
            if (form.IsModal) form.Close();
        }
        public void CloseForm(Form form) => Shown.Remove(form);
        public void ProcessEventsUntil(Func<bool> done) { }
        public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) => done(null);
        public void PostToUiThread(Action action) => action();
        public void ShowModal(Form form, Action onClosed) { RunModal(form); onClosed(); }
    }

    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-ide-").FullName;

    static FakeHost NewHost()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var host = new FakeHost();
        PlatformHost.Current = host;
        return host;
    }

    static DesignerForm Start(string? project, string settings)
    {
        var designer = new DesignerForm(project, settings);
        designer.Settings.Editor = EditorKind.Custom;
        designer.Settings.CustomEditorCommand = "dotnet --version";
        designer.SetBounds(0, 0, 1220, 780);
        designer.PerformLayout();
        return designer;
    }

    static (DesignerForm Designer, FakeHost Host, RadProject Project, string Settings) StartWithProject(string name)
    {
        var host = NewHost();
        var project = RadProject.Create(TempDir(), name);
        var settings = Path.Combine(TempDir(), "designer.json");
        return (Start(project.ProjectFile, settings), host, project, settings);
    }

    static IEnumerable<MenuItem> Leaves(IEnumerable<MenuItem> items)
    {
        foreach (var item in items)
        {
            if (item.IsSeparator) continue;
            if (item.HasSubmenu) { foreach (var leaf in Leaves(item.Items)) yield return leaf; }
            else yield return item;
        }
    }

    static (int X, int Y) Center(Control c, Rect r) => (c.AbsoluteLeft + r.X + r.Width / 2, c.AbsoluteTop + r.Y + r.Height / 2);

    // ---- Commands ----

    [Fact]
    public void Every_menu_item_and_tool_button_resolves_to_a_registered_command()
    {
        var (d, _, _, _) = StartWithProject("Commands");
        var leaves = Leaves(((MainMenu)d.FindControl("mnuMain")!).Items).ToList();
        Assert.True(leaves.Count > 50);
        Assert.All(leaves, item =>
        {
            var id = d.Commands.CommandOf(item);
            Assert.True(id != null, $"menu item \"{item.Caption}\" is not a command");
            Assert.NotNull(d.Commands.Find(id!));
        });
        var palette = (TreeView)d.FindControl("tvPalette")!;
        Assert.DoesNotContain(palette.Nodes.SelectMany(n => n.Nodes), n => n.Text is "DocumentArea" or "ShortcutBox");   // IDE controls aren't components
        foreach (var button in ((ToolBar)d.FindControl("tbMain")!).Children.OfType<ToolButton>())
            Assert.True(d.Commands.CommandOf(button) != null, $"tool button {button.Name} is not a command");

        // The context menus too: the design surface's, a document tab's and a tool window tab's.
        var surface = d.ActiveForm!.Surface;
        var (sx, sy) = surface.DesignToScreen(200, 150);
        d.Simulate.Click(surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy, MouseButton.Right);
        Assert.All(Leaves(Assert.IsType<MenuPopup>(d.Popups[^1]).Items), i => Assert.NotNull(d.Commands.CommandOf(i)));
        d.CloseAllPopups();

        var (tx, ty) = Center(d.Documents, d.Documents.TabRects()[0]);
        d.Simulate.Click(tx, ty, MouseButton.Right);
        Assert.All(Leaves(Assert.IsType<MenuPopup>(d.Popups[^1]).Items), i => Assert.NotNull(d.Commands.CommandOf(i)));
        d.CloseAllPopups();

        var bottom = d.Dock.Region(DockSide.Bottom);
        var (wx, wy) = Center(bottom, bottom.TabRects()[0]);
        d.Simulate.Click(wx, wy, MouseButton.Right);
        var windowMenu = Assert.IsType<MenuPopup>(d.Popups[^1]);
        Assert.Contains(windowMenu.Items, i => i.Caption == "Dock Left");
        Assert.All(Leaves(windowMenu.Items), i => Assert.NotNull(d.Commands.CommandOf(i)));
    }

    [Fact]
    public void Rebinding_a_shortcut_in_the_dialog_works_and_persists()
    {
        var (d, host, project, settings) = StartWithProject("Keys");
        var doc = d.Document!;
        doc.Create(ComponentRegistry.Find("Label")!, doc.Form, 10, 10);
        Assert.True(d.ActiveForm!.IsDirty);

        host.AnswerModal = form =>
        {
            var dialog = Assert.IsType<KeyboardShortcutsDialog>(form);
            dialog.SelectCommand("File.SaveAll");
            var box = (ShortcutBox)form.FindControl("edtShortcut")!;
            form.Simulate.Click(box);
            form.Simulate.Key("Ctrl+Shift+K");
            Assert.Equal("Mod+Shift+K", box.Shortcut);
            form.Simulate.Click(form.FindControl("btnAssign")!);
            return "btnOk";
        };
        Assert.True(d.Commands.Execute("Tools.KeyboardShortcuts"));
        Assert.Equal("Mod+Shift+K", d.Commands["File.SaveAll"].Shortcut);

        d.Simulate.Key("Ctrl+Shift+S");               // the old binding does nothing now
        Assert.True(d.ActiveForm!.IsDirty);
        d.Simulate.Key("Ctrl+Shift+K");               // the new one saves everything
        Assert.False(d.ActiveForm!.IsDirty);

        var item = Leaves(((MainMenu)d.FindControl("mnuMain")!).Items).First(i => d.Commands.CommandOf(i) == "File.SaveAll");
        Assert.Equal("Mod+Shift+K", item.Shortcut);   // the menu shows the new keys

        // "Restart": the keymap is read back from the settings folder.
        Assert.True(d.Close());
        var again = Start(project.ProjectFile, settings);
        Assert.Equal("Mod+Shift+K", again.Commands["File.SaveAll"].Shortcut);
        Assert.Equal("Mod+S", again.Commands["File.Save"].Shortcut);
        Assert.True(File.Exists(again.Paths.Keymap));
    }

    [Fact]
    public void Rebinding_to_a_taken_shortcut_moves_it_and_reset_restores_the_default()
    {
        var registry = new CommandRegistry();
        int a = 0, b = 0;
        registry.Register(new Command("A", "A", "Test", () => a++, "Mod+S"));
        registry.Register(new Command("B", "B", "Test", () => b++, "Mod+B"));
        Shortcut.CommandModifier = KeyModifiers.Ctrl;

        var losers = registry.Rebind("B", "Ctrl+S");
        Assert.Equal("A", Assert.Single(losers).Id);
        Assert.Equal("", registry["A"].Shortcut);
        Assert.True(registry.ProcessShortcut(new KeyEventArgs(Key.S, KeyModifiers.Ctrl)));
        Assert.Equal((0, 1), (a, b));

        var path = Path.Combine(TempDir(), "keymap.json");
        registry.SaveKeymap(path);
        var loaded = new CommandRegistry();
        loaded.LoadKeymap(path);
        loaded.Register(new Command("A", "A", "Test", () => { }, "Mod+S"));
        loaded.Register(new Command("B", "B", "Test", () => { }, "Mod+B"));
        Assert.Equal(("", "Mod+S"), (loaded["A"].Shortcut, loaded["B"].Shortcut));

        loaded.ResetAllShortcuts();
        Assert.Equal(("Mod+S", "Mod+B"), (loaded["A"].Shortcut, loaded["B"].Shortcut));
        Assert.Empty(loaded.Overrides);
    }

    // ---- Documents ----

    [Fact]
    public void Double_clicking_a_control_opens_the_embedded_code_document_at_the_handler_line()
    {
        var (d, _, project, _) = StartWithProject("Handlers");
        var doc = d.Document!;
        var button = doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 24, 24);
        doc.Select(null);
        var surface = d.ActiveForm!.Surface;
        var (sx, sy) = surface.DesignToScreen(button.Left + 10, button.Top + 10);
        d.Simulate.DoubleClick(surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy);

        var code = Assert.IsType<CodeDocument>(d.ActiveDocument);
        Assert.Equal(Path.Combine(project.Directory, "MainForm.cs"), code.Path);
        Assert.False(code.IsDirty);                                  // written and saved
        Assert.Contains("void Button1_Click(Control sender)", File.ReadAllText(code.Path));
        var lines = code.Text.Split('\n');
        int signature = Array.FindIndex(lines, l => l.Contains("void Button1_Click")) + 1;
        Assert.Equal(signature + 2, code.Editor.Caret.Line);         // inside the body, below the brace
        Assert.Equal(lines[signature + 1].Length + 1, code.Editor.Caret.Column);
        Assert.Equal(["MainForm", "MainForm.cs"], d.Documents.Documents.Select(x => x.Title));
    }

    [Fact]
    public void F12_toggles_a_form_and_its_code_in_place_and_split_view_shows_both()
    {
        var (d, _, _, _) = StartWithProject("Toggle");
        var form = d.ActiveForm!;
        d.Simulate.Key(Key.F12);
        var code = Assert.IsType<CodeDocument>(d.ActiveDocument);
        Assert.Equal(form.Path, code.FormFile);
        Assert.False(form.Surface.IsEffectivelyVisible);              // in place: the code replaced the designer
        d.Simulate.Key(Key.F12);
        Assert.Same(form, d.ActiveDocument);
        Assert.False(code.View.IsEffectivelyVisible);

        d.Commands.Execute("View.SplitFormCode");
        Assert.True(d.SplitView);
        d.PerformLayout();
        Assert.True(form.Surface.IsEffectivelyVisible);
        Assert.True(code.View.IsEffectivelyVisible);
        Assert.True(form.Surface.AbsoluteLeft < code.View.AbsoluteLeft);   // designer on the left, code beside it
        d.Commands.Execute("View.SplitFormCode");
        Assert.False(d.SplitView);
    }

    [Fact]
    public void Code_documents_save_edits_keep_line_endings_and_undo_through_the_edit_menu()
    {
        var (d, _, project, _) = StartWithProject("CodeEdit");
        var helper = Path.Combine(project.Directory, "Helper.cs");
        File.WriteAllText(helper, "namespace CodeEdit;\r\npublic static class Helper { }\r\n");
        var code = d.OpenCodeFile(helper)!;
        Assert.Equal("Helper.cs", DocumentArea.TabCaption(code));
        code.GoTo(2, 1);
        d.Simulate.Type("// x\n");
        Assert.True(code.IsDirty);
        Assert.Equal("Helper.cs *", DocumentArea.TabCaption(code));
        d.Commands.Execute("Edit.Undo");
        Assert.False(code.IsDirty);
        d.Simulate.Type("// y");
        d.Simulate.Key("Ctrl+S");
        Assert.False(code.IsDirty);
        Assert.Equal("namespace CodeEdit;\r\n// ypublic static class Helper { }\r\n", File.ReadAllText(helper));
    }

    [Fact]
    public void Tabs_preview_close_with_middle_click_close_others_and_reorder_by_drag()
    {
        var (d, _, project, _) = StartWithProject("Tabs");
        File.WriteAllText(Path.Combine(project.Directory, "A.cs"), "class A { }\n");
        File.WriteAllText(Path.Combine(project.Directory, "B.cs"), "class B { }\n");
        Assert.True(d.OpenProject(project.ProjectFile));   // reopen, so the Project window lists the new files
        d.Dock.Show(DesignerForm.ToolWindows.Project);
        d.PerformLayout();
        var list = (ListBox)d.FindControl("lstForms")!;
        void ClickRow(string label, bool twice = false)
        {
            int i = Array.IndexOf(list.Items, label);
            var (x, y) = Center(list, list.ItemRect(i));
            if (twice) d.Simulate.DoubleClick(x, y); else d.Simulate.Click(x, y);
        }

        ClickRow("A.cs");                                     // a single click previews
        Assert.True(d.Documents.IsPreview(d.ActiveDocument!));
        ClickRow("B.cs");                                     // the next preview replaces it
        Assert.Equal(["MainForm", "B.cs"], d.Documents.Documents.Select(x => x.Title));
        ClickRow("A.cs", twice: true);                        // a double-click opens it for good
        Assert.Equal(["MainForm", "A.cs"], d.Documents.Documents.Select(x => x.Title));
        Assert.Null(d.Documents.Preview);
        ClickRow("B.cs", twice: true);
        Assert.Equal(["MainForm", "A.cs", "B.cs"], d.Documents.Documents.Select(x => x.Title));

        // Drag B's tab to the front.
        d.PerformLayout();
        var rects = d.Documents.TabRects();
        var (bx, by) = Center(d.Documents, rects[2]);
        d.Simulate.Drag(bx, by, d.Documents.AbsoluteLeft + 5, by, steps: 6);
        Assert.Equal(["B.cs", "MainForm", "A.cs"], d.Documents.Documents.Select(x => x.Title));

        // Middle-click closes a tab.
        rects = d.Documents.TabRects();
        var (ax, ay) = Center(d.Documents, rects[2]);
        d.Simulate.Click(ax, ay, MouseButton.Middle);
        Assert.Equal(["B.cs", "MainForm"], d.Documents.Documents.Select(x => x.Title));

        // Close Others from the tab's menu.
        d.Commands.Execute("File.CloseOthers", d.FindDocument(Path.Combine(project.Directory, "MainForm.radform.json")));
        Assert.Equal(["MainForm"], d.Documents.Documents.Select(x => x.Title));
    }

    [Fact]
    public void Closing_an_unsaved_code_document_asks_first()
    {
        var (d, host, project, _) = StartWithProject("Unsaved");
        var code = d.OpenCodeFile(Path.Combine(project.Directory, "MainForm.cs"))!;
        code.Text += "// edited\n";
        host.AnswerModal = _ => "btnCancel";
        Assert.False(d.CloseDocument(code));
        Assert.Contains(code, d.Documents.Documents);
        Assert.False(d.CloseProject());
        Assert.NotNull(d.Project);

        host.AnswerModal = _ => "btnYes";
        Assert.True(d.CloseProject());
        Assert.EndsWith("// edited\n", File.ReadAllText(code.Path).Replace("\r\n", "\n"));
    }

    [Fact]
    public void Files_changed_on_disk_reload_when_clean_and_ask_when_dirty()
    {
        var (d, host, project, _) = StartWithProject("Disk");
        var path = Path.Combine(project.Directory, "MainForm.cs");
        var code = d.OpenCodeFile(path)!;

        File.WriteAllText(path, "// outside 1\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        d.CheckDiskChanges();
        Assert.Equal("// outside 1\n", code.Text);           // clean: reloaded silently
        Assert.Empty(host.Prompts);

        code.Text = "// mine\n";
        File.WriteAllText(path, "// outside 2\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
        host.AnswerModal = _ => "btnNo";
        d.CheckDiskChanges();
        Assert.Single(host.Prompts);
        Assert.Equal("// mine\n", code.Text);                // kept
        d.CheckDiskChanges();
        Assert.Single(host.Prompts);                         // not asked again for the same change

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(3));
        host.AnswerModal = _ => "btnYes";
        d.CheckDiskChanges();
        Assert.Equal("// outside 2\n", code.Text);
        Assert.False(code.IsDirty);
    }

    [Fact]
    public void Run_saves_every_document_first()
    {
        var (d, _, project, _) = StartWithProject("RunSaves");
        var code = d.OpenCodeFile(Path.Combine(project.Directory, "MainForm.cs"))!;
        code.Text += "// before run\n";
        d.Run();
        Assert.False(code.IsDirty);
        Assert.Contains("// before run", File.ReadAllText(code.Path));
    }

    [Fact]
    public void The_external_editor_setting_routes_navigation_outside()
    {
        var (d, _, project, _) = StartWithProject("External");
        d.IdeSettings.CodeNavigation = CodeNavigation.External;
        Assert.IsType<ExternalEditorNavigator>(d.CodeNavigator);
        var doc = d.Document!;
        var button = doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 24, 24);
        d.EditHandler(button, CodeIntegration.DefaultEvent(button)!);
        Assert.Equal(["MainForm"], d.Documents.Documents.Select(x => x.Title));   // nothing opened inside
        Assert.StartsWith("Opened MainForm.cs:", ((StatusBar)d.FindControl("sbMain")!).SimpleText);
    }

    // ---- Dock host ----

    [Fact]
    public void Tool_windows_move_by_dragging_their_tab_collapse_and_dock_from_the_menu()
    {
        var (d, _, _, _) = StartWithProject("Dock");
        var output = d.FindToolWindow(DesignerForm.ToolWindows.Output);
        var bottom = d.Dock.Region(DockSide.Bottom);
        var left = d.Dock.Region(DockSide.Left);
        int i = bottom.Windows.ToList().IndexOf(output);
        var (x, y) = Center(bottom, bottom.TabRects()[i]);
        d.Simulate.Drag(x, y, left.AbsoluteLeft + left.Width / 2, left.AbsoluteTop + left.Height / 2, steps: 8);
        Assert.Same(left, output.Region);
        Assert.Same(output, left.ActiveWindow);
        Assert.NotNull(d.FindControl("lstOutput"));

        d.Dock.SetCollapsed(DockSide.Left, true);
        d.PerformLayout();
        Assert.True(left.Width < 40);
        Assert.False(d.FindControl("lstOutput")!.IsEffectivelyVisible);
        var (cx, cy) = Center(left, left.TabRects()[left.Windows.ToList().IndexOf(output)]);
        d.Simulate.Click(cx, cy);                      // a strip button expands the region again
        Assert.False(left.Collapsed);

        Assert.True(d.Commands.Execute("Window.DockRight", output));
        Assert.Equal(DockSide.Right, output.Region!.Side);
        Assert.True(d.Commands.Execute("Window.Hide", output));
        Assert.False(output.IsShown);
        Assert.NotNull(d.FindControl("lstOutput"));    // parked, not gone
        Assert.True(d.Commands.Execute("View.ToolWindow.output"));
        Assert.Equal(DockSide.Right, output.Region!.Side);   // comes back where it was
    }

    // ---- Session ----

    [Fact]
    public void A_restart_restores_the_project_documents_split_view_carets_and_layout()
    {
        var host = NewHost();
        var project = RadProject.Create(TempDir(), "Session");
        var settings = Path.Combine(TempDir(), "designer.json");

        // Open the project, then the form and its code, and split them.
        var d = Start(null, settings);
        Assert.True(d.WelcomeVisible);
        Assert.True(d.OpenProject(project.ProjectFile));
        var form = d.OpenFormFile(Path.Combine(project.Directory, "MainForm.radform.json"))!;
        d.Simulate.Key(Key.F12);
        var code = Assert.IsType<CodeDocument>(d.ActiveDocument);
        code.GoTo(3, 2);
        d.Commands.Execute("View.SplitFormCode");
        Assert.True(d.SplitView);

        // Move Output to the left by dragging its tab, and resize the right region.
        d.PerformLayout();
        var output = d.FindToolWindow(DesignerForm.ToolWindows.Output);
        var bottom = d.Dock.Region(DockSide.Bottom);
        var left = d.Dock.Region(DockSide.Left);
        var (x, y) = Center(bottom, bottom.TabRects()[bottom.Windows.ToList().IndexOf(output)]);
        d.Simulate.Drag(x, y, left.AbsoluteLeft + left.Width / 2, left.AbsoluteTop + 100, steps: 8);
        Assert.Equal(DockSide.Left, output.Region!.Side);
        d.Dock.SplitterOf(DockSide.Right).DragBy(-40);
        int rightWidth = d.Dock.Region(DockSide.Right).Width;
        d.Dock.SetCollapsed(DockSide.Bottom, true);

        // "Restart": close the IDE and start a new one with the same settings folder.
        Assert.True(d.Close());
        var again = Start(null, settings);
        Assert.Equal(project.ProjectFile, again.Project?.ProjectFile);
        Assert.Equal(["MainForm", "MainForm.cs"], again.Documents.Documents.Select(x => x.Title));
        var restoredCode = Assert.IsType<CodeDocument>(again.ActiveDocument);
        Assert.Equal((3, 2), restoredCode.Editor.Caret);
        Assert.True(again.SplitView);
        Assert.Same(again.OpenForms.Single(), again.ActiveForm);   // the inspector is on the form

        var restoredOutput = again.FindToolWindow(DesignerForm.ToolWindows.Output);
        Assert.Equal(DockSide.Left, restoredOutput.Region!.Side);
        Assert.Same(restoredOutput, again.Dock.Region(DockSide.Left).ActiveWindow);
        Assert.Equal(rightWidth, again.Dock.Region(DockSide.Right).Width);
        Assert.True(again.Dock.Region(DockSide.Bottom).Collapsed);

        // Closing the project is remembered too: the next start shows the welcome page.
        Assert.True(again.CloseProject());
        Assert.True(again.Close());
        Assert.True(Start(null, settings).WelcomeVisible);
        _ = host;
        _ = form;
    }
}
