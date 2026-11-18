namespace RadSharp.Design.Code;

/// <summary>
/// A RadSharp app project: an ordinary .csproj whose folder holds forms (<c>X.radform.json</c>) next to
/// their code-behind (<c>X.cs</c>).
/// </summary>
public sealed class RadProject
{
    public RadProject(string projectFile)
    {
        ProjectFile = Path.GetFullPath(projectFile);
        if (!File.Exists(ProjectFile)) throw new FileNotFoundException("Project file not found.", ProjectFile);
    }

    public string ProjectFile { get; }
    public string Directory => Path.GetDirectoryName(ProjectFile)!;
    public string Name => Path.GetFileNameWithoutExtension(ProjectFile);

    /// <summary>The project's namespace: RootNamespace from the .csproj, or the project name.</summary>
    public string RootNamespace
    {
        get
        {
            var text = File.ReadAllText(ProjectFile);
            int i = text.IndexOf("<RootNamespace>", StringComparison.Ordinal);
            if (i < 0) return Sanitize(Name);
            int j = text.IndexOf("</RootNamespace>", i, StringComparison.Ordinal);
            return text[(i + "<RootNamespace>".Length)..j].Trim();
        }
    }

    static IEnumerable<string> Files(string dir, string pattern) =>
        System.IO.Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or ".vs" or ".vscode" or "packages" or ".git"))
            .OrderBy(f => f, StringComparer.Ordinal);

    public IReadOnlyList<string> FormFiles => [.. Files(Directory, "*.radform.json")];
    public IReadOnlyList<string> SourceFiles => [.. Files(Directory, "*.cs")];

    /// <summary>The class name of a form file: <c>MainForm.radform.json</c> → <c>MainForm</c>.</summary>
    public static string FormClassName(string formFile) =>
        Path.GetFileName(formFile)[..^".radform.json".Length];

    /// <summary>The code-behind of a form file (it may not exist yet).</summary>
    public static string CodeFileFor(string formFile) =>
        Path.Combine(Path.GetDirectoryName(formFile)!, FormClassName(formFile) + ".cs");

    static string Sanitize(string name) =>
        new([.. name.Select((c, i) => char.IsLetterOrDigit(c) || c == '_' ? c : '_')]);

    // ---- Creating ----

    /// <summary>
    /// Creates a new app project in <paramref name="directory"/>: .csproj, Program.cs and a main form.
    /// <paramref name="packageSource"/> (a folder of RadSharp packages) is written to a nuget.config.
    /// </summary>
    public static RadProject Create(string directory, string name, string radSharpVersion = "0.1.0-dev*", string? packageSource = null)
    {
        System.IO.Directory.CreateDirectory(directory);
        var ns = Sanitize(name);
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <RootNamespace>{ns}</RootNamespace>
                <PublishAot>true</PublishAot>
                <EnableAotAnalyzer>true</EnableAotAnalyzer>
                <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="RadSharp.Platform.Sdl" Version="{radSharpVersion}" />
              </ItemGroup>

            </Project>

            """);
        File.WriteAllText(Path.Combine(directory, "Program.cs"), $"""
            using RadSharp.Platform.Sdl;
            using {ns};

            SdlHost.Initialize();
            SdlHost.Run(new MainForm());

            """);
        if (packageSource != null)
            File.WriteAllText(Path.Combine(directory, "nuget.config"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="packages" />
                  </config>
                  <packageSources>
                    <add key="radsharp-local" value="{packageSource}" />
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                  </packageSources>
                </configuration>

                """);
        File.WriteAllText(Path.Combine(directory, ".gitignore"), "bin/\nobj/\npackages/\n");
        var workflows = Path.Combine(directory, ".github", "workflows");
        System.IO.Directory.CreateDirectory(workflows);
        File.WriteAllText(Path.Combine(workflows, "build.yml"), CrossPlatformWorkflow);
        WriteDebugFiles(directory, name);
        var project = new RadProject(Path.Combine(directory, name + ".csproj"));
        project.AddForm("MainForm", name);
        project.EnsureSolution();
        return project;
    }

    /// <summary>
    /// Writes what F5 needs: <c>.vscode/launch.json</c> + <c>tasks.json</c> (VS Code, C# extension, <c>coreclr</c>) and
    /// <c>Properties/launchSettings.json</c> (Visual Studio, Rider). Matches templates/radsharp-app.
    /// </summary>
    static void WriteDebugFiles(string directory, string name)
    {
        var vscode = Path.Combine(directory, ".vscode");
        System.IO.Directory.CreateDirectory(vscode);
        File.WriteAllText(Path.Combine(vscode, "launch.json"), $$"""
            {
              "version": "0.2.0",
              "configurations": [
                {
                  "name": "Run {{name}}",
                  "type": "coreclr",
                  "request": "launch",
                  "preLaunchTask": "build",
                  "program": "${workspaceFolder}/bin/Debug/net10.0/{{name}}.dll",
                  "cwd": "${workspaceFolder}",
                  "console": "internalConsole",
                  "stopAtEntry": false
                }
              ]
            }

            """);
        File.WriteAllText(Path.Combine(vscode, "tasks.json"), $$"""
            {
              "version": "2.0.0",
              "tasks": [
                {
                  "label": "build",
                  "command": "dotnet",
                  "type": "process",
                  "args": ["build", "${workspaceFolder}/{{name}}.csproj"],
                  "problemMatcher": "$msCompile",
                  "group": { "kind": "build", "isDefault": true }
                },
                {
                  "label": "publish (native executable)",
                  "command": "dotnet",
                  "type": "process",
                  "args": ["publish", "${workspaceFolder}/{{name}}.csproj", "-c", "Release"],
                  "problemMatcher": "$msCompile"
                }
              ]
            }

            """);
        var properties = Path.Combine(directory, "Properties");
        System.IO.Directory.CreateDirectory(properties);
        File.WriteAllText(Path.Combine(properties, "launchSettings.json"), $$"""
            {
              "profiles": {
                "{{name}}": {
                  "commandName": "Project"
                }
              }
            }

            """);
    }

    // ---- Form names and renaming ----

    /// <summary>The first <c>FormN</c> (N ≥ 2) that <see cref="ValidateFormName"/> accepts.</summary>
    public string NextFreeFormName()
    {
        for (int n = 2; ; n++)
            if (ValidateFormName($"Form{n}") == null) return $"Form{n}";
    }

    /// <summary>
    /// Why <paramref name="name"/> can't be the class name of a new form (not a C# identifier, a keyword, already a
    /// form or a type in the project, or a file in the way), or null if it can.
    /// </summary>
    public string? ValidateFormName(string name, string? renaming = null)
    {
        if (!Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(name))
            return "The class name must be a C# identifier (letters, digits and '_', not starting with a digit).";
        if (Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None)
            return $"'{name}' is a C# keyword.";
        // Contextual keywords the compiler refuses as type names (CS9056 / CS8105).
        if (name is "file" or "record" or "required" or "scoped" or "var")
            return $"'{name}' can't be used as a class name.";
        if (typeof(Form).Assembly.GetType("RadSharp." + name) != null)
            return $"'{name}' is a RadSharp type; a form with that name would hide it.";
        // Renaming a form may change only its case ("MainForm" to "Mainform"): its own files are not in the way.
        string? ownForm = renaming == null ? null : Path.GetFullPath(renaming);
        string? ownCode = ownForm == null ? null : CodeFileFor(ownForm);
        bool Own(string file, string? own) => own != null && string.Equals(Path.GetFullPath(file), own, StringComparison.OrdinalIgnoreCase);
        var formFile = Path.Combine(Directory, name + ".radform.json");
        var codeFile = Path.Combine(Directory, name + ".cs");
        if ((File.Exists(formFile) && !Own(formFile, ownForm)) || FormFiles.Any(f => !Own(f, ownForm) && string.Equals(FormClassName(f), name, StringComparison.OrdinalIgnoreCase)))
            return $"The project already has a form named {name}.";
        if (File.Exists(codeFile) && !Own(codeFile, ownCode)) return $"{name}.cs already exists.";
        foreach (var file in SourceFiles)
            if (DeclaredTypes(file).Contains(name)) return $"The project already declares a type named {name} ({Path.GetFileName(file)}).";
        return null;
    }

    static HashSet<string> DeclaredTypes(string file)
    {
        var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
        return [.. root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax>().Select(t => t.Identifier.Text)];
    }

    /// <summary>
    /// Renames a form's class: <c>Old.radform.json</c> and <c>Old.cs</c> become <c>New.…</c>, and every identifier
    /// <c>Old</c> in the project's source files follows (textual, not semantic: see
    /// <see cref="CodeIntegration.RenameIdentifier"/>; an unrelated identifier of the same name is renamed too).
    /// Nothing is touched if the new name is invalid or a target file exists; if a write fails midway, the files
    /// already changed are restored. Returns the new form file.
    /// </summary>
    public string RenameForm(string formFile, string newName)
    {
        formFile = Path.GetFullPath(formFile);
        var oldName = FormClassName(formFile);
        if (!File.Exists(formFile)) throw new FileNotFoundException("Form file not found.", formFile);
        if (ValidateFormName(newName, formFile) is { } problem) throw new IOException(problem);
        var newForm = Path.Combine(Path.GetDirectoryName(formFile)!, newName + ".radform.json");
        var oldCode = CodeFileFor(formFile);
        var newCode = CodeFileFor(newForm);

        // Compute everything in memory first.
        var writes = new List<(string Path, string Text)>();
        foreach (var file in SourceFiles)
        {
            var source = File.ReadAllText(file);
            var updated = CodeIntegration.RenameIdentifier(source, oldName, newName);
            bool isCode = string.Equals(file, oldCode, StringComparison.OrdinalIgnoreCase);
            if (isCode || updated != source) writes.Add((isCode ? newCode : file, updated));
        }

        var undo = new List<Action>();
        try
        {
            File.Move(formFile, newForm);
            undo.Add(() => File.Move(newForm, formFile));
            if (File.Exists(oldCode))
            {
                File.Move(oldCode, newCode);
                undo.Add(() => File.Move(newCode, oldCode));
            }
            foreach (var (path, text) in writes)
            {
                var original = File.ReadAllText(path);
                undo.Add(() => File.WriteAllText(path, original));   // before the write: a write can fail after truncating
                File.WriteAllText(path, text);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            for (int i = undo.Count - 1; i >= 0; i--)
                try { undo[i](); } catch (Exception) { }
            throw;
        }
        return newForm;
    }

    // ---- Main form (Delphi: Project > Options > Forms > Main form) ----

    string ProgramFile => Path.Combine(Directory, "Program.cs");

    /// <summary>The class <c>Program.cs</c> passes to <c>SdlHost.Run(new X())</c>, or null.</summary>
    public string? MainFormClass
    {
        get
        {
            if (!File.Exists(ProgramFile)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(ProgramFile), @"\bSdlHost\.Run\(\s*new\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(");
            return m.Success ? m.Groups[1].Value : null;
        }
    }

    /// <summary>Makes <paramref name="className"/> the form <c>SdlHost.Run</c> starts with.</summary>
    public bool SetMainForm(string className)
    {
        if (!File.Exists(ProgramFile)) return false;
        var text = File.ReadAllText(ProgramFile);
        var updated = System.Text.RegularExpressions.Regex.Replace(text, @"(\bSdlHost\.Run\(\s*new\s+)[A-Za-z_][A-Za-z0-9_]*(\s*\()", "${1}" + className + "${2}");
        if (updated == text) return false;
        File.WriteAllText(ProgramFile, updated);
        return true;
    }

    /// <summary>Deletes a form and its code-behind (in an SDK-style project, every file in the folder is compiled).</summary>
    public void RemoveForm(string formFile)
    {
        File.Delete(formFile);
        var code = CodeFileFor(formFile);
        if (File.Exists(code)) File.Delete(code);
    }

    /// <summary>
    /// The nearest solution (.sln or .slnx) in a folder above the project that lists this project, or null. Looks up
    /// to six folders up and stops at the repository root (a folder with .git).
    /// </summary>
    public string? FindListingSolution()
    {
        var dir = new DirectoryInfo(Directory).Parent;
        for (int depth = 0; dir != null && depth < 6; depth++, dir = dir.Parent)
        {
            foreach (var sln in dir.EnumerateFiles("*.sln").Concat(dir.EnumerateFiles("*.slnx")))
                if (Lists(sln.FullName)) return sln.FullName;
            if (System.IO.Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) break;
        }
        return null;
    }

    /// <summary>Whether a solution file names this project (its path relative to the solution, either slash).</summary>
    bool Lists(string solutionFile)
    {
        string rel = Path.GetRelativePath(Path.GetDirectoryName(solutionFile)!, ProjectFile).Replace('\\', '/');
        string text;
        try { text = File.ReadAllText(solutionFile).Replace('\\', '/'); }
        catch (IOException) { return false; }
        return text.Contains("\"" + rel + "\"", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The project's solution file (for Visual Studio and Rider): one next to the project, else one in a folder
    /// above that already lists it (a repository with several projects keeps its solution at the top), else a new
    /// one written next to the project, as Unity does for "Open C# Project".
    /// </summary>
    public string EnsureSolution()
    {
        var existing = System.IO.Directory.EnumerateFiles(Directory, "*.sln").Concat(System.IO.Directory.EnumerateFiles(Directory, "*.slnx")).FirstOrDefault();
        if (existing != null) return existing;
        if (FindListingSolution() is { } above) return above;
        var path = Path.Combine(Directory, Name + ".sln");
        var id = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
        const string csharp = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}";
        var lines = new[]
        {
            "",
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            "# Visual Studio Version 17",
            "VisualStudioVersion = 17.0.31903.59",
            "MinimumVisualStudioVersion = 10.0.40219.1",
            $"Project(\"{csharp}\") = \"{Name}\", \"{Name}.csproj\", \"{id}\"",
            "EndProject",
            "Global",
            "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution",
            "\t\tDebug|Any CPU = Debug|Any CPU",
            "\t\tRelease|Any CPU = Release|Any CPU",
            "\tEndGlobalSection",
            "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution",
            $"\t\t{id}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
            $"\t\t{id}.Debug|Any CPU.Build.0 = Debug|Any CPU",
            $"\t\t{id}.Release|Any CPU.ActiveCfg = Release|Any CPU",
            $"\t\t{id}.Release|Any CPU.Build.0 = Release|Any CPU",
            "\tEndGlobalSection",
            "EndGlobal",
        };
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
        return path;
    }

    /// <summary>The form file for a class name, if the project has it.</summary>
    public string? FormFileFor(string className) =>
        FormFiles.FirstOrDefault(f => FormClassName(f) == className);

    /// <summary>A GitHub Actions workflow that builds the app natively on Windows, macOS and Linux.</summary>
    public const string CrossPlatformWorkflow = """
        name: Build

        # Builds a native executable for Windows, macOS and Linux (NativeAOT builds for the OS it runs on).
        on:
          push:
          workflow_dispatch:

        jobs:
          build:
            strategy:
              matrix:
                os: [windows-latest, macos-latest, ubuntu-latest]
            runs-on: ${{ matrix.os }}
            steps:
              - uses: actions/checkout@v4
              - uses: actions/setup-dotnet@v4
                with:
                  dotnet-version: 10.0.x
              - run: dotnet publish -c Release --use-current-runtime -o out
              - uses: actions/upload-artifact@v4
                with:
                  name: app-${{ matrix.os }}
                  path: out/
        # Before shipping: sign the Windows .exe (SmartScreen warns about unsigned apps) and sign and
        # notarize the macOS app (Gatekeeper blocks unsigned downloads). See the RadSharp docs.
        """;

    /// <summary>Adds an empty form and its code-behind; returns the form file.</summary>
    public string AddForm(string className, string? caption = null)
    {
        var formFile = Path.Combine(Directory, className + ".radform.json");
        if (File.Exists(formFile)) throw new IOException($"{Path.GetFileName(formFile)} already exists.");
        var form = new Form { Name = "frm" + className.Replace("Form", ""), Caption = caption ?? className };
        form.SetBounds(0, 0, 480, 320);
        form.DesignWidth = 480;
        form.DesignHeight = 320;
        File.WriteAllText(formFile, FormWriter.Save(form));
        var codeFile = CodeFileFor(formFile);
        if (!File.Exists(codeFile))
            File.WriteAllText(codeFile, $$"""
                using RadSharp;

                namespace {{RootNamespace}};

                public partial class {{className}} : Form
                {
                    public {{className}}() => InitializeComponent();
                }

                """);
        return formFile;
    }
}
