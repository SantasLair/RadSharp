using SDL;

namespace RadSharp.Platform.Sdl;

/// <summary>Translates SDL input codes to RADSharp's platform-neutral ones.</summary>
internal static class SdlInput
{
    public static KeyModifiers Modifiers(SDL_Keymod m)
    {
        var r = KeyModifiers.None;
        if ((m & SDL_Keymod.SDL_KMOD_SHIFT) != 0) r |= KeyModifiers.Shift;
        if ((m & SDL_Keymod.SDL_KMOD_CTRL) != 0) r |= KeyModifiers.Ctrl;
        if ((m & SDL_Keymod.SDL_KMOD_ALT) != 0) r |= KeyModifiers.Alt;
        if ((m & SDL_Keymod.SDL_KMOD_GUI) != 0) r |= KeyModifiers.Meta;
        return r;
    }

    public static MouseButton? Button(byte sdlButton) => sdlButton switch
    {
        1 => MouseButton.Left,
        2 => MouseButton.Middle,
        3 => MouseButton.Right,
        _ => null,
    };

    public static Key Key(SDL_Keycode code)
    {
        uint k = (uint)code;
        if (k >= 'a' && k <= 'z') return RadSharp.Key.A + (int)(k - 'a');
        if (k >= '0' && k <= '9') return RadSharp.Key.D0 + (int)(k - '0');
        return code switch
        {
            SDL_Keycode.SDLK_F1 => RadSharp.Key.F1, SDL_Keycode.SDLK_F2 => RadSharp.Key.F2,
            SDL_Keycode.SDLK_F3 => RadSharp.Key.F3, SDL_Keycode.SDLK_F4 => RadSharp.Key.F4,
            SDL_Keycode.SDLK_F5 => RadSharp.Key.F5, SDL_Keycode.SDLK_F6 => RadSharp.Key.F6,
            SDL_Keycode.SDLK_F7 => RadSharp.Key.F7, SDL_Keycode.SDLK_F8 => RadSharp.Key.F8,
            SDL_Keycode.SDLK_F9 => RadSharp.Key.F9, SDL_Keycode.SDLK_F10 => RadSharp.Key.F10,
            SDL_Keycode.SDLK_F11 => RadSharp.Key.F11, SDL_Keycode.SDLK_F12 => RadSharp.Key.F12,
            SDL_Keycode.SDLK_RETURN or SDL_Keycode.SDLK_KP_ENTER => RadSharp.Key.Enter,
            SDL_Keycode.SDLK_ESCAPE => RadSharp.Key.Escape,
            SDL_Keycode.SDLK_TAB => RadSharp.Key.Tab,
            SDL_Keycode.SDLK_BACKSPACE => RadSharp.Key.Backspace,
            SDL_Keycode.SDLK_DELETE => RadSharp.Key.Delete,
            SDL_Keycode.SDLK_INSERT => RadSharp.Key.Insert,
            SDL_Keycode.SDLK_SPACE => RadSharp.Key.Space,
            SDL_Keycode.SDLK_HOME => RadSharp.Key.Home,
            SDL_Keycode.SDLK_END => RadSharp.Key.End,
            SDL_Keycode.SDLK_PAGEUP => RadSharp.Key.PageUp,
            SDL_Keycode.SDLK_PAGEDOWN => RadSharp.Key.PageDown,
            SDL_Keycode.SDLK_LEFT => RadSharp.Key.Left,
            SDL_Keycode.SDLK_RIGHT => RadSharp.Key.Right,
            SDL_Keycode.SDLK_UP => RadSharp.Key.Up,
            SDL_Keycode.SDLK_DOWN => RadSharp.Key.Down,
            SDL_Keycode.SDLK_MINUS => RadSharp.Key.Minus,
            SDL_Keycode.SDLK_EQUALS => RadSharp.Key.Equals,
            SDL_Keycode.SDLK_COMMA => RadSharp.Key.Comma,
            SDL_Keycode.SDLK_PERIOD => RadSharp.Key.Period,
            SDL_Keycode.SDLK_SLASH => RadSharp.Key.Slash,
            SDL_Keycode.SDLK_BACKSLASH => RadSharp.Key.Backslash,
            SDL_Keycode.SDLK_SEMICOLON => RadSharp.Key.Semicolon,
            SDL_Keycode.SDLK_APOSTROPHE => RadSharp.Key.Apostrophe,
            SDL_Keycode.SDLK_LEFTBRACKET => RadSharp.Key.LeftBracket,
            SDL_Keycode.SDLK_RIGHTBRACKET => RadSharp.Key.RightBracket,
            SDL_Keycode.SDLK_GRAVE => RadSharp.Key.Grave,
            SDL_Keycode.SDLK_APPLICATION or SDL_Keycode.SDLK_MENU => RadSharp.Key.Menu,
            SDL_Keycode.SDLK_LALT or SDL_Keycode.SDLK_RALT => RadSharp.Key.Alt,
            _ => RadSharp.Key.Unknown,
        };
    }

    public static SDL_SystemCursor SystemCursor(Cursor c) => c switch
    {
        Cursor.IBeam => SDL_SystemCursor.SDL_SYSTEM_CURSOR_TEXT,
        Cursor.Hand => SDL_SystemCursor.SDL_SYSTEM_CURSOR_POINTER,
        Cursor.SizeWE => SDL_SystemCursor.SDL_SYSTEM_CURSOR_EW_RESIZE,
        Cursor.SizeNS => SDL_SystemCursor.SDL_SYSTEM_CURSOR_NS_RESIZE,
        Cursor.SizeNWSE => SDL_SystemCursor.SDL_SYSTEM_CURSOR_NWSE_RESIZE,
        Cursor.SizeNESW => SDL_SystemCursor.SDL_SYSTEM_CURSOR_NESW_RESIZE,
        Cursor.SizeAll => SDL_SystemCursor.SDL_SYSTEM_CURSOR_MOVE,
        Cursor.Wait => SDL_SystemCursor.SDL_SYSTEM_CURSOR_WAIT,
        Cursor.NotAllowed => SDL_SystemCursor.SDL_SYSTEM_CURSOR_NOT_ALLOWED,
        Cursor.Crosshair => SDL_SystemCursor.SDL_SYSTEM_CURSOR_CROSSHAIR,
        _ => SDL_SystemCursor.SDL_SYSTEM_CURSOR_DEFAULT,
    };
}
