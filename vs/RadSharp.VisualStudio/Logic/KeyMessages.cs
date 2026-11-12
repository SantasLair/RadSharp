using System;
using System.Windows.Input;

namespace RadSharp.VisualStudio
{
    /// <summary>Win32 keyboard messages as VS hands them to IVsWindowPane.TranslateAccelerator.</summary>
    public static class KeyMessages
    {
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_CHAR = 0x0102;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

        /// <summary>The WPF key of a WM_KEYDOWN / WM_SYSKEYDOWN, or false for any other message.</summary>
        public static bool TryGetKeyDown(int message, IntPtr wParam, out Key key)
        {
            key = Key.None;
            if (message != WM_KEYDOWN && message != WM_SYSKEYDOWN)
                return false;
            key = KeyInterop.KeyFromVirtualKey(unchecked((int)wParam.ToInt64()));
            return key != Key.None;
        }

        /// <summary>
        /// Should a key-down reach the pane's WPF content instead of VS's key bindings? Modifier keys pressed alone are
        /// never claimed (VS needs them for chords and Alt menu access); Alt combinations arrive as WM_SYSKEYDOWN and are
        /// global (see <see cref="KeyRouting.IsGlobalShortcut"/>).
        /// </summary>
        public static bool ShouldClaim(int message, IntPtr wParam, ModifierKeys modifiers, KeyContext context)
        {
            if (!TryGetKeyDown(message, wParam, out var key))
                return false;
            switch (key)
            {
                case Key.LeftShift:
                case Key.RightShift:
                case Key.LeftCtrl:
                case Key.RightCtrl:
                case Key.LeftAlt:
                case Key.RightAlt:
                case Key.LWin:
                case Key.RWin:
                    return false;
            }

            if (message == WM_SYSKEYDOWN)
                modifiers |= ModifierKeys.Alt;
            return KeyRouting.Classify(key, modifiers, context) == KeyRoute.Host;
        }
    }
}
