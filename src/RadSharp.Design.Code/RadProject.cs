namespace RadSharp.Design.Code;

/// <summary>
/// A RADSharp app project: an ordinary .csproj whose folder holds forms (<c>X.radform.json</c>) next to
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
                .Any(part => part is "bin" or "obj" or ".vs" or ".vscode"))
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
    /// <paramref name="packageSource"/> (a folder of RADSharp packages) is written to a nuget.config.
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
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="RadSharp.Platform.Sdl" Version="{radSharpVersion}" />
              </ItemGroup>

            </Project>

            """);
        File.WriteAllText(Path.Combine(directory, "Program.cs"), $"""
            using RadSharp.Platform.Sdl;
            using {ns};

            Application.Initialize();
            Application.Run(new MainForm());

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
        var project = new RadProject(Path.Combine(directory, name + ".csproj"));
        project.AddForm("MainForm", name);
        project.EnsureSolution();
        return project;
    }

    // ---- Main form (Delphi: Project > Options > Forms > Main form) ----

    string ProgramFile => Path.Combine(Directory, "Program.cs");

    /// <summary>The class <c>Program.cs</c> passes to <c>Application.Run(new X())</c>, or null.</summary>
    public string? MainFormClass
    {
        get
        {
            if (!File.Exists(ProgramFile)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(ProgramFile), @"Application\.Run\(\s*new\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(");
            return m.Success ? m.Groups[1].Value : null;
        }
    }

    /// <summary>Makes <paramref name="className"/> the form <c>Application.Run</c> starts with.</summary>
    public bool SetMainForm(string className)
    {
        if (!File.Exists(ProgramFile)) return false;
        var text = File.ReadAllText(ProgramFile);
        var updated = System.Text.RegularExpressions.Regex.Replace(text, @"(Application\.Run\(\s*new\s+)[A-Za-z_][A-Za-z0-9_]*(\s*\()", "${1}" + className + "${2}");
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
    /// The project's solution file (for Visual Studio and Rider), written next to the project if there is
    /// none yet, as Unity does for "Open C# Project".
    /// </summary>
    public string EnsureSolution()
    {
        var existing = System.IO.Directory.EnumerateFiles(Directory, "*.sln").Concat(System.IO.Directory.EnumerateFiles(Directory, "*.slnx")).FirstOrDefault();
        if (existing != null) return existing;
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
        # notarize the macOS app (Gatekeeper blocks unsigned downloads). See the RADSharp docs.
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
