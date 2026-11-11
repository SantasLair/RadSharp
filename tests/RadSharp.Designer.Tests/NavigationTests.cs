using System.Collections.Concurrent;
using RadSharp.CodeEditing;
using RadSharp.Design.Code;
using RadSharp.Designer;
using RadSharp.Threading;

namespace RadSharp.Tests;

/// <summary>
/// Navigation and search in the IDE (milestone I5), driven through the real shortcuts on the designer form: the command palette, quick open
/// (file, symbol, line), Find in Files, back/forward and the structure/code sync. Like <see cref="LanguageIntegrationTests"/> the host queues posted
/// work and the tests pump it until a condition holds; waits are event-driven and bounded, never sleeps. Tests that don't need Roslyn run without
/// the workspace.
/// </summary>
[Collection("Designer")]   // sets PlatformHost.Current and LanguageHost's statics
public sealed class NavigationTests : IDisposable
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    sealed class QueueHost : IPlatformServices
    {
        readonly ConcurrentQueue<Action> _queue = new();
        readonly SemaphoreSlim _wake = new(0);
        public readonly List<string> Modals = [];
        public Func<Form, string?>? Answer;

        public void PostToUiThread(Action action) { _queue.Enqueue(action); _wake.Release(); }
        public void ShowForm(Form form) => form.PerformLayout();
        public void RunModal(Form form)
        {
            form.PerformLayout();
            Modals.Add(form.Caption);
            if (Answer?.Invoke(form) is { } name && form.FindControl(name) is Button b) form.Simulate.Click(b);
            if (form.IsModal) form.Close();
        }
        public void ShowModal(Form form, Action onClosed) { RunModal(form); onClosed(); }
        public void CloseForm(Form form) { }
        public void ProcessEventsUntil(Func<bool> done) { }
        public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) => done(null);

        public void PumpUntil(Func<bool> done, string what)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (true)
            {
                while (_queue.TryDequeue(out var action)) action();
                if (done()) return;
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !_wake.Wait(left)) throw new TimeoutException($"Timed out waiting for {what}.");
            }
        }

        public void PumpUntil(Task task, string what)
        {
            task.ContinueWith(_ => _wake.Release(), TaskScheduler.Default);
            PumpUntil(() => task.IsCompleted, what);
        }
    }

    readonly SynchronizationContext? _oldContext = SynchronizationContext.Current;
    readonly QueueHost _host = new();

    public NavigationTests()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        PlatformHost.Current = _host;
        LanguageHost.Enabled = false;
    }

    public void Dispose()
    {
        LanguageHost.Enabled = false;
        LanguageHost.DefaultOptions = null;
        LanguageHost.DiagnosticsDelay = TimeSpan.FromMilliseconds(300);
        SynchronizationContext.SetSynchronizationContext(_oldContext);
    }

    static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>A copy of samples/SettingsDialog in a temp folder with a self-contained .csproj, plus a bin and an obj folder holding a file that matches.</summary>
    static RadProject SampleCopy()
    {
        var dir = Directory.CreateTempSubdirectory("radsharp-nav-").FullName;
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot, "samples", "SettingsDialog")))
            if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(dir, "SettingsDialog.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <RootNamespace>SettingsDialog</RootNamespace>
              </PropertyGroup>
            </Project>
            """);
        foreach (var skipped in new[] { "bin", "obj" })
        {
            Directory.CreateDirectory(Path.Combine(dir, skipped));
            File.WriteAllText(Path.Combine(dir, skipped, "Generated.cs"), "// Save automatically (build output)\n");
        }
        return new RadProject(Path.Combine(dir, "SettingsDialog.csproj"));
    }

    (DesignerForm Designer, RadProject Project, string Settings) Start(bool language = false)
    {
        if (language)
        {
            // As on the real UI thread: the editor marshals results through the UI context (set in the test body, not the constructor).
            SynchronizationContext.SetSynchronizationContext(new UiSynchronizationContext(_host.PostToUiThread));
            LanguageHost.Enabled = true;
            LanguageHost.DefaultOptions = new RadSharp.Ide.Language.ProjectWorkspaceOptions { WatchFiles = false, Restore = RadSharp.Ide.Language.RestoreMode.Never };
            LanguageHost.DiagnosticsDelay = TimeSpan.FromMilliseconds(50);
        }
        var project = SampleCopy();
        var settings = Path.Combine(Directory.CreateTempSubdirectory("radsharp-nav-settings-").FullName, "designer.json");
        var d = new DesignerForm(project.ProjectFile, settings);
        d.Settings.Editor = EditorKind.Custom;
        d.Settings.CustomEditorCommand = "dotnet --version";
        d.SetBounds(0, 0, 1220, 780);
        d.PerformLayout();
        if (language)
        {
            var host = Assert.Single(d.LanguageHosts);
            _host.PumpUntil(host.Ready, "the workspace to open");
            _host.PumpUntil(() => host.IsReady || host.Error != null, "the host to be ready");
            Assert.True(host.IsReady, host.Error);
        }
        return (d, project, settings);
    }

    static string FileOf(RadProject project, string name) => Path.Combine(project.Directory, name);

    static CodeEditor EditorOf(CodeDocument doc) => Assert.IsType<CodeEditor>(doc.View);

    static int LineOf(string text, string marker)
    {
        int i = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{marker}' not found");
        return text.Take(i).Count(c => c == '\n') + 1;
    }

    QuickPick Pick(DesignerForm d) => Assert.IsType<QuickPick>(d.ActiveQuickPick);

    /// <summary>Presses a shortcut, types into the open picker and waits for rows.</summary>
    QuickPick Open(DesignerForm d, string shortcut, string? type = null, Func<QuickPick, bool>? ready = null)
    {
        d.Simulate.Key(shortcut);
        var pick = Pick(d);
        if (type != null) d.Simulate.Type(type);
        if (ready != null) _host.PumpUntil(() => ready(pick), $"rows for {shortcut} {type}");
        return pick;
    }

    // ---- The scripted path (the milestone's done-when) ----

    [Fact]
    public void A_scripted_path_reaches_a_symbol_a_file_a_line_a_command_and_a_control_and_back_forward_replays_it()
    {
        var (d, project, _) = Start(language: true);
        var mainJson = FileOf(project, "MainForm.radform.json");
        var settingsCs = FileOf(project, "SettingsForm.cs");
        var settingsJson = FileOf(project, "SettingsForm.radform.json");
        Assert.IsType<FormDocument>(d.ActiveDocument);   // the project opens on its main form

        // Ctrl+T: a symbol of the workspace (Roslyn), reached by a few letters.
        var pick = Open(d, "Ctrl+T", "Load", p => p.Rows.Any(r => r.Label == "Load"));
        Assert.Equal("Load", pick.Selected!.Label);
        Assert.Contains("Method", pick.Selected.Detail);
        d.Simulate.Key(Key.Enter);
        Assert.Null(d.ActiveQuickPick);
        var code = Assert.IsType<CodeDocument>(d.ActiveDocument);
        Assert.Equal(settingsCs, code.Path, ignoreCase: true);
        int loadLine = LineOf(code.Text, "public void Load(Settings s)");
        Assert.Equal(loadLine, code.Editor.Caret.Line);

        // Ctrl+P: a file, by fuzzy name.
        pick = Open(d, "Ctrl+P", "MainForm.cs", p => p.Rows.Count > 0);
        Assert.Equal("MainForm.cs", pick.Selected!.Label);
        d.Simulate.Key(Key.Enter);
        code = Assert.IsType<CodeDocument>(d.ActiveDocument);
        Assert.Equal("MainForm.cs", Path.GetFileName(code.Path));
        Assert.Equal(1, code.Editor.Caret.Line);

        // Ctrl+G: a line of that file.
        d.Simulate.Key("Ctrl+G");
        Assert.True(Pick(d).InputOnly);
        d.Simulate.Type("12");
        d.Simulate.Key(Key.Enter);
        Assert.Equal(12, code.Editor.Caret.Line);
        Assert.Null(d.ActiveQuickPick);

        // Ctrl+Shift+P: a command, by fuzzy title.
        var grid = d.ActiveForm!.Surface.ShowGrid;
        pick = Open(d, "Ctrl+Shift+P", "show grid", p => p.Rows.Count > 0);
        Assert.Equal("View.ShowGrid", ((Command)pick.Selected!.Tag!).Id);
        d.Simulate.Key(Key.Enter);
        Assert.NotEqual(grid, d.ActiveForm.Surface.ShowGrid);

        // Ctrl+Shift+F: a hit inside a form file opens the designer with the owning control selected.
        d.Simulate.Key("Ctrl+Shift+F");
        Assert.True(d.FindInFilesPanel.FindBox.Focused);
        d.Simulate.Type("Save automatically");
        d.Simulate.Key(Key.Enter);
        _host.PumpUntil(() => !d.FindInFilesPanel.Busy && d.FindInFilesResults.Count > 0, "the search results");
        var file = Assert.Single(d.FindInFilesResults);            // bin/obj copies were skipped
        Assert.Equal(settingsJson, file.File, ignoreCase: true);
        var hit = Assert.Single(file.Hits);
        Assert.Equal("chkAutosave", hit.Owner);
        var list = (ListBox)d.FindControl("lstFindResults")!;
        int row = Array.FindIndex(list.Items, s => s.Contains("[chkAutosave]"));
        Assert.True(row > 0, string.Join("\n", list.Items));
        d.OpenFindResult(row);                                       // what a double-click does
        var form = Assert.IsType<FormDocument>(d.ActiveDocument);
        Assert.Equal(settingsJson, form.Path, ignoreCase: true);
        Assert.Equal("chkAutosave", Assert.Single(form.Document.Selection).Name);

        // The history has every stop, in order: the form we started on, then each jump.
        string Describe(NavLocation l) => l.Kind == NavLocation.Form ? $"{Path.GetFileName(l.Path)}#{l.Control}" : $"{Path.GetFileName(l.Path)}:{l.Line}";
        Assert.Equal(
            [$"MainForm.radform.json#", $"SettingsForm.cs:{loadLine}", "MainForm.cs:1", "MainForm.cs:12", "SettingsForm.radform.json#chkAutosave"],
            d.History.Entries.Select(Describe).ToArray());
        Assert.True(d.Commands["Go.Back"].Enabled);
        Assert.False(d.Commands["Go.Forward"].Enabled);

        // Alt+Left walks back through them, in order.
        d.Simulate.Key("Alt+Left");
        code = Assert.IsType<CodeDocument>(d.ActiveDocument);
        Assert.Equal(("MainForm.cs", 12), (Path.GetFileName(code.Path), code.Editor.Caret.Line));
        d.Simulate.Key("Alt+Left");
        Assert.Equal(("MainForm.cs", 1), (Path.GetFileName(code.Path), code.Editor.Caret.Line));
        d.Simulate.Key("Alt+Left");
        code = Assert.IsType<CodeDocument>(d.ActiveDocument);
        Assert.Equal(("SettingsForm.cs", loadLine), (Path.GetFileName(code.Path), code.Editor.Caret.Line));
        d.Simulate.Key("Alt+Left");
        Assert.Equal(mainJson, Assert.IsType<FormDocument>(d.ActiveDocument).Path, ignoreCase: true);
        Assert.False(d.Commands["Go.Back"].Enabled);
        d.Simulate.Key("Alt+Left");                                  // at the start: nothing happens
        Assert.Equal(mainJson, d.ActiveDocument!.Path, ignoreCase: true);

        // Alt+Right replays them.
        d.Simulate.Key("Alt+Right");
        Assert.Equal(("SettingsForm.cs", loadLine), (Path.GetFileName(d.ActiveDocument!.Path!), ((CodeDocument)d.ActiveDocument).Editor.Caret.Line));
        d.Simulate.Key("Alt+Right");
        d.Simulate.Key("Alt+Right");
        Assert.Equal(("MainForm.cs", 12), (Path.GetFileName(d.ActiveDocument!.Path!), ((CodeDocument)d.ActiveDocument).Editor.Caret.Line));
        d.Simulate.Key("Alt+Right");
        var last = Assert.IsType<FormDocument>(d.ActiveDocument);
        Assert.Equal("chkAutosave", Assert.Single(last.Document.Selection).Name);
        Assert.False(d.Commands["Go.Forward"].Enabled);
        Assert.Equal(5, d.History.Entries.Count);                    // walking the history added nothing
    }

    // ---- Command palette ----

    [Fact]
    public void The_palette_lists_every_command_with_category_shortcut_and_disabled_state_and_runs_the_chosen_one()
    {
        var (d, _, settings) = Start();
        var pick = Open(d, "Ctrl+Shift+P");
        var save = Assert.Single(pick.Items, i => i.Label == "Save All");
        Assert.Equal("File", save.Detail);
        Assert.Equal("Ctrl+Shift+S", save.Right);
        Assert.True(save.Enabled);
        // The palette lists what a menu can run on its own: no parameter-only commands, and not itself.
        Assert.DoesNotContain(pick.Items, i => i.Label.StartsWith("Reopen") || i.Label.StartsWith("Dock ") || i.Label == "Command Palette…");
        Assert.Contains(pick.Items, i => i.Label == "Go to File…" && i.Right == "Ctrl+P");
        // A command that can't run now is listed greyed and can't be chosen.
        Assert.False(Assert.Single(pick.Items, i => i.Label == "Navigate Forward").Enabled);

        // Fuzzy: word starts, in order.
        d.Simulate.Type("sva");
        Assert.Equal("Save All", pick.Selected!.Label);
        Assert.Equal([0, 2, 5], pick.Selected.LabelMatches);   // S-v-A: the S, the v, the A of "All"
        d.Simulate.Key(Key.Escape);
        Assert.Null(d.ActiveQuickPick);

        // Arrow keys move, Enter runs; a disabled row can't be chosen.
        pick = Open(d, "Ctrl+Shift+P", "navigate");
        Assert.Equal(["Navigate Back", "Navigate Forward"], pick.Rows.Select(r => r.Label).ToArray());
        d.Simulate.Key(Key.Down);
        Assert.Equal("Navigate Forward", pick.Selected!.Label);
        d.Simulate.Key(Key.Enter);
        Assert.NotNull(d.ActiveQuickPick);                           // disabled: still open
        d.Simulate.Key(Key.Down);                                    // wraps to the first
        Assert.Equal("Navigate Back", pick.Selected!.Label);
        d.Simulate.Key(Key.Escape);

        // Run "Show Grid": it is remembered, comes first next time, and survives a restart.
        var surface = d.ActiveForm!.Surface;
        bool grid = surface.ShowGrid;
        pick = Open(d, "Ctrl+Shift+P", "show grid");
        d.Simulate.Key(Key.Enter);
        Assert.NotEqual(grid, surface.ShowGrid);
        Assert.Equal(["View.ShowGrid"], d.RecentCommands.Ids.ToArray());
        pick = Open(d, "Ctrl+Shift+P");
        Assert.Equal("Show Grid", pick.Rows[0].Label);               // recently used first, before the rest
        d.Simulate.Key(Key.Escape);

        var restarted = new DesignerForm(null, settings);
        Assert.Equal(["View.ShowGrid"], restarted.RecentCommands.Ids.ToArray());
    }

    [Fact]
    public void Every_navigation_shortcut_is_unique_and_the_go_menu_and_toolbar_run_the_commands()
    {
        var (d, _, _) = Start();
        var shortcuts = d.Commands.All.Where(c => c.Shortcut.Length > 0).GroupBy(c => Shortcut.Parse(c.Shortcut)).Where(g => g.Count() > 1).ToList();
        Assert.Empty(shortcuts);

        var expected = new Dictionary<string, string>
        {
            ["Go.CommandPalette"] = "Ctrl+Shift+P", ["Go.File"] = "Ctrl+P", ["Go.SymbolInWorkspace"] = "Ctrl+T", ["Go.SymbolInFile"] = "Ctrl+Shift+O",
            ["Go.Line"] = "Ctrl+G", ["Go.Back"] = "Alt+Left", ["Go.Forward"] = "Alt+Right",
            ["Search.FindInFiles"] = "Ctrl+Shift+F", ["Search.ReplaceInFiles"] = "Ctrl+Shift+H",
        };
        foreach (var (id, shortcut) in expected) Assert.Equal(Shortcut.Parse(shortcut), Shortcut.Parse(d.Commands[id].Shortcut));

        var menu = (MainMenu)d.FindControl("mnuMain")!;
        var go = Assert.Single(menu.Items, m => m.Caption == "Go");
        var ids = go.Items.Where(i => !i.IsSeparator).Select(i => d.Commands.CommandOf(i)).ToList();
        Assert.Equal(["Go.Back", "Go.Forward", "Go.File", "Go.SymbolInWorkspace", "Go.SymbolInFile", "Go.Line", "Go.CommandPalette"], ids);
        var edit = Assert.Single(menu.Items, m => m.Caption == "Edit");
        Assert.Contains(edit.Items, i => d.Commands.CommandOf(i) == "Search.FindInFiles");

        var back = (ToolButton)d.FindControl("btnBack")!;
        var forward = (ToolButton)d.FindControl("btnForward")!;
        Assert.Equal("Go.Back", d.Commands.CommandOf(back));
        Assert.False(back.Enabled);
        // Jump somewhere: Back is available, and the button goes there.
        d.OpenCodeFile(FileOf(d.Project!, "MainForm.cs"));
        Assert.True(back.Enabled);
        d.Simulate.Click(back);
        Assert.IsType<FormDocument>(d.ActiveDocument);
        Assert.True(forward.Enabled);
        d.Simulate.Click(forward);
        Assert.IsType<CodeDocument>(d.ActiveDocument);
    }

    // ---- Quick open ----

    [Fact]
    public void Go_to_file_matches_name_and_path_skips_build_folders_and_opens_forms_in_the_designer()
    {
        var (d, project, _) = Start();
        var pick = Open(d, "Ctrl+P");
        Assert.DoesNotContain(pick.Items, i => i.Detail.StartsWith("bin") || i.Detail.StartsWith("obj"));
        Assert.Contains(pick.Items, i => i.Label == "SettingsForm.cs");
        // The open document is listed first.
        Assert.Equal("MainForm.radform.json", pick.Rows[0].Label);

        d.Simulate.Type("sfc");                                      // SettingsForm.cs: word starts
        Assert.Equal("SettingsForm.cs", pick.Selected!.Label);
        d.Simulate.Key(Key.Escape);

        pick = Open(d, "Ctrl+P", "settingsform.radform");
        d.Simulate.Key(Key.Enter);
        var form = Assert.IsType<FormDocument>(d.ActiveDocument);
        Assert.Equal(FileOf(project, "SettingsForm.radform.json"), form.Path, ignoreCase: true);

        // An open code file just comes to the front, with its caret where it was.
        var code = d.OpenCodeFile(FileOf(project, "MainForm.cs"))!;
        code.GoTo(18, 3);
        d.OpenFormFile(FileOf(project, "MainForm.radform.json"));
        Open(d, "Ctrl+P", "MainForm.cs");
        d.Simulate.Key(Key.Enter);
        Assert.Same(code, d.ActiveDocument);
        Assert.Equal((18, 3), code.Editor.Caret);
    }

    [Fact]
    public void Go_to_symbol_in_file_and_go_to_line_use_the_editor_buffer()
    {
        var (d, project, _) = Start();
        var code = d.OpenCodeFile(FileOf(project, "MainForm.cs"))!;
        var pick = Open(d, "Ctrl+Shift+O");
        Assert.Equal(["Settings", "MainForm", "_settings", "MainForm", "btnSettings_Click", "ShowSettings"], pick.Rows.Select(r => r.Label).ToArray());   // in source order
        d.Simulate.Type("shs");
        Assert.Equal("ShowSettings", pick.Selected!.Label);
        d.Simulate.Key(Key.Enter);
        Assert.Equal(LineOf(code.Text, "void ShowSettings()"), code.Editor.Caret.Line);

        // Unsaved text counts: a method typed now is listed.
        var editor = EditorOf(code);
        editor.ApplyEdits([TextEdit.Insert(editor.Text.IndexOf("    void ShowSettings", StringComparison.Ordinal), "    void Typed() { }\n")]);
        pick = Open(d, "Ctrl+Shift+O", "typed");
        Assert.Equal("Typed", pick.Selected!.Label);
        d.Simulate.Key(Key.Escape);

        // Go to line: line, line:column, clamping, and an input that isn't a line.
        int lines = editor.LineCount;
        d.Simulate.Key("Ctrl+G");
        pick = Pick(d);
        d.Simulate.Type("abc");
        d.Simulate.Key(Key.Enter);
        Assert.NotNull(d.ActiveQuickPick);                           // not a line: stays open
        pick.Text = "9999";
        d.Simulate.Key(Key.Enter);
        Assert.Equal(lines, code.Editor.Caret.Line);                 // clamped to the last line
        d.Simulate.Key("Ctrl+G");
        d.Simulate.Type("14:5");
        d.Simulate.Key(Key.Enter);
        Assert.Equal((14, 5), code.Editor.Caret);

        Assert.True(DesignerForm.TryParseLine(" 7 ", 100, out int line, out _) && line == 7);
        Assert.False(DesignerForm.TryParseLine("0", 100, out _, out _));
        Assert.False(DesignerForm.TryParseLine("3:x", 100, out _, out _));

        // On a form, the same shortcut lists its controls.
        d.OpenFormFile(FileOf(project, "SettingsForm.radform.json"));
        pick = Open(d, "Ctrl+Shift+O", "autosave");
        Assert.Equal("chkAutosave", pick.Selected!.Label);
        d.Simulate.Key(Key.Enter);
        Assert.Equal("chkAutosave", Assert.Single(d.ActiveForm!.Document.Selection).Name);
    }

    // ---- Find in Files ----

    static FindOptions O(string text, bool matchCase = false, bool word = false, bool regex = false, string filter = "") => new(text, matchCase, word, regex, filter);

    [Fact]
    public void Find_in_files_searches_open_buffers_and_form_files_groups_hits_and_skips_build_output()
    {
        var (d, project, _) = Start();
        var code = d.OpenCodeFile(FileOf(project, "MainForm.cs"))!;
        var editor = EditorOf(code);
        editor.ApplyEdits([TextEdit.Insert(0, "// zebra in an unsaved buffer\n")]);   // only in the buffer, not on disk

        _host.PumpUntil(d.FindInFilesAsync(O("zebra")), "the search");
        var file = Assert.Single(d.FindInFilesResults);
        Assert.Equal(code.Path, file.File, ignoreCase: true);
        Assert.Equal(1, file.Hits[0].Line);
        Assert.DoesNotContain("zebra", File.ReadAllText(code.Path));

        // The list: a header per file (relative, with the count), then one row per hit; a double-click goes there with the match selected.
        var list = (ListBox)d.FindControl("lstFindResults")!;
        Assert.Equal(["MainForm.cs  (1)"], list.Items.Take(1).ToArray());
        Assert.Contains("zebra in an unsaved buffer", list.Items[1]);
        code.GoTo(20, 1);
        d.OpenFindResult(1);
        Assert.Equal(1, code.Editor.Caret.Line);
        Assert.Equal("zebra", editor.SelectedText);

        // bin/obj are skipped; the filter narrows; case and whole word and regex.
        _host.PumpUntil(d.FindInFilesAsync(O("Save automatically")), "the search");
        Assert.Equal(["SettingsForm.radform.json"], d.FindInFilesResults.Select(f => Path.GetFileName(f.File)).ToArray());
        _host.PumpUntil(d.FindInFilesAsync(O("settings", filter: "*.cs")), "the search");
        Assert.All(d.FindInFilesResults, f => Assert.EndsWith(".cs", f.File));
        Assert.Contains(d.FindInFilesResults, f => Path.GetFileName(f.File) == "SettingsForm.cs");
        _host.PumpUntil(d.FindInFilesAsync(O("settings", matchCase: true, word: true, filter: "MainForm.cs")), "the search");
        Assert.DoesNotContain(d.FindInFilesResults.SelectMany(f => f.Hits), h => h.LineText.Contains("SettingsForm"));
        _host.PumpUntil(d.FindInFilesAsync(O(@"btn\w+_Click", regex: true)), "the search");
        Assert.Contains(d.FindInFilesResults.SelectMany(f => f.Hits), h => h.LineText.Contains("void btnSettings_Click"));
        var bad = d.FindInFilesAsync(O("(", regex: true));
        _host.PumpUntil(bad, "the bad regex to be refused");
        Assert.Contains("(", d.FindInFilesPanel.Status);   // the regex error is shown, nothing ran
        Assert.False(d.FindInFilesPanel.Busy);
    }

    [Fact]
    public void Find_in_files_can_be_stopped_and_a_newer_search_wins()
    {
        var (d, _, _) = Start();
        var first = d.FindInFilesAsync(O("settings"));
        var second = d.FindInFilesAsync(O("Save automatically"));   // supersedes the first
        _host.PumpUntil(Task.WhenAll(first, second), "both searches");
        Assert.Equal(["SettingsForm.radform.json"], d.FindInFilesResults.Select(f => Path.GetFileName(f.File)).ToArray());

        var stopped = d.FindInFilesAsync(O("settings"));
        d.Simulate.Click(d.FindInFilesPanel.StopButton);            // the Stop button cancels the running search
        _host.PumpUntil(stopped, "the stopped search");
        Assert.Equal("Search stopped", d.FindInFilesPanel.Status);
        Assert.False(d.FindInFilesPanel.Busy);
        Assert.Equal(["SettingsForm.radform.json"], d.FindInFilesResults.Select(f => Path.GetFileName(f.File)).ToArray());   // the old results stay
    }

    [Fact]
    public void Replace_in_files_edits_the_open_buffer_in_one_undo_step_and_writes_the_closed_file_to_disk()
    {
        var (d, project, _) = Start();
        var main = FileOf(project, "MainForm.cs");
        var settings = FileOf(project, "SettingsForm.cs");
        var code = d.OpenCodeFile(main)!;
        var diskBefore = File.ReadAllText(main);
        Assert.Null(d.FindDocument(settings));

        // "Settings" as a whole word: in both files (SettingsForm and ShowSettings are other words).
        var options = O("Settings", matchCase: true, word: true, filter: "*.cs");
        int inMainFile = FileSearch(options, main), inSettingsFile = FileSearch(options, settings);
        Assert.True(inMainFile > 0 && inSettingsFile > 0);

        // Declined: nothing changes.
        _host.Answer = f => "btnNo";
        _host.PumpUntil(d.ReplaceInFilesAsync(options, "Prefs"), "the declined replace");
        Assert.Equal(diskBefore, code.Text);
        Assert.False(code.IsDirty);
        Assert.Contains("Replace ", d.LastReplaceConfirmation);

        // Confirmed: the open buffer changes through the editor (unsaved, one undo step); the closed file is written.
        _host.Answer = f => "btnYes";
        _host.PumpUntil(d.ReplaceInFilesAsync(options, "Prefs"), "the replace");
        Assert.Contains($"Replace {inMainFile + inSettingsFile} occurrence(s)", d.LastReplaceConfirmation);
        Assert.Contains("1 open file(s) change in the editor", d.LastReplaceConfirmation);
        Assert.Contains("1 other file(s) are written to disk", d.LastReplaceConfirmation);

        Assert.DoesNotContain("record Settings", code.Text);
        Assert.Contains("record Prefs", code.Text);
        Assert.True(code.IsDirty);                                   // not saved
        Assert.Equal(diskBefore, File.ReadAllText(main));
        Assert.True(code.CanUndo);
        code.Undo();                                                  // one step brings back every change
        Assert.Equal(diskBefore, code.Text);
        Assert.False(code.CanUndo);
        code.Redo();
        Assert.Contains("record Prefs", code.Text);

        var written = File.ReadAllText(settings);
        Assert.Contains("Load(Prefs s)", written);
        Assert.DoesNotContain("Settings s", written.Replace("SettingsForm", ""));
        Assert.Contains("SettingsForm", written);                    // whole word: SettingsForm is not Settings
        Assert.Null(d.FindDocument(settings));                       // it stayed closed
        Assert.Empty(d.FindInFilesResults);                          // the refreshed search finds nothing left
        Assert.Contains($"Replaced {inMainFile + inSettingsFile} occurrence(s) in 2 file(s)", d.FindInFilesPanel.Status);
    }

    int FileSearch(FindOptions options, string file)
    {
        var regex = options.ToRegex(out _)!;
        return FindInFiles.Count(regex, File.ReadAllText(file));
    }

    [Fact]
    public void Replace_with_a_regex_uses_groups_and_a_form_file_open_in_the_designer_changes_in_the_designer()
    {
        var (d, project, _) = Start();
        var json = FileOf(project, "SettingsForm.radform.json");
        var form = d.OpenFormFile(json)!;
        Assert.Equal("chkAutosave", form.Document.Form.FindControl("chkAutosave")!.Name);

        var options = O(@"""caption"": ""Save (\w+)""", regex: true, filter: "*.radform.json");
        _host.Answer = f => "btnYes";
        _host.PumpUntil(d.ReplaceInFilesAsync(options, "\"caption\": \"Keep $1\""), "the replace");
        Assert.Equal("Keep automatically", ((CheckBox)form.Document.Form.FindControl("chkAutosave")!).Caption);
        Assert.True(form.IsDirty);                                   // changed in the designer, not saved
        Assert.DoesNotContain("Keep", File.ReadAllText(json));
        form.Document.Undo();                                        // one undo step
        Assert.Equal("Save automatically", ((CheckBox)form.Document.Form.FindControl("chkAutosave")!).Caption);
    }

    // ---- History ----

    [Fact]
    public void History_reopens_closed_documents_records_caret_jumps_and_ignores_small_moves()
    {
        var (d, project, _) = Start();
        var code = d.OpenCodeFile(FileOf(project, "MainForm.cs"))!;
        code.GoTo(2, 1);
        code.GoTo(9, 1);                                             // within 10 lines: the same place
        Assert.Equal(2, d.History.Entries.Count);                    // the form we came from, and this file
        code.GoTo(30, 1);                                            // a large move: a new place
        Assert.Equal(3, d.History.Entries.Count);
        d.OpenFormFile(FileOf(project, "SettingsForm.radform.json"));
        var settings = d.ActiveForm!;
        settings.Document.Select(settings.Document.Form.FindControl("btnOk"));
        Assert.Equal("btnOk", d.History.Current!.Control);           // a selection is a place
        settings.Document.Select(settings.Document.Form.FindControl("btnCancel"));
        Assert.Equal(6, d.History.Entries.Count);

        // Close the code file; going back to it reopens it at its caret.
        Assert.True(d.CloseDocument(code));
        Assert.Null(d.FindDocument(code.Path));
        d.GoBack();                                                  // btnOk
        Assert.Equal("btnOk", Assert.Single(d.ActiveForm!.Document.Selection).Name);
        d.GoBack();                                                  // the form with nothing selected
        d.GoBack();                                                  // MainForm.cs line 30, reopened
        var reopened = Assert.IsType<CodeDocument>(d.ActiveDocument);
        Assert.NotSame(code, reopened);
        Assert.Equal(30, reopened.Editor.Caret.Line);
    }

    [Fact]
    public void Going_back_skips_a_place_whose_file_was_deleted_and_forgets_it()
    {
        var (d, project, _) = Start();
        var scratch = FileOf(project, "Scratch.cs");
        File.WriteAllText(scratch, "class Scratch { }\n");
        var doc = d.OpenCodeFile(scratch)!;
        d.OpenFormFile(FileOf(project, "SettingsForm.radform.json"));
        Assert.Equal(3, d.History.Entries.Count);
        Assert.True(d.CloseDocument(doc));
        File.Delete(scratch);

        d.GoBack();
        Assert.Equal(FileOf(project, "MainForm.radform.json"), d.ActiveDocument!.Path, ignoreCase: true);
        Assert.DoesNotContain(d.History.Entries, e => e.Path.EndsWith("Scratch.cs"));
        Assert.Equal(2, d.History.Entries.Count);
        Assert.False(d.GoBack());                                    // nothing earlier
        Assert.True(d.GoForward());
        Assert.EndsWith("SettingsForm.radform.json", d.ActiveDocument!.Path);
    }

    // ---- Structure and code in step ----

    [Fact]
    public void Selecting_a_control_highlights_its_handler_and_fields_in_the_code_and_the_caret_in_a_handler_outlines_its_controls()
    {
        var (d, project, _) = Start();
        var form = d.ActiveForm!;
        var code = d.OpenCodeFile(FileOf(project, "MainForm.cs"))!;
        var editor = EditorOf(code);
        var button = form.Document.Form.FindControl("btnSettings")!;
        Assert.Equal("btnSettings_Click", button.EventHandlers["onClick"]);

        // Structure window (or the surface): selecting the control highlights its handler method's line, nothing else of the handler.
        Assert.Empty(d.CodeHighlightsOf(code));
        var tree = (TreeView)d.FindControl("tvStructure")!;
        var node = tree.Nodes.SelectMany(Walk).First(n => n.Tag == button);
        tree.Selected = node;
        Assert.Same(button, Assert.Single(form.Document.Selection));
        var handler = Assert.Single(d.CodeHighlightsOf(code), h => h.Message == "btnSettings.onClick handler");
        Assert.Equal("    void btnSettings_Click(Control sender)", editor.Snapshot.GetText(handler.Start, handler.Length));

        // Another control: the highlights move with it (lblSummary is used in the handler, and has no handler of its own).
        form.Document.Select(form.Document.Form.FindControl("lblSummary"));
        var items = d.CodeHighlightsOf(code);
        Assert.DoesNotContain(items, h => h.Message?.Contains("handler") == true);
        Assert.Equal(2, items.Count(i => editor.Snapshot.GetText(i.Start, i.Length) == "lblSummary"));   // its uses, lighter

        // A field declared in the file is highlighted as a declaration.
        editor.ApplyEdits([TextEdit.Insert(editor.Text.IndexOf("    Settings _settings", StringComparison.Ordinal), "    Label lblSummary;\n")]);
        form.Document.Select(null);
        Assert.Empty(d.CodeHighlightsOf(code));
        form.Document.Select(form.Document.Form.FindControl("lblSummary"));
        Assert.Contains(d.CodeHighlightsOf(code), h => h.Message == "Field lblSummary" && editor.Snapshot.GetText(h.Start, h.Length) == "    Label lblSummary;");

        // Caret in the handler: the controls wired to it are outlined on the surface.
        Assert.Empty(form.Surface.HighlightedControls);
        editor.Select(editor.Text.IndexOf("dialog.Load(_settings)", StringComparison.Ordinal) + 3, 0);
        Assert.Same(button, Assert.Single(form.Surface.HighlightedControls));
        Assert.Same(button, Assert.Single(d.ControlsWiredToCaret(code)));
        editor.Select(editor.Text.IndexOf("Settings _settings", StringComparison.Ordinal), 0);   // outside any method
        Assert.Empty(form.Surface.HighlightedControls);
        // Closing the code file clears what it showed.
        editor.Select(editor.Text.IndexOf("dialog.Load(_settings)", StringComparison.Ordinal) + 3, 0);
        Assert.NotEmpty(form.Surface.HighlightedControls);
        _host.Answer = f => "btnNo";                                // discard the field typed above
        Assert.True(d.CloseDocument(code));
        Assert.Empty(form.Surface.HighlightedControls);
    }

    static IEnumerable<TreeNode> Walk(TreeNode n) => new[] { n }.Concat(n.Nodes.SelectMany(Walk));
}
