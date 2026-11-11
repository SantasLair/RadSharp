using RadSharp.CodeEditing;
using RadSharp.Design.Code;
using RadSharp.Designer;
using static RadSharp.Tests.PolishSupport;

namespace RadSharp.Tests;

/// <summary>I9 follow-ups that need the Roslyn workspace: the editor context menu, re-opening the workspace after a .csproj change, a moved form.</summary>
[Collection("Designer")]   // sets PlatformHost.Current and LanguageHost's statics
public sealed class PolishLanguageTests : IDisposable
{
    readonly SynchronizationContext? _oldContext = SynchronizationContext.Current;
    readonly PolishQueueHost _host = new();

    public PolishLanguageTests()
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

    /// <summary>A copy of samples/SettingsDialog with a self-contained .csproj (nothing restores).</summary>
    internal static RadProject SampleCopy()
    {
        var dir = Directory.CreateTempSubdirectory("radsharp-i9-lang-").FullName;
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
        return new RadProject(Path.Combine(dir, "SettingsDialog.csproj"));
    }

    (DesignerForm Designer, RadProject Project, LanguageHost Host) Start()
    {
        _host.SetUiContext();
        var project = SampleCopy();
        var d = PolishSupport.Start(project.ProjectFile);
        var host = Assert.Single(d.LanguageHosts);
        _host.PumpUntil(host.Ready, "the workspace to open");
        _host.PumpUntil(() => host.IsReady || host.Error != null, "the host to be ready");
        Assert.True(host.IsReady, host.Error);
        return (d, project, host);
    }

    static CodeEditor EditorOf(CodeDocument doc) => Assert.IsType<CodeEditor>(doc.View);

    // ---- The editor context menu ----

    [Fact]
    public void Right_click_in_a_code_document_offers_the_language_and_edit_commands()
    {
        var (d, project, host) = Start();
        var code = d.OpenCodeFile(Path.Combine(project.Directory, "MainForm.cs"))!;
        var editor = EditorOf(code);
        var lang = host.Find(code)!;
        _host.PumpUntil(() => lang.HasSemanticColours, "semantic colours");
        code.Focus();
        d.PerformLayout();

        int use = editor.Text.IndexOf("ShowSettings();", StringComparison.Ordinal) + 3;
        (int X, int Y) At(int offset) { var p = editor.PointOfOffset(offset); return (editor.AbsoluteLeft + p.X + 2, editor.AbsoluteTop + p.Y + editor.LineHeight / 2); }
        editor.Select(use, 0);
        var (x, y) = At(use);
        d.Simulate.Click(x, y, MouseButton.Right);
        var popup = Assert.IsType<MenuPopup>(d.Popups[^1]);
        string[] expected = ["Go to Definition", "Find All References", "Rename…", "Format Document", "Quick Fixes…", "Toggle Breakpoint", "Cut", "Copy", "Paste", "Undo", "Redo", "Select All"];
        Assert.Equal(expected, popup.Items.Where(i => !i.IsSeparator).Select(i => i.Caption));
        Assert.Equal(["Code.GoToDefinition", "Code.FindReferences", "Code.Rename", "Code.FormatDocument", "Code.QuickFix", "Debug.ToggleBreakpoint", "Edit.Cut", "Edit.Copy", "Edit.Paste", "Edit.Undo", "Edit.Redo", "Edit.SelectAll"],
            popup.Items.Where(i => !i.IsSeparator).Select(i => d.Commands.CommandOf(i)));
        Assert.True(popup.Items.First(i => i.Caption == "Go to Definition").Enabled);
        Assert.True(popup.Items.First(i => i.Caption == "Find All References").Enabled);

        void Choose(string caption)
        {
            var current = (MenuPopup)d.Popups[^1];
            int index = current.Items.ToList().FindIndex(i => i.Caption == caption);
            var r = current.ItemRect(index);
            d.Simulate.Click(current.AbsoluteLeft + r.X + r.Width / 2, current.AbsoluteTop + r.Y + r.Height / 2);
        }

        // Go to Definition: the caret lands on the method's declaration.
        Choose("Go to Definition");
        int declLine = Array.FindIndex(File.ReadAllLines(code.Path), l => l.Contains("void ShowSettings")) + 1;
        _host.PumpUntil(() => code.Editor.Caret.Line == declLine, "the definition");

        // Find All References lists the call and the declaration.
        d.Simulate.Click(editor.AbsoluteLeft + At(editor.Text.IndexOf("void ShowSettings", StringComparison.Ordinal) + 8).X - editor.AbsoluteLeft,
            At(editor.Text.IndexOf("void ShowSettings", StringComparison.Ordinal) + 8).Y, MouseButton.Right);
        Choose("Find All References");
        _host.PumpUntil(() => d.FindResults.Count >= 2, "the references");

        // Copy puts the selection on the clipboard.
        editor.Select(editor.Text.IndexOf("ShowSettings", StringComparison.Ordinal), "ShowSettings".Length);
        var (cx, cy) = At(editor.Text.IndexOf("ShowSettings", StringComparison.Ordinal) + 3);
        d.Simulate.Click(cx, cy, MouseButton.Right);
        Choose("Copy");
        Assert.Equal("ShowSettings", RadSharp.Text.Clipboard.GetText());
    }

    [Fact]
    public void A_non_csharp_document_gets_only_the_edit_commands_in_its_context_menu()
    {
        NewHost();
        var project = NewProject();
        var d = PolishSupport.Start(project.ProjectFile);
        var json = d.OpenCodeFile(project.FormFiles.Single())!;
        var menu = d.BuildCodeContextMenu(json);
        Assert.DoesNotContain(menu.Items, i => i.Caption == "Go to Definition");
        Assert.Contains(menu.Items, i => i.Caption == "Paste");
    }

    // ---- Re-opening the workspace when the .csproj changes ----

    [Fact]
    public void Changing_the_csproj_recreates_the_language_host_and_reattaches_open_documents()
    {
        var (d, project, oldHost) = Start();
        var code = d.OpenCodeFile(Path.Combine(project.Directory, "MainForm.cs"))!;
        var form = d.OpenFormFile(Path.Combine(project.Directory, "MainForm.radform.json"))!;
        Assert.NotNull(oldHost.Find(code));
        Assert.Equal(0, d.CheckProjectFilesChanged());   // nothing changed yet

        // A NuGet add or a properties edit rewrites the project file.
        File.WriteAllText(project.ProjectFile, File.ReadAllText(project.ProjectFile).Replace("</PropertyGroup>", "  <LangVersion>latest</LangVersion>\n  </PropertyGroup>"));
        File.SetLastWriteTimeUtc(project.ProjectFile, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(1, d.CheckProjectFilesChanged());

        var newHost = Assert.Single(d.LanguageHosts);
        Assert.NotSame(oldHost, newHost);
        Assert.Null(oldHost.Find(code));   // the old one let go of the editor
        Assert.NotNull(newHost.Find(code));
        _host.PumpUntil(newHost.Ready, "the new workspace");
        _host.PumpUntil(() => newHost.IsReady || newHost.Error != null, "ready");
        Assert.True(newHost.IsReady, newHost.Error);
        Assert.Contains(newHost.Workspace!.FormFiles, f => string.Equals(f, form.Path, StringComparison.OrdinalIgnoreCase));
        var lang = newHost.Find(code)!;
        _host.PumpUntil(() => lang.HasSemanticColours, "semantic colours from the new workspace");
        Assert.Equal(0, d.CheckProjectFilesChanged());   // the new state is the baseline
        Assert.Contains(d.OutputLines, l => l.Contains("reloading the language service"));
    }

    [Fact]
    public void Reload_Language_Service_is_a_command_and_works_without_a_change()
    {
        var (d, _, oldHost) = Start();
        Assert.True(d.Commands.Execute("Code.ReloadLanguage"));
        Assert.NotSame(oldHost, Assert.Single(d.LanguageHosts));
    }

    // ---- A moved form file ----

    [Fact]
    public void A_moved_form_is_re_keyed_in_the_workspace_at_once()
    {
        var (d, project, host) = Start();
        var form = d.OpenFormFile(Path.Combine(project.Directory, "MainForm.radform.json"))!;
        var oldPath = form.Path;
        _host.PumpUntil(() => host.Workspace!.FormFiles.Any(f => string.Equals(f, oldPath, StringComparison.OrdinalIgnoreCase)), "the form in the workspace");

        var sub = Directory.CreateDirectory(Path.Combine(project.Directory, "Moved")).FullName;
        var newPath = Path.Combine(sub, "MainForm.radform.json");
        File.Move(oldPath, newPath);
        form.MovedTo(newPath);   // no pump: the entry is re-keyed synchronously

        var forms = host.Workspace!.FormFiles;
        Assert.Contains(forms, f => string.Equals(f, newPath, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(forms, f => string.Equals(f, oldPath, StringComparison.OrdinalIgnoreCase));
    }
}
