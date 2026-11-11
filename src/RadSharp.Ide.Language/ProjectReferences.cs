using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using RadSharp.Design.Code;

namespace RadSharp.Ide.Language;

/// <summary>When <see cref="ProjectWorkspace"/> runs <c>dotnet restore</c>.</summary>
public enum RestoreMode
{
    /// <summary>When the project has package or project references and <c>obj/project.assets.json</c> is missing or older than the .csproj.</summary>
    IfNeeded,
    /// <summary>Never; use whatever <c>project.assets.json</c> there is.</summary>
    Never,
    /// <summary>Always, before reading <c>project.assets.json</c>.</summary>
    Always,
}

/// <summary>
/// The metadata references of a project, as the workspace sees them: the host process's assemblies (exactly
/// what <see cref="LiveSession.Compile"/> compiles against), plus the compile assets of the project's
/// packages and the outputs of its project references, read from <c>obj/project.assets.json</c>. An
/// assembly the host already provides is not added twice, so RADSharp's own types are the host's.
/// </summary>
public sealed record ProjectReferences(IReadOnlyList<string> Paths, IReadOnlyList<string> Notes)
{
    /// <summary>Resolves the references, running <c>dotnet restore</c> first if <paramref name="restore"/> says so.</summary>
    public static async Task<ProjectReferences> ResolveAsync(RadProject project, RestoreMode restore = RestoreMode.IfNeeded, CancellationToken cancel = default)
    {
        var notes = new List<string>();
        var paths = new List<string>(CompilationSetup.HostReferencePaths);
        var names = new HashSet<string>(paths.Select(p => Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase);

        var assets = Path.Combine(project.Directory, "obj", "project.assets.json");
        bool hasReferences = HasReferenceItems(project.ProjectFile);
        bool stale = !File.Exists(assets) || File.GetLastWriteTimeUtc(assets) < File.GetLastWriteTimeUtc(project.ProjectFile);
        if (restore == RestoreMode.Always || (restore == RestoreMode.IfNeeded && hasReferences && stale))
        {
            var (exit, output) = await RestoreAsync(project, cancel).ConfigureAwait(false);
            if (exit != 0) notes.Add($"dotnet restore failed ({exit}): {output.Trim()}");
        }

        if (File.Exists(assets))
        {
            try
            {
                foreach (var path in ReadAssets(project, assets, notes))
                    if (names.Add(Path.GetFileNameWithoutExtension(path))) paths.Add(path);
            }
            catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or KeyNotFoundException)
            {
                notes.Add($"Couldn't read {assets}: {e.Message}");
            }
        }
        else if (hasReferences) notes.Add("The project has references but no obj/project.assets.json; restore it.");
        return new ProjectReferences(paths, notes);
    }

    static bool HasReferenceItems(string projectFile)
    {
        try
        {
            var doc = XDocument.Load(projectFile);
            return doc.Descendants().Any(e => e.Name.LocalName is "PackageReference" or "ProjectReference");
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException) { return false; }
    }

    static async Task<(int ExitCode, string Output)> RestoreAsync(RadProject project, CancellationToken cancel)
    {
        var psi = new ProcessStartInfo("dotnet", $"restore \"{project.ProjectFile}\" -nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = project.Directory,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancel);
        var stderr = process.StandardError.ReadToEndAsync(cancel);
        try { await process.WaitForExitAsync(cancel).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }

    /// <summary>Compile-time assemblies from <c>project.assets.json</c>: package compile assets and project-reference outputs.</summary>
    static IEnumerable<string> ReadAssets(RadProject project, string assetsFile, List<string> notes)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(assetsFile));
        var root = doc.RootElement;
        var folders = root.TryGetProperty("packageFolders", out var pf)
            ? pf.EnumerateObject().Select(p => p.Name).ToList()
            : [];
        var libraries = root.GetProperty("libraries");

        // The target without a runtime identifier ("net10.0", not "net10.0/win-x64").
        var target = root.GetProperty("targets").EnumerateObject().FirstOrDefault(t => !t.Name.Contains('/'));
        if (target.Value.ValueKind != JsonValueKind.Object) yield break;

        foreach (var library in target.Value.EnumerateObject())
        {
            var type = library.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!libraries.TryGetProperty(library.Name, out var info)) continue;
            var relative = info.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";

            if (type == "package")
            {
                if (!library.Value.TryGetProperty("compile", out var compile)) continue;
                foreach (var file in compile.EnumerateObject())
                {
                    if (!file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;   // "_._" placeholders
                    var found = folders.Select(f => Path.GetFullPath(Path.Combine(f, relative, file.Name))).FirstOrDefault(File.Exists);
                    if (found != null) yield return found;
                    else notes.Add($"Package asset not found: {library.Name}/{file.Name}");
                }
            }
            else if (type == "project")
            {
                // Project references have placeholder compile paths; use the referenced project's build output.
                var referenced = Path.GetFullPath(Path.Combine(project.Directory, relative));
                var output = ProjectOutput(referenced);
                if (output != null) yield return output;
                else notes.Add($"No build output for project reference {Path.GetFileName(referenced)}; build it once.");
            }
        }
    }

    /// <summary>The newest <c>bin/&lt;configuration&gt;/&lt;tfm&gt;/&lt;name&gt;.dll</c> of a referenced project.</summary>
    static string? ProjectOutput(string projectFile)
    {
        var bin = Path.Combine(Path.GetDirectoryName(projectFile)!, "bin");
        if (!System.IO.Directory.Exists(bin)) return null;
        var name = Path.GetFileNameWithoutExtension(projectFile) + ".dll";
        return System.IO.Directory.EnumerateDirectories(bin)
            .SelectMany(config => System.IO.Directory.EnumerateDirectories(config))
            .Select(tfm => Path.Combine(tfm, name))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
