using System.Net;
using RadSharp.Design.Code;
using RadSharp.Designer;

namespace RadSharp.Tests;

/// <summary>I8 on the real designer form: the project tree, file operations, git markers, NuGet and properties dialogs, solutions.</summary>
[Collection("Designer")]   // sets PlatformHost.Current, like IdeShellTests
public class ProjectTreeTests
{
    sealed class FakeHost : IPlatformServices
    {
        public Func<Form, string?>? AnswerModal;   // returns the name of the button to press
        public readonly List<string> Captions = [];

        public void ShowForm(Form form) => form.PerformLayout();
        public void RunModal(Form form)
        {
            form.PerformLayout();
            Captions.Add(form.Caption);
            if (AnswerModal?.Invoke(form) is { } button && form.FindControl(button) is Button b) form.Simulate.Click(b);
            if (form.IsModal) form.Close();
        }
        public void CloseForm(Form form) { }
        public void ProcessEventsUntil(Func<bool> done) { }
        public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) => done(null);
        public void PostToUiThread(Action action) => action();
        public void ShowModal(Form form, Action onClosed) { RunModal(form); onClosed(); }
    }

    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-i8ui-").FullName;

    static (DesignerForm D, FakeHost Host, RadProject Project) Start(string name, Action<RadProject>? prepare = null, string? open = null)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var host = new FakeHost();
        PlatformHost.Current = host;
        var project = RadProject.Create(TempDir(), name);
        prepare?.Invoke(project);
        var d = new DesignerForm(open ?? project.ProjectFile, Path.Combine(TempDir(), "designer.json"));
        d.Settings.Editor = EditorKind.Custom;
        d.Settings.CustomEditorCommand = "dotnet --version";
        d.SetBounds(0, 0, 1220, 780);
        d.Dock.Show(DesignerForm.ToolWindows.Project);
        d.PerformLayout();
        return (d, host, project);
    }

    /// <summary>Answers prompts: the name goes into <c>edtValue</c>, a message box gets the given button.</summary>
    static void Answer(FakeHost host, string? name = null, string messageButton = "btnYes", string? choice = null) =>
        host.AnswerModal = form =>
        {
            if (form.FindControl("edtValue") is Edit e) { if (name != null) e.Text = name; return "btnOk"; }
            if (form.FindControl("cboChoice") is ComboBox c)
            {
                if (choice != null) c.ItemIndex = Array.IndexOf(c.Items, choice);
                return "btnOk";
            }
            return messageButton;
        };

    static TreeNode Node(DesignerForm d, string text) => d.ProjectTree.VisibleNodes().First(n => n.Text == text || n.Text.StartsWith(text + "  "));

    static (int X, int Y) RowCenter(DesignerForm d, TreeNode n)
    {
        var tree = d.ProjectTree;
        var r = tree.NodeRect(n);
        return (tree.AbsoluteLeft + r.X + 60 + n.Level * 18, tree.AbsoluteTop + r.Y + r.Height / 2);
    }

    static IEnumerable<MenuItem> Leaves(IEnumerable<MenuItem> items) =>
        items.Where(i => !i.IsSeparator).SelectMany(i => i.HasSubmenu ? Leaves(i.Items) : [i]);

    [Fact]
    public void Tree_has_real_folders_forms_with_nested_code_behind_icons_and_a_command_menu()
    {
        var (d, _, project) = Start("TreeShape", p =>
        {
            var ops = new ProjectFileOps(p);
            ops.NewFile(ops.CreateFolder(p.Directory, "Models"), "Customer", FileTemplate.Class);
            ops.CreateFolder(p.Directory, "Empty");
        });
        var root = Assert.Single(d.ProjectTree.Nodes);
        Assert.Equal("TreeShape", root.Text);
        Assert.True(root.Expanded);
        // Folders first, then files; a form shows under its class name.
        var names = root.Nodes.Select(n => n.Text).ToList();
        Assert.Equal(["Empty", "Models"], names.Take(3).Where(n => n is "Empty" or "Models"));
        Assert.True(names.IndexOf("Models") < names.IndexOf("MainForm   (main form)"));
        Assert.Contains("Program.cs", names);
        Assert.Contains("TreeShape.csproj", names);
        var form = root.Nodes.First(n => n.Text.StartsWith("MainForm"));
        Assert.Equal(["MainForm.cs"], form.Nodes.Select(n => n.Text));   // the code-behind is nested, not listed beside it
        Assert.DoesNotContain(root.Nodes, n => n.Text == "MainForm.cs");
        Assert.Equal(["Customer.cs"], root.Nodes.First(n => n.Text == "Models").Nodes.Select(n => n.Text));
        Assert.All(ProjectTreeBuilder.Walk(d.ProjectTree.Nodes), n => Assert.NotNull(n.Icon));

        // Right-click selects the row and opens a menu made of commands only.
        var (x, y) = RowCenter(d, Node(d, "Models"));
        d.Simulate.Click(x, y, MouseButton.Right);
        Assert.Equal("Models", d.ProjectTree.Selected!.Text);
        var menu = Assert.IsType<MenuPopup>(d.Popups[^1]);
        Assert.All(Leaves(menu.Items), i => Assert.NotNull(d.Commands.CommandOf(i)));
        Assert.Contains(menu.Items, i => i.Caption == "New Class…");
        Assert.Contains(menu.Items, i => i.Caption == "Reveal in File Manager");
        d.CloseAllPopups();
        Assert.Null(project.FormFileFor("Nope"));
    }

    [Fact]
    public void New_folder_and_new_files_from_templates_through_commands()
    {
        var (d, host, project) = Start("NewFiles");
        var root = d.SelectedProjectItem ?? ProjectTreeBuilder.ItemOf(d.ProjectTree.Nodes[0])!;
        Answer(host, "Domain");
        d.Commands.Execute("Project.NewFolder", root);
        var domain = Path.Combine(project.Directory, "Domain");
        Assert.True(Directory.Exists(domain));
        Assert.Equal(domain, d.SelectedProjectItem!.Path);

        Answer(host, "Order");
        d.Commands.Execute("Project.NewClass", d.SelectedProjectItem);   // into the selected folder
        var order = Path.Combine(domain, "Order.cs");
        Assert.Contains("namespace NewFiles.Domain;", File.ReadAllText(order));
        Assert.Equal(order, d.ActiveDocument!.Path);
        Assert.NotNull(Node(d, "Order.cs"));

        Answer(host, "IOrderRepo");
        d.Commands.Execute("Project.NewInterface", ProjectTreeBuilder.ItemOf(Node(d, "Domain")));
        Assert.Contains("public interface IOrderRepo", File.ReadAllText(Path.Combine(domain, "IOrderRepo.cs")));

        Answer(host, "AboutForm");
        d.Commands.Execute("Project.NewFormFile", ProjectTreeBuilder.ItemOf(Node(d, "Domain")));
        Assert.IsType<FormDocument>(d.ActiveDocument);
        Assert.Equal(Path.Combine(domain, "AboutForm.radform.json"), d.ActiveDocument!.Path);
        Assert.Equal(["AboutForm.cs"], Node(d, "AboutForm").Nodes.Select(n => n.Text));

        Answer(host, "Order");   // a clash is reported, not thrown
        d.Commands.Execute("Project.NewClass", ProjectTreeBuilder.ItemOf(Node(d, "Domain")));
        Assert.Equal("Error", host.Captions[^1]);
        Assert.DoesNotContain(host.Captions, c => c.Contains("Frame"));   // no Frame template: the framework has no frame form files yet
    }

    [Fact]
    public void Renaming_a_form_renames_both_files_the_class_and_updates_the_open_tabs()
    {
        var (d, host, project) = Start("RenameUi");
        var form = d.ActiveForm!;
        var code = d.ToggleFormCode() as CodeDocument;
        Assert.NotNull(code);
        Answer(host, "ShellForm");
        d.Commands.Execute("Project.Rename", ProjectTreeBuilder.ItemOf(Node(d, "MainForm")));

        Assert.Equal(Path.Combine(project.Directory, "ShellForm.radform.json"), form.Path);
        Assert.Equal(Path.Combine(project.Directory, "ShellForm.cs"), code!.Path);
        Assert.Equal("ShellForm", form.Title);
        Assert.Equal("ShellForm.cs", code.Title);
        Assert.Equal(code.Path, form.CodeFile);
        Assert.Contains("class ShellForm", code.Text);   // the open buffer was reloaded after the rewrite
        Assert.False(File.Exists(Path.Combine(project.Directory, "MainForm.radform.json")));
        Assert.Contains("new ShellForm()", File.ReadAllText(Path.Combine(project.Directory, "Program.cs")));
        Assert.Equal(["ShellForm", "ShellForm.cs"], d.Documents.Documents.Select(x => x.Title));
        Assert.Equal(code.FormFile, form.Path);
        Assert.Equal(["ShellForm.cs"], Node(d, "ShellForm").Nodes.Select(n => n.Text));
        // The session remembers the new paths.
        Assert.Contains(IdeSession.Load(d.Paths.Session).Documents, doc => doc.Path == form.Path);
    }

    [Fact]
    public void Dragging_a_file_onto_a_folder_moves_it_offers_the_namespace_fix_and_the_tab_follows()
    {
        var (d, host, project) = Start("DragMove", p =>
        {
            var ops = new ProjectFileOps(p);
            ops.CreateFolder(p.Directory, "Models");
            ops.NewFile(p.Directory, "Customer", FileTemplate.Class);
        });
        var customer = Path.Combine(project.Directory, "Customer.cs");
        Answer(host, messageButton: "btnYes");
        d.OpenProjectItem(ProjectTreeBuilder.ItemOf(Node(d, "Customer.cs"))!, preview: false);
        var doc = Assert.IsType<CodeDocument>(d.ActiveDocument);

        var from = RowCenter(d, Node(d, "Customer.cs"));
        var to = RowCenter(d, Node(d, "Models"));
        d.Simulate.Drag(from.X, from.Y, to.X, to.Y, steps: 6);

        var moved = Path.Combine(project.Directory, "Models", "Customer.cs");
        Assert.True(File.Exists(moved));
        Assert.False(File.Exists(customer));
        Assert.Contains("namespace DragMove.Models;", File.ReadAllText(moved));
        Assert.Equal(moved, doc.Path);
        Assert.Contains("namespace DragMove.Models;", doc.Text);
        Assert.Contains("Confirm", host.Captions);   // the offer
        Assert.Contains(Node(d, "Models").Nodes, n => n.Text == "Customer.cs");
        Assert.Equal(moved, d.SelectedProjectItem!.Path);

        // Declining the offer moves without touching the namespace; a folder can't be dropped into itself.
        Answer(host, messageButton: "btnNo");
        Assert.True(d.MoveProjectItem(moved, project.Directory));
        Assert.Contains("namespace DragMove.Models;", File.ReadAllText(customer));
        Assert.False(d.MoveProjectItem(Path.Combine(project.Directory, "Models"), Path.Combine(project.Directory, "Models")));
    }

    [Fact]
    public void Move_command_picks_a_folder_and_cancel_aborts()
    {
        var (d, host, project) = Start("MoveCmd", p =>
        {
            var ops = new ProjectFileOps(p);
            ops.CreateFolder(p.Directory, "Sub");
            ops.NewFile(p.Directory, "Thing", FileTemplate.Class);
        });
        Answer(host, choice: "Sub");
        Answer(host, messageButton: "btnCancel", choice: "Sub");   // the namespace offer is cancelled: nothing moves
        d.Commands.Execute("Project.Move", ProjectTreeBuilder.ItemOf(Node(d, "Thing.cs")));
        Assert.True(File.Exists(Path.Combine(project.Directory, "Thing.cs")));

        Answer(host, messageButton: "btnYes", choice: "Sub");
        d.Commands.Execute("Project.Move", ProjectTreeBuilder.ItemOf(Node(d, "Thing.cs")));
        Assert.True(File.Exists(Path.Combine(project.Directory, "Sub", "Thing.cs")));
        Assert.Contains("MoveCmd.Sub", File.ReadAllText(Path.Combine(project.Directory, "Sub", "Thing.cs")));
    }

    [Fact]
    public void Deleting_a_form_removes_both_files_and_closes_its_tabs()
    {
        var (d, host, project) = Start("DeleteUi");
        var second = d.NewForm()!;
        d.ToggleFormCode();
        Assert.Equal(3, d.Documents.Documents.Count);
        Answer(host, messageButton: "btnNo");
        d.Commands.Execute("Project.Delete", ProjectTreeBuilder.ItemOf(Node(d, "Form2")));
        Assert.True(File.Exists(second.Path));   // "No" keeps it
        Answer(host, messageButton: "btnYes");
        d.Commands.Execute("Project.Delete", ProjectTreeBuilder.ItemOf(Node(d, "Form2")));
        Assert.False(File.Exists(second.Path));
        Assert.False(File.Exists(Path.Combine(project.Directory, "Form2.cs")));
        Assert.Equal(["MainForm"], d.Documents.Documents.Select(x => x.Title));
        Assert.DoesNotContain(d.ProjectTree.VisibleNodes(), n => n.Text.StartsWith("Form2"));
    }

    [Fact]
    public void Reveal_uses_the_handler_with_the_selected_path()
    {
        var (d, _, project) = Start("RevealUi");
        var seen = new List<string>();
        d.RevealHandler = p => { seen.Add(p); return true; };
        d.Commands.Execute("Project.Reveal", ProjectTreeBuilder.ItemOf(Node(d, "Program.cs")));
        d.Commands.Execute("Project.Reveal", ProjectTreeBuilder.ItemOf(d.ProjectTree.Nodes[0]));
        Assert.Equal([Path.Combine(project.Directory, "Program.cs"), project.ProjectFile], seen);
    }

    [Fact]
    public void Git_markers_and_compare_with_head_in_a_repository_and_nothing_outside_one()
    {
        if (!ProjectToolsTests.GitAvailable()) return;
        var (d, host, project) = Start("GitUi", p =>
        {
            ProjectToolsTests.Git(p.Directory, "init", "-q");
            ProjectToolsTests.Git(p.Directory, "config", "user.email", "t@example.com");
            ProjectToolsTests.Git(p.Directory, "config", "user.name", "t");
            ProjectToolsTests.Git(p.Directory, "add", ".");
            ProjectToolsTests.Git(p.Directory, "commit", "-q", "-m", "init");
            File.AppendAllText(Path.Combine(p.Directory, "Program.cs"), "// changed\n");
            File.WriteAllText(Path.Combine(p.Directory, "Fresh.cs"), "class Fresh { }\n");
            Directory.CreateDirectory(Path.Combine(p.Directory, "Sub"));
            File.WriteAllText(Path.Combine(p.Directory, "Sub", "Deep.cs"), "class Deep { }\n");
        });
        d.RefreshGit(sync: true);
        Assert.Equal(GitState.Modified, d.GitStateOf(Path.Combine(project.Directory, "Program.cs")));
        Assert.Equal("Program.cs  M", Node(d, "Program.cs").Text);
        Assert.Equal("Fresh.cs  U", Node(d, "Fresh.cs").Text);
        Assert.Equal("Sub  •", Node(d, "Sub").Text);   // a folder holding changes
        Assert.Equal("MainForm.cs", Node(d, "MainForm").Nodes[0].Text);   // clean files carry no marker

        // Compare with HEAD: a read-only unified diff document, for the tree's file or the active one.
        var diff = d.CompareWithHead(ProjectTreeBuilder.ItemOf(Node(d, "Program.cs")));
        Assert.NotNull(diff);
        Assert.Equal("Program.cs (HEAD)", diff!.Title);
        Assert.Contains("+// changed", diff.Text);
        Assert.StartsWith("--- a/Program.cs\n+++ b/Program.cs\n", diff.Text);
        Assert.Same(diff, d.ActiveDocument);
        Assert.Null(diff.Path);   // not a file: not saved or restored
        var untracked = d.CompareWithHead(ProjectTreeBuilder.ItemOf(Node(d, "Fresh.cs")));
        Assert.Contains("--- /dev/null", untracked!.Text);
        Assert.Null(d.CompareWithHead(ProjectTreeBuilder.ItemOf(Node(d, "MainForm").Nodes[0])));   // unchanged: nothing to show

        // Committing everything clears the markers on the next refresh.
        ProjectToolsTests.Git(project.Directory, "add", ".");
        ProjectToolsTests.Git(project.Directory, "commit", "-q", "-m", "all");
        d.RefreshGit(sync: true);
        Assert.Equal("Program.cs", Node(d, "Program.cs").Text);
        Assert.Equal(GitState.Clean, d.GitStateOf(Path.Combine(project.Directory, "Program.cs")));

        // A project outside any repository: no markers, and the command is disabled.
        var (plain, _, _) = Start("NoGit");
        plain.RefreshGit(sync: true);
        Assert.DoesNotContain(ProjectTreeBuilder.Walk(plain.ProjectTree.Nodes), n => n.Text.EndsWith("  U") || n.Text.EndsWith("  M"));
        Assert.False(plain.Commands["Project.CompareWithHead"].Enabled);
    }

    // ---- NuGet and properties ----

    sealed class FakeHandler : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var json = request.RequestUri!.AbsolutePath.EndsWith("index.json")
                ? """{"resources":[{"@id":"https://search.test/query","@type":"SearchQueryService"}]}"""
                : """{"totalHits":1,"data":[{"id":"Serilog","version":"4.0.0","description":"Logging\nmore","totalDownloads":2500000,"versions":[{"version":"3.1.0"},{"version":"4.0.0"}]}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    sealed class RecordingRunner(RadProject project) : IPackageRunner
    {
        public readonly List<string> Calls = [];
        public Task<int> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, Action<string> onLine, CancellationToken cancel = default)
        {
            Calls.Add(string.Join(' ', arguments.Skip(2).Prepend(arguments[0])));
            onLine("restore done");
            if (arguments[0] == "add")
                File.WriteAllText(project.ProjectFile, File.ReadAllText(project.ProjectFile).Replace("</ItemGroup>", $"  <PackageReference Include=\"{arguments[3]}\" Version=\"{arguments[5]}\" />\n  </ItemGroup>"));
            else
                File.WriteAllText(project.ProjectFile, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(project.ProjectFile), $"[ \\t]*<PackageReference Include=\"{arguments[3]}\"[^>]*/>\\r?\\n?", ""));
            return Task.FromResult(0);
        }
    }

    [Fact]
    public async Task NuGet_dialog_searches_through_the_service_index_with_a_generic_agent_and_adds_and_removes()
    {
        var (d, _, project) = Start("NuGetUi");
        var handler = new FakeHandler();
        var runner = new RecordingRunner(project);
        var output = new List<string>();
        var dialog = new NuGetDialog(project, new NuGetService(runner, handler), output.Add, d);
        Assert.Contains(dialog.Installed, p => p.Id == "RadSharp.Platform.Sdl");

        dialog.Query = "serilog logging";
        await dialog.SearchAsync();
        Assert.Equal("Serilog", Assert.Single(dialog.Results).Id);
        Assert.Equal(["https://api.nuget.org/v3/index.json", "https://search.test/query?q=serilog%20logging&prerelease=false&skip=0&take=25&semVerLevel=2.0.0"],
            handler.Requests.Select(r => r.RequestUri!.AbsoluteUri));
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("RADSharp-IDE", r.Headers.UserAgent.ToString());
            Assert.Null(r.Headers.Authorization);
            Assert.DoesNotContain(Environment.UserName, r.Headers.UserAgent.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Null(r.Content);
        });

        await dialog.AddSelectedAsync();   // the newest version is preselected
        Assert.Equal("add package Serilog --version 4.0.0", runner.Calls[0]);
        Assert.Contains(dialog.Installed, p => p.Id == "Serilog" && p.Version == "4.0.0");
        Assert.Contains(output, l => l.StartsWith("dotnet add ") && l.EndsWith("package Serilog --version 4.0.0"));
        Assert.Contains("restore done", output);

        dialog.SelectInstalled("Serilog");
        await dialog.RemoveSelectedAsync();
        Assert.DoesNotContain(dialog.Installed, p => p.Id == "Serilog");
        Assert.Equal("remove package Serilog", runner.Calls[1]);
        Assert.Contains("done", dialog.Status);
    }

    [Fact]
    public void Properties_dialog_edits_the_csproj_and_keeps_everything_else()
    {
        var (d, host, project) = Start("PropsUi");
        var before = File.ReadAllText(project.ProjectFile);
        host.AnswerModal = form =>
        {
            Assert.Equal("1.0.0", ((Edit)form.FindControl("edtVersion")!).Placeholder);
            ((Edit)form.FindControl("edtVersion")!).Text = "3.2.1";
            ((Edit)form.FindControl("edtAssemblyName")!).Text = "My App";
            ((Edit)form.FindControl("edtRuntimeIdentifiers")!).Text = "win-x64;linux-x64";
            ((CheckBox)form.FindControl("chkPublishAot")!).Checked = false;
            return "btnOk";
        };
        d.ShowProjectProperties();
        var after = File.ReadAllText(project.ProjectFile);
        var props = CsprojEditor.Parse(after).Read();
        Assert.Equal("3.2.1", props.Version);
        Assert.Equal("My App", props.AssemblyName);
        Assert.Equal("win-x64;linux-x64", props.RuntimeIdentifiers);
        Assert.False(props.PublishAot);
        Assert.Equal("PropsUi", props.RootNamespace);
        Assert.Contains("<PackageReference Include=\"RadSharp.Platform.Sdl\"", after);
        Assert.Contains("<EnableAotAnalyzer>true</EnableAotAnalyzer>", after);

        // Cancel, or OK without edits, leaves the file alone.
        host.AnswerModal = _ => "btnCancel";
        d.ShowProjectProperties();
        Assert.Equal(after, File.ReadAllText(project.ProjectFile));
        host.AnswerModal = _ => "btnOk";
        d.ShowProjectProperties();
        Assert.Equal(after, File.ReadAllText(project.ProjectFile));
        Assert.NotEqual(before, after);
    }

    // ---- Solutions ----

    [Fact]
    public void Solution_lists_each_project_and_the_active_document_picks_the_target_project()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var host = new FakeHost();
        PlatformHost.Current = host;
        var dir = TempDir();
        var app = RadProject.Create(Path.Combine(dir, "App"), "App");
        var lib = RadProject.Create(Path.Combine(dir, "Lib"), "Lib");
        File.WriteAllText(Path.Combine(lib.Directory, "Util.cs"), "namespace Lib;\npublic static class Util { }\n");
        var slnx = Path.Combine(dir, "All.slnx");
        File.WriteAllText(slnx, "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");

        var d = new DesignerForm(slnx, Path.Combine(TempDir(), "designer.json"));
        d.Settings.Editor = EditorKind.Custom;
        d.Settings.CustomEditorCommand = "dotnet --version";
        d.SetBounds(0, 0, 1220, 780);
        d.Dock.Show(DesignerForm.ToolWindows.Project);
        d.PerformLayout();

        Assert.Equal(slnx, d.SolutionFilePath);
        Assert.Equal(["App", "Lib"], d.Projects.Select(p => p.Name));
        Assert.Equal(["App", "Lib"], d.ProjectTree.Nodes.Select(n => n.Text));   // each project is a top-level node
        Assert.True(d.ProjectTree.Nodes[0].Expanded);
        Assert.False(d.ProjectTree.Nodes[1].Expanded);
        Assert.Equal("App", d.Project!.Name);
        Assert.Equal("All", ((Label)d.FindControl("lblProject")!).Caption);

        // A file of the library: the library becomes the project Run/Build/New Form work on.
        d.OpenCodeFile(Path.Combine(lib.Directory, "Util.cs"));
        Assert.Equal("Lib", d.Project!.Name);
        d.OpenFormFile(app.FormFileFor("MainForm")!);
        Assert.Equal("App", d.Project!.Name);
        d.OpenCodeFile(Path.Combine(lib.Directory, "Util.cs"));
        var added = d.NewForm()!;
        Assert.Equal(lib.Directory, Path.GetDirectoryName(added.Path));
        Assert.Equal("Lib", d.ProjectOf(added.Path)!.Name);
        Assert.Equal(Path.Combine(lib.Directory, "Form2.cs"), added.CodeFile);
        Assert.Contains("namespace Lib;", File.ReadAllText(added.CodeFile));

        // The tree acts on the right project: a new class lands in Lib's folder with Lib's namespace.
        Answer(host, "Extra");
        d.Commands.Execute("Project.NewClass", ProjectTreeBuilder.ItemOf(d.ProjectTree.Nodes[1]));
        Assert.Contains("namespace Lib;", File.ReadAllText(Path.Combine(lib.Directory, "Extra.cs")));

        // The session stores the solution, so the next start reopens all of it.
        Assert.Equal(slnx, IdeSession.Load(d.Paths.Session).Project);
        Assert.True(d.CloseProject());
        Assert.Null(d.SolutionFilePath);
        Assert.Empty(d.Projects);
    }
}
