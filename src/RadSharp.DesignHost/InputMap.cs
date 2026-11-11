namespace RadSharp.DesignHost;

/// <summary>Maps protocol input values to RADSharp's.</summary>
internal static class InputMap
{
    public static KeyModifiers Modifiers(Protocol.Modifiers mods)
    {
        var result = KeyModifiers.None;
        if ((mods & Protocol.Modifiers.Shift) != 0) result |= KeyModifiers.Shift;
        if ((mods & Protocol.Modifiers.Ctrl) != 0) result |= KeyModifiers.Ctrl;
        if ((mods & Protocol.Modifiers.Alt) != 0) result |= KeyModifiers.Alt;
        if ((mods & Protocol.Modifiers.Meta) != 0) result |= KeyModifiers.Meta;
        return result;
    }

    public static MouseButton Button(Protocol.MouseButton button) => button switch
    {
        Protocol.MouseButton.Right => MouseButton.Right,
        Protocol.MouseButton.Middle => MouseButton.Middle,
        _ => MouseButton.Left,
    };

    /// <summary>A RADSharp <see cref="RadSharp.Key"/> by name ("A", "Enter", "F9"...), or null if there is no such key.</summary>
    public static Key? Key(string name)
    {
        if (string.IsNullOrEmpty(name) || !char.IsLetter(name[0])) return null;   // no numeric values
        return Enum.TryParse<Key>(name, ignoreCase: false, out var key) && key != RadSharp.Key.Unknown && Enum.IsDefined(key) ? key : null;
    }
}
