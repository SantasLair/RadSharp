using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RadSharp.Design.Code;

namespace RadSharp.Tests;

public class RenameFormTests
{
    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-").FullName;

    [Theory]
    [InlineData("file")]
    [InlineData("record")]
    [InlineData("required")]
    [InlineData("scoped")]
    [InlineData("var")]
    public void Reserved_type_names_that_do_not_compile_are_rejected(string name)
    {
        var errors = CSharpSyntaxTree.ParseText($"public partial class {name} {{ }}").GetDiagnostics()
            .Concat(CSharpCompilation.Create("t", [CSharpSyntaxTree.ParseText($"public partial class {name} {{ }}")]).GetDiagnostics())
            .Any(d => d.Severity == DiagnosticSeverity.Error);
        if (!errors) return;   // the language accepts it as a class name: nothing to reject
        var project = RadProject.Create(TempDir(), "Reserved");
        Assert.NotNull(project.ValidateFormName(name));
    }

    [Theory]
    [InlineData("Form")]     // class Form : Form is circular
    [InlineData("Button")]   // would shadow RadSharp.Button in every file of the project
    [InlineData("Label")]
    public void Names_of_RadSharp_types_are_rejected(string name)
    {
        var project = RadProject.Create(TempDir(), "Clash");
        Assert.NotNull(project.ValidateFormName(name));
    }

    [Fact]
    public void A_form_can_change_only_the_case_of_its_name()
    {
        var project = RadProject.Create(TempDir(), "CaseApp");
        var form = project.FormFiles.Single();
        var renamed = project.RenameForm(form, "Mainform");
        Assert.Equal("Mainform", RadProject.FormClassName(renamed));
        Assert.Contains(project.FormFiles, f => Path.GetFileName(f) == "Mainform.radform.json");
        Assert.Contains("partial class Mainform : Form", File.ReadAllText(RadProject.CodeFileFor(renamed)));
        Assert.Contains("new Mainform()", File.ReadAllText(Path.Combine(project.Directory, "Program.cs")));
    }

    [Fact]
    public void A_failed_write_restores_files_and_names()
    {
        var project = RadProject.Create(TempDir(), "Rollback");
        var other = Path.Combine(project.Directory, "A.cs");
        File.WriteAllText(other, "class A { object M() => new MainForm(); }");
        var program = Path.Combine(project.Directory, "Program.cs");   // sorts after A.cs
        var programBefore = File.ReadAllText(program);
        var formBefore = File.ReadAllText(project.FormFiles.Single());
        var codeBefore = File.ReadAllText(Path.Combine(project.Directory, "MainForm.cs"));
        File.SetAttributes(program, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<IOException>(() => ThrowIo(() => project.RenameForm(project.FormFiles.Single(), "Shell")));
        }
        finally { File.SetAttributes(program, FileAttributes.Normal); }
        Assert.Equal("class A { object M() => new MainForm(); }", File.ReadAllText(other));
        Assert.Equal(programBefore, File.ReadAllText(program));
        Assert.Equal(formBefore, File.ReadAllText(Path.Combine(project.Directory, "MainForm.radform.json")));
        Assert.Equal(codeBefore, File.ReadAllText(Path.Combine(project.Directory, "MainForm.cs")));
        Assert.False(File.Exists(Path.Combine(project.Directory, "Shell.radform.json")));
        Assert.False(File.Exists(Path.Combine(project.Directory, "Shell.cs")));
    }

    // UnauthorizedAccessException is not an IOException: surface it as one for the assertion.
    static void ThrowIo(Action a)
    {
        try { a(); }
        catch (UnauthorizedAccessException e) { throw new IOException(e.Message, e); }
    }
}
