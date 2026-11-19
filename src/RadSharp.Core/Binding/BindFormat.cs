using System.Globalization;

namespace RadSharp;

/// <summary>
/// How a value is shown in, and read back from, a text box bound by <see cref="Binder{T}"/>: a format function and a parse
/// function. A parse that throws <see cref="FormatException"/>, <see cref="OverflowException"/> or <see cref="ArgumentException"/>
/// becomes a validation error with <see cref="ErrorMessage"/>; the binder never throws for bad input.
/// </summary>
public sealed class BindFormat<TValue>(Func<TValue, string> format, Func<string, TValue> parse, string errorMessage = "Enter a valid value.")
{
    /// <summary>The message of the validation error when the text doesn't parse.</summary>
    public string ErrorMessage { get; } = errorMessage;

    /// <summary>The text for a value.</summary>
    public string Format(TValue value) => format(value);

    /// <summary>Reads a value from text; false (with <paramref name="error"/>) if it isn't one.</summary>
    public bool TryParse(string text, out TValue value, out string? error)
    {
        try
        {
            value = parse(text);
            error = null;
            return true;
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            value = default!;
            error = ErrorMessage;
            return false;
        }
    }
}

/// <summary>Ready-made <see cref="BindFormat{TValue}"/>s for numbers, in a culture (the current culture when none is given).</summary>
public static class BindFormat
{
    /// <summary>A decimal shown with a .NET numeric format (e.g. <c>"0.00"</c>, <c>"N2"</c>; empty is the general format).</summary>
    public static BindFormat<decimal> Decimal(string format = "", IFormatProvider? culture = null, string errorMessage = "Enter a number.") =>
        new(v => v.ToString(format, culture ?? CultureInfo.CurrentCulture),
            s => decimal.Parse(s.Trim(), NumberStyles.Number, culture ?? CultureInfo.CurrentCulture), errorMessage);

    /// <summary>An optional decimal: empty text is null.</summary>
    public static BindFormat<decimal?> NullableDecimal(string format = "", IFormatProvider? culture = null, string errorMessage = "Enter a number or leave it empty.") =>
        new(v => v is { } d ? d.ToString(format, culture ?? CultureInfo.CurrentCulture) : "",
            s => s.Trim().Length == 0 ? null : decimal.Parse(s.Trim(), NumberStyles.Number, culture ?? CultureInfo.CurrentCulture), errorMessage);

    /// <summary>A whole number (thousands separators allowed).</summary>
    public static BindFormat<int> Int32(string format = "", IFormatProvider? culture = null, string errorMessage = "Enter a whole number.") =>
        new(v => v.ToString(format, culture ?? CultureInfo.CurrentCulture),
            s => int.Parse(s.Trim(), NumberStyles.Integer | NumberStyles.AllowThousands, culture ?? CultureInfo.CurrentCulture), errorMessage);

    /// <summary>A floating-point number.</summary>
    public static BindFormat<double> Double(string format = "", IFormatProvider? culture = null, string errorMessage = "Enter a number.") =>
        new(v => v.ToString(format, culture ?? CultureInfo.CurrentCulture),
            s => double.Parse(s.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, culture ?? CultureInfo.CurrentCulture), errorMessage);
}
