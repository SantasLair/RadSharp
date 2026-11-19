using RadSharp.Design;
using RadSharp.Design.Code;

namespace RadSharp.Tests;

public class CodeIntegrationTests
{
    const string Code = """
        using RadSharp;

        namespace App;

        public partial class MainForm : Form
        {
            public MainForm() => InitializeComponent();

            void btnOld_Click(Control sender) => btnOld.Caption = "btnOld";
        }
        """;

    [Fact]
    public void Handler_stubs_are_inserted_once_with_matching_indentation()
    {
        var (source, line) = CodeIntegration.EnsureHandler(Code, "MainForm", "btnSave_Click", "Control sender");
        Assert.Contains("    void btnSave_Click(Control sender)\n    {\n\n    }\n}", source.Replace("\r\n", "\n"));
        Assert.Equal("", source.Replace("\r\n", "\n").Split('\n')[line - 1]);   // the caret line is the empty body

        var (again, line2) = CodeIntegration.EnsureHandler(source, "MainForm", "btnSave_Click", "Control sender");
        Assert.Equal(source, again);
        Assert.Equal(line, line2);

        var (paint, _) = CodeIntegration.EnsureHandler(Code, "MainForm", "pbx_Paint", "Control sender, Canvas canvas");
        Assert.Contains("using RadSharp.Drawing;", paint);
    }

    [Fact]
    public void Stubs_use_the_files_dominant_newline_and_have_a_clean_empty_body()
    {
        var crlf = Code.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var (source, line) = CodeIntegration.EnsureHandler(crlf, "MainForm", "pbx_Paint", "Control sender, Canvas canvas");
        Assert.DoesNotMatch("(?<!\r)\n", source);   // no bare \n anywhere, usings included
        Assert.Equal("", source.Split("\r\n")[line - 1]);   // the caret line: empty body, no trailing spaces
        Assert.Contains("using RadSharp.Drawing;\r\n", source);

        var (lf, _) = CodeIntegration.EnsureHandler(Code.Replace("\r\n", "\n"), "MainForm", "x_Click", "Control sender");
        Assert.DoesNotContain("\r", lf);
    }

    [Fact]
    public void Renames_touch_identifiers_but_not_strings()
    {
        var renamed = CodeIntegration.RenameIdentifier(Code, "btnOld", "btnNew");
        Assert.Contains("btnNew.Caption = \"btnOld\"", renamed);
        Assert.Contains("void btnOld_Click", renamed);   // a different identifier
        renamed = CodeIntegration.RenameIdentifier(renamed, "btnOld_Click", "btnNew_Click");
        Assert.Contains("void btnNew_Click", renamed);
    }

    [Fact]
    public void Default_events_follow_delphi()
    {
        Assert.Equal("onClick", CodeIntegration.DefaultEvent(new Button())!.Name);
        Assert.Equal("onChange", CodeIntegration.DefaultEvent(new Edit())!.Name);
        Assert.Equal("onTimer", CodeIntegration.DefaultEvent(new Timer())!.Name);
        Assert.Equal("onPaint", CodeIntegration.DefaultEvent(new PaintBox())!.Name);
        Assert.Equal("onChange", CodeIntegration.DefaultEvent(new ListBox())!.Name);
        Assert.Equal("onChange", CodeIntegration.DefaultEvent(new ListView())!.Name);
        Assert.Equal("onChange", CodeIntegration.DefaultEvent(new StringGrid())!.Name);   // as for the lists
        Assert.Equal("onChange", CodeIntegration.DefaultEvent(new DrawGrid())!.Name);
    }

    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-").FullName;

    /// <summary>
    /// A new project references RadSharp.Platform.Sdl 0.1.0-dev*, which only a local pack provides: resolving it would
    /// query the package sources (nuget.org) and, failing, fall back anyway. These tests are about the forms and the code.
    /// </summary>
    static readonly LiveRunOptions DesignerOnly = new() { UseProjectDependencies = false };

    /// <summary>M11 item 7: designer-created projects opt into the AOT/trim analyzers, not just the template.</summary>
    [Fact]
    public void New_project_enables_aot_and_trim_analyzers()
    {
        var project = RadProject.Create(TempDir(), "AnalyzedApp");
        var csproj = File.ReadAllText(project.ProjectFile);
        Assert.Contains("<EnableAotAnalyzer>true</EnableAotAnalyzer>", csproj);
        Assert.Contains("<EnableTrimAnalyzer>true</EnableTrimAnalyzer>", csproj);
    }

    /// <summary>
    /// Roadmap M8, "done when": new project → drop a Button → double-click → write the handler → F9
    /// (live run: the handler really runs) → (Build: see the slow test below).
    /// </summary>
    [Fact]
    public void New_project_drop_button_double_click_handler_live_run()
    {
        var project = RadProject.Create(TempDir(), "HelloApp");
        Assert.True(File.Exists(Path.Combine(project.Directory, ".github", "workflows", "build.yml")));
        var formFile = project.FormFiles.Single();
        var codeFile = RadProject.CodeFileFor(formFile);

        // Designer: open the form, drop a button, double-click it.
        var doc = new DesignDocument();
        doc.Load(File.ReadAllText(formFile));
        var button = doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 24, 24);
        var ev = CodeIntegration.DefaultEvent(button)!;
        var handler = CodeIntegration.HandlerName(button, doc.Form, ev);
        button.EventHandlers[ev.Name] = handler;
        var (source, line) = CodeIntegration.EnsureHandler(File.ReadAllText(codeFile), "MainForm", handler, ev.Parameters);
        Assert.Equal("Button1_Click", handler);

        // The user types the handler body in their editor.
        var lines = source.Split('\n');
        lines[line - 1] = "        ((Button)sender).Caption = \"Clicked!\";";
        File.WriteAllText(codeFile, string.Join('\n', lines));

        // F9: compile with the designer's unsaved form text and run the form live.
        using var session = LiveSession.Compile(project, new Dictionary<string, string> { [Path.GetFullPath(formFile)] = doc.Save() }, DesignerOnly);
        Assert.True(session.Success, string.Join("\n", session.Messages));
        var live = session.CreateForm("MainForm");
        var liveButton = (Button)live.FindControl("Button1")!;
        live.Simulate.Click(liveButton);
        Assert.Equal("Clicked!", liveButton.Caption);
        Assert.NotSame(typeof(Button), live.GetType());   // the form class came from the live assembly
    }

    [Fact]
    public void Live_run_reports_missing_handlers_and_code_errors_with_locations()
    {
        var project = RadProject.Create(TempDir(), "Broken");
        var formFile = project.FormFiles.Single();
        var doc = new DesignDocument();
        doc.Load(File.ReadAllText(formFile));
        var button = doc.Create(ComponentRegistry.Find("Button")!, doc.Form, 24, 24);
        button.EventHandlers["onClick"] = "Button1_Click";   // no method written yet
        File.AppendAllText(RadProject.CodeFileFor(formFile), "\nclass Oops { int x = ; }\n");

        using var session = LiveSession.Compile(project, new Dictionary<string, string> { [Path.GetFullPath(formFile)] = doc.Save() }, DesignerOnly);
        Assert.False(session.Success);
        Assert.Contains(session.Messages, m => m.Id == "RAD1001" && m.File.EndsWith("MainForm.radform.json"));
        Assert.Contains(session.Messages, m => m.Id.StartsWith("CS") && m.File.EndsWith("MainForm.cs") && m.Line > 1);
    }

    /// <summary>Build: a real NativeAOT publish of a new project (slow; set RADSHARP_SLOW_TESTS=1 and pack first).</summary>
    [Fact]
    public async Task New_project_builds_a_native_executable()
    {
        if (Environment.GetEnvironmentVariable("RADSHARP_SLOW_TESTS") != "1") return;
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var packages = Path.Combine(repo, "artifacts", "packages");
        var project = RadProject.Create(TempDir(), "BuiltApp", packageSource: packages);
        var log = new List<string>();
        var (exit, output) = await ProjectBuilder.PublishAsync(project, log.Add,
            new Dictionary<string, string> { ["NUGET_PACKAGES"] = Path.Combine(TempDir(), "nuget") });
        Assert.True(exit == 0, string.Join("\n", log.TakeLast(30)));
        Assert.True(File.Exists(Path.Combine(output!, OperatingSystem.IsWindows() ? "BuiltApp.exe" : "BuiltApp")));
    }

    [Fact]
    public void New_application_writes_the_package_folder_into_nuget_config_relative_to_the_project()
    {
        var root = TempDir();
        var packages = Path.Combine(root, "artifacts", "packages");
        Directory.CreateDirectory(packages);
        var project = RadProject.Create(Path.Combine(root, "apps", "Rel"), "Rel", packageSource: packages);
        var config = System.Xml.Linq.XDocument.Load(Path.Combine(project.Directory, "nuget.config"));
        var value = config.Descendants("add").Single(a => (string?)a.Attribute("key") == "radsharp-local").Attribute("value")!.Value;
        Assert.Equal("../../artifacts/packages", value);
        Assert.Equal(Path.GetFullPath(packages), Path.GetFullPath(Path.Combine(project.Directory, value)));

        // A URL feed is kept as given.
        var web = RadProject.Create(Path.Combine(root, "apps", "Web"), "Web", packageSource: "https://example.com/v3/index.json");
        Assert.Contains("value=\"https://example.com/v3/index.json\"", File.ReadAllText(Path.Combine(web.Directory, "nuget.config")));
    }
}
