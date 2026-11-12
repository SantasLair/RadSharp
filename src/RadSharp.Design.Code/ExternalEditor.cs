using System.Diagnostics;

namespace RadSharp.Design.Code;

public enum EditorKind { VSCode, VisualStudio, Rider, Custom }

/// <summary>An installed code editor the designer can hand code to.</summary>
public sealed record EditorInfo(EditorKind Kind, string Name, string Executable);

/// <summary>
/// Opens code in the user's IDE, Unity style: VS Code, Visual Studio or Rider (detected), or a custom
/// command. VS Code and Rider open at the line; Visual Studio opens the file in the running instance.
/// </summary>
public static class ExternalEditor
{
    /// <summary>The editors found on this machine, in the order VS Code, Visual Studio, Rider.</summary>
    public static IReadOnlyList<EditorInfo> Detect()
    {
        var list = new List<EditorInfo>();
        if (FindVSCode() is { } code) list.Add(new(EditorKind.VSCode, "Visual Studio Code", code));
        if (FindVisualStudio() is { } devenv) list.Add(new(EditorKind.VisualStudio, "Visual Studio", devenv));
        if (FindRider() is { } rider) list.Add(new(EditorKind.Rider, "JetBrains Rider", rider));
        return list;
    }

    static string? FindOnPath(params string[] names)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in dirs)
            foreach (var n in names)
            {
                try
                {
                    var p = Path.Combine(dir.Trim('"'), n);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
        return null;
    }

    static string? FirstExisting(params string?[] paths) => paths.FirstOrDefault(p => p != null && File.Exists(p));

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static string? FindVSCode() =>
        OperatingSystem.IsWindows()
            ? FirstExisting(Path.Combine(LocalAppData, "Programs", "Microsoft VS Code", "bin", "code.cmd"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "bin", "code.cmd"))
              ?? FindOnPath("code.cmd", "code.exe")
            : OperatingSystem.IsMacOS()
                ? FirstExisting("/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code") ?? FindOnPath("code")
                : FindOnPath("code") ?? FirstExisting("/snap/bin/code", "/usr/bin/code");

    static string? FindVisualStudio()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(vswhere, "-latest -prerelease -property productPath")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var path = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return File.Exists(path) ? path : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    static string? FindRider()
    {
        if (OperatingSystem.IsWindows())
        {
            var candidates = new List<string?>
            {
                Path.Combine(LocalAppData, "JetBrains", "Toolbox", "scripts", "rider.cmd"),
                Path.Combine(LocalAppData, "Programs", "Rider", "bin", "rider64.exe"),
            };
            var jetbrains = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "JetBrains");
            if (Directory.Exists(jetbrains))
                candidates.AddRange(Directory.GetDirectories(jetbrains, "JetBrains Rider*").OrderDescending()
                    .Select(d => Path.Combine(d, "bin", "rider64.exe")));
            return FirstExisting([.. candidates]) ?? FindOnPath("rider64.exe", "rider.cmd");
        }
        if (OperatingSystem.IsMacOS())
            return FirstExisting("/Applications/Rider.app/Contents/MacOS/rider",
                                 Path.Combine(Home, "Library", "Application Support", "JetBrains", "Toolbox", "scripts", "rider")) ?? FindOnPath("rider");
        return FindOnPath("rider") ?? FirstExisting(Path.Combine(Home, ".local", "share", "JetBrains", "Toolbox", "scripts", "rider"), "/opt/rider/bin/rider.sh");
    }

    /// <summary>The command line for an editor: an executable plus arguments with {file}, {line}, {folder}, {solution}.</summary>
    public static string CommandTemplate(EditorKind kind, string? executable, string? customTemplate) => kind switch
    {
        EditorKind.VSCode => $"\"{executable ?? "code"}\" \"{{folder}}\" -g \"{{file}}:{{line}}\"",
        EditorKind.Rider => $"\"{executable ?? "rider"}\" \"{{solution}}\" --line {{line}} \"{{file}}\"",
        // devenv can't take a line number; /edit opens the file in the running instance.
        EditorKind.VisualStudio => $"\"{executable ?? "devenv"}\" /edit \"{{file}}\"",
        _ => customTemplate is { Length: > 0 } t ? t : "\"{file}\"",
    };

    /// <summary>Opens <paramref name="file"/> at <paramref name="line"/> in the chosen editor.</summary>
    public static bool Open(EditorKind kind, string? executable, string? customTemplate, RadProject? project, string file, int line)
    {
        var template = CommandTemplate(kind, executable, customTemplate);
        var solution = project?.EnsureSolution() ?? "";
        var folder = project?.Directory ?? Path.GetDirectoryName(file) ?? "";
        return Run(template
            .Replace("{file}", file)
            .Replace("{line}", Math.Max(1, line).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{folder}", folder)
            .Replace("{solution}", solution));
    }

    /// <summary>Opens the whole project: the folder in VS Code, the solution in Visual Studio or Rider.</summary>
    public static bool OpenProject(EditorKind kind, string? executable, string? customTemplate, RadProject project)
    {
        var solution = project.EnsureSolution();
        var command = kind switch
        {
            EditorKind.VSCode => $"\"{executable ?? "code"}\" \"{project.Directory}\"",
            EditorKind.Rider or EditorKind.VisualStudio => $"\"{executable ?? (kind == EditorKind.Rider ? "rider" : "devenv")}\" \"{solution}\"",
            _ => null,
        };
        return command == null
            ? Run(CommandTemplate(kind, executable, customTemplate).Replace("{file}", solution).Replace("{line}", "1")
                .Replace("{folder}", project.Directory).Replace("{solution}", solution))
            : Run(command);
    }

    /// <summary>Splits "exe" args… and starts it; falls back to the OS's default app for a bare file.</summary>
    static bool Run(string command)
    {
        command = command.Trim();
        string exe, args;
        if (command.StartsWith('"'))
        {
            int close = command.IndexOf('"', 1);
            exe = command[1..close];
            args = command[(close + 1)..].Trim();
        }
        else
        {
            int space = command.IndexOf(' ');
            exe = space < 0 ? command : command[..space];
            args = space < 0 ? "" : command[(space + 1)..].Trim();
        }
        try
        {
            // .cmd/.bat scripts (code.cmd, rider.cmd) need the shell on Windows.
            bool shell = OperatingSystem.IsWindows() && (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
            var psi = shell
                ? new ProcessStartInfo("cmd.exe", $"/c \"\"{exe}\" {args}\"") { UseShellExecute = false, CreateNoWindow = true }
                : new ProcessStartInfo(exe, args) { UseShellExecute = args.Length == 0, CreateNoWindow = true };
            Process.Start(psi);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
}

/// <summary>Where the designer finds RadSharp packages for new projects.</summary>
public static class RadSharpPackages
{
    /// <summary>
    /// A local folder of RadSharp packages: RADSHARP_PACKAGES if set, otherwise <c>artifacts/packages</c>
    /// of the repository the designer runs from (found by walking up from the designer's folder). Null if
    /// none: new projects then restore from nuget.org.
    /// </summary>
    public static string? FindLocalSource()
    {
        if (Environment.GetEnvironmentVariable("RADSHARP_PACKAGES") is { Length: > 0 } env && Directory.Exists(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "packages");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "RadSharp.Platform.Sdl.*.nupkg").Any()) return candidate;
        }
        return null;
    }
}
