using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace RadSharp.Ide.Debug.Acquisition;

public sealed class NetCoreDbgAcquisitionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Finds or downloads netcoredbg. Resolution order: an explicit path, the <c>NETCOREDBG</c> environment variable
/// (path to the executable), <c>netcoredbg</c> on PATH, the per-user cache, then a download of the pinned release
/// asset for this platform into the cache. Downloads send no identifying data (a fixed generic User-Agent, no
/// cookies, no machine or user information) and are verified against a pinned SHA-256 before anything is extracted.
/// </summary>
public sealed class NetCoreDbgAcquisition
{
    public const string EnvironmentVariable = "NETCOREDBG";
    private const string VerifiedMarker = ".verified";

    private readonly HttpClient _http;

    /// <param name="cacheRoot">Tool cache root; defaults to <see cref="DefaultCacheRoot"/>. Versions live in subfolders.</param>
    /// <param name="http">Optional client (tests inject a fake handler); a default one is created otherwise.</param>
    public NetCoreDbgAcquisition(string? cacheRoot = null, HttpClient? http = null)
    {
        CacheRoot = cacheRoot ?? DefaultCacheRoot();
        _http = http ?? new HttpClient();
    }

    public string CacheRoot { get; }

    /// <summary>%LOCALAPPDATA%/RadSharp/tools/netcoredbg (or the platform's equivalent).</summary>
    public static string DefaultCacheRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadSharp", "tools", "netcoredbg");

    /// <summary>Directory an asset is extracted to: <c>&lt;root&gt;/&lt;version&gt;/&lt;rid&gt;</c>.</summary>
    public string GetInstallDirectory(NetCoreDbgAsset asset) => Path.Combine(CacheRoot, asset.Version, asset.Rid);

    /// <summary>The cached executable for an asset if it was installed and verified earlier, else null. No network.</summary>
    public string? FindInCache(NetCoreDbgAsset asset)
    {
        var dir = GetInstallDirectory(asset);
        if (!File.Exists(Path.Combine(dir, VerifiedMarker))) return null;
        return FindExecutable(dir, asset.ExecutableName);
    }

    /// <summary>Finds an already-available netcoredbg without downloading: explicit path, env var, PATH, cache.</summary>
    public string? FindExisting(string? explicitPath = null, NetCoreDbgAsset? asset = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : throw new FileNotFoundException("netcoredbg not found at the given path.", explicitPath);

        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return Path.GetFullPath(env);

        if (FindOnPath() is { } onPath) return onPath;

        asset ??= NetCoreDbgAssets.ForCurrentPlatform();
        return asset is null ? null : FindInCache(asset);
    }

    /// <summary>Looks for <c>netcoredbg</c> (<c>.exe</c> on Windows) in each PATH directory.</summary>
    public static string? FindOnPath(string? pathVariable = null)
    {
        var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        var name = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* invalid path characters in PATH entry */ }
        }
        return null;
    }

    /// <summary>
    /// Returns a usable netcoredbg executable, downloading the pinned release asset for this platform on first use.
    /// </summary>
    public Task<string> EnsureAsync(string? explicitPath = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var existing = FindExisting(explicitPath);
        if (existing is not null) return Task.FromResult(existing);

        var asset = NetCoreDbgAssets.ForCurrentPlatform()
            ?? throw new NetCoreDbgAcquisitionException(
                $"netcoredbg has no build for this platform ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}). Set {EnvironmentVariable} to a netcoredbg executable.");
        return InstallAsync(asset, progress, ct);
    }

    /// <summary>
    /// Downloads, verifies and extracts <paramref name="asset"/> into the cache (a no-op when it is already there)
    /// and returns the executable path. An asset without a pinned hash is refused unless <paramref name="allowUnpinned"/> is set.
    /// </summary>
    public async Task<string> InstallAsync(NetCoreDbgAsset asset, IProgress<double>? progress = null, CancellationToken ct = default, bool allowUnpinned = false)
    {
        if (FindInCache(asset) is { } cached) return cached;
        if (asset.Sha256 is null && !allowUnpinned)
            throw new NetCoreDbgAcquisitionException($"No pinned SHA-256 for {asset.FileName} ({asset.Version}); refusing to install an unverifiable download.");

        Directory.CreateDirectory(CacheRoot);
        var temp = Path.Combine(CacheRoot, ".tmp-" + Guid.NewGuid().ToString("N"));
        var archivePath = temp + ".archive";
        var stage = temp + ".extract";
        try
        {
            var actual = await DownloadAsync(asset, archivePath, progress, ct).ConfigureAwait(false);
            if (asset.Sha256 is not null && !string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new NetCoreDbgAcquisitionException(
                    $"SHA-256 mismatch for {asset.FileName}: expected {asset.Sha256}, got {actual}. The download was discarded.");

            Directory.CreateDirectory(stage);
            await Task.Run(() => Extract(archivePath, asset.Kind, stage), ct).ConfigureAwait(false);
            if (FindExecutable(stage, asset.ExecutableName) is null)
                throw new NetCoreDbgAcquisitionException($"{asset.FileName} did not contain {asset.ExecutableName}.");
            EnsureExecutable(stage, asset.ExecutableName);
            File.WriteAllText(Path.Combine(stage, VerifiedMarker), actual);

            var final = GetInstallDirectory(asset);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            if (Directory.Exists(final)) Directory.Delete(final, true); // an unverified or partial earlier attempt
            try { Directory.Move(stage, final); }
            catch (IOException) when (FindInCache(asset) is not null) { /* another process won the race */ }
            return FindInCache(asset) ?? throw new NetCoreDbgAcquisitionException("Installation finished but the executable was not found.");
        }
        finally
        {
            TryDelete(archivePath);
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
        }
    }

    private async Task<string> DownloadAsync(NetCoreDbgAsset asset, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        // GitHub rejects requests without a User-Agent. This one names only the tool, nothing about the user.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RadSharp-Ide-ToolFetch", "1"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new NetCoreDbgAcquisitionException($"Downloading {asset.Url} failed: HTTP {(int)response.StatusCode}.");

        var total = response.Content.Headers.ContentLength;
        using var sha = SHA256.Create();
        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(destination);
        var buffer = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha.TransformBlock(buffer, 0, n, null, 0);
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n;
            if (total is > 0) progress?.Report((double)done / total.Value);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }

    private static void Extract(string archive, ArchiveKind kind, string destination)
    {
        // Both ExtractToDirectory implementations refuse entries that escape the destination.
        if (kind == ArchiveKind.Zip)
        {
            ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: true);
        }
        else
        {
            using var file = File.OpenRead(archive);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, destination, overwriteFiles: true);
        }
    }

    private static string? FindExecutable(string dir, string executableName)
    {
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, executableName, SearchOption.AllDirectories)
            .OrderBy(p => p.Length) // the top-level one, not a nested copy
            .FirstOrDefault();
    }

    private static void EnsureExecutable(string dir, string executableName)
    {
        if (OperatingSystem.IsWindows()) return;
        var exe = FindExecutable(dir, executableName);
        if (exe is null) return;
        var mode = File.GetUnixFileMode(exe);
        File.SetUnixFileMode(exe, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
