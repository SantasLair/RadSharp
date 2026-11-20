using System.Diagnostics;

namespace RadSharp.RefApp;

/// <summary>
/// The package path, out of process: the repository's pack script (<c>pack.ps1</c>, <c>pack.sh</c> elsewhere) packs
/// RadSharp.Core (which carries the generators in analyzers/dotnet/cs and the form MSBuild wiring in build/ and
/// buildTransitive/), RadSharp.Platform.Sdl and the templates into artifacts/packages with one version, then <c>dotnet build</c> refapp/Tally, which restores them through its nuget.config as a user's
/// project would. It is slow on a cold cache (Release builds, a restore from nuget.org into refapp/Tally/packages) and it
/// adds three packages to artifacts/packages, so it only runs when asked for:
/// <c>RADSHARP_REFAPP_PACK=1 dotnet test tests/RadSharp.RefApp.Tests --filter Category=Pack</c>.
/// </summary>
public class TallyBuildTests
{
    [PackFact, Trait("Category", "Pack")]
    public void Tally_builds_against_the_packed_packages()
    {
        // The repository's pack script, as a user follows getting-started.md: one version for Core, Platform.Sdl and the
        // templates. The version is passed so the test knows which packages to expect (a fresh one, newer than any packed
        // before, so the floating 0.1.0-dev* reference picks it).
        var version = "0.1.0-dev." + DateTime.UtcNow.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);
        if (OperatingSystem.IsWindows())
            Run("powershell", RefApp.Root, $"-NoProfile -ExecutionPolicy Bypass -File pack.ps1 -Version {version}");
        else
            Run("sh", RefApp.Root, $"./pack.sh {version}");
        foreach (var package in new[] { "RadSharp.Core", "RadSharp.Platform.Sdl", "RadSharp.Templates" })
            Assert.True(File.Exists(Path.Combine(RefApp.Packages, $"{package}.{version}.nupkg")), $"pack didn't make {package} {version}.");

        // A floating version (0.1.0-dev*) is only re-resolved when restore is forced to; a no-op restore keeps the old one.
        Dotnet(RefApp.Tally, "restore Tally.csproj --force-evaluate");
        var output = Dotnet(RefApp.Tally, "build Tally.csproj --no-restore -nologo -c Debug");
        Assert.Contains("0 Warning(s)", output);
        Assert.Contains($"radsharp.platform.sdl/{version}", File.ReadAllText(Path.Combine(RefApp.Tally, "obj", "project.assets.json")).ToLowerInvariant());
    }

    static string Dotnet(string folder, string arguments) => Run("dotnet", folder, arguments);

    static string Run(string program, string folder, string arguments)
    {
        var psi = new ProcessStartInfo(program, arguments)
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(8))) { p.Kill(entireProcessTree: true); throw new TimeoutException($"dotnet {arguments} timed out."); }
        var text = stdout.Result + stderr.Result;
        Assert.True(p.ExitCode == 0, $"dotnet {arguments} failed ({p.ExitCode}):\n{text}");
        return text;
    }
}

/// <summary>A fact that runs only with <c>RADSHARP_REFAPP_PACK=1</c> (it packs and restores; see <see cref="TallyBuildTests"/>).</summary>
public sealed class PackFactAttribute : FactAttribute
{
    public PackFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RADSHARP_REFAPP_PACK") != "1")
            Skip = "Packs RadSharp and builds refapp/Tally from the packages: set RADSHARP_REFAPP_PACK=1 to run it.";
    }
}
