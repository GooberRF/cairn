using System.Globalization;
using Cairn.Atx.Schema;

namespace Cairn.Atx.Editing;

/// <summary>A scalar TOML value to write into a document.</summary>
public readonly struct AtxValue : IEquatable<AtxValue>
{
    private AtxValue(AtxValueKind kind, long number, bool flag, string? text)
    {
        Kind = kind;
        Number = number;
        Flag = flag;
        Text = text;
    }

    /// <summary>The TOML type that will be written.</summary>
    public AtxValueKind Kind { get; }

    /// <summary>The integer payload, for <see cref="AtxValueKind.Integer"/>.</summary>
    public long Number { get; }

    /// <summary>The boolean payload, for <see cref="AtxValueKind.Boolean"/>.</summary>
    public bool Flag { get; }

    /// <summary>The string payload, for <see cref="AtxValueKind.String"/>.</summary>
    public string? Text { get; }

    public static AtxValue String(string value) => new(AtxValueKind.String, 0, false, value);

    public static AtxValue Integer(long value) => new(AtxValueKind.Integer, value, false, null);

    public static AtxValue Boolean(bool value) => new(AtxValueKind.Boolean, 0, value, null);

    /// <summary>The value as it should appear in the file, e.g. <c>"name.tga"</c> or <c>80</c>.</summary>
    public string ToToml() => Kind switch
    {
        AtxValueKind.Integer => Number.ToString(CultureInfo.InvariantCulture),
        AtxValueKind.Boolean => Flag ? "true" : "false",
        _ => TomlText.QuoteBasicString(Text ?? string.Empty),
    };

    public bool Equals(AtxValue other) =>
        Kind == other.Kind && Number == other.Number && Flag == other.Flag &&
        string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is AtxValue v && Equals(v);

    public override int GetHashCode() => HashCode.Combine(Kind, Number, Flag, Text);

    public static bool operator ==(AtxValue a, AtxValue b) => a.Equals(b);

    public static bool operator !=(AtxValue a, AtxValue b) => !a.Equals(b);

    public override string ToString() => ToToml();
}

/// <summary>TOML text helpers used when generating document text.</summary>
public static class TomlText
{
    /// <summary>Wraps <paramref name="value"/> in a TOML basic string with correct escaping.</summary>
    public static string QuoteBasicString(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (c < 0x20 || c == 0x7F)
                        sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
