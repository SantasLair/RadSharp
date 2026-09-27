using System.Globalization;
using System.Text;

namespace RadSharp;

/// <summary>
/// Writes JSON in the canonical form-file layout (spec, Appendix A): two-space indentation, one
/// member per line, small structs and scalar arrays on one line, integers without a fraction, reals in
/// shortest round-trip form, strings UTF-8 with only <c>"</c>, <c>\</c> and control characters escaped,
/// <c>\n</c> line endings.
/// </summary>
public sealed class FormJsonWriter
{
    readonly StringBuilder _sb = new();
    readonly Stack<bool> _hasMembers = new();
    int _depth;

    public override string ToString() => _sb.ToString();

    void Indent() => _sb.Append(' ', _depth * 2);

    public void BeginObject()
    {
        _sb.Append('{');
        _hasMembers.Push(false);
        _depth++;
    }

    public void EndObject() => Close('}');

    public void BeginArray()
    {
        _sb.Append('[');
        _hasMembers.Push(false);
        _depth++;
    }

    public void EndArray() => Close(']');

    void Close(char close)
    {
        _depth--;
        if (_hasMembers.Pop()) { _sb.Append('\n'); Indent(); }
        _sb.Append(close);
    }

    /// <summary>Starts a member of the current object: writes the separator, newline, indent and key.</summary>
    public void Key(string name)
    {
        NextElement();
        WriteStringLiteral(name);
        _sb.Append(": ");
    }

    /// <summary>Starts an element of the current array.</summary>
    public void NextElement()
    {
        if (_hasMembers.Pop()) _sb.Append(',');
        _hasMembers.Push(true);
        _sb.Append('\n');
        Indent();
    }

    public void WriteString(string value) => WriteStringLiteral(value);
    public void WriteInt(int value) => _sb.Append(value.ToString(CultureInfo.InvariantCulture));
    public void WriteReal(double value) => _sb.Append(FormatReal(value));
    public void WriteBool(bool value) => _sb.Append(value ? "true" : "false");
    public void WriteNull() => _sb.Append("null");

    /// <summary><c>["left", "top"]</c> on one line.</summary>
    public void WriteInlineArray(IEnumerable<string> values)
    {
        _sb.Append('[');
        bool first = true;
        foreach (var v in values)
        {
            if (!first) _sb.Append(", ");
            WriteStringLiteral(v);
            first = false;
        }
        _sb.Append(']');
    }

    /// <summary><c>{ "l": 6, "t": 0, "r": 6, "b": 48 }</c> on one line.</summary>
    public void WriteInlineObject(IEnumerable<(string Key, int Value)> fields)
    {
        _sb.Append("{ ");
        bool first = true;
        foreach (var (k, v) in fields)
        {
            if (!first) _sb.Append(", ");
            WriteStringLiteral(k);
            _sb.Append(": ").Append(v.ToString(CultureInfo.InvariantCulture));
            first = false;
        }
        _sb.Append(" }");
    }

    /// <summary>Like the <c>int</c> overload, but for a struct with mixed-type fields (string, real or bool).</summary>
    public void WriteInlineObject(IEnumerable<(string Key, object Value)> fields)
    {
        _sb.Append("{ ");
        bool first = true;
        foreach (var (k, v) in fields)
        {
            if (!first) _sb.Append(", ");
            WriteStringLiteral(k);
            _sb.Append(": ");
            switch (v)
            {
                case string s: WriteStringLiteral(s); break;
                case bool b: _sb.Append(b ? "true" : "false"); break;
                case int i: _sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case float or double: _sb.Append(FormatReal(Convert.ToDouble(v, CultureInfo.InvariantCulture))); break;
                default: throw new ArgumentException($"Unsupported inline value type {v?.GetType()}.", nameof(fields));
            }
            first = false;
        }
        _sb.Append(" }");
    }

    /// <summary>Shortest round-trip form, invariant culture, no exponent; whole numbers without a fraction.</summary>
    public static string FormatReal(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Form files can't store NaN or infinity.");
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15) return ((long)value).ToString(CultureInfo.InvariantCulture);
        var s = value.ToString("R", CultureInfo.InvariantCulture);
        if (s.Contains('E')) s = ((decimal)value).ToString(CultureInfo.InvariantCulture);
        return s;
    }

    void WriteStringLiteral(string s)
    {
        _sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': _sb.Append("\\\""); break;
                case '\\': _sb.Append("\\\\"); break;
                case '\n': _sb.Append("\\n"); break;
                case '\r': _sb.Append("\\r"); break;
                case '\t': _sb.Append("\\t"); break;
                default:
                    if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else _sb.Append(c);
                    break;
            }
        }
        _sb.Append('"');
    }
}
