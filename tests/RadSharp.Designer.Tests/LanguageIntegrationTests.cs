using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using RadSharp.CodeEditing;
using RadSharp.Design.Code;
using RadSharp.Designer;
using RadSharp.Threading;

namespace RadSharp.Tests;

/// <summary>
/// The code editor and the Roslyn language service in the IDE (I3 phase B, the editor half of I2), on a copy of the
/// SettingsDialog sample. The host queues posted work like the real UI thread does, and the tests pump it until a
/// condition holds; waits are bounded and event-driven (every post and every awaited task wakes the pump), never sleeps.
/// </summary>
[Collection("Designer")]   // sets PlatformHost.Current and LanguageHost's statics
public sealed class LanguageIntegrationTests : IDisposable
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>A platform host with a real UI queue: posts wait until the test pumps them.</summary>
    sealed class QueueHost : IPlatformServices
    {
        readonly ConcurrentQueue<Action> _queue = new();
        readonly SemaphoreSlim _wake = new(0);
        public readonly List<string> Modals = [];
        public Func<Form, string?>? Answer;   // the name of the button to press in a modal

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

        /// <summary>Runs posted work until <paramref name="done"/> holds; fails after the timeout.</summary>
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

        /// <summary>Pumps until <paramref name="task"/> completed (its completion wakes the pump).</summary>
        public void PumpUntil(Task task, string what)
        {
            task.ContinueWith(_ => _wake.Release(), TaskScheduler.Default);
            PumpUntil(() => task.IsCompleted, what);
        }
    }

    readonly SynchronizationContext? _oldContext = SynchronizationContext.Current;
    readonly QueueHost _host = new();

    public LanguageIntegrationTests()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        PlatformHost.Current = _host;
        LanguageHost.Enabled = true;
        LanguageHost.DefaultOptions = new RadSharp.Ide.Language.ProjectWorkspaceOptions { WatchFiles = false, Restore = RadSharp.Ide.Language.RestoreMode.Never };
        LanguageHost.DiagnosticsDelay = TimeSpan.FromMilliseconds(50);
    }

    public void Dispose()
    {
        LanguageHost.Enabled = false;
        LanguageHost.DefaultOptions = null;
        LanguageHost.DiagnosticsDelay = TimeSpan.FromMilliseconds(300);
        SynchronizationContext.SetSynchronizationContext(_oldContext);
    }

    static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>A copy of samples/SettingsDialog in a temp folder, with a self-contained .csproj (no project references, so nothing restores).</summary>
    static RadProject SampleCopy(Action<string>? edit = null)
    {
        var dir = Directory.CreateTempSubdirectory("radsharp-lang-").FullName;
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
        edit?.Invoke(dir);
        return new RadProject(Path.Combine(dir, "SettingsDialog.csproj"));
    }

    (DesignerForm Designer, RadProject Project, LanguageHost Host) Start(Action<string>? edit = null)
    {
        // As on the real UI thread: the editor marshals completion results through the UI context. (Set here, in the
        // test body: xunit installs its own context between the constructor and the test method.)
        SynchronizationContext.SetSynchronizationContext(new UiSynchronizationContext(_host.PostToUiThread));
        var project = SampleCopy(edit);
        var settings = Path.Combine(Directory.CreateTempSubdirectory("radsharp-lang-settings-").FullName, "designer.json");
        var d = new DesignerForm(project.ProjectFile, settings);
        d.Settings.Editor = EditorKind.Custom;
        d.Settings.CustomEditorCommand = "dotnet --version";
        d.SetBounds(0, 0, 1220, 780);
        d.PerformLayout();
        var host = Assert.Single(d.LanguageHosts);
        _host.PumpUntil(host.Ready, "the workspace to open");
        _host.PumpUntil(() => host.IsReady || host.Error != null, "the host to be ready");
        Assert.True(host.IsReady, host.Error);
        return (d, project, host);
    }

    static string FileOf(RadProject project, string name) => Path.Combine(project.Directory, name);

    static CodeEditor EditorOf(CodeDocument doc) => Assert.IsType<CodeEditor>(doc.View);

    /// <summary>Puts the caret just after the first occurrence of <paramref name="marker"/> (or at its '|').</summary>
    static void CaretAt(CodeEditor editor, string marker)
    {
        int bar = marker.IndexOf('|');
        var find = marker.Replace("|", "");
        int i = editor.Text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{find}' not found");
        editor.Select(i + (bar >= 0 ? bar : find.Length), 0);
    }

    // ---- The editor in the shell ----

    [Fact]
    public void Opening_a_code_file_shows_the_code_editor_with_semantic_colours()
    {
        var (d, project, host) = Start();
        var code = d.OpenCodeFile(FileOf(project, "SettingsForm.cs"))!;
        var editor = EditorOf(code);
        Assert.Equal(File.ReadAllText(code.Path), code.Text);                                                  // the buffer keeps the file's line breaks
        var lang = host.Find(code)!;
        _host.PumpUntil(() => lang.HasSemanticColours, "semantic colours");

        int line = editor.Snapshot.GetLineFromOffset(editor.Text.IndexOf("public void Load(Settings s)", StringComparison.Ordinal));
        var tokens = lang.GetTokens(editor.Snapshot, line)!;
        Assert.Contains(tokens, t => t.Classification is "record class name" or "class name");   // Settings: a type, not just an identifier
        Assert.Contains(tokens, t => t.Classification == "parameter name");
        Assert.Contains(tokens, t => t.Classification == "keyword");
    }

    [Fact]
    public void A_dropped_control_completes_in_its_code_without_saving()
    {
        var (d, project, _) = Start();
        var form = d.ActiveForm!;
        var button = form.Document.Create(ComponentRegistry.Find("Button")!, form.Document.Form, 20, 150);
        Assert.True(form.Document.Rename(button, "btnGo"));
        Assert.True(form.IsDirty);

        var code = d.OpenCodeFile(FileOf(project, "MainForm.cs"))!;
        var editor = EditorOf(code);
        code.Focus();
        CaretAt(editor, "        ShowSettings();");
        d.Simulate.Key(Key.Enter);
        d.Simulate.Type("btn");
        d.Simulate.Key("Ctrl+Space");
        _host.PumpUntil(() => editor.IsCompletionOpen && editor.CompletionItems.Any(i => i.DisplayText == "btnGo"), "btnGo in completion");
        d.Simulate.Key(Key.Enter);
        Assert.Contains("btnGo", editor.Snapshot.GetLineText(editor.CaretPosition.Line));
        Assert.DoesNotContain("btnGo", File.ReadAllText(form.Path));   // the form wasn't saved
    }

    [Fact]
    public void Deleting_a_handler_in_code_shows_an_error_squiggle_and_live_problems()
    {
        var (d, project, host) = Start();
        var code = d.OpenCodeFile(FileOf(project, "MainForm.cs"))!;
        var editor = EditorOf(code);
        var lang = host.Find(code)!;
        int start = editor.Text.IndexOf("    void btnSettings_Click", StringComparison.Ordinal);
        int end = editor.Text.IndexOf("    void ShowSettings", StringComparison.Ordinal);
        editor.ApplyEdits([TextEdit.Delete(start, end - start)]);

        _host.PumpUntil(() => lang.Decorations?.Items.Any(i => i.Kind == DecorationKind.ErrorSquiggle && i.Message!.Contains("RAD1001")) == true,
            "a RAD1001 squiggle");
        var squiggle = lang.Decorations!.Items.First(i => i.Kind == DecorationKind.ErrorSquiggle);
        Assert.Equal("MainForm", lang.Decorations.Snapshot.GetText(squiggle.Start, squiggle.Length));   // on the class the handler belongs in
        Assert.Contains("btnSettings_Click", squiggle.Message);
        Assert.Contains(lang.Decorations.Items, i => i.Kind == DecorationKind.GutterGlyph && i.Glyph == GutterGlyph.Error);
        // The same diagnostics are the Problems window's "live" source.
        Assert.Contains(d.Problems.Find(DesignerForm.LiveProblemSource)!.Problems, m => m.Id == "RAD1001");
    }

    [Fact]
    public void A_code_error_appears_in_Problems_as_live_without_building()
    {
        var (d, project, host) = Start();
        var code = d.OpenCodeFile(FileOf(project, "SettingsForm.cs"))!;
        code.Text = code.Text.Replace("edtUser.Text = s.User;", "edtUser.Text = s.Missing;");
        long version = host.Flush();
        _host.PumpUntil(host.WaitForDiagnosticsAsync(version), "diagnostics for the edit");
        _host.PumpUntil(() => d.Problems.Find(DesignerForm.LiveProblemSource)?.Problems.Any(m => m.Id == "CS1061") == true, "CS1061 in Problems");
        var problem = d.Problems.Find(DesignerForm.LiveProblemSource)!.Problems.Single(m => m.Id == "CS1061");
        Assert.Equal(code.Path, problem.File, ignoreCase: true);
        Assert.Equal(code.Text.Split('\n').ToList().FindIndex(l => l.Contains("s.Missing")) + 1, problem.Line);
        Assert.Null(d.Problems.Find(DesignerForm.ProblemSources.Build)?.Problems.FirstOrDefault());   // nothing was built
        Assert.Contains("1 error", d.ProblemCounts);
    }

    // ---- Navigation and rename ----

    [Fact]
    public void F12_on_a_type_opens_its_definition_at_the_right_line()
    {
        var (d, project, _) = Start();
        var code = d.OpenCodeFile(FileOf(project, "SettingsForm.cs"))!;
        code.Focus();
        CaretAt(EditorOf(code), "public void Load(Sett|ings s)");
        d.Simulate.Key(Key.F12);
        var main = FileOf(project, "MainForm.cs");
        _host.PumpUntil(() => d.ActiveDocument is CodeDocument c && c.Path == main, "MainForm.cs to open");
        var target = (CodeDocument)d.ActiveDocument!;
        var lines = File.ReadAllLines(main);
        int line = Array.FindIndex(lines, l => l.Contains("public sealed record Settings(")) + 1;
        Assert.Equal((line, lines[line - 1].IndexOf("Settings(", StringComparison.Ordinal) + 1), target.Editor.Caret);

        // On a form, F12 still toggles to the code-behind.
        d.OpenFormFile(FileOf(project, "MainForm.radform.json"));
        d.Simulate.Key(Key.F12);
        Assert.Same(target, d.ActiveDocument);
    }

    [Fact]
    public void F2_renames_across_two_files_in_their_open_buffers()
    {
        var (d, project, _) = Start();
        var main = d.OpenCodeFile(FileOf(project, "MainForm.cs"))!;
        var settings = d.OpenCodeFile(FileOf(project, "SettingsForm.cs"))!;
        settings.Focus();
        CaretAt(EditorOf(settings), "public void Load(Sett|ings s)");
        var mainBefore = main.Text;
        var settingsBefore = settings.Text;

        _host.Answer = form => form switch
        {
            RenameSymbolDialog => SetName(form, "AppSettings"),
            RenamePreviewDialog => "btnApply",
            _ => null,
        };
        d.Simulate.Key(Key.F2);
        _host.PumpUntil(() => main.Text.Contains("record AppSettings(") && settings.Text.Contains("Load(AppSettings s)"), "both buffers renamed");

        Assert.Equal(["Rename", "Rename to AppSettings"], _host.Modals);
        Assert.DoesNotMatch(@"\bSettings\b(?! saved)", main.Text);   // ("Settings saved." is a string, left alone)
        Assert.DoesNotMatch(@"\bSettings\b", settings.Text);
        Assert.True(main.IsDirty && settings.IsDirty);                    // renamed in the buffers, not saved
        Assert.DoesNotContain("AppSettings", File.ReadAllText(main.Path));
        // One undo step per buffer.
        settings.Undo();
        Assert.Equal(settingsBefore, settings.Text);
        Assert.Contains("AppSettings", main.Text);
        main.Undo();
        Assert.Equal(mainBefore, main.Text);

        static string SetName(Form form, string name)
        {
            ((Edit)form.FindControl("edtNewName")!).Text = name;
            return "btnOk";
        }
    }

    // ---- The designer and the open code ----

    [Fact]
    public void Double_clicking_a_control_inserts_the_handler_and_keeps_the_editors_undo_history()
    {
        var (d, project, _) = Start();
        var form = d.ActiveForm!;
        var code = d.OpenCodeFile(FileOf(project, "MainForm.cs"))!;
        var editor = EditorOf(code);
        code.Focus();
        CaretAt(editor, "        ShowSettings();");
        d.Simulate.Type(" // typed");
        var typed = code.Text;
        Assert.True(code.CanUndo);

        d.OpenFormFile(form.Path);
        var button = form.Document.Create(ComponentRegistry.Find("Button")!, form.Document.Form, 20, 150);
        form.Document.Select(null);
        var (sx, sy) = form.Surface.DesignToScreen(button.Left + 10, button.Top + 10);
        d.Simulate.DoubleClick(form.Surface.AbsoluteLeft + sx, form.Surface.AbsoluteTop + sy);

        Assert.Same(code, d.ActiveDocument);
        Assert.Contains("void Button1_Click(Control sender)", code.Text);
        Assert.Contains(" // typed", code.Text);
        d.Simulate.Key("Ctrl+Z");                       // just the stub
        Assert.Equal(typed, code.Text);
        d.Simulate.Key("Ctrl+Z");                       // then what was typed before
        Assert.DoesNotContain(" // typed", code.Text);
    }

    [Fact]
    public void Renaming_a_control_in_the_inspector_renames_its_uses_semantically()
    {
        // A same-named local in another class: a textual rename would change it, a semantic one doesn't.
        var (d, project, host) = Start(dir =>
        {
            var file = Path.Combine(dir, "SettingsForm.cs");
            File.WriteAllText(file, File.ReadAllText(file).Replace("        edtUser.Text = s.User;", "        var lblSummary = s.User;\n        edtUser.Text = lblSummary;"));
        });
        var main = d.OpenCodeFile(FileOf(project, "MainForm.cs"))!;
        var form = d.OpenFormFile(FileOf(project, "MainForm.radform.json"))!;
        host.Flush();
        form.Document.Select(form.Document.Form.FindControl("lblSummary"));
        var name = (Edit)d.FindControl("edtName")!;
        name.Focus();
        name.Text = "lblStatus";
        d.Simulate.Key(Key.Enter);

        Assert.NotNull(d.PendingControlRename);
        _host.PumpUntil(d.PendingControlRename!, "the semantic rename");
        _host.PumpUntil(() => main.Text.Contains("lblStatus.Caption"), "the open buffer renamed");
        Assert.DoesNotContain("lblSummary", main.Text);
        Assert.False(main.IsDirty);                                                          // clean before, so saved
        Assert.Contains("var lblSummary = s.User;", File.ReadAllText(FileOf(project, "SettingsForm.cs")));   // untouched
        Assert.Contains("\"lblStatus\"", File.ReadAllText(form.Path));
        Assert.DoesNotContain(d.OutputLines, l => l.Contains("renaming textually"));
    }

    // ---- Small parts ----

    [Theory]
    [InlineData("a\nb\nc\n", "a\nB\nc\n")]
    [InlineData("class A\r\n{\r\n}\r\n", "class A\r\n{\r\n    void X() { }\r\n}\r\n")]
    [InlineData("x foo y\nfoo\nz foo\n", "x bar y\nbar\nz bar\n")]
    [InlineData("", "new")]
    [InlineData("old", "")]
    public void Buffer_diff_edits_turn_one_text_into_the_other(string before, string after)
    {
        var edits = BufferDiff.Edits(before, after);
        var text = before;
        foreach (var e in edits.OrderByDescending(e => e.Start)) text = text[..e.Start] + e.NewText + text[e.End..];
        Assert.Equal(after, text);
    }

    [Fact]
    public void A_handler_stub_is_one_small_edit()
    {
        var before = "class A\n{\n    void B() { }\n}\n";
        var after = "class A\n{\n    void B() { }\n\n    void C()\n    {\n    }\n}\n";
        var edit = Assert.Single(BufferDiff.Edits(before, after));
        Assert.Equal(0, edit.Length);
        Assert.Equal("\n    void C()\n    {\n    }\n", edit.NewText);
    }

    [Fact]
    public void Json_tokens_tell_names_from_values()
    {
        var tokens = JsonTokenSource.Tokenize("  \"name\": \"btnOk\", \"width\": 90, \"default\": true");
        Assert.Equal(["property name", "punctuation", "string", "punctuation", "property name", "punctuation", "number", "punctuation", "property name", "punctuation", "keyword"],
            tokens.Select(t => t.Classification));
    }
}
