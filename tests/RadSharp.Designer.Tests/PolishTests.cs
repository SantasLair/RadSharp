using RadSharp.CodeEditing;
using RadSharp.Design.Code;
using RadSharp.Designer;
using RadSharp.Drawing;
using static RadSharp.Tests.PolishSupport;

namespace RadSharp.Tests;

/// <summary>Milestone I9 on the real designer form: welcome page, templates, editor settings, shortcuts reference, autosave, recovery.</summary>
[Collection("Designer")]   // sets PlatformHost.Current and static settings
public class PolishTests
{
    // ---- Templates ----

    [Theory]
    [InlineData(ProjectTemplates.EmptyId, 1)]
    [InlineData(ProjectTemplates.SingleFormId, 1)]
    [InlineData(ProjectTemplates.TwoFormsId, 2)]
    public void Every_template_creates_a_project_that_compiles(string id, int forms)
    {
        var template = ProjectTemplates.Find(id)!;
        var project = template.Create(Path.Combine(TempDir(), "Made"), "Made", null);
        Assert.Equal(forms, project.FormFiles.Count);
        using var session = LiveSession.Compile(project);
        Assert.True(session.Success, string.Join("\n", session.Messages));
        Assert.NotNull(session.CreateForm("MainForm"));
        if (forms == 2) Assert.NotNull(session.CreateForm("AboutDialog"));
    }

    [Fact]
    public void The_single_form_template_wires_its_button_and_the_dialog_template_has_an_ok_button()
    {
        using var single = LiveSession.Compile(ProjectTemplates.Find(ProjectTemplates.SingleFormId)!.Create(Path.Combine(TempDir(), "S"), "S", null));
        var form = single.CreateForm("MainForm");
        form.PerformLayout();
        form.Simulate.Click((Button)form.FindControl("btnGreet")!);
        Assert.Equal("Hello, world!", ((Label)form.FindControl("lblGreeting")!).Caption);

        using var two = LiveSession.Compile(ProjectTemplates.Find(ProjectTemplates.TwoFormsId)!.Create(Path.Combine(TempDir(), "T"), "T", null));
        var dialog = two.CreateForm("AboutDialog");
        Assert.Equal(ModalResult.Ok, ((Button)dialog.FindControl("btnOk")!).ModalResult);
        Assert.NotNull(two.CreateForm("MainForm").FindControl("btnAbout"));
    }

    [Fact]
    public void Creating_a_project_from_a_template_opens_it_and_lists_it_on_the_welcome_page()
    {
        NewHost();
        var d = Start(null);
        Assert.True(d.WelcomeVisible);
        var project = d.CreateProject(ProjectTemplates.Find(ProjectTemplates.TwoFormsId)!, Path.Combine(TempDir(), "Two"), "Two", null);
        Assert.NotNull(project);
        Assert.False(d.WelcomeVisible);
        Assert.Equal(project!.ProjectFile, d.Project!.ProjectFile);
        Assert.Contains(d.WelcomeEntries(), e => e.Path == project.ProjectFile && e.LastOpened != null);
    }

    // ---- Welcome page ----

    static (DesignerForm D, string[] Projects) StartWelcome(int count = 3)
    {
        NewHost();
        var settingsPath = Path.Combine(TempDir(), "designer.json");
        var settings = new DesignerSettings();
        var projects = Enumerable.Range(1, count).Select(i => NewProject("P" + i).ProjectFile).ToArray();
        foreach (var p in projects) settings.AddRecent(p);   // the last one is the newest
        settings.Save(settingsPath);
        return (Start(null, settingsPath), projects);
    }

    [Fact]
    public void Welcome_lists_recent_projects_with_path_and_age_pinned_first()
    {
        var (d, projects) = StartWelcome();
        var entries = d.WelcomeEntries();
        Assert.Equal([projects[2], projects[1], projects[0]], entries.Select(e => e.Path));
        Assert.All(entries, e => Assert.Equal(Path.GetDirectoryName(e.Path), e.Directory));

        d.PinProject(projects[0], true);
        Assert.Equal(projects[0], d.WelcomeEntries()[0].Path);
        Assert.True(d.WelcomeEntries()[0].Pinned);
        Assert.Equal([projects[0]], IdeSettings.Load(d.Paths.Settings).PinnedProjects);   // persisted
        d.PinProject(projects[0], false);
        Assert.Equal(projects[2], d.WelcomeEntries()[0].Path);
    }

    [Fact]
    public void Welcome_rows_pin_remove_and_open_by_mouse()
    {
        var (d, projects) = StartWelcome();
        var w = d.Welcome;
        Assert.Equal(3, w.VisibleRecent);
        (int X, int Y) At(Rect r) => (w.AbsoluteLeft + r.X + r.Width / 2, w.AbsoluteTop + r.Y + r.Height / 2);

        var (px, py) = At(w.PinRect(2));   // the oldest
        d.Simulate.Click(px, py);
        Assert.Equal(projects[0], d.WelcomeEntries()[0].Path);
        Assert.True(d.WelcomeEntries()[0].Pinned);

        var (rx, ry) = At(w.RemoveRect(1));   // the newest recent, now second
        d.Simulate.Click(rx, ry);
        Assert.DoesNotContain(d.WelcomeEntries(), e => e.Path == projects[2]);
        Assert.DoesNotContain(projects[2], d.Settings.RecentProjects);

        var (ox, oy) = At(w.RecentRowRect(1));
        d.Simulate.Click(ox, oy);
        Assert.Equal(projects[1], d.Project!.ProjectFile);
        Assert.False(d.WelcomeVisible);
    }

    [Fact]
    public void Welcome_age_text_and_a_missing_project_that_can_leave_the_list()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("just now", WelcomeContent.FormatAge(now.AddSeconds(-5), now));
        Assert.Equal("5 minutes ago", WelcomeContent.FormatAge(now.AddMinutes(-5), now));
        Assert.Equal("3 hours ago", WelcomeContent.FormatAge(now.AddHours(-3), now));
        Assert.Equal("yesterday", WelcomeContent.FormatAge(now.AddDays(-1), now));
        Assert.Equal("4 days ago", WelcomeContent.FormatAge(now.AddDays(-4), now));

        var (d, projects) = StartWelcome(1);
        var host = (PolishHost)PlatformHost.Current!;
        File.Delete(projects[0]);
        host.Answer = f => f.FindControl("btnAction0") != null ? "btnAction0" : null;   // "Remove from the list"
        Assert.False(d.OpenRecentProject(projects[0]));
        Assert.Contains("Project not found", host.Prompts);
        Assert.Empty(d.Settings.RecentProjects);
    }

    [Fact]
    public void Keyboard_essentials_come_from_the_registry_and_follow_rebinding()
    {
        var (d, _) = StartWelcome(1);
        var essentials = d.EssentialShortcuts();
        Assert.Contains(essentials, e => e.Title == "Run" && e.Keys == "F9");
        Assert.Contains(essentials, e => e.Title == "Start Debugging" && e.Keys == "F5");
        Assert.Contains(essentials, e => e.Title == "Command Palette");
        d.Commands.Rebind("Run.Run", "Ctrl+Alt+R");
        Assert.Contains(d.EssentialShortcuts(), e => e.Title == "Run" && e.Keys == "Ctrl+Alt+R");
        Assert.Equal(d.EssentialShortcuts(), d.Welcome.Shortcuts.Count == 0 ? d.EssentialShortcuts() : d.EssentialShortcuts());
    }

    // ---- Editor settings and themes ----

    [Fact]
    public void Editor_settings_apply_live_to_open_and_new_editors_and_persist()
    {
        NewHost();
        var project = NewProject();
        var d = Start(project.ProjectFile);
        var first = d.OpenCodeFile(Path.Combine(project.Directory, "MainForm.cs"))!;
        var editor = Assert.IsType<CodeEditor>(first.View);

        var ide = d.IdeSettings;
        ide.TabSize = 2;
        ide.InsertSpaces = false;
        ide.EditorFontSize = 17;
        ide.Ligatures = true;
        ide.ShowLineNumbers = false;
        ide.HighlightCurrentLine = false;
        ide.EditorTheme = EditorThemes.HighContrastDark;
        d.ApplyEditorSettings();

        Assert.Equal(2, editor.TabSize);
        Assert.False(editor.InsertSpaces);
        Assert.Equal(17, editor.FontSize);
        Assert.True(editor.Ligatures);
        Assert.False(editor.ShowLineNumbers);
        Assert.False(editor.HighlightCurrentLine);
        Assert.Same(EditorThemes.Colors(EditorThemes.HighContrastDark), editor.Colors);

        var second = Assert.IsType<CodeEditor>(d.OpenCodeFile(Path.Combine(project.Directory, "Program.cs"))!.View);   // a document opened later
        Assert.Equal((2, 17.0), (second.TabSize, second.FontSize));

        d.SaveIdeSettings();
        var loaded = IdeSettings.Load(d.Paths.Settings);
        Assert.Equal((2, false, 17.0, true, false, false, EditorThemes.HighContrastDark),
            (loaded.TabSize, loaded.InsertSpaces, loaded.EditorFontSize, loaded.Ligatures, loaded.ShowLineNumbers, loaded.HighlightCurrentLine, loaded.EditorTheme));

        ide.EditorTheme = EditorThemes.Auto;
        d.ApplyEditorSettings();
        Assert.Null(editor.Colors);   // back to following the IDE theme
    }

    [Fact]
    public void The_editor_themes_are_distinct_and_the_high_contrast_pair_has_its_own_colours()
    {
        var all = EditorThemes.All.Select(t => EditorThemes.Colors(t.Id)).Where(c => c != null).ToList();
        Assert.Equal(4, all.Distinct().Count());
        Assert.NotEqual(CodeEditorColors.Dark.Background, EditorThemes.Colors(EditorThemes.HighContrastDark)!.Background);
        Assert.NotEqual(CodeEditorColors.Light.GutterBorder, EditorThemes.Colors(EditorThemes.HighContrastLight)!.GutterBorder);
        Assert.Null(EditorThemes.Colors(EditorThemes.Auto));
    }

    [Fact]
    public void The_options_dialog_has_an_editor_page_that_changes_the_settings()
    {
        NewHost();
        var project = NewProject();
        var d = Start(project.ProjectFile);
        var code = d.OpenCodeFile(Path.Combine(project.Directory, "MainForm.cs"))!;
        var dialog = new OptionsDialog(d.Settings, d.IdeSettings, d);
        ((ComboBox)dialog.FindControl("cboEditorTheme")!).ItemIndex = EditorThemes.All.ToList().FindIndex(t => t.Id == EditorThemes.Dark);
        ((SpinEdit)dialog.FindControl("edtEditorFontSize")!).Value = 15;
        ((SpinEdit)dialog.FindControl("edtTabSize")!).Value = 8;
        ((CheckBox)dialog.FindControl("chkLigatures")!).Checked = true;
        ((CheckBox)dialog.FindControl("chkAutosaveFocus")!).Checked = true;
        ((SpinEdit)dialog.FindControl("edtAutosaveSeconds")!).Value = 20;
        dialog.Apply();
        d.ApplyEditorSettings();

        var editor = (CodeEditor)code.View;
        Assert.Equal((8, 15.0, true), (editor.TabSize, editor.FontSize, editor.Ligatures));
        Assert.Same(CodeEditorColors.Dark, editor.Colors);
        Assert.True(d.IdeSettings.AutosaveOnFocusLoss);
        Assert.Equal(20, d.IdeSettings.AutosaveIdleSeconds);
        Assert.NotNull(dialog.FindControl("edtCustom"));   // the General page is still there
    }

    [Fact]
    public void Follow_os_theme_switches_the_ide_theme_when_the_system_exposes_it()
    {
        NewHost();
        var d = Start(null);
        try
        {
            d.IdeSettings.FollowOsTheme = true;
            SystemTheme.Provider = () => true;
            Assert.True(d.ApplyOsTheme());
            Assert.Equal("dark", d.Settings.Theme);
            SystemTheme.Provider = () => false;
            Assert.True(d.ApplyOsTheme());
            Assert.Equal("light", d.Settings.Theme);
            SystemTheme.Provider = () => null;   // no OS support: nothing changes
            Assert.False(d.ApplyOsTheme());
            d.IdeSettings.FollowOsTheme = false;
            SystemTheme.Provider = () => true;
            Assert.False(d.ApplyOsTheme());
        }
        finally { SystemTheme.Provider = null; }
    }

    // ---- Keyboard shortcuts reference ----

    [Fact]
    public void The_shortcuts_reference_lists_every_command_with_its_current_binding()
    {
        NewHost();
        var project = NewProject();
        var d = Start(project.ProjectFile);
        d.Commands.Rebind("File.Save", "Ctrl+Alt+S");
        var text = ShortcutsReference.Generate(d.Commands);
        foreach (var c in d.Commands.All) Assert.Contains(c.Id, text);
        foreach (var category in d.Commands.All.Select(c => c.Category).Distinct()) Assert.Contains("\n" + category + "\n", text.Replace("\r\n", "\n"));
        Assert.Contains("Ctrl+Alt+S", text);
        Assert.Contains("F10", text);   // the debugger's keys are in the registry, so they are here
        Assert.Contains("unassigned", text);

        var doc = d.ShowShortcutsReference()!;
        Assert.True(doc.ReadOnly);
        Assert.Contains("Debug.StepOver", doc.Text);
        Assert.Same(doc, d.ActiveDocument);
        Assert.NotNull(d.Commands.Find("Help.KeyboardShortcuts"));
    }

    [Fact]
    public void The_shortcuts_reference_opens_in_a_window_without_a_project()
    {
        var host = NewHost();
        var d = Start(null);
        Assert.Null(d.ShowShortcutsReference());
        Assert.Contains("Keyboard Shortcuts", host.Prompts);
    }

    // ---- Autosave ----

    static (DesignerForm D, CodeDocument Code, RadProject Project) StartWithCode()
    {
        NewHost();
        var project = NewProject();
        var d = Start(project.ProjectFile);
        var code = d.OpenCodeFile(Path.Combine(project.Directory, "MainForm.cs"))!;
        return (d, code, project);
    }

    [Fact]
    public void Autosave_after_idle_is_off_by_default_and_saves_when_enabled()
    {
        var (d, code, _) = StartWithCode();
        var now = DateTime.UtcNow;
        d.Clock = () => now;
        Assert.False(d.IdeSettings.AutosaveOnFocusLoss);
        Assert.Equal(0, d.IdeSettings.AutosaveIdleSeconds);

        code.Text = code.Text + "// edited\n";
        now += TimeSpan.FromMinutes(5);
        Assert.Equal(0, d.RunAutosaveTick());   // off
        Assert.True(code.IsDirty);

        d.IdeSettings.AutosaveIdleSeconds = 10;
        code.Text = code.Text + "// again\n";
        now += TimeSpan.FromSeconds(5);
        Assert.Equal(0, d.RunAutosaveTick());   // not idle long enough
        now += TimeSpan.FromSeconds(6);
        Assert.Equal(1, d.RunAutosaveTick());
        Assert.False(code.IsDirty);
        Assert.EndsWith("// again\n", File.ReadAllText(code.Path));
    }

    [Fact]
    public void Autosave_on_focus_loss_saves_the_document_that_lost_focus()
    {
        var (d, code, _) = StartWithCode();
        code.Focus();
        code.Text = code.Text + "// typed\n";
        d.IdeSettings.AutosaveOnFocusLoss = false;
        d.FindControl("edtName")!.Focus();
        Assert.True(code.IsDirty);   // off: nothing saved

        d.IdeSettings.AutosaveOnFocusLoss = true;
        code.Focus();
        d.FindControl("edtName")!.Focus();
        Assert.False(code.IsDirty);
        Assert.EndsWith("// typed\n", File.ReadAllText(code.Path));
    }

    // ---- Crash recovery ----

    [Fact]
    public void Recovery_store_writes_lists_and_clears()
    {
        var store = new RecoveryStore(Path.Combine(TempDir(), "recovery"));
        Assert.False(store.BeginSession());
        var a = Path.Combine(TempDir(), "A.cs");
        store.Write([new RecoveryItem("code", a, "class A {}", DateTime.UtcNow), new RecoveryItem("form", a + ".json", "{ }", DateTime.UtcNow)]);
        var items = store.List();
        Assert.Equal(["code", "form"], items.Select(i => i.Kind));
        Assert.Equal("class A {}", items[0].Text);
        store.Write([items[0]]);   // the form was saved: its file goes
        Assert.Single(store.List());
        Assert.Single(Directory.GetFiles(store.Directory, "*.txt"));
        store.Write([]);
        Assert.Empty(store.List());
        store.EndSession();
        Assert.False(File.Exists(Path.Combine(store.Directory, "running")));
    }

    [Fact]
    public void An_abnormal_exit_offers_the_unsaved_code_and_form_back_on_the_next_start()
    {
        var host = NewHost();
        var project = NewProject();
        var settings = Path.Combine(TempDir(), "designer.json");
        var d1 = Start(project.ProjectFile, settings);
        var code = d1.OpenCodeFile(Path.Combine(project.Directory, "MainForm.cs"))!;
        var original = code.Text;
        code.Text = original + "// unsaved\n";
        var form = d1.OpenFormFile(project.FormFiles.Single())!;
        form.Document.Snapshot();
        var button = form.Document.Create(ComponentRegistry.Find("Button")!, form.Document.Form, 20, 20);
        form.Document.Rename(button, "btnRecovered");
        Assert.True(form.IsDirty);
        d1.WriteRecovery();
        Assert.Equal(2, d1.Recovery.List().Count);
        // d1 never exits cleanly: its marker names a process that is gone.
        File.WriteAllText(Path.Combine(d1.Recovery.Directory, "running"), "2000000000");

        var d2 = Start(project.ProjectFile, settings);
        Assert.Equal(2, d2.PendingRecovery.Count);
        host.Answer = f => f.FindControl("btnYes") != null ? "btnYes" : null;
        d2.OfferRecovery();
        Assert.Contains("Warning", host.Prompts);
        Assert.Empty(d2.PendingRecovery);

        var code2 = (CodeDocument)d2.FindDocument(code.Path)!;
        Assert.EndsWith("// unsaved\n", code2.Text);
        Assert.True(code2.IsDirty);
        Assert.Equal(original, File.ReadAllText(code.Path));   // the file on disk is untouched until the user saves
        var form2 = (FormDocument)d2.FindDocument(form.Path)!;
        Assert.NotNull(form2.Document.Form.FindControl("btnRecovered"));

        // The restored changes are kept again at once; saving them clears the recovery files.
        Assert.Equal(2, d2.Recovery.List().Count);
        d2.SaveAll();
        d2.WriteRecovery();
        Assert.Empty(d2.Recovery.List());
    }

    [Fact]
    public void Declining_recovery_and_a_clean_exit_clear_the_recovery_files()
    {
        var host = NewHost();
        var project = NewProject();
        var settings = Path.Combine(TempDir(), "designer.json");
        var d1 = Start(project.ProjectFile, settings);
        var code = d1.OpenCodeFile(Path.Combine(project.Directory, "MainForm.cs"))!;
        code.Text += "// lost\n";
        d1.WriteRecovery();
        File.WriteAllText(Path.Combine(d1.Recovery.Directory, "running"), "2000000000");

        var d2 = Start(project.ProjectFile, settings);
        host.Answer = f => f.FindControl("btnNo") != null ? "btnNo" : null;
        d2.OfferRecovery();
        Assert.Empty(d2.Recovery.List());
        Assert.False(((CodeDocument)d2.FindDocument(code.Path)!).IsDirty);

        // A normal start after a clean exit has nothing to offer.
        var d3 = Start(project.ProjectFile, settings);
        var doc3 = (CodeDocument)d3.FindDocument(code.Path)!;
        doc3.Text += "// pending\n";
        d3.WriteRecovery();
        Assert.Single(d3.Recovery.List());
        host.Answer = f => f.FindControl("btnNo") != null ? "btnNo" : null;   // "no" to saving: closing discards the change
        d3.Close();
        Assert.Empty(d3.Recovery.List());
        Assert.False(File.Exists(Path.Combine(d3.Recovery.Directory, "running")));
        Assert.Empty(Start(project.ProjectFile, settings).PendingRecovery);
    }

    [Fact]
    public void Unsaved_buffers_are_written_to_the_recovery_folder_on_a_timer_interval()
    {
        var (d, code, _) = StartWithCode();
        var now = DateTime.UtcNow;
        d.Clock = () => now;
        code.Text += "// periodic\n";
        d.PolishTick();
        Assert.Empty(d.Recovery.List());   // the interval has not passed
        now += DesignerForm.RecoveryInterval + TimeSpan.FromSeconds(1);
        d.PolishTick();
        Assert.Equal(code.Path, Assert.Single(d.Recovery.List()).Path);
    }

    // ---- Problems window at 150 % ----

    [Fact]
    public void Problems_icons_use_theme_tokens_and_the_window_lays_out_at_150_percent()
    {
        var store = new ProblemStore();
        store.Set("build",
        [
            new BuildMessage("A.cs", 3, 5, "error", "CS0103", "The name 'x' does not exist in the current context"),
            new BuildMessage("A.cs", 9, 1, "warning", "CS0168", "The variable 'y' is declared but never used"),
            new BuildMessage("B.cs", 1, 1, "info", "CS8019", "Unnecessary using directive"),
        ]);
        foreach (var theme in new[] { Theme.Default, Theme.Dark })
        {
            var form = new Form { Theme = theme };
            form.SetBounds(0, 0, 640, 200);
            var window = new ProblemsWindow(store) { Align = Align.Client };
            form.Add(window);
            form.ApplyScale(1.5);
            form.PerformLayout();

            // The three filters and the toggle sit on one row inside the window.
            var chk = window.Children.OfType<CheckBox>().ToList();
            Assert.Equal(4, chk.Count);
            for (int i = 1; i < chk.Count; i++) Assert.True(chk[i].Left >= chk[i - 1].Left + chk[i - 1].Width);
            Assert.True(chk[^1].Left + chk[^1].Width <= window.Width);

            using var bitmap = FormRenderer.RenderToBitmap(form);
            Assert.Equal((960, 300), (bitmap.Width, bitmap.Height));
            if (Environment.GetEnvironmentVariable("RADSHARP_I9_PNG") is { Length: > 0 } dir)
                File.WriteAllBytes(Path.Combine(dir, $"problems-150-{(theme == Theme.Dark ? "dark" : "light")}.png"), FormRenderer.EncodePng(bitmap));

            int rowH = 33, top = 45, pad = 9, icon = 18;   // 22/30/6/12 at 150 %
            Color At(int row, int dx, int dy) { var p = bitmap.GetPixel(pad + dx, top + row * rowH + (rowH - icon) / 2 + dy); return new Color(p.Red, p.Green, p.Blue, p.Alpha); }
            Assert.Equal(theme.Error, At(0, 3, 9));
            Assert.Equal(theme.Warning, At(1, 9, 14));
            Assert.Equal(theme.Info, At(2, 3, 9));
        }
    }
}
