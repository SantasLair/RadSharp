using System.Windows.Input;

namespace RadSharp.VisualStudio
{
    /// <summary>Where a key pressed in a RadSharp pane goes.</summary>
    public enum KeyRoute
    {
        /// <summary>Leave it to Visual Studio: a global shortcut, or (in the designer) an Edit command that VS turns into
        /// a command routed to the pane's IOleCommandTarget.</summary>
        VisualStudio,

        /// <summary>Send it to the design host as a key (and the text it types).</summary>
        Host,
    }

    /// <summary>Which pane has focus; the Edit shortcuts mean different things in them.</summary>
    public enum KeyContext
    {
        /// <summary>The design surface (document tab): Ctrl+C etc. are designer commands (copy controls).</summary>
        Designer,

        /// <summary>The Properties / Toolbox tool windows: Ctrl+C etc. edit text in the host's editors.</summary>
        ToolWindow,
    }

    /// <summary>
    /// Decides which keys Visual Studio keeps. VS translates key bindings into commands before the pane sees them, so a
    /// pane must claim the keys it wants (DesignerPane / RadSharpToolWindow do that in PreProcessMessage). No VS SDK
    /// dependency, so it is unit-tested.
    /// </summary>
    public static class KeyRouting
    {
        public static KeyRoute Classify(Key key, ModifierKeys modifiers, KeyContext context)
        {
            if (IsGlobalShortcut(key, modifiers))
                return KeyRoute.VisualStudio;
            if (context == KeyContext.Designer && IsEditCommandKey(key, modifiers))
                return KeyRoute.VisualStudio;
            return KeyRoute.Host;
        }

        /// <summary>The Edit-menu shortcuts: Ctrl+Z/Y/X/C/V/A, Ctrl+Shift+Z, Del and the Insert-key clipboard combinations.</summary>
        public static bool IsEditCommandKey(Key key, ModifierKeys modifiers)
        {
            var m = modifiers & ~ModifierKeys.Windows;
            switch (m)
            {
                case ModifierKeys.Control:
                    return key == Key.Z || key == Key.Y || key == Key.X || key == Key.C || key == Key.V || key == Key.A || key == Key.Insert;
                case ModifierKeys.Control | ModifierKeys.Shift:
                    return key == Key.Z;
                case ModifierKeys.Shift:
                    return key == Key.Insert || key == Key.Delete;
                case ModifierKeys.None:
                    return key == Key.Delete;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Shortcuts VS must keep wherever focus is: any Alt combination (menus, Alt+F4), function keys except F2 (debug,
        /// build, F4 Properties, F7 View Code...), Ctrl+Tab / Ctrl+F4 / Ctrl+S and other Ctrl+letter commands that make no
        /// sense in a form, and Ctrl+Shift+letter (build, find in files, new project...) except Ctrl+Shift+Z (redo).
        /// </summary>
        public static bool IsGlobalShortcut(Key key, ModifierKeys modifiers)
        {
            if ((modifiers & ModifierKeys.Alt) != 0 || key == Key.LeftAlt || key == Key.RightAlt || key == Key.System)
                return true;
            if (key >= Key.F1 && key <= Key.F24)
                return key != Key.F2 || (modifiers & ModifierKeys.Control) != 0;

            var ctrl = (modifiers & ModifierKeys.Control) != 0;
            var shift = (modifiers & ModifierKeys.Shift) != 0;
            if (!ctrl)
                return false;

            switch (key)
            {
                case Key.Tab:          // next / previous document
                case Key.OemComma:     // Go To All
                case Key.OemMinus:     // navigate backward / forward
                case Key.OemPlus:
                case Key.Pause:        // Ctrl+Break: stop build
                case Key.Cancel:
                    return true;
            }

            if (key >= Key.A && key <= Key.Z)
            {
                if (shift)
                    return key != Key.Z;
                // Ctrl+letter: VS commands that a form editor doesn't use. The Edit letters are not here (see
                // IsEditCommandKey); the rest (Ctrl+B, Ctrl+D, ...) go to the host.
                switch (key)
                {
                    case Key.S: // save
                    case Key.O: // open
                    case Key.N: // new
                    case Key.Q: // search
                    case Key.F: // find
                    case Key.H: // replace
                    case Key.P: // print
                    case Key.T: // go to
                    case Key.G: // go to line
                    case Key.W: // window chord (Ctrl+W, S = Solution Explorer ...)
                    case Key.K: // editor chords
                    case Key.R: // refactor / run tests chords
                    case Key.E: // editor chords
                    case Key.M: // outlining chords
                        return true;
                }
            }

            return false;
        }
    }
}
