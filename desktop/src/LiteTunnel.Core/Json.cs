using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace LiteTunnel.Core;

/// <summary>
/// A JSON tree. Servers keep their sing-box outbound in this form, so a link,
/// a sing-box subscription and a hand-edited server all end up the same way.
/// Immutable; equality is structural, and an integer never equals a double.
/// </summary>
public abstract class Json : IEquatable<Json>
{
    private protected Json() { }

    public virtual Json? this[string key] => null;
    public virtual Json? this[int index] => null;

    public string? AsString => (this as JString)?.Value;

    public long? AsLong => this switch
    {
        JInt i => i.Value,
        JDouble d when d.Value % 1.0 == 0.0 && Math.Abs(d.Value) < 9.2e18 => (long)d.Value,
        JString s when long.TryParse(s.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) => v,
        _ => null,
    };

    public int? AsInt => AsLong is long l && l >= int.MinValue && l <= int.MaxValue ? (int)l : null;
    public bool? AsBool => (this as JBool)?.Value;
    public IReadOnlyList<Json>? AsArray => (this as JArray)?.Items;
    public IReadOnlyDictionary<string, Json>? AsObject => (this as JObject)?.Map;

    /// <summary>A copy of this object with <paramref name="key"/> set, or removed when <paramref name="value"/> is null.</summary>
    public Json With(string key, Json? value)
    {
        if (this is not JObject o) return this;
        var next = new Dictionary<string, Json>(o.Map);
        if (value is null) next.Remove(key); else next[key] = value;
        return new JObject(next);
    }

    public string ToJsonString(bool pretty = false)
    {
        var sb = new StringBuilder();
        JsonWriter.Write(this, sb, pretty, 0);
        return sb.ToString();
    }

    public override string ToString() => ToJsonString();

    public static Json Parse(string text) => new JsonParser(text).ParseDocument();

    public static Json Strings(IEnumerable<string> values) => new JArray(values.Select(v => (Json)new JString(v)).ToList());

    public static readonly Json Null = JNull.Instance;

    /// <summary>Builds an object; null values are left out.</summary>
    public static Json Object(params (string Key, Json? Value)[] pairs)
    {
        var map = new Dictionary<string, Json>();
        foreach (var (key, value) in pairs)
            if (value is not null) map[key] = value;
        return new JObject(map);
    }

    public static Json Array(params Json[] items) => new JArray(items.ToList());
    public static Json Array(IEnumerable<Json> items) => new JArray(items.ToList());

    /// <summary>A null string stays null, so <see cref="Object"/> leaves the key out.</summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static implicit operator Json?(string? value) => value is null ? null : new JString(value);
    public static implicit operator Json(bool value) => new JBool(value);
    public static implicit operator Json(int value) => new JInt(value);
    public static implicit operator Json(long value) => new JInt(value);
    public static implicit operator Json(double value) => new JDouble(value);

    public abstract bool Equals(Json? other);
    public override bool Equals(object? obj) => obj is Json j && Equals(j);
    public abstract override int GetHashCode();

    public static bool operator ==(Json? a, Json? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(Json? a, Json? b) => !(a == b);
}

public sealed class JNull : Json
{
    public static readonly JNull Instance = new();
    private JNull() { }
    public override bool Equals(Json? other) => other is JNull;
    public override int GetHashCode() => 0;
}

public sealed class JBool(bool value) : Json
{
    public bool Value { get; } = value;
    public override bool Equals(Json? other) => other is JBool b && b.Value == Value;
    public override int GetHashCode() => Value ? 1 : 2;
}

public sealed class JInt(long value) : Json
{
    public long Value { get; } = value;
    public override bool Equals(Json? other) => other is JInt i && i.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
}

public sealed class JDouble(double value) : Json
{
    public double Value { get; } = value;
    public override bool Equals(Json? other) => other is JDouble d && d.Value.Equals(Value);
    public override int GetHashCode() => Value.GetHashCode();
}

public sealed class JString(string value) : Json
{
    public string Value { get; } = value;
    public override bool Equals(Json? other) => other is JString s && s.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
}

public sealed class JArray(IReadOnlyList<Json> items) : Json
{
    public IReadOnlyList<Json> Items { get; } = items;
    public override Json? this[int index] => index >= 0 && index < Items.Count ? Items[index] : null;
    public override bool Equals(Json? other) => other is JArray a && a.Items.SequenceEqual(Items);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var item in Items) h.Add(item);
        return h.ToHashCode();
    }
}

public sealed class JObject(IReadOnlyDictionary<string, Json> map) : Json
{
    public IReadOnlyDictionary<string, Json> Map { get; } = map;
    public override Json? this[string key] => Map.TryGetValue(key, out var v) ? v : null;

    public override bool Equals(Json? other)
    {
        if (other is not JObject o || o.Map.Count != Map.Count) return false;
        foreach (var (key, value) in Map)
            if (!o.Map.TryGetValue(key, out var v) || !v.Equals(value)) return false;
        return true;
    }

    public override int GetHashCode()
    {
        // Order-independent, like the equality.
        int h = Map.Count;
        foreach (var (key, value) in Map) h ^= HashCode.Combine(key, value);
        return h;
    }
}

public sealed class JsonException(string message) : Exception(message);

internal static class JsonWriter
{
    public static void Write(Json value, StringBuilder sb, bool pretty, int depth)
    {
        switch (value)
        {
            case JNull: sb.Append("null"); break;
            case JBool b: sb.Append(b.Value ? "true" : "false"); break;
            case JInt i: sb.Append(i.Value.ToString(CultureInfo.InvariantCulture)); break;
            case JDouble d: WriteDouble(d.Value, sb); break;
            case JString s: WriteString(s.Value, sb); break;
            case JArray a:
                if (a.Items.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int n = 0; n < a.Items.Count; n++)
                {
                    if (n > 0) sb.Append(',');
                    Newline(sb, pretty, depth + 1);
                    Write(a.Items[n], sb, pretty, depth + 1);
                }
                Newline(sb, pretty, depth);
                sb.Append(']');
                break;
            case JObject o:
                if (o.Map.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                // Sorted, like the Swift and Android versions: the same server gives the same text.
                var keys = o.Map.Keys.ToList();
                keys.Sort(StringComparer.Ordinal);
                for (int n = 0; n < keys.Count; n++)
                {
                    if (n > 0) sb.Append(',');
                    Newline(sb, pretty, depth + 1);
                    WriteString(keys[n], sb);
                    sb.Append(pretty ? " : " : ":");
                    Write(o.Map[keys[n]], sb, pretty, depth + 1);
                }
                Newline(sb, pretty, depth);
                sb.Append('}');
                break;
        }
    }

    private static void Newline(StringBuilder sb, bool pretty, int depth)
    {
        if (!pretty) return;
        sb.Append('\n');
        for (int n = 0; n < depth; n++) sb.Append("  ");
    }

    private static void WriteDouble(double d, StringBuilder sb)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
        else if (d % 1.0 == 0.0 && Math.Abs(d) < 1e15) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
        else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}

internal sealed class JsonParser(string text)
{
    private int _i = text.StartsWith('﻿') ? 1 : 0;

    public Json ParseDocument()
    {
        var value = ParseValue();
        SkipSpace();
        if (_i != text.Length) throw Fail("лишние символы");
        return value;
    }

    private Json ParseValue()
    {
        SkipSpace();
        if (_i >= text.Length) throw Fail("конец текста");
        var c = text[_i];
        return c switch
        {
            '{' => ParseObject(),
            '[' => ParseArray(),
            '"' => new JString(ParseString()),
            't' => Literal("true", new JBool(true)),
            'f' => Literal("false", new JBool(false)),
            'n' => Literal("null", JNull.Instance),
            _ when c == '-' || char.IsAsciiDigit(c) => ParseNumber(),
            _ => throw Fail($"символ {c}"),
        };
    }

    private Json ParseObject()
    {
        _i++;
        var map = new Dictionary<string, Json>();
        SkipSpace();
        if (Peek() == '}') { _i++; return new JObject(map); }
        while (true)
        {
            SkipSpace();
            if (Peek() != '"') throw Fail("ключ");
            var key = ParseString();
            SkipSpace();
            if (Peek() != ':') throw Fail(":");
            _i++;
            map[key] = ParseValue();
            SkipSpace();
            switch (Peek())
            {
                case ',': _i++; break;
                case '}': _i++; return new JObject(map);
                default: throw Fail(", или }");
            }
        }
    }

    private Json ParseArray()
    {
        _i++;
        var items = new List<Json>();
        SkipSpace();
        if (Peek() == ']') { _i++; return new JArray(items); }
        while (true)
        {
            items.Add(ParseValue());
            SkipSpace();
            switch (Peek())
            {
                case ',': _i++; break;
                case ']': _i++; return new JArray(items);
                default: throw Fail(", или ]");
            }
        }
    }

    private string ParseString()
    {
        _i++;
        var sb = new StringBuilder();
        while (true)
        {
            if (_i >= text.Length) throw Fail("строка не закрыта");
            var c = text[_i++];
            if (c == '"') return sb.ToString();
            if (c == '\\')
            {
                if (_i >= text.Length) throw Fail("экранирование");
                var e = text[_i++];
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
                        if (_i + 4 > text.Length ||
                            !int.TryParse(text.AsSpan(_i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                            throw Fail("\\u");
                        sb.Append((char)code);
                        _i += 4;
                        break;
                    default: throw Fail($"экранирование \\{e}");
                }
            }
            else if (c < ' ') throw Fail("управляющий символ в строке");
            else sb.Append(c);
        }
    }

    private Json ParseNumber()
    {
        int start = _i;
        if (Peek() == '-') _i++;
        while (_i < text.Length && (char.IsAsciiDigit(text[_i]) || ".eE+-".Contains(text[_i]))) _i++;
        var raw = text[start.._i];
        if (raw.IndexOfAny(['.', 'e', 'E']) < 0 &&
            long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
            return new JInt(l);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) throw Fail($"число {raw}");
        // An integral number reads as an integer, as JSONDecoder does on Apple platforms.
        if (d % 1.0 == 0.0 && Math.Abs(d) < 9.0e15) return new JInt((long)d);
        return new JDouble(d);
    }

    private Json Literal(string word, Json value)
    {
        if (string.CompareOrdinal(text, _i, word, 0, word.Length) != 0) throw Fail(word);
        _i += word.Length;
        return value;
    }

    private char? Peek() => _i < text.Length ? text[_i] : null;

    private void SkipSpace()
    {
        while (_i < text.Length && text[_i] is ' ' or '\t' or '\n' or '\r') _i++;
    }

    private JsonException Fail(string what) => new($"JSON: {what} (позиция {_i})");
}
