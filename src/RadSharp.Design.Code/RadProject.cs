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
    public static RadProject Create(string directory, string name, string radSharpVersion = "0.1.0-dev", string? packageSource = null)
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
                  <packageSources>
                    <add key="radsharp-local" value="{packageSource}" />
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                  </packageSources>
                </configuration>

                """);
        var workflows = Path.Combine(directory, ".github", "workflows");
        System.IO.Directory.CreateDirectory(workflows);
        File.WriteAllText(Path.Combine(workflows, "build.yml"), CrossPlatformWorkflow);
        var project = new RadProject(Path.Combine(directory, name + ".csproj"));
        project.AddForm("MainForm", name);
        return project;
    }

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
