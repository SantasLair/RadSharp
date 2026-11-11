using System.Runtime.Versioning;

namespace RadSharp.Designer;

/// <summary>
/// The operating system's light/dark preference. Neither <c>IPlatformServices</c> nor <c>Application</c> exposes it, so the
/// designer asks the OS itself where it can (the Windows registry); elsewhere <see cref="IsDark"/> is null and
/// "Follow OS theme" is unavailable. <see cref="Provider"/> lets tests substitute an answer.
/// </summary>
public static class SystemTheme
{
    /// <summary>Replaces the OS query (tests).</summary>
    public static Func<bool?>? Provider { get; set; }

    /// <summary>True for dark, false for light, null when the OS preference can't be read.</summary>
    public static bool? IsDark => Provider != null ? Provider() : Read();

    public static bool Available => IsDark != null;

    static bool? Read()
    {
        try { return OperatingSystem.IsWindows() ? ReadWindows() : null; }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return null; }
    }

    [SupportedOSPlatform("windows")]
    static bool? ReadWindows()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light ? light == 0 : null;
    }
}
