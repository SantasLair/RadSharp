using System.Diagnostics;

namespace RadSharp.RefApp;

/// <summary>
/// The package path, out of process: <c>dotnet pack</c> RadSharp.Core (which carries the generators in
/// analyzers/dotnet/cs and the form MSBuild wiring in build/ and buildTransitive/) and RadSharp.Platform.Sdl into
/// artifacts/packages, then <c>dotnet build</c> refapp/Tally, which restores them through its nuget.config as a user's
/// project would. It is slow on a cold cache (Release builds, a restore from nuget.org into refapp/Tally/packages) and it
/// adds two packages to artifacts/packages, so it only runs when asked for:
/// <c>RADSHARP_REFAPP_PACK=1 dotnet test tests/RadSharp.RefApp.Tests --filter Category=Pack</c>.
/// </summary>
public class TallyBuildTests
{
    [PackFact, Trait("Category", "Pack")]
    public void Tally_builds_against_the_packed_packages()
    {
        // One version for both packages: Platform.Sdl's dependency on Core is the version Core had in Sdl's own build, so
        // two packs that straddle a minute (Directory.Build.props stamps yyyyMMddHHmm) would otherwise disagree.
        var version = "0.1.0-dev." + DateTime.UtcNow.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var project in new[] { "src/RadSharp.Core/RadSharp.Core.csproj", "src/RadSharp.Platform.Sdl/RadSharp.Platform.Sdl.csproj" })
            Dotnet(RefApp.Root, $"pack {project} -c Release -nologo -p:Version={version}");
        Assert.True(File.Exists(Path.Combine(RefApp.Packages, $"RadSharp.Platform.Sdl.{version}.nupkg")));

        // A floating version (0.1.0-dev*) is only re-resolved when restore is forced to; a no-op restore keeps the old one.
        Dotnet(RefApp.Tally, "restore Tally.csproj --force-evaluate");
        var output = Dotnet(RefApp.Tally, "build Tally.csproj --no-restore -nologo -c Debug");
        Assert.Contains("0 Warning(s)", output);
        Assert.Contains($"radsharp.platform.sdl/{version}", File.ReadAllText(Path.Combine(RefApp.Tally, "obj", "project.assets.json")).ToLowerInvariant());
    }

    static string Dotnet(string folder, string arguments)
    {
        var psi = new ProcessStartInfo("dotnet", arguments)
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
