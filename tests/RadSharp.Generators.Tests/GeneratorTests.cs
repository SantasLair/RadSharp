using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RadSharp.Generators;

namespace RadSharp.Generators.Tests;

public class GeneratorTests
{
    sealed class InMemoryText(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    static ImmutableArray<MetadataReference> BuildReferences()
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return [.. tpa.Select(p => MetadataReference.CreateFromFile(p)), MetadataReference.CreateFromFile(typeof(Control).Assembly.Location)];
    }

    /// <summary>Runs both generators and returns the final compilation's errors plus the generator diagnostics.</summary>
    static (ImmutableArray<Diagnostic> Generator, ImmutableArray<Diagnostic> Compile, string Generated) Run(string source, params (string Path, string Text)[] forms)
    {
        var compilation = CSharpCompilation.Create("TestApp",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new FormGenerator().AsSourceGenerator(), new MetadataGenerator().AsSourceGenerator()],
            additionalTexts: [.. forms.Select(f => (AdditionalText)new InMemoryText(f.Path, f.Text))],
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        var generated = string.Join("\n", driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()));
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        return (generatorDiagnostics, errors, generated);
    }

    const string MainFormJson = """
        {
          "format": "radform",
          "version": "1.0",
          "root": {
            "class": "Form",
            "name": "frmMain",
            "onResize": "frmMain_Resize",
            "children": [
              { "class": "Button", "name": "btnSave", "caption": "Save", "onClick": "btnSave_Click" },
              { "class": "Panel", "name": "pnlTools", "children": [
                  { "class": "CheckBox", "name": "chkWrap", "onChange": "chkWrap_Change", "onMouseDown": "chkWrap_MouseDown" } ] }
            ]
          }
        }
        """;

    const string MainFormCs = """
        using RadSharp;
        namespace App;
        public partial class MainForm : Form
        {
            public MainForm() => InitializeComponent();
            void frmMain_Resize(Control sender) { }
            void btnSave_Click(Control sender) { btnSave.Caption = "Saved"; }
            void chkWrap_Change(Control sender) { _ = chkWrap.Checked; _ = pnlTools; }
            void chkWrap_MouseDown(Control sender, MouseEventArgs e) { }
        }
        """;

    [Fact]
    public void Form_generates_typed_fields_and_compile_time_bindings()
    {
        var (gen, compile, code) = Run(MainFormCs, ("C:/app/MainForm.radform.json", MainFormJson));
        Assert.Empty(gen);
        Assert.Empty(compile);
        Assert.Contains("internal global::RadSharp.Button btnSave = null!;", code);
        Assert.Contains("internal global::RadSharp.CheckBox chkWrap = null!;", code);
        Assert.Contains("btnSave.Click += btnSave_Click;", code);
        Assert.Contains("chkWrap.Change += chkWrap_Change;", code);
        Assert.Contains("chkWrap.MouseDown += chkWrap_MouseDown;", code);
        Assert.Contains("this.Resized += frmMain_Resize;", code);
        Assert.Contains("namespace App", code);
        Assert.Contains("\"MainForm.radform.json\"", code);
    }

    [Fact]
    public void Missing_handler_is_a_compile_error_pointing_into_the_form_file()
    {
        var source = MainFormCs.Replace("void btnSave_Click(Control sender) { btnSave.Caption = \"Saved\"; }", "");
        var (gen, _, _) = Run(source, ("C:/app/MainForm.radform.json", MainFormJson));
        var d = Assert.Single(gen);
        Assert.Equal("RAD1001", d.Id);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Contains("btnSave_Click", d.GetMessage());
        Assert.Contains("void btnSave_Click(Control sender)", d.GetMessage());
        Assert.EndsWith("MainForm.radform.json", d.Location.GetLineSpan().Path);
        Assert.Equal(8, d.Location.GetLineSpan().StartLinePosition.Line);  // the "onClick" value's line (0-based)
    }

    [Fact]
    public void Unknown_events_and_classes_warn()
    {
        const string json = """
            { "format": "radform", "version": "1.0", "root": { "class": "Form", "children": [
              { "class": "Button", "name": "b", "onExplode": "b_Explode" },
              { "class": "Gizmo", "name": "g" } ] } }
            """;
        var (gen, compile, code) = Run("namespace App; public partial class Dlg : RadSharp.Form { }", ("Dlg.radform.json", json));
        Assert.Contains(gen, d => d.Id == "RAD1003" && d.Severity == DiagnosticSeverity.Warning);
        Assert.Contains(gen, d => d.Id == "RAD1002" && d.Severity == DiagnosticSeverity.Warning);
        Assert.Contains("internal global::RadSharp.Control g = null!;", code);
        Assert.Empty(compile);
    }

    [Fact]
    public void Invalid_json_and_duplicate_names_are_errors()
    {
        var (bad, _, _) = Run("", ("Broken.radform.json", "{ \"format\": \"radform\", \"root\": { \"class\": }"));
        Assert.Equal("RAD1000", Assert.Single(bad).Id);

        const string dup = """
            { "format": "radform", "version": "1.0", "root": { "class": "Form", "children": [
              { "class": "Button", "name": "b" }, { "class": "Label", "name": "b" } ] } }
            """;
        var (gen, _, _) = Run("namespace App; public partial class Dup : RadSharp.Form { }", ("Dup.radform.json", dup));
        Assert.Equal("RAD1005", Assert.Single(gen).Id);
    }

    [Fact]
    public void Form_without_user_class_uses_the_file_name_and_compiles()
    {
        const string json = """{ "format": "radform", "version": "1.0", "root": { "class": "Form", "children": [ { "class": "Label", "name": "lbl" } ] } }""";
        var (gen, compile, code) = Run("", ("About.radform.json", json));
        Assert.Empty(gen);
        Assert.Empty(compile);
        Assert.Contains("partial class About : global::RadSharp.Form", code);
    }

    [Fact]
    public void Menu_items_are_bound_by_path_and_named_items_get_fields()
    {
        const string json = """
            { "format": "radform", "version": "1.0", "root": { "class": "Form", "children": [
              { "class": "MainMenu", "name": "mnuMain", "items": [
                { "caption": "File", "items": [
                  { "caption": "Open", "onClick": "mnuOpen_Click" },
                  { "name": "mniExit", "caption": "Exit", "onClick": "mnuExit_Click" } ] } ] } ] } }
            """;
        const string source = """
            using RadSharp;
            namespace App;
            public partial class Main : Form
            {
                void mnuOpen_Click(MenuItem sender) { }
                void mnuExit_Click(object sender) { _ = mniExit.Caption; }
            }
            """;
        var (gen, compile, code) = Run(source, ("Main.radform.json", json));
        Assert.Empty(gen);
        Assert.Empty(compile);
        Assert.Contains("mnuMain.Items[0].Items[0].Click += mnuOpen_Click;", code);
        Assert.Contains("mniExit = mnuMain.Items[0].Items[1];", code);
        Assert.Contains("mniExit.Click += mnuExit_Click;", code);

        var (missing, _, _) = Run(source.Replace("void mnuOpen_Click(MenuItem sender) { }", ""), ("Main.radform.json", json));
        var d = Assert.Single(missing);
        Assert.Equal("RAD1001", d.Id);
        Assert.Contains("void mnuOpen_Click(MenuItem sender)", d.GetMessage());
        Assert.Contains("menu item \"Open\"", d.GetMessage());
    }

    [Fact]
    public void User_controls_get_metadata_and_bad_published_types_are_reported()
    {
        const string source = """
            using RadSharp;
            namespace App;
            public enum Mood { Happy, VeryHappy }
            public class Gauge : Control
            {
                [Published(Category = "Appearance")] public int Value { get; set; }
                [Published] public Mood Mood { get; set; }
                [Published("onOverflow")] public event NotifyEvent? Overflow;
                [Published] public System.DateTime When { get; set; }
            }
            """;
        var (gen, compile, code) = Run(source);
        var d = Assert.Single(gen);
        Assert.Equal("RAD0002", d.Id);
        Assert.Contains("When", d.GetMessage());
        Assert.Empty(compile);
        Assert.Contains("\"Gauge\", typeof(global::App.Gauge), typeof(global::RadSharp.Control)", code);
        Assert.Contains("global::RadSharp.Converters.Enum<global::App.Mood>()", code);
        Assert.Contains("new global::RadSharp.EventMeta(\"onOverflow\", \"Overflow\", \"Control sender\")", code);
    }
}
