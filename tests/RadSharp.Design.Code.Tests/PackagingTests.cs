using System.Text.RegularExpressions;
using RadSharp.Design.Code;

namespace RadSharp.Tests;

/// <summary>M17 packaging: what RadProject.Create writes (icon, version resource properties, build workflow) and that
/// the dotnet new template agrees with it.</summary>
public class PackagingTests
{
    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-pack-").FullName;

    static string TemplateDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "templates", "radsharp-app");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("templates/radsharp-app not found above the test folder.");
    }

    static string Normalize(string text) => text.Replace("\r\n", "\n");

    static string Property(string csproj, string name)
    {
        var m = Regex.Match(csproj, $@"<{name}(?:\s[^>]*)?>(.*?)</{name}>");
        Assert.True(m.Success, $"{name} is missing from the .csproj");
        return m.Groups[1].Value;
    }

    /// <summary>The text of one top-level job: from "  name:" to the next two-space-indented key.</summary>
    static string Job(string workflow, string name)
    {
        var m = Regex.Match(Normalize(workflow), $@"^  {name}:\n(.*?)(?=^  \S|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(m.Success, $"job {name} is missing");
        return m.Groups[1].Value;
    }

    [Fact]
    public void Create_writes_icon_and_version_resource_properties()
    {
        var project = RadProject.Create(TempDir(), "My App-2");
        var csproj = File.ReadAllText(project.ProjectFile);
        Assert.Equal("app.ico", Property(csproj, "ApplicationIcon"));
        Assert.Equal("1.0.0", Property(csproj, "Version"));
        Assert.Equal("My App-2", Property(csproj, "Product"));
        Assert.Equal("My App-2", Property(csproj, "Company"));
        Assert.Contains("Copyright", csproj);
        Assert.Equal("com.example.MyApp2", Property(csproj, "BundleId"));

        var ico = File.ReadAllBytes(Path.Combine(project.Directory, "app.ico"));
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, ico[..4]);
        Assert.True(BitConverter.ToUInt16(ico, 4) >= 6, "the icon should hold several sizes");
        var png = File.ReadAllBytes(Path.Combine(project.Directory, "app.png"));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        Assert.Equal(1024, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));   // IHDR width
        Assert.Contains("out/", File.ReadAllText(Path.Combine(project.Directory, ".gitignore")));
    }

    [Fact]
    public void Workflow_has_a_job_per_os_with_named_artifacts()
    {
        var workflow = Normalize(File.ReadAllText(Path.Combine(
            RadProject.Create(TempDir(), "WfApp").Directory, ".github", "workflows", "build.yml")));
        Assert.DoesNotContain('\t', workflow);
        Assert.StartsWith("name: Build", workflow);
        foreach (var job in new[] { "meta", "windows", "macos", "linux" }) Job(workflow, job);

        var windows = Job(workflow, "windows");
        Assert.Contains("runs-on: windows-latest", windows);
        Assert.Contains("-r win-x64", windows);
        Assert.Contains("-win-x64.zip", windows);
        Assert.Contains("-win-x64", windows);

        var macos = Job(workflow, "macos");
        Assert.Contains("osx-arm64", macos);
        Assert.Contains("osx-x64", macos);
        Assert.Contains("Info.plist", macos);
        Assert.Contains("CFBundleIdentifier", macos);
        Assert.Contains("LSMinimumSystemVersion", macos);
        Assert.Contains("NSHighResolutionCapable", macos);
        Assert.Contains("iconutil -c icns", macos);
        Assert.Contains("ditto -c -k --keepParent", macos);
        Assert.Contains("codesign --force --options runtime --timestamp", macos);
        Assert.Contains("xcrun notarytool submit", macos);
        Assert.Contains("--wait", macos);
        Assert.Contains("xcrun stapler staple", macos);

        var linux = Job(workflow, "linux");
        Assert.Contains("base=\"$NAME-$VERSION-linux-x64\"", linux);
        Assert.Contains(".tar.gz", linux);
        Assert.Contains("[Desktop Entry]", linux);
        Assert.Contains("appimagetool", linux);
        Assert.Contains("sha256sum --check", linux);
        Assert.Contains("continue-on-error: true", linux);   // AppImage is best effort

        // Everything comes from the .csproj, nothing is hard-coded.
        Assert.Contains("-getProperty:", workflow);
        Assert.Equal(3, Regex.Matches(workflow, Regex.Escape("dotnet publish \"$PROJECT\" -c Release -r ")).Count);
        foreach (var property in new[] { "AssemblyName", "Version", "Product", "BundleId" }) Assert.Contains($"get {property}", workflow);
        Assert.DoesNotContain("WfApp", workflow);

        // Actions are pinned to a major version.
        foreach (Match m in Regex.Matches(workflow, @"uses: (\S+)"))
            Assert.Matches(@"@v\d+$", m.Groups[1].Value);
    }

    [Fact]
    public void Signing_steps_run_only_when_their_secrets_exist()
    {
        var workflow = Normalize(RadProject.CrossPlatformWorkflow);
        // No secret at workflow or job level: the publish steps (third-party MSBuild targets) never see them.
        // (Job-level env values sit at 6 spaces of indentation, step-level ones at 10.)
        Assert.Empty(Regex.Matches(workflow, @"^ {0,6}\w+: \$\{\{ secrets\.", RegexOptions.Multiline));

        // Every step that sees a secret is a gate step (sets a flag) or is itself gated on a flag, never on `secrets`/env.
        Assert.DoesNotContain("if: env.", workflow);
        Assert.DoesNotContain("secrets.", Regex.Match(workflow, @"- name: Publish \(NativeAOT\)(.*?)(?=
      - )", RegexOptions.Singleline).Value);

        var windows = Job(workflow, "windows");
        Assert.Contains("id: wincert", windows);
        Assert.Contains("if: steps.wincert.outputs.has_cert == 'true'", windows);
        Assert.Contains("signtool", windows);
        Assert.Contains("WINDOWS_CERT_PFX: ${{ secrets.WINDOWS_CERT_PFX }}", windows);

        var macos = Job(workflow, "macos");
        Assert.Contains("id: maccert", macos);
        Assert.Contains("if: steps.maccert.outputs.has_cert == 'true'", macos);          // keychain import
        Assert.Contains("if: steps.maccert.outputs.has_notary == 'true'", macos);        // notarization
        Assert.Contains("security create-keychain", macos);
        Assert.Contains("if [ \"$HAS_CERT\" = \"true\" ]", macos);                         // Developer ID vs ad-hoc
        Assert.Contains("codesign --force --deep --sign -", macos);
        // No secret is ever referenced in a run script directly.
        foreach (Match m in Regex.Matches(workflow, @"^\s+run: .*\$\{\{ secrets\.", RegexOptions.Multiline)) Assert.Fail(m.Value);
    }

    [Fact]
    public void Plist_values_are_xml_escaped_and_one_csproj_is_required()
    {
        var workflow = Normalize(RadProject.CrossPlatformWorkflow);
        foreach (var v in new[] { "$x_name", "$x_bundle_id", "$x_product", "$x_version" }) Assert.Contains(v, workflow);
        foreach (var raw in new[] { "<string>$NAME</string>", "<string>$PRODUCT</string>", "<string>$BUNDLE_ID</string>", "<string>$short_version</string>" })
            Assert.DoesNotContain(raw, workflow);
        Assert.Contains("Expected exactly one .csproj", workflow);
    }

    [Fact]
    public void Create_xml_escapes_the_name_in_the_csproj_and_guards_the_icon()
    {
        var project = RadProject.Create(TempDir(), "Tom & Jerry's");
        var csproj = File.ReadAllText(project.ProjectFile);
        var doc = System.Xml.Linq.XDocument.Parse(csproj);   // throws when the escaping is wrong
        Assert.Equal("Tom & Jerry's", doc.Descendants("Product").Single().Value);
        Assert.Equal("Copyright (c) Tom & Jerry's", doc.Descendants("Copyright").Single().Value);
        Assert.Equal("com.example.TomJerrys", doc.Descendants("BundleId").Single().Value);
        // A deleted app.ico must not fail the publish.
        Assert.Equal("Exists('app.ico')", doc.Descendants("ApplicationIcon").Single().Attribute("Condition")?.Value);
        var templated = File.ReadAllText(Path.Combine(TemplateDir(), "RadSharpApp.csproj"));
        Assert.Equal("Exists('app.ico')", System.Xml.Linq.XDocument.Parse(templated).Descendants("ApplicationIcon").Single().Attribute("Condition")?.Value);
    }

    [Fact]
    public void Template_and_Create_agree_on_the_workflow_and_icon()
    {
        var template = TemplateDir();
        Assert.Equal(Normalize(RadProject.CrossPlatformWorkflow),
            Normalize(File.ReadAllText(Path.Combine(template, ".github", "workflows", "build.yml"))));
        Assert.Equal(RadProject.PackagingResource("app.ico"), File.ReadAllBytes(Path.Combine(template, "app.ico")));
        Assert.Equal(RadProject.PackagingResource("app.png"), File.ReadAllBytes(Path.Combine(template, "app.png")));

        // The packaging properties of the template's .csproj match Create's (the name is the only difference).
        var created = File.ReadAllText(RadProject.Create(TempDir(), "RadSharpApp").ProjectFile);
        var templated = File.ReadAllText(Path.Combine(template, "RadSharpApp.csproj"));
        foreach (var property in new[] { "ApplicationIcon", "Version", "Product", "Company", "Copyright", "BundleId" })
            Assert.Equal(Property(created, property), Property(templated, property));
    }
}
