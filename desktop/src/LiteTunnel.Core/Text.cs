using System.Globalization;
using System.Text;

namespace LiteTunnel.Core;

public static class Base64Text
{
    /// <summary>Decodes standard or URL-safe base64, with or without padding and line breaks.</summary>
    public static byte[]? Decode(string value)
    {
        var s = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (s.Length == 0) return null;
        s = s.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        switch (s.Length % 4)
        {
            case 1: return null;
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }

    public static string? DecodeString(string value) => Decode(value) is { } bytes ? Utf8OrNull(bytes) : null;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>The bytes as UTF-8 text, or null when they are not valid UTF-8.</summary>
    public static string? Utf8OrNull(byte[] bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }
}

internal static class TextUtil
{
    /// <summary>
    /// Like Foundation's removingPercentEncoding: null when a % is not followed
    /// by two hex digits or the bytes are not UTF-8. <c>+</c> stays <c>+</c>.
    /// </summary>
    public static string? PercentDecode(string s)
    {
        if (!s.Contains('%')) return s;
        var bytes = new List<byte>(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '%')
            {
                if (i + 2 >= s.Length) return null;
                int hi = HexValue(s[i + 1]);
                int lo = HexValue(s[i + 2]);
                if (hi < 0 || lo < 0) return null;
                bytes.Add((byte)(hi * 16 + lo));
                i += 3;
            }
            else
            {
                int end = char.IsHighSurrogate(c) && i + 1 < s.Length ? i + 2 : i + 1;
                bytes.AddRange(Encoding.UTF8.GetBytes(s, i, end - i));
                i = end;
            }
        }
        return Base64Text.Utf8OrNull(bytes.ToArray());
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>Like Swift's Int(String): optional sign, ASCII digits, nothing else.</summary>
    public static int? ParseInt(string? s) =>
        s is not null && int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static long? ParseLong(string? s) =>
        s is not null && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static double? ParseDouble(string? s) =>
        s is not null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Splits on the first <paramref name="separator"/>; empty parts are kept.</summary>
    public static (string Head, string? Tail) SplitOnce(string s, char separator)
    {
        int at = s.IndexOf(separator);
        return at < 0 ? (s, null) : (s[..at], s[(at + 1)..]);
    }
}
