using System.Globalization;
using System.Text.Json;
using RadSharp.Drawing;

namespace RadSharp;

/// <summary>Reads, writes, compares and formats one kind of published value.</summary>
public abstract class ValueConverter<T>
{
    public abstract PropertyType Type { get; }
    public virtual IReadOnlyList<string>? Values => null;
    public abstract T Read(JsonElement json);
    public abstract void Write(FormJsonWriter writer, T value);
    public virtual bool AreEqual(T a, T b) => EqualityComparer<T>.Default.Equals(a, b);
    public virtual string Format(T value) => value?.ToString() ?? "";
    public abstract bool TryParse(string text, out T value);

    protected static FormatException Wrong(JsonElement v, string expected) =>
        new($"expected {expected}, got {v.GetRawText()}");
}

/// <summary>The converters the metadata generator assigns to published property types.</summary>
public static class Converters
{
    public static readonly ValueConverter<string> String = new StringConverter();
    public static readonly ValueConverter<int> Int = new IntConverter();
    public static readonly ValueConverter<double> Real = new RealConverter();
    public static readonly ValueConverter<bool> Bool = new BoolConverter();
    public static readonly ValueConverter<Color> Color = new ColorConverter();
    public static readonly ValueConverter<Color?> NullableColor = new NullableColorConverter();
    public static readonly ValueConverter<Anchors> Anchors = new AnchorsConverter();
    public static readonly ValueConverter<Thickness> Thickness = new ThicknessConverter();

    public static ValueConverter<T> Enum<T>() where T : struct, System.Enum => EnumConverter<T>.Instance;

    sealed class StringConverter : ValueConverter<string>
    {
        public override PropertyType Type => PropertyType.String;
        public override string Read(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw Wrong(v, "a string");
        public override void Write(FormJsonWriter w, string value) => w.WriteString(value);
        public override bool TryParse(string text, out string value) { value = text; return true; }
    }

    sealed class IntConverter : ValueConverter<int>
    {
        public override PropertyType Type => PropertyType.Int;
        public override int Read(JsonElement v) =>
            v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : throw Wrong(v, "an integer");
        public override void Write(FormJsonWriter w, int value) => w.WriteInt(value);
        public override string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
        public override bool TryParse(string text, out int value) =>
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    sealed class RealConverter : ValueConverter<double>
    {
        public override PropertyType Type => PropertyType.Real;
        public override double Read(JsonElement v) =>
            v.ValueKind == JsonValueKind.Number ? v.GetDouble() : throw Wrong(v, "a number");
        public override void Write(FormJsonWriter w, double value) => w.WriteReal(value);
        public override string Format(double value) => FormJsonWriter.FormatReal(value);
        public override bool TryParse(string text, out double value) =>
            double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    sealed class BoolConverter : ValueConverter<bool>
    {
        public override PropertyType Type => PropertyType.Bool;
        public override IReadOnlyList<string> Values { get; } = ["false", "true"];
        public override bool Read(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Wrong(v, "true or false"),
        };
        public override void Write(FormJsonWriter w, bool value) => w.WriteBool(value);
        public override string Format(bool value) => value ? "true" : "false";
        public override bool TryParse(string text, out bool value) => bool.TryParse(text.Trim(), out value);
    }

    sealed class ColorConverter : ValueConverter<Color>
    {
        public override PropertyType Type => PropertyType.Color;
        public override Color Read(JsonElement v) =>
            v.ValueKind == JsonValueKind.String && Drawing.Color.TryParse(v.GetString(), out var c) ? c : throw Wrong(v, "a colour \"#RRGGBB\"");
        public override void Write(FormJsonWriter w, Color value) => w.WriteString(value.ToHex());
        public override string Format(Color value) => value.ToHex();
        public override bool TryParse(string text, out Color value) => Drawing.Color.TryParse(text.Trim(), out value);
    }

    sealed class NullableColorConverter : ValueConverter<Color?>
    {
        public override PropertyType Type => PropertyType.Color;
        public override Color? Read(JsonElement v) => v.ValueKind == JsonValueKind.Null ? null : Color.Read(v);
        public override void Write(FormJsonWriter w, Color? value)
        {
            if (value is { } c) w.WriteString(c.ToHex()); else w.WriteNull();
        }
        public override string Format(Color? value) => value?.ToHex() ?? "";
        public override bool TryParse(string text, out Color? value)
        {
            value = null;
            if (text.Trim().Length == 0) return true;
            if (!Drawing.Color.TryParse(text.Trim(), out var c)) return false;
            value = c;
            return true;
        }
    }

    sealed class AnchorsConverter : ValueConverter<Anchors>
    {
        static readonly (Anchors Flag, string Name)[] Order =
            [(RadSharp.Anchors.Left, "left"), (RadSharp.Anchors.Top, "top"), (RadSharp.Anchors.Right, "right"), (RadSharp.Anchors.Bottom, "bottom")];

        public override PropertyType Type => PropertyType.Anchors;

        public override Anchors Read(JsonElement v)
        {
            if (v.ValueKind != JsonValueKind.Array) throw Wrong(v, "an array of anchor names");
            var result = RadSharp.Anchors.None;
            foreach (var item in v.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                var match = Order.FirstOrDefault(o => o.Name == name);
                if (match.Name == null) throw Wrong(item, "left, top, right or bottom");
                result |= match.Flag;
            }
            return result;
        }

        /// <summary>Written in the order left, top, right, bottom (spec, Appendix A).</summary>
        public override void Write(FormJsonWriter w, Anchors value) =>
            w.WriteInlineArray(Order.Where(o => value.HasFlag(o.Flag)).Select(o => o.Name));

        public override string Format(Anchors value) =>
            string.Join(", ", Order.Where(o => value.HasFlag(o.Flag)).Select(o => o.Name));

        public override bool TryParse(string text, out Anchors value)
        {
            value = RadSharp.Anchors.None;
            foreach (var part in text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Order.FirstOrDefault(o => o.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (match.Name == null) return false;
                value |= match.Flag;
            }
            return true;
        }
    }

    sealed class ThicknessConverter : ValueConverter<Thickness>
    {
        public override PropertyType Type => PropertyType.Struct;

        public override Thickness Read(JsonElement v)
        {
            if (v.ValueKind != JsonValueKind.Object) throw Wrong(v, "an object {l, t, r, b}");
            int Field(string n) => v.TryGetProperty(n, out var f) ? Int.Read(f) : 0;
            return new Thickness(Field("l"), Field("t"), Field("r"), Field("b"));
        }

        /// <summary>Written whole, fields in declared order l, t, r, b.</summary>
        public override void Write(FormJsonWriter w, Thickness t) =>
            w.WriteInlineObject([("l", t.L), ("t", t.T), ("r", t.R), ("b", t.B)]);

        public override string Format(Thickness t) => $"{t.L}, {t.T}, {t.R}, {t.B}";

        public override bool TryParse(string text, out Thickness value)
        {
            value = default;
            var parts = text.Split(',');
            var n = new int[4];
            if (parts.Length == 1 && int.TryParse(parts[0].Trim(), out var all)) { value = new Thickness(all, all, all, all); return true; }
            if (parts.Length != 4) return false;
            for (int i = 0; i < 4; i++)
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i])) return false;
            value = new Thickness(n[0], n[1], n[2], n[3]);
            return true;
        }
    }

    /// <summary>Enum values are written camelCased: <c>Align.Client</c> → <c>"client"</c>, <c>Cursor.SizeWE</c> → <c>"sizeWE"</c>.</summary>
    sealed class EnumConverter<T> : ValueConverter<T> where T : struct, System.Enum
    {
        public static readonly EnumConverter<T> Instance = new();
        readonly T[] _values = System.Enum.GetValues<T>();
        readonly string[] _names;

        EnumConverter() => _names = [.. System.Enum.GetNames<T>().Select(n => char.ToLowerInvariant(n[0]) + n[1..])];

        public override PropertyType Type => PropertyType.Enum;
        public override IReadOnlyList<string> Values => _names;

        public override T Read(JsonElement v)
        {
            var s = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (s != null && TryParse(s, out var value)) return value;
            throw Wrong(v, "one of " + string.Join(", ", _names));
        }

        public override void Write(FormJsonWriter w, T value) => w.WriteString(Format(value));

        public override string Format(T value)
        {
            int i = Array.IndexOf(_values, value);
            return i >= 0 ? _names[i] : value.ToString();
        }

        public override bool TryParse(string text, out T value)
        {
            int i = Array.FindIndex(_names, n => n.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));
            value = i >= 0 ? _values[i] : default;
            return i >= 0;
        }
    }
}
