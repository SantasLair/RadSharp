using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RadSharp.Design.Code;

/// <summary>The C# projects a <c>.sln</c> or <c>.slnx</c> lists (full paths; missing files and other project types are skipped).</summary>
public static class SolutionFile
{
    public static bool IsSolution(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ProjectFiles(string solutionFile)
    {
        solutionFile = Path.GetFullPath(solutionFile);
        var dir = Path.GetDirectoryName(solutionFile)!;
        var relative = new List<string>();
        var text = File.ReadAllText(solutionFile);
        if (solutionFile.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var e in XDocument.Parse(text).Descendants().Where(e => e.Name.LocalName == "Project"))
                if ((string?)e.Attribute("Path") is { } p) relative.Add(p);
        }
        else
        {
            foreach (Match m in Regex.Matches(text, @"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+)""", RegexOptions.Multiline))
                relative.Add(m.Groups[1].Value);
        }
        return [.. relative
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(Path.Combine(dir, p.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(File.Exists)];
    }
}
