using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RadSharp.Generators;

/// <summary>A parsed JSON value with its position (0-based line and column) in the source text.</summary>
internal sealed class JsonNode
{
    public JsonKind Kind;
    public string? String;
    public double Number;
    public bool Bool;
    public List<KeyValuePair<string, JsonNode>>? Members;  // objects, in source order
    public List<JsonNode>? Items;                           // arrays
    public int Line, Column;
    /// <summary>Position of this value's key, for object members.</summary>
    public int KeyLine, KeyColumn;

    public JsonNode? this[string key]
    {
        get
        {
            if (Members == null) return null;
            foreach (var m in Members) if (m.Key == key) return m.Value;
            return null;
        }
    }
}

internal enum JsonKind { Object, Array, String, Number, Bool, Null }

internal sealed class JsonException(string message, int line, int column) : Exception(message)
{
    public int Line { get; } = line;
    public int Column { get; } = column;
}

/// <summary>
/// A minimal JSON parser for form files inside the source generator (which can't easily load
/// System.Text.Json). Keeps member order and source positions for diagnostics.
/// </summary>
internal sealed class JsonLite
{
    readonly string _s;
    int _i, _line, _lineStart;

    JsonLite(string s) { _s = s; }

    public static JsonNode Parse(string text)
    {
        var p = new JsonLite(text);
        if (p._i < text.Length && text[p._i] == '﻿') p._i++;
        var v = p.Value();
        p.Ws();
        if (p._i < text.Length) throw p.Error("unexpected text after the JSON value");
        return v;
    }

    JsonException Error(string message) => new(message, _line, _i - _lineStart);

    void Ws()
    {
        while (_i < _s.Length)
        {
            char c = _s[_i];
            if (c == '\n') { _i++; _line++; _lineStart = _i; }
            else if (c == ' ' || c == '\t' || c == '\r') _i++;
            else break;
        }
    }

    JsonNode Value()
    {
        Ws();
        if (_i >= _s.Length) throw Error("unexpected end of file");
        var node = new JsonNode { Line = _line, Column = _i - _lineStart };
        char c = _s[_i];
        switch (c)
        {
            case '{': Obj(node); break;
            case '[': Arr(node); break;
            case '"': node.Kind = JsonKind.String; node.String = Str(); break;
            case 't': Lit("true"); node.Kind = JsonKind.Bool; node.Bool = true; break;
            case 'f': Lit("false"); node.Kind = JsonKind.Bool; break;
            case 'n': Lit("null"); node.Kind = JsonKind.Null; break;
            default:
                if (c == '-' || (c >= '0' && c <= '9')) { node.Kind = JsonKind.Number; node.Number = Num(); }
                else throw Error($"unexpected character '{c}'");
                break;
        }
        return node;
    }

    void Lit(string word)
    {
        if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw Error($"expected {word}");
        _i += word.Length;
    }

    void Obj(JsonNode node)
    {
        node.Kind = JsonKind.Object;
        node.Members = new List<KeyValuePair<string, JsonNode>>();
        _i++;
        Ws();
        if (_i < _s.Length && _s[_i] == '}') { _i++; return; }
        while (true)
        {
            Ws();
            if (_i >= _s.Length || _s[_i] != '"') throw Error("expected a property name");
            int keyLine = _line, keyCol = _i - _lineStart;
            var key = Str();
            Ws();
            if (_i >= _s.Length || _s[_i] != ':') throw Error("expected ':'");
            _i++;
            var v = Value();
            v.KeyLine = keyLine;
            v.KeyColumn = keyCol;
            node.Members.Add(new KeyValuePair<string, JsonNode>(key, v));
            Ws();
            if (_i < _s.Length && _s[_i] == ',') { _i++; continue; }
            if (_i < _s.Length && _s[_i] == '}') { _i++; return; }
            throw Error("expected ',' or '}'");
        }
    }

    void Arr(JsonNode node)
    {
        node.Kind = JsonKind.Array;
        node.Items = new List<JsonNode>();
        _i++;
        Ws();
        if (_i < _s.Length && _s[_i] == ']') { _i++; return; }
        while (true)
        {
            node.Items.Add(Value());
            Ws();
            if (_i < _s.Length && _s[_i] == ',') { _i++; continue; }
            if (_i < _s.Length && _s[_i] == ']') { _i++; return; }
            throw Error("expected ',' or ']'");
        }
    }

    string Str()
    {
        _i++;  // opening quote
        var sb = new StringBuilder();
        while (true)
        {
            if (_i >= _s.Length) throw Error("unterminated string");
            char c = _s[_i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (_i >= _s.Length) throw Error("unterminated string");
            char e = _s[_i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (_i + 4 > _s.Length) throw Error("bad \\u escape");
                    sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    _i += 4;
                    break;
                default: throw Error($"bad escape '\\{e}'");
            }
        }
    }

    double Num()
    {
        int start = _i;
        if (_s[_i] == '-') _i++;
        while (_i < _s.Length && "0123456789.eE+-".IndexOf(_s[_i]) >= 0) _i++;
        if (!double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            throw Error("bad number");
        return d;
    }
}
