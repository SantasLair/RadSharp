using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace RadSharp.Design.Code;

/// <summary>A <c>PackageReference</c> in a project file.</summary>
public sealed record PackageRef(string Id, string? Version);

/// <summary>One hit of a nuget.org search.</summary>
public sealed record PackageSearchResult(string Id, string Version, string Description, long TotalDownloads, IReadOnlyList<string> Versions);

/// <summary>Runs <c>dotnet</c> with an argument list, reporting output lines; returns the exit code. Fake it in tests.</summary>
public interface IPackageRunner
{
    Task<int> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, Action<string> onLine, CancellationToken cancel = default);
}

/// <summary>The real runner: <c>dotnet</c> on the PATH.</summary>
public sealed class DotnetPackageRunner : IPackageRunner
{
    public async Task<int> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, Action<string> onLine, CancellationToken cancel = default)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(psi) ?? throw new IOException("Could not start dotnet.");
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancel);
        return process.ExitCode;
    }
}

/// <summary>
/// NuGet package management for a project: the installed references (read from the .csproj), searching nuget.org
/// through its public search API, and add/update/remove through <c>dotnet add/remove package</c>. Parsing and
/// command building are pure and testable without a network; the HTTP and process parts sit behind
/// <see cref="HttpClient"/> and <see cref="IPackageRunner"/>.
/// </summary>
public sealed class NuGetService
{
    public const string ServiceIndexUrl = "https://api.nuget.org/v3/index.json";
    /// <summary>Used if the service index can't be read.</summary>
    public const string FallbackSearchUrl = "https://azuresearch-usnc.nuget.org/query";

    readonly IPackageRunner _runner;
    readonly HttpClient _http;
    string? _searchUrl;

    public NuGetService(IPackageRunner? runner = null, HttpClient? http = null)
    {
        _runner = runner ?? new DotnetPackageRunner();
        _http = http ?? CreateHttpClient();
    }

    /// <summary>An HTTP client that identifies itself generically: no machine, user or project details.</summary>
    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RADSharp-IDE");
        return http;
    }

    // ---- Installed packages ----

    /// <summary>The <c>PackageReference</c>s of a project file (version from the attribute or a child element).</summary>
    public static IReadOnlyList<PackageRef> InstalledPackages(string projectFile) =>
        InstalledPackagesFromXml(File.ReadAllText(projectFile));

    public static IReadOnlyList<PackageRef> InstalledPackagesFromXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        var list = new List<PackageRef>();
        foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var id = (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var version = (string?)e.Attribute("Version") ?? (string?)e.Elements().FirstOrDefault(c => c.Name.LocalName == "Version");
            list.Add(new PackageRef(id, string.IsNullOrWhiteSpace(version) ? null : version.Trim()));
        }
        return list;
    }

    // ---- Search: URL building and parsing ----

    /// <summary>The search-query endpoint from a NuGet v3 service index (<c>SearchQueryService</c>), or null.</summary>
    public static string? ParseServiceIndex(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("resources", out var resources)) return null;
        foreach (var r in resources.EnumerateArray())
        {
            var type = r.TryGetProperty("@type", out var t) ? t.GetString() : null;
            if (type != null && type.StartsWith("SearchQueryService", StringComparison.Ordinal) && r.TryGetProperty("@id", out var id))
                return id.GetString();
        }
        return null;
    }

    public static string BuildSearchUrl(string endpoint, string query, bool includePrerelease = false, int skip = 0, int take = 25) =>
        $"{endpoint}?q={Uri.EscapeDataString(query)}&prerelease={(includePrerelease ? "true" : "false")}&skip={skip}&take={take}&semVerLevel=2.0.0";

    /// <summary>Parses a search response (<c>{"totalHits":…,"data":[{id,version,description,totalDownloads,versions:[{version}]}]}</c>).</summary>
    public static IReadOnlyList<PackageSearchResult> ParseSearchResults(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var results = new List<PackageSearchResult>();
        if (!doc.RootElement.TryGetProperty("data", out var data)) return results;
        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (string.IsNullOrEmpty(id)) continue;
            var version = item.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            var description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            long downloads = item.TryGetProperty("totalDownloads", out var n) && n.TryGetInt64(out var l) ? l : 0;
            var versions = new List<string>();
            if (item.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Array)
                foreach (var x in vs.EnumerateArray())
                    if (x.TryGetProperty("version", out var xv) && xv.GetString() is { } s) versions.Add(s);
            results.Add(new PackageSearchResult(id, version, description, downloads, versions));
        }
        return results;
    }

    public async Task<IReadOnlyList<PackageSearchResult>> SearchAsync(string query, bool includePrerelease = false, CancellationToken cancel = default)
    {
        var endpoint = await SearchEndpointAsync(cancel);
        var json = await _http.GetStringAsync(BuildSearchUrl(endpoint, query, includePrerelease), cancel);
        return ParseSearchResults(json);
    }

    async Task<string> SearchEndpointAsync(CancellationToken cancel)
    {
        if (_searchUrl != null) return _searchUrl;
        try { _searchUrl = ParseServiceIndex(await _http.GetStringAsync(ServiceIndexUrl, cancel)); }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException) { }
        return _searchUrl ??= FallbackSearchUrl;
    }

    // ---- dotnet command building ----

    /// <summary><c>dotnet add "proj" package id [--version v] [--prerelease]</c>. Without a version it adds (or updates to) the latest.</summary>
    public static IReadOnlyList<string> AddArguments(string projectFile, string id, string? version = null, bool prerelease = false)
    {
        var args = new List<string> { "add", projectFile, "package", id };
        if (!string.IsNullOrWhiteSpace(version)) { args.Add("--version"); args.Add(version.Trim()); }
        else if (prerelease) args.Add("--prerelease");
        return args;
    }

    public static IReadOnlyList<string> RemoveArguments(string projectFile, string id) => ["remove", projectFile, "package", id];

    // ---- Running ----

    public Task<int> AddAsync(RadProject project, string id, string? version, Action<string> onLine, bool prerelease = false, CancellationToken cancel = default) =>
        RunAsync(AddArguments(project.ProjectFile, id, version, prerelease), project, onLine, cancel);

    /// <summary>Updates to the latest version (or to <paramref name="version"/>): <c>dotnet add package</c> replaces the reference.</summary>
    public Task<int> UpdateAsync(RadProject project, string id, Action<string> onLine, string? version = null, CancellationToken cancel = default) =>
        RunAsync(AddArguments(project.ProjectFile, id, version), project, onLine, cancel);

    public Task<int> RemoveAsync(RadProject project, string id, Action<string> onLine, CancellationToken cancel = default) =>
        RunAsync(RemoveArguments(project.ProjectFile, id), project, onLine, cancel);

    Task<int> RunAsync(IReadOnlyList<string> args, RadProject project, Action<string> onLine, CancellationToken cancel)
    {
        onLine("dotnet " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        return _runner.RunAsync(args, project.Directory, onLine, cancel);
    }
}
