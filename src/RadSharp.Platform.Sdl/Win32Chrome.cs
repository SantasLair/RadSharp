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

    const int GWL_STYLE = -16;
    const long WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong32(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong32(nint hwnd, int index, int value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    /// <summary>A dialog frame: takes the minimise and maximise buttons off the title bar.</summary>
    public static void RemoveMinMaxButtons(nint hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return;
        try
        {
            long style = nint.Size == 8 ? GetWindowLongPtr(hwnd, GWL_STYLE) : GetWindowLong32(hwnd, GWL_STYLE);
            long wanted = style & ~(WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            if (wanted == style) return;
            if (nint.Size == 8) SetWindowLongPtr(hwnd, GWL_STYLE, (nint)wanted);
            else SetWindowLong32(hwnd, GWL_STYLE, unchecked((int)wanted));
            SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
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
