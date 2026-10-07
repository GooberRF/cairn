using System.Globalization;
using System.Text;

namespace Cairn.Rfa.Linting;

/// <summary>
/// Makes text taken from a file (bone names, texture names, file names) safe to quote inside a problem
/// message: control characters are escaped and long values are cut short, so a stray byte cannot split
/// a Problems row or hide what is wrong. Ported from ATX Workbench.
/// </summary>
public static class UserText
{
    /// <summary>How much of a value a message quotes before trailing off.</summary>
    public const int MaxLength = 80;

    /// <summary>The value as it should appear inside a message.</summary>
    public static string Printable(string? value) => Printable(value, MaxLength);

    /// <summary>The value as it should appear inside a message, cut at <paramref name="maxLength"/> characters.</summary>
    public static string Printable(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        maxLength = Math.Max(1, maxLength);
        bool truncated = value.Length > maxLength;
        var source = truncated ? value.AsSpan(0, maxLength) : value.AsSpan();
        var sb = new StringBuilder(source.Length + 8);
        foreach (char c in source)
        {
            switch (c)
            {
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == 0x7F) sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        if (truncated) sb.Append('…');
        return sb.ToString();
    }

    /// <summary>Ticks as frames and seconds for messages, e.g. "tick 480 (frame 3, 0.10 s)".</summary>
    public static string Ticks(int ticks) =>
        string.Create(CultureInfo.InvariantCulture, $"tick {ticks} (frame {ticks / 160.0:0.##}, {ticks / 4800.0:0.00} s)");
}
