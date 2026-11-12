using System.Runtime.InteropServices;
using RadSharp.Drawing;

namespace RadSharp.Platform.Sdl;

/// <summary>Windows-only window chrome: a dark title bar through DWM (Windows 10 2004+; caption colours need Windows 11).</summary>
internal static partial class Win32Chrome
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    /// <summary>The window's real OS state, which SDL's own flags can lag behind while the window is being maximised.</summary>
    public static bool IsMaximizedOrMinimized(nint hwnd)
    {
        try { return IsZoomed(hwnd) || IsIconic(hwnd); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    /// <summary>Dark title bar; the caption is painted in <paramref name="caption"/> with <paramref name="text"/> where supported.</summary>
    public static void ApplyDarkTitleBar(nint hwnd, Color caption, Color text)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return;
        try
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            int cap = ColorRef(caption), txt = ColorRef(text);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref cap, sizeof(int));   // fails harmlessly before Windows 11
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref txt, sizeof(int));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }
}
