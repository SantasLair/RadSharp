using System.Collections.Generic;
using System.Windows.Input;
using P = RadSharp.DesignHost.Protocol;

namespace RadSharp.VisualStudio.Client;

/// <summary>
/// Maps WPF input to the names and flags the design host understands. Key names are RadSharp <c>Key</c> enum names
/// (src/RadSharp.Core/Input/Input.cs); a test checks every name here against that file.
/// </summary>
public static class InputMapping
{
    private static readonly Dictionary<Key, string> Keys = BuildKeys();

    /// <summary>All WPF keys that have a RadSharp name, with that name.</summary>
    public static IReadOnlyDictionary<Key, string> KeyNames => Keys;

    /// <summary>
    /// The RadSharp key name for a WPF key event, or null when RadSharp has no such key. Unwraps
    /// <see cref="Key.System"/> (keys pressed with Alt), <see cref="Key.ImeProcessed"/> and
    /// <see cref="Key.DeadCharProcessed"/>.
    /// </summary>
    public static string? MapKey(KeyEventArgs e) => MapKey(RealKey(e));

    /// <summary>The RadSharp key name for <paramref name="key"/>, or null when there is none.</summary>
    public static string? MapKey(Key key) => Keys.TryGetValue(key, out var name) ? name : null;

    /// <summary>The key actually pressed: <see cref="KeyEventArgs.Key"/> with the System / IME / dead-key wrappers removed.</summary>
    public static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key,
    };

    /// <summary>WPF modifier keys to protocol modifier flags.</summary>
    public static P.Modifiers MapModifiers(ModifierKeys m)
    {
        var r = P.Modifiers.None;
        if ((m & ModifierKeys.Shift) != 0) r |= P.Modifiers.Shift;
        if ((m & ModifierKeys.Control) != 0) r |= P.Modifiers.Ctrl;
        if ((m & ModifierKeys.Alt) != 0) r |= P.Modifiers.Alt;
        if ((m & ModifierKeys.Windows) != 0) r |= P.Modifiers.Meta;
        return r;
    }

    /// <summary>WPF mouse button to protocol button, or null for the X buttons.</summary>
    public static P.MouseButton? MapButton(MouseButton b) => b switch
    {
        MouseButton.Left => P.MouseButton.Left,
        MouseButton.Right => P.MouseButton.Right,
        MouseButton.Middle => P.MouseButton.Middle,
        _ => null,
    };

    /// <summary>The RadSharp cursor names the host sends (src/RadSharp.Core/Input/Input.cs, <c>enum Cursor</c>).</summary>
    public static IReadOnlyDictionary<string, Cursor> CursorNames { get; } = new Dictionary<string, Cursor>
    {
        ["Default"] = Cursors.Arrow,
        ["IBeam"] = Cursors.IBeam,
        ["Hand"] = Cursors.Hand,
        ["SizeWE"] = Cursors.SizeWE,
        ["SizeNS"] = Cursors.SizeNS,
        ["SizeNWSE"] = Cursors.SizeNWSE,
        ["SizeNESW"] = Cursors.SizeNESW,
        ["SizeAll"] = Cursors.SizeAll,
        ["Wait"] = Cursors.Wait,
        ["NotAllowed"] = Cursors.No,
        ["Crosshair"] = Cursors.Cross,
    };

    /// <summary>The WPF cursor for a RadSharp cursor name; the arrow for unknown names.</summary>
    public static Cursor MapCursor(string? name) =>
        name != null && CursorNames.TryGetValue(name, out var c) ? c : Cursors.Arrow;

    /// <summary>True for keys that type a character: letters, digits, Space, the Oem punctuation keys and the numpad digits and
    /// operators. A KeyDown for one of these must stay unhandled so WPF still raises TextInput for it.</summary>
    public static bool ProducesText(Key key) =>
        (key >= Key.A && key <= Key.Z) || (key >= Key.D0 && key <= Key.D9) || (key >= Key.NumPad0 && key <= Key.NumPad9)
        || key == Key.Space || key == Key.Decimal || key == Key.Divide || key == Key.Subtract
        || key == Key.OemMinus || key == Key.OemPlus || key == Key.OemComma || key == Key.OemPeriod || key == Key.OemQuestion
        || key == Key.OemPipe || key == Key.OemBackslash || key == Key.OemSemicolon || key == Key.OemQuotes
        || key == Key.OemOpenBrackets || key == Key.OemCloseBrackets || key == Key.OemTilde;

    /// <summary>True when <paramref name="text"/> is printable text (no control characters), as typed text should be.</summary>
    public static bool IsPrintable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var ch in text!)
            if (ch < 0x20 || ch == 0x7F) return false;
        return true;
    }

    private static Dictionary<Key, string> BuildKeys()
    {
        // Indexer assignment, not Add: several WPF Key members are aliases of each other (Return/Enter, Prior/PageUp...).
        var d = new Dictionary<Key, string>();
        for (var k = Key.A; k <= Key.Z; k++) d[k] = ((char)('A' + (k - Key.A))).ToString();
        for (var k = Key.D0; k <= Key.D9; k++) d[k] = "D" + (k - Key.D0);
        for (var k = Key.NumPad0; k <= Key.NumPad9; k++) d[k] = "D" + (k - Key.NumPad0);
        for (var k = Key.F1; k <= Key.F12; k++) d[k] = "F" + (k - Key.F1 + 1);
        d[Key.Enter] = "Enter";
        d[Key.Escape] = "Escape";
        d[Key.Tab] = "Tab";
        d[Key.Back] = "Backspace";
        d[Key.Delete] = "Delete";
        d[Key.Insert] = "Insert";
        d[Key.Space] = "Space";
        d[Key.Home] = "Home";
        d[Key.End] = "End";
        d[Key.PageUp] = "PageUp";
        d[Key.PageDown] = "PageDown";
        d[Key.Left] = "Left";
        d[Key.Right] = "Right";
        d[Key.Up] = "Up";
        d[Key.Down] = "Down";
        d[Key.OemMinus] = "Minus";
        d[Key.Subtract] = "Minus";
        d[Key.OemPlus] = "Equals"; // the =/+ key
        d[Key.OemComma] = "Comma";
        d[Key.OemPeriod] = "Period";
        d[Key.Decimal] = "Period";
        d[Key.OemQuestion] = "Slash";
        d[Key.Divide] = "Slash";
        d[Key.OemPipe] = "Backslash";
        d[Key.OemBackslash] = "Backslash"; // the extra key next to left Shift on ISO keyboards
        d[Key.OemSemicolon] = "Semicolon";
        d[Key.OemQuotes] = "Apostrophe";
        d[Key.OemOpenBrackets] = "LeftBracket";
        d[Key.OemCloseBrackets] = "RightBracket";
        d[Key.OemTilde] = "Grave";
        d[Key.Apps] = "Menu";
        d[Key.LeftAlt] = "Alt";
        d[Key.RightAlt] = "Alt";
        return d;
    }
}
