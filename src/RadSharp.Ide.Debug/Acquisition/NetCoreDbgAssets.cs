using System.Runtime.InteropServices;

namespace RadSharp.Ide.Debug.Acquisition;

public enum ArchiveKind { Zip, TarGz }

/// <summary>One downloadable netcoredbg build.</summary>
/// <param name="Rid">.NET runtime identifier (win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64).</param>
/// <param name="Version">The release tag on github.com/Samsung/netcoredbg, e.g. "3.2.0-1092".</param>
/// <param name="FileName">The release asset's file name.</param>
/// <param name="Sha256">Pinned lower-case hex SHA-256 of the archive; null means "not verifiable" and is refused unless explicitly allowed.</param>
public sealed record NetCoreDbgAsset(string Rid, string Version, string FileName, ArchiveKind Kind, string? Sha256)
{
    public Uri Url => new($"https://github.com/Samsung/netcoredbg/releases/download/{Version}/{FileName}");

    public string ExecutableName => Rid.StartsWith("win", StringComparison.Ordinal) ? "netcoredbg.exe" : "netcoredbg";
}

/// <summary>
/// The pinned asset catalogue. Hashes are the SHA-256 digests GitHub reports for each release asset (and, for
/// win-x64, were re-checked by hashing the downloaded file). Pinning means a swapped or corrupted release asset is
/// rejected. Bump a version by editing the entries here.
/// </summary>
public static class NetCoreDbgAssets
{
    public const string Repository = "Samsung/netcoredbg";

    public static readonly IReadOnlyList<string> SupportedRids = ["win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];

    // Release 3.2.0-1092 ships no osx-x64 build, so that RID stays on the last release that has one (3.1.3-1062).
    private static readonly NetCoreDbgAsset[] Catalog =
    [
        new("win-x64", "3.2.0-1092", "netcoredbg-win64.zip", ArchiveKind.Zip, "3c410a45fa502415203a94fcb88654af65bf8e3dac158a5527a722e7a6b9274a"),
        new("linux-x64", "3.2.0-1092", "netcoredbg-linux-amd64.tar.gz", ArchiveKind.TarGz, "080eb3b2d2152465f599d3b33d1ee6e747794e11cc0a3773ec689f5e5f2c5afa"),
        new("linux-arm64", "3.2.0-1092", "netcoredbg-linux-arm64.tar.gz", ArchiveKind.TarGz, "065ff49badec8a695dbea2de6ab6a330c774a191e426a217ab8cc05250627ccb"),
        new("osx-arm64", "3.2.0-1092", "netcoredbg-osx-arm64.zip", ArchiveKind.Zip, "f4fa33b3ff874910cc184b4bb3b9c56d0abdf5c6521cee0b144d7c6e4a6e59ea"),
        new("osx-x64", "3.1.3-1062", "netcoredbg-osx-amd64.tar.gz", ArchiveKind.TarGz, "49459b066836b6a452f418501d7ecab57bcd7e60d8464faac21ff70b496b8634"),
    ];

    public static NetCoreDbgAsset? ForRid(string rid) =>
        Catalog.FirstOrDefault(a => string.Equals(a.Rid, rid, StringComparison.OrdinalIgnoreCase));

    /// <summary>Maps an OS and CPU architecture to a RID, or null when netcoredbg has no build for it.</summary>
    public static string? ResolveRid(bool isWindows, bool isLinux, bool isMacOS, Architecture arch) => (isWindows, isLinux, isMacOS, arch) switch
    {
        (true, _, _, Architecture.X64) => "win-x64",
        (_, true, _, Architecture.X64) => "linux-x64",
        (_, true, _, Architecture.Arm64) => "linux-arm64",
        (_, _, true, Architecture.X64) => "osx-x64",
        (_, _, true, Architecture.Arm64) => "osx-arm64",
        _ => null,
    };

    public static string? CurrentRid() => ResolveRid(
        OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), RuntimeInformation.OSArchitecture);

    public static NetCoreDbgAsset? ForCurrentPlatform() => CurrentRid() is { } rid ? ForRid(rid) : null;
}
