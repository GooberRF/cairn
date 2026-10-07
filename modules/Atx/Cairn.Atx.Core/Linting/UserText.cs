using System.Text;

namespace Cairn.Atx.Linting;

/// <summary>
/// Makes text the designer wrote safe to quote inside a problem message. A filename can legally
/// hold a newline, a tab or a control character, and the Problems panel draws one row per problem:
/// dropping such a value in raw would split the row, or leave an invisible character where the
/// designer expects to see what is wrong with their file.
/// </summary>
public static class UserText
{
    /// <summary>How much of a value a message quotes before trailing off.</summary>
    public const int MaxLength = 80;

    /// <summary>
    /// The value as it should appear inside a message: control characters written the way they
    /// would be typed in the file (as a backslash escape), and anything longer than
    /// <see cref="MaxLength"/> cut short with an ellipsis.
    /// </summary>
    public static string Printable(string? value) => Printable(value, MaxLength);

    /// <summary>
    /// The same, with a length of the caller's choosing — a file path shown in a dialog has room
    /// for far more than a problem message's quoted value does.
    /// </summary>
    /// <param name="value">The text to make safe.</param>
    /// <param name="maxLength">How much of it to keep.</param>
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
                    if (c < 0x20 || c == 0x7F)
                        sb.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        if (truncated) sb.Append('…');
        return sb.ToString();
    }
}
