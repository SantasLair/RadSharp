using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RadSharp.Design.Code;
using RadSharp.Drawing;

namespace RadSharp.RefApp;

/// <summary>Where the reference app and its outputs live, and the write-back used to update the committed forms.</summary>
public static class RefApp
{
    /// <summary>The repository root (the folder with RadSharp.slnx).</summary>
    public static string Root { get; } = FindRoot();

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "RadSharp.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("RadSharp.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The committed app: <c>refapp/Tally</c>.</summary>
    public static string Tally => Path.Combine(Root, "refapp", "Tally");

    /// <summary>The packed RadSharp packages (<c>artifacts/packages</c>), where <c>dotnet pack</c> puts them.</summary>
    public static string Packages => Path.Combine(Root, "artifacts", "packages");

    /// <summary>PNG renders for people and agents to look at: <c>TestResults/refapp</c>.</summary>
    public static string Results
    {
        get
        {
            var dir = Path.Combine(Root, "TestResults", "refapp");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary><c>RADSHARP_REFAPP_WRITE=1</c>: copy the recipes' output over the committed app instead of comparing.</summary>
    public static bool Write => Environment.GetEnvironmentVariable("RADSHARP_REFAPP_WRITE") == "1";

    public static string TempDir(string prefix) => Directory.CreateTempSubdirectory(prefix).FullName;

    static bool Generated(string relative) =>
        relative.Split('/', '\\').Any(part => part is "bin" or "obj" or "packages" or ".vs");

    /// <summary>Every file of a project folder, relative, without build output and restored packages.</summary>
    public static List<string> Files(string folder, string pattern = "*") =>
        [.. Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Where(f => !Generated(f))
            .Order(StringComparer.Ordinal)];

    /// <summary>Copies a project folder (without build output and packages), e.g. to run the committed app from a temp copy.</summary>
    public static void CopyProject(string from, string to)
    {
        foreach (var rel in Files(from))
        {
            var target = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(from, rel), target, overwrite: true);
        }
    }

    /// <summary>
    /// <c>RADSHARP_REFAPP_WRITE=1</c>: makes the committed app match a recipe run. The first time (no committed app yet)
    /// everything File › New Application wrote is copied. After that only forms are copied; a new form's code-behind is
    /// copied if missing, and a handler stub the recipe's designer wrote that the committed code lacks is added the way a
    /// double-click in the designer would add it (<see cref="CodeIntegration.EnsureHandler"/>). Other code is never
    /// touched: it belongs to whoever writes the app.
    /// </summary>
    public static List<string> WriteBack(RadProject produced, string committed)
    {
        var log = new List<string>();
        bool first = !Directory.Exists(committed);
        foreach (var rel in Files(produced.Directory))
        {
            var source = Path.Combine(produced.Directory, rel);
            var target = Path.Combine(committed, rel);
            bool isForm = rel.EndsWith(".radform.json", StringComparison.Ordinal);
            bool isFormCode = produced.FormFiles.Any(f => RadProject.CodeFileFor(f) == Path.GetFullPath(source));
            if (first || isForm || (isFormCode && !File.Exists(target)))
            {
                if (File.Exists(target) && File.ReadAllText(target) == File.ReadAllText(source)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                log.Add("wrote " + rel);
            }
            else if (isFormCode)
            {
                var className = Path.GetFileNameWithoutExtension(rel);
                var code = File.ReadAllText(target);
                var updated = code;
                foreach (var (name, parameters) in Methods(File.ReadAllText(source), className))
                    updated = CodeIntegration.EnsureHandler(updated, className, name, parameters).Source;
                if (updated != code)
                {
                    File.WriteAllText(target, updated);
                    log.Add("added handler stubs to " + rel);
                }
            }
        }
        foreach (var rel in Files(committed, "*.radform.json"))
            if (!File.Exists(Path.Combine(produced.Directory, rel)))
            {
                File.Delete(Path.Combine(committed, rel));
                log.Add("deleted " + rel + " (no recipe makes it)");
            }
        return log;
    }

    /// <summary>The void methods of a class: name and parameter list text (the designer's handler stubs).</summary>
    static IEnumerable<(string Name, string Parameters)> Methods(string source, string className)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (cls == null) yield break;
        foreach (var m in cls.Members.OfType<MethodDeclarationSyntax>())
            if (m.ReturnType.ToString() == "void")
                yield return (m.Identifier.Text, string.Join(", ", m.ParameterList.Parameters.Select(p => p.ToString())));
    }

    /// <summary>Writes a PNG of <paramref name="form"/> to <see cref="Results"/>; returns its path.</summary>
    public static string Render(Form form, string name)
    {
        using var bitmap = FormRenderer.RenderToBitmap(form);
        var path = Path.Combine(Results, name + ".png");
        File.WriteAllBytes(path, FormRenderer.EncodePng(bitmap));
        return path;
    }

    /// <summary>Where two texts first differ, for a readable assertion message.</summary>
    public static string FirstDifference(string expected, string actual)
    {
        var a = expected.Split('\n');
        var b = actual.Split('\n');
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : "<end>";
            var y = i < b.Length ? b[i] : "<end>";
            if (x != y) return $"line {i + 1}: committed \"{x.Trim()}\", recipe \"{y.Trim()}\"";
        }
        return "identical lines, different bytes (line endings?)";
    }
}
