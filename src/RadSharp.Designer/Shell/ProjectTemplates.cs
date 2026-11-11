using RadSharp.Design;
using RadSharp.Design.Code;

namespace RadSharp.Designer;

/// <summary>A starting point for File › New Application and the welcome page: what <see cref="Create"/> writes into the new folder.</summary>
public sealed record ProjectTemplate(string Id, string Name, string Description, Func<string, string, string?, RadProject> Create);

/// <summary>
/// The new-project templates, built on <see cref="RadProject.Create"/> and <see cref="RadProject.AddForm"/>: every one is a
/// complete project that compiles (the tests run <see cref="LiveSession.Compile(RadProject, IReadOnlyDictionary{string, string}?)"/> on each).
/// </summary>
public static class ProjectTemplates
{
    public const string EmptyId = "empty", SingleFormId = "single-form", TwoFormsId = "two-forms";

    public static IReadOnlyList<ProjectTemplate> All { get; } =
    [
        new(EmptyId, "Empty", "A blank main form and Program.cs.", (dir, name, pkg) => RadProject.Create(dir, name, packageSource: pkg)),
        new(SingleFormId, "Single form", "A main form with a label and a button wired to a handler.", CreateSingleForm),
        new(TwoFormsId, "Two forms with a dialog", "A main form that opens a modal About dialog.", CreateTwoForms),
    ];

    public static ProjectTemplate Empty => All[0];
    public static ProjectTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    static RadProject CreateSingleForm(string dir, string name, string? pkg)
    {
        var project = RadProject.Create(dir, name, packageSource: pkg);
        var main = project.FormFiles.Single();
        EditForm(main, doc =>
        {
            var label = Add(doc, "Label", "lblGreeting", 24, 24, 400, 28);
            ((Label)label).Caption = "Hello from RADSharp";
            var button = Add(doc, "Button", "btnGreet", 24, 68, 140, 32);
            ((Button)button).Caption = "Say hello";
            button.EventHandlers["onClick"] = "btnGreet_Click";
        });
        AddHandler(main, "MainForm", "btnGreet_Click", "lblGreeting.Caption = \"Hello, world!\";");
        return project;
    }

    static RadProject CreateTwoForms(string dir, string name, string? pkg)
    {
        var project = RadProject.Create(dir, name, packageSource: pkg);
        var main = project.FormFiles.Single();
        EditForm(main, doc =>
        {
            var button = Add(doc, "Button", "btnAbout", 24, 24, 160, 32);
            ((Button)button).Caption = "About…";
            button.EventHandlers["onClick"] = "btnAbout_Click";
        });
        AddHandler(main, "MainForm", "btnAbout_Click", "new AboutDialog().ShowModal();");

        var dialog = project.AddForm("AboutDialog", "About");
        EditForm(dialog, doc =>
        {
            doc.Form.SetBounds(0, 0, 360, 180);
            doc.Form.DesignWidth = 360;
            doc.Form.DesignHeight = 180;
            var label = Add(doc, "Label", "lblAbout", 24, 24, 310, 28);
            ((Label)label).Caption = name + " — built with RADSharp";
            var ok = Add(doc, "Button", "btnOk", 244, 124, 90, 32);
            ((Button)ok).Caption = "OK";
            ((Button)ok).Default = true;
            ((Button)ok).ModalResult = ModalResult.Ok;
        });
        return project;
    }

    static Control Add(DesignDocument doc, string cls, string name, int x, int y, int w, int h)
    {
        var c = doc.Create(ComponentRegistry.Find(cls)!, doc.Form, x, y);
        doc.Rename(c, name);
        c.SetBounds(x, y, w, h);
        return c;
    }

    static void EditForm(string formFile, Action<DesignDocument> edit)
    {
        var doc = new DesignDocument();
        doc.Load(File.ReadAllText(formFile));
        edit(doc);
        File.WriteAllText(formFile, doc.Save());
    }

    static void AddHandler(string formFile, string className, string handler, string body)
    {
        var code = RadProject.CodeFileFor(formFile);
        var (source, line) = CodeIntegration.EnsureHandler(File.ReadAllText(code), className, handler, "Control sender");
        var lines = source.Split('\n').ToList();
        lines[line - 1] = "        " + body;
        File.WriteAllText(code, string.Join('\n', lines));
    }
}
