using System.Text.Json;

namespace RadSharp;

/// <summary>Converts form-file JSON values to property values; throws <see cref="FormatException"/> on a wrong type.</summary>
internal static class JsonValues
{
    public static int Int(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : throw Wrong(v, "an integer");

    public static bool Bool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Wrong(v, "true or false"),
    };

    public static string String(JsonElement v) =>
        v.ValueKind == JsonValueKind.String ? v.GetString()! : throw Wrong(v, "a string");

    public static T Enum<T>(JsonElement v) where T : struct, System.Enum =>
        v.ValueKind == JsonValueKind.String
        && System.Enum.TryParse<T>(v.GetString(), ignoreCase: true, out var e)
        && System.Enum.IsDefined(e)
            ? e
            : throw Wrong(v, $"one of {string.Join(", ", System.Enum.GetNames<T>()).ToLowerInvariant()}");

    public static Anchors AnchorSet(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Array) throw Wrong(v, "an array of anchor names");
        var result = Anchors.None;
        foreach (var item in v.EnumerateArray())
        {
            result |= item.GetString() switch
            {
                "left" => Anchors.Left,
                "top" => Anchors.Top,
                "right" => Anchors.Right,
                "bottom" => Anchors.Bottom,
                _ => throw Wrong(item, "left, top, right or bottom"),
            };
        }
        return result;
    }

    public static Thickness Thickness(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object) throw Wrong(v, "an object {l, t, r, b}");
        int Field(string n) => v.TryGetProperty(n, out var f) ? Int(f) : 0;
        return new Thickness(Field("l"), Field("t"), Field("r"), Field("b"));
    }

    public static Drawing.Color Color(JsonElement v) =>
        v.ValueKind == JsonValueKind.String && Drawing.Color.TryParse(v.GetString(), out var c)
            ? c
            : throw Wrong(v, "a colour \"#RRGGBB\" or \"#RRGGBBAA\"");

    static FormatException Wrong(JsonElement v, string expected) =>
        new($"expected {expected}, got {v.GetRawText()}");
}
