using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RadSharp.Ide.Debug.Acquisition;

namespace RadSharp.Ide.Debug.Tests;

// The NETCOREDBG environment variable is process-global, so these tests must not run in parallel with each other.
[Collection("netcoredbg-env")]
public class AcquisitionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "radsharp-acq-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Theory]
    [InlineData(true, false, false, Architecture.X64, "win-x64")]
    [InlineData(false, true, false, Architecture.X64, "linux-x64")]
    [InlineData(false, true, false, Architecture.Arm64, "linux-arm64")]
    [InlineData(false, false, true, Architecture.X64, "osx-x64")]
    [InlineData(false, false, true, Architecture.Arm64, "osx-arm64")]
    public void Platform_maps_to_rid(bool win, bool linux, bool mac, Architecture arch, string rid) =>
        Assert.Equal(rid, NetCoreDbgAssets.ResolveRid(win, linux, mac, arch));

    [Theory]
    [InlineData(true, false, false, Architecture.Arm64)]
    [InlineData(false, true, false, Architecture.X86)]
    [InlineData(false, false, false, Architecture.X64)]
    public void Unsupported_platform_has_no_rid(bool win, bool linux, bool mac, Architecture arch) =>
        Assert.Null(NetCoreDbgAssets.ResolveRid(win, linux, mac, arch));

    [Fact]
    public void Every_supported_rid_has_a_pinned_github_asset()
    {
        foreach (var rid in NetCoreDbgAssets.SupportedRids)
        {
            var a = NetCoreDbgAssets.ForRid(rid);
            Assert.NotNull(a);
            Assert.Equal(rid, a!.Rid);
            Assert.NotNull(a.Sha256);
            Assert.Equal(64, a.Sha256!.Length);
            Assert.Equal("github.com", a.Url.Host);
            Assert.StartsWith("/Samsung/netcoredbg/releases/download/" + a.Version + "/", a.Url.AbsolutePath);
            Assert.EndsWith(a.Kind == ArchiveKind.Zip ? ".zip" : ".tar.gz", a.FileName);
        }
        Assert.Equal("netcoredbg.exe", NetCoreDbgAssets.ForRid("win-x64")!.ExecutableName);
        Assert.Equal("netcoredbg", NetCoreDbgAssets.ForRid("linux-x64")!.ExecutableName);
        Assert.Null(NetCoreDbgAssets.ForRid("freebsd-x64"));
    }

    [Fact]
    public void Default_cache_lives_under_local_app_data()
    {
        var root = NetCoreDbgAcquisition.DefaultCacheRoot();
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), root);
        Assert.EndsWith(Path.Combine("RadSharp", "tools", "netcoredbg"), root);
    }

    // ---- install / cache, against a fake HTTP handler ----

    private sealed class FakeHandler(byte[] payload, int status = 200) : HttpMessageHandler
    {
        public int Calls;
        public readonly List<HttpRequestMessage> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new ByteArrayContent(payload) });
        }
    }

    private static byte[] MakeZip(string exeName)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var e = zip.CreateEntry("netcoredbg/" + exeName);
            using (var w = new StreamWriter(e.Open())) w.Write("fake exe");
            var lib = zip.CreateEntry("netcoredbg/ManagedPart.dll");
            using (var w = new StreamWriter(lib.Open())) w.Write("lib");
        }
        return ms.ToArray();
    }

    private static NetCoreDbgAsset FakeAsset(byte[] zip, string? sha = "auto") =>
        new("win-x64", "9.9.9-1", "netcoredbg-win64.zip", ArchiveKind.Zip,
            sha == "auto" ? Convert.ToHexStringLower(SHA256.HashData(zip)) : sha);

    [Fact]
    public async Task Install_downloads_verifies_extracts_and_then_uses_the_cache()
    {
        var zip = MakeZip("netcoredbg.exe");
        var handler = new FakeHandler(zip);
        var acq = new NetCoreDbgAcquisition(_root, new HttpClient(handler));
        var asset = FakeAsset(zip);

        Assert.Null(acq.FindInCache(asset));
        var exe = await acq.InstallAsync(asset);

        Assert.True(File.Exists(exe));
        Assert.Equal("netcoredbg.exe", Path.GetFileName(exe));
        Assert.StartsWith(Path.Combine(_root, "9.9.9-1", "win-x64"), exe);
        Assert.Equal(1, handler.Calls);

        // second call: no network
        var again = await acq.InstallAsync(asset);
        Assert.Equal(exe, again);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(exe, acq.FindInCache(asset));

        // no leftovers of the staging area
        Assert.DoesNotContain(Directory.GetFileSystemEntries(_root), p => Path.GetFileName(p).StartsWith(".tmp-"));
    }

    [Fact]
    public async Task Download_request_is_anonymous_and_goes_to_the_pinned_url()
    {
        var zip = MakeZip("netcoredbg.exe");
        var handler = new FakeHandler(zip);
        var acq = new NetCoreDbgAcquisition(_root, new HttpClient(handler));
        var asset = FakeAsset(zip);
        await acq.InstallAsync(asset);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(asset.Url, req.RequestUri);
        Assert.Equal("RadSharp-Ide-ToolFetch/1", req.Headers.UserAgent.ToString());
        Assert.Equal(["User-Agent"], req.Headers.Select(h => h.Key));
        Assert.Null(req.Headers.Authorization);
    }

    [Fact]
    public async Task Checksum_mismatch_is_rejected_and_nothing_is_cached()
    {
        var zip = MakeZip("netcoredbg.exe");
        var acq = new NetCoreDbgAcquisition(_root, new HttpClient(new FakeHandler(zip)));
        var asset = FakeAsset(zip, sha: new string('0', 64));

        var ex = await Assert.ThrowsAsync<NetCoreDbgAcquisitionException>(() => acq.InstallAsync(asset));
        Assert.Contains("mismatch", ex.Message);
        Assert.Null(acq.FindInCache(asset));
        Assert.False(Directory.Exists(acq.GetInstallDirectory(asset)));
    }

    [Fact]
    public async Task Unpinned_asset_is_refused_unless_allowed()
    {
        var zip = MakeZip("netcoredbg.exe");
        var handler = new FakeHandler(zip);
        var acq = new NetCoreDbgAcquisition(_root, new HttpClient(handler));
        var asset = FakeAsset(zip, sha: null);

        await Assert.ThrowsAsync<NetCoreDbgAcquisitionException>(() => acq.InstallAsync(asset));
        Assert.Equal(0, handler.Calls);
        Assert.NotNull(await acq.InstallAsync(asset, allowUnpinned: true));
    }

    [Fact]
    public async Task Http_failure_and_archive_without_the_executable_are_reported()
    {
        var acq = new NetCoreDbgAcquisition(_root, new HttpClient(new FakeHandler([], 404)));
        await Assert.ThrowsAsync<NetCoreDbgAcquisitionException>(() => acq.InstallAsync(FakeAsset([1, 2, 3])));

        var zip = MakeZip("something-else.exe");
        var acq2 = new NetCoreDbgAcquisition(_root, new HttpClient(new FakeHandler(zip)));
        var ex = await Assert.ThrowsAsync<NetCoreDbgAcquisitionException>(() => acq2.InstallAsync(FakeAsset(zip)));
        Assert.Contains("did not contain", ex.Message);
    }

    [Fact]
    public async Task A_cache_entry_without_the_verified_marker_is_not_trusted()
    {
        var zip = MakeZip("netcoredbg.exe");
        var handler = new FakeHandler(zip);
        var acq = new NetCoreDbgAcquisition(_root, new HttpClient(handler));
        var asset = FakeAsset(zip);
        var dir = acq.GetInstallDirectory(asset);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "netcoredbg.exe"), "half-extracted");

        Assert.Null(acq.FindInCache(asset));
        var exe = await acq.InstallAsync(asset);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("fake exe", File.ReadAllText(exe));
    }

    // ---- lookup order ----

    [Fact]
    public void Explicit_path_wins_and_a_missing_explicit_path_throws()
    {
        Directory.CreateDirectory(_root);
        var exe = Path.Combine(_root, "my-netcoredbg.exe");
        File.WriteAllText(exe, "x");
        var acq = new NetCoreDbgAcquisition(_root);
        Assert.Equal(exe, acq.FindExisting(exe));
        Assert.Throws<FileNotFoundException>(() => acq.FindExisting(Path.Combine(_root, "nope.exe")));
    }

    [Fact]
    public void Environment_variable_is_used_when_no_explicit_path()
    {
        Directory.CreateDirectory(_root);
        var exe = Path.Combine(_root, "from-env.exe");
        File.WriteAllText(exe, "x");
        var old = Environment.GetEnvironmentVariable(NetCoreDbgAcquisition.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(NetCoreDbgAcquisition.EnvironmentVariable, exe);
            Assert.Equal(exe, new NetCoreDbgAcquisition(_root).FindExisting());
        }
        finally { Environment.SetEnvironmentVariable(NetCoreDbgAcquisition.EnvironmentVariable, old); }
    }

    [Fact]
    public void FindOnPath_scans_each_directory()
    {
        var a = Path.Combine(_root, "a");
        var b = Path.Combine(_root, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        var name = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        File.WriteAllText(Path.Combine(b, name), "x");
        var path = string.Join(Path.PathSeparator, a, "", b);
        Assert.Equal(Path.Combine(b, name), NetCoreDbgAcquisition.FindOnPath(path));
        Assert.Null(NetCoreDbgAcquisition.FindOnPath(a));
    }
}
