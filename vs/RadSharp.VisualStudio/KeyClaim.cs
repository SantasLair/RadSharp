using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace RadSharp.VisualStudio
{
    /// <summary>
    /// Keeps keys away from VS's key bindings. VS calls the active pane's IVsWindowPane.TranslateAccelerator (MPF:
    /// WindowPane.PreProcessMessage) with every key message before it translates key bindings into commands. Returning
    /// true there means "handled", so VS neither runs a command nor dispatches the message. For the keys a RadSharp pane
    /// wants (<see cref="KeyMessages.ShouldClaim"/>), we translate and dispatch the message ourselves, so it reaches the
    /// focused WPF element (the RemoteWindowView) as a normal KeyDown plus TextInput, and VS never sees it as a shortcut.
    /// Every other key is left to VS: global shortcuts, and in the designer the Edit shortcuts, which VS turns into
    /// commands for the pane's IOleCommandTarget.
    /// </summary>
    internal static class KeyClaim
    {
        public static bool PreProcess(ref System.Windows.Forms.Message m, UIElement content, KeyContext context)
        {
            if (!content.IsKeyboardFocusWithin)
                return false;
            if (!KeyMessages.ShouldClaim(m.Msg, m.WParam, Keyboard.Modifiers, context))
                return false;

            var msg = new MSG { hwnd = m.HWnd, message = (uint)m.Msg, wParam = m.WParam, lParam = m.LParam };
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    }
}
