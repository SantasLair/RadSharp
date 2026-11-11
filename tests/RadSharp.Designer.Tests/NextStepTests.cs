using RadSharp.Design.Code;
using RadSharp.Designer;
using static RadSharp.Tests.PolishSupport;

namespace RadSharp.Tests;

/// <summary>I9, "no dead ends": the clear error cases end in an action (open the file, retry, open settings), not just OK.</summary>
[Collection("Designer")]
public class NextStepTests
{
    static string[] ActionsIn(Form f) => [.. f.Children.OfType<Button>().Where(b => b.Name.StartsWith("btnAction")).Select(b => b.Caption)];

    [Fact]
    public void A_damaged_form_offers_to_open_it_as_text()
    {
        var host = NewHost();
        var project = NewProject();
        var d = Start(project.ProjectFile);
        var extra = Path.Combine(project.Directory, "Broken.radform.json");
        File.WriteAllText(extra, "{ this is not a form");
        string[]? offered = null;
        host.Answer = f => { offered = ActionsIn(f); return "btnAction0"; };
        Assert.Null(d.OpenFormFile(extra));
        Assert.Equal(["Open as text", "Try again"], offered!);
        Assert.IsType<CodeDocument>(d.FindDocument(extra));   // the file is open for fixing
    }

    [Fact]
    public void A_missing_project_offers_retry_removal_and_open_another()
    {
        var host = NewHost();
        var d = Start(null);
        var gone = Path.Combine(TempDir(), "Gone.csproj");
        d.Settings.AddRecent(gone);
        string[]? offered = null;
        host.Answer = f => { offered = ActionsIn(f); return f.FindControl("btnAction1") != null ? "btnAction1" : null; };
        Assert.False(d.OpenProject(gone));
        Assert.Equal(["Try again", "Remove from the recent list", "Open another project…"], offered!);
        Assert.DoesNotContain(gone, d.Settings.RecentProjects);
    }

    [Fact]
    public void A_missing_external_editor_offers_the_built_in_one_or_the_options()
    {
        var host = NewHost();
        var project = NewProject();
        var d = Start(project.ProjectFile);
        d.IdeSettings.CodeNavigation = CodeNavigation.External;
        d.Settings.Editor = EditorKind.Custom;
        d.Settings.CustomEditorCommand = "radsharp-no-such-editor-xyz \"{file}\"";
        var file = Path.Combine(project.Directory, "MainForm.cs");
        string[]? offered = null;
        host.Answer = f => { offered = ActionsIn(f); return "btnAction0"; };
        Assert.False(d.NavigateToCode(file, 1, 1));
        Assert.Equal(["Open it in the built-in editor", "Choose another editor…"], offered!);
        Assert.IsType<CodeDocument>(d.FindDocument(file));
    }

    [Fact]
    public void A_failed_save_says_why_and_offers_to_try_again()
    {
        var host = NewHost();
        var project = NewProject();
        var d = Start(project.ProjectFile);
        var code = d.OpenCodeFile(Path.Combine(project.Directory, "MainForm.cs"))!;
        code.Text += "// edit\n";
        var info = new FileInfo(code.Path);
        info.IsReadOnly = true;
        try
        {
            string[]? offered = null;
            host.Answer = f => { offered = ActionsIn(f); return null; };
            Assert.False(d.Save(code));
            Assert.Equal(["Try again"], offered!);
        }
        finally { info.IsReadOnly = false; }
    }

    [Fact]
    public async Task A_missing_dotnet_explains_what_to_install_and_offers_the_download_page()
    {
        var host = NewHost();
        var project = NewProject();
        var d = Start(project.ProjectFile);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        string[]? offered = null;
        host.Answer = f => { offered = ActionsIn(f); return null; };
        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.GetTempPath());   // no dotnet here
            Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.GetTempPath());
            await d.BuildProjectAsync();
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); Environment.SetEnvironmentVariable("DOTNET_ROOT", null); }
        Assert.Equal(["Open the download page"], offered!);
        Assert.Contains(d.OutputLines, l => l.StartsWith("Could not run dotnet"));
        Assert.False(d.IsBuilding);
    }
}
