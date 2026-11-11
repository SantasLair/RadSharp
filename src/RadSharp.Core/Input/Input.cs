namespace RadSharp;

public enum MouseButton { Left, Right, Middle }

[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
    /// <summary>⌘ on macOS, the Windows key elsewhere.</summary>
    Meta = 8,
}

/// <summary>Platform-neutral key codes.</summary>
public enum Key
{
    Unknown,
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Enter, Escape, Tab, Backspace, Delete, Insert, Space,
    Home, End, PageUp, PageDown, Left, Right, Up, Down,
    Minus, Equals, Comma, Period, Slash, Backslash, Semicolon, Apostrophe, LeftBracket, RightBracket, Grave,
    /// <summary>The context-menu (Application) key.</summary>
    Menu,
    /// <summary>An Alt key by itself (left or right); sent with <see cref="KeyModifiers.Alt"/>.</summary>
    Alt,
}

public enum Cursor { Default, IBeam, Hand, SizeWE, SizeNS, SizeNWSE, SizeNESW, SizeAll, Wait, NotAllowed, Crosshair }

public class MouseEventArgs(int x, int y, MouseButton button, KeyModifiers modifiers, int clicks = 1) : EventArgs
{
    /// <summary>Pointer position relative to the control receiving the event.</summary>
    public int X { get; } = x;
    public int Y { get; } = y;
    public MouseButton Button { get; } = button;
    public KeyModifiers Modifiers { get; } = modifiers;
    /// <summary>1 for a single click, 2 for a double click, and so on.</summary>
    public int Clicks { get; } = clicks;
}

public sealed class MouseWheelEventArgs(int x, int y, float deltaX, float deltaY, KeyModifiers modifiers) : EventArgs
{
    public int X { get; } = x;
    public int Y { get; } = y;
    /// <summary>Wheel movement in notches; positive = right.</summary>
    public float DeltaX { get; } = deltaX;
    /// <summary>Wheel movement in notches; positive = away from the user (scroll up).</summary>
    public float DeltaY { get; } = deltaY;
    public KeyModifiers Modifiers { get; } = modifiers;
    /// <summary>Set by a handler to stop the event bubbling to the parent.</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// An in-app drag, or a file drop from the desktop, in progress (Delphi's OnDragOver/OnDragDrop). For an
/// in-app drag, <see cref="Source"/> is the control it began on and there's no separate payload object — a
/// target inspects the source itself (e.g. <c>e.Source is TreeView tv ? tv.Selected?.Tag : null</c>) to
/// decide what's being dragged, the same way Delphi's own <c>Source: TObject</c> parameter works. A file
/// drop from the desktop uses the same event so one control (say, an image viewer) can handle both a drag
/// from elsewhere in the app and a file dragged in from Explorer/Finder the same way — <see cref="Files"/>
/// is set, and <see cref="Source"/> is the form, which carries no useful "what's being dragged" itself.
/// </summary>
public sealed class DragEventArgs(Control source, int x, int y) : EventArgs
{
    public Control Source { get; } = source;
    /// <summary>Pointer position relative to the control receiving the event.</summary>
    public int X { get; } = x;
    public int Y { get; } = y;
    /// <summary>Set only for a file drop from the desktop: the paths being dropped. Null for an in-app drag.</summary>
    public IReadOnlyList<string>? Files { get; init; }
    public bool IsFileDrop => Files != null;
    /// <summary>Set true by an <c>OnDragOver</c> handler to allow a drop here; read again on drop.</summary>
    public bool Accept { get; set; }
    /// <summary>Set by a handler to stop the event bubbling to the parent.</summary>
    public bool Handled { get; set; }
}

public sealed class KeyEventArgs(Key key, KeyModifiers modifiers, bool repeat = false) : EventArgs
{
    public Key Key { get; } = key;
    public KeyModifiers Modifiers { get; } = modifiers;
    public bool IsRepeat { get; } = repeat;
    public bool Shift => Modifiers.HasFlag(KeyModifiers.Shift);
    /// <summary>The platform command modifier is down (Ctrl, or ⌘ on macOS).</summary>
    public bool Command => Modifiers.HasFlag(Shortcut.CommandModifier);
    /// <summary>Set by a handler to stop further processing (shortcuts, default buttons).</summary>
    public bool Handled { get; set; }
}

public sealed class TextInputEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
    public bool Handled { get; set; }
}

/// <summary>
/// An input method (IME) composition in progress: the text being composed (not yet committed) and
/// the cursor within it. Empty text means the composition ended; the committed text arrives as
/// ordinary text input.
/// </summary>
public sealed class TextCompositionEventArgs(string text, int cursor, int selectionLength) : EventArgs
{
    public string Text { get; } = text;
    public int Cursor { get; } = cursor;
    public int SelectionLength { get; } = selectionLength;
}

public delegate void NotifyEvent(Control sender);
public delegate void MouseEvent(Control sender, MouseEventArgs e);
public delegate void MouseWheelEvent(Control sender, MouseWheelEventArgs e);
public delegate void KeyEvent(Control sender, KeyEventArgs e);
public delegate void TextInputEvent(Control sender, TextInputEventArgs e);
public delegate void DragEvent(Control sender, DragEventArgs e);

/// <summary>Delphi's <c>TDragMode</c>: whether pressing and moving on this control starts an in-app drag by
/// itself, or only when its own code calls <see cref="Form.BeginDrag"/>.</summary>
public enum DragMode { Manual, Automatic }

/// <summary>
/// A key combination written as text, e.g. <c>"Mod+Shift+Z"</c> (spec, Appendix C). <c>Mod</c> is the
/// platform command key: Ctrl on Windows/Linux, ⌘ (Meta) on macOS.
/// </summary>
public readonly record struct Shortcut(Key Key, KeyModifiers Modifiers)
{
    /// <summary>What <c>Mod</c> means on this platform. Settable for tests.</summary>
    public static KeyModifiers CommandModifier { get; set; } = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Ctrl;

    static readonly Dictionary<string, Key> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = Key.Enter, ["Return"] = Key.Enter, ["Esc"] = Key.Escape, ["Escape"] = Key.Escape,
        ["Tab"] = Key.Tab, ["Backspace"] = Key.Backspace, ["Del"] = Key.Delete, ["Delete"] = Key.Delete,
        ["Ins"] = Key.Insert, ["Insert"] = Key.Insert, ["Space"] = Key.Space, ["Home"] = Key.Home, ["End"] = Key.End,
        ["PgUp"] = Key.PageUp, ["PageUp"] = Key.PageUp, ["PgDn"] = Key.PageDown, ["PageDown"] = Key.PageDown,
        ["Left"] = Key.Left, ["Right"] = Key.Right, ["Up"] = Key.Up, ["Down"] = Key.Down,
        ["-"] = Key.Minus, ["="] = Key.Equals, [","] = Key.Comma, ["."] = Key.Period, ["/"] = Key.Slash,
        ["Menu"] = Key.Menu, ["Apps"] = Key.Menu,
    };

    public static bool TryParse(string? text, out Shortcut shortcut)
    {
        shortcut = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+');
        var mods = KeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "mod": mods |= CommandModifier; break;
                case "ctrl": mods |= KeyModifiers.Ctrl; break;
                case "alt": case "option": mods |= KeyModifiers.Alt; break;
                case "shift": mods |= KeyModifiers.Shift; break;
                case "meta": case "cmd": mods |= KeyModifiers.Meta; break;
                default: return false;
            }
        }
        var keyName = parts[^1].Trim();
        if (keyName.Length == 0 && text.EndsWith('+')) keyName = "+";
        Key key;
        if (keyName.Length == 1 && char.IsAsciiLetter(keyName[0])) key = Key.A + (char.ToUpperInvariant(keyName[0]) - 'A');
        else if (keyName.Length == 1 && char.IsAsciiDigit(keyName[0])) key = Key.D0 + (keyName[0] - '0');
        else if (keyName.Length is 2 or 3 && (keyName[0] is 'F' or 'f') && int.TryParse(keyName.AsSpan(1), out var fn) && fn is >= 1 and <= 12) key = Key.F1 + (fn - 1);
        else if (!Names.TryGetValue(keyName, out key)) return false;
        shortcut = new Shortcut(key, mods);
        return true;
    }

    public static Shortcut Parse(string text) =>
        TryParse(text, out var s) ? s : throw new FormatException($"Invalid shortcut \"{text}\".");

    public bool Matches(KeyEventArgs e) => e.Key == Key && e.Modifiers == Modifiers;

    /// <summary>Display text for menus: <c>Ctrl+Shift+Z</c>, or <c>⌘⇧Z</c> style names on macOS.</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add(OperatingSystem.IsMacOS() ? "Option" : "Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Meta)) parts.Add(OperatingSystem.IsMacOS() ? "Cmd" : "Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    static string KeyName(Key k) => k switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (k - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F12 => $"F{k - Key.F1 + 1}",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        Key.Escape => "Esc",
        Key.Delete => "Del",
        Key.Minus => "-", Key.Equals => "=", Key.Comma => ",", Key.Period => ".", Key.Slash => "/",
        _ => k.ToString(),
    };
}
